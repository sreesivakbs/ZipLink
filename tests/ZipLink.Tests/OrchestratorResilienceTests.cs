using System.Text.Json;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Tests;

/// <summary>Slice B behaviour: retries, rollback, safe-stop, re-planning and metrics.</summary>
public class OrchestratorResilienceTests
{
    private const string Actor = "tester";

    private sealed class FlakyExecutor : IStageExecutor
    {
        private readonly int _failuresBeforeSuccess;

        public FlakyExecutor(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int Calls { get; private set; }

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken token)
        {
            Calls++;

            return Task.FromResult(
                Calls > _failuresBeforeSuccess
                    ? StageResult.Success($"ok on attempt {Calls}", new Payload($"attempt-{Calls}"))
                    : StageResult.Failure($"attempt {Calls} failed"));
        }
    }

    private sealed class SimpleExecutor : IStageExecutor
    {
        private readonly Func<StageContext, StageResult> _result;

        public SimpleExecutor(Func<StageContext, StageResult>? result = null)
        {
            _result = result ?? (_ => StageResult.Success("ok", new Payload("value")));
        }

        public int Calls { get; private set; }

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken token)
        {
            Calls++;

            return Task.FromResult(_result(context));
        }
    }

    private sealed record Payload(string Value);

    // ---------- bounded retries ----------

    [Fact]
    public async Task AFailingStageIsRetriedAndCanRecover()
    {
        using var repo = new TempRepository();

        var flaky = new FlakyExecutor(failuresBeforeSuccess: 2);

        var pipeline = new StagePipeline(
            [new("flaky", "Flaky", [], false)],
            new Dictionary<string, IStageExecutor> { ["flaky"] = flaky });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root, new RetryPolicy(MaxAttempts: 3));

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.Equal(3, flaky.Calls);
        Assert.Equal(3, state.Stage("flaky")!.Attempts);
    }

    [Fact]
    public async Task RetriesAreBoundedAndTheRunEventuallyFails()
    {
        using var repo = new TempRepository();

        var alwaysFails = new FlakyExecutor(failuresBeforeSuccess: int.MaxValue);

        var pipeline = new StagePipeline(
            [new("flaky", "Flaky", [], false)],
            new Dictionary<string, IStageExecutor> { ["flaky"] = alwaysFails });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root, new RetryPolicy(MaxAttempts: 3));

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Equal(3, alwaysFails.Calls);
        Assert.Equal(StageState.Failed, state.Stage("flaky")!.State);
    }

    [Fact]
    public async Task RetryPolicyNoneMeansASingleAttempt()
    {
        using var repo = new TempRepository();

        var alwaysFails = new FlakyExecutor(failuresBeforeSuccess: int.MaxValue);

        var pipeline = new StagePipeline(
            [new("flaky", "Flaky", [], false)],
            new Dictionary<string, IStageExecutor> { ["flaky"] = alwaysFails });

        var engine = new Orchestrator(
            pipeline, new RunStore(repo.Root), repo.Root, RetryPolicy.None);

        await engine.StartAsync("anything");

        Assert.Equal(1, alwaysFails.Calls);
    }

    [Fact]
    public async Task RetriesAreRecordedInTheAuditLog()
    {
        using var repo = new TempRepository();

        var pipeline = new StagePipeline(
            [new("flaky", "Flaky", [], false)],
            new Dictionary<string, IStageExecutor>
            {
                ["flaky"] = new FlakyExecutor(failuresBeforeSuccess: 1)
            });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root, new RetryPolicy(MaxAttempts: 3));

        var state = await engine.StartAsync("anything");

        Assert.Contains(store.ReadAudit(state.RunId), entry => entry.Event == "StageRetrying");
    }

    // ---------- rollback ----------

    [Fact]
    public async Task WorkFromTheSameStepIsRolledBackWhenAStageFails()
    {
        using var repo = new TempRepository();

        var pipeline = new StagePipeline(
            [
                new("root", "Root", [], false),
                new("good", "Good", ["root"], false),
                new("bad", "Bad", ["root"], false)
            ],
            new Dictionary<string, IStageExecutor>
            {
                ["root"] = new SimpleExecutor(),
                ["good"] = new SimpleExecutor(),
                ["bad"] = new SimpleExecutor(_ => StageResult.Failure("nope"))
            });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root, RetryPolicy.None);

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Equal(StageState.Failed, state.Stage("bad")!.State);

        // 'good' succeeded in the same step, so its work is undone.
        Assert.Equal(StageState.RolledBack, state.Stage("good")!.State);
        Assert.Null(state.Stage("good")!.ArtifactFile);
        Assert.DoesNotContain("good", store.ReadArtifacts(state.RunId).Keys);

        // The stage that ran in an earlier, successful step is untouched.
        Assert.Equal(StageState.Succeeded, state.Stage("root")!.State);
        Assert.Contains("root", store.ReadArtifacts(state.RunId).Keys);
    }

    [Fact]
    public async Task RollbackIsAudited()
    {
        using var repo = new TempRepository();

        var pipeline = new StagePipeline(
            [
                new("good", "Good", [], false),
                new("bad", "Bad", [], false)
            ],
            new Dictionary<string, IStageExecutor>
            {
                ["good"] = new SimpleExecutor(),
                ["bad"] = new SimpleExecutor(_ => StageResult.Failure("nope"))
            });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root, RetryPolicy.None);

        var state = await engine.StartAsync("anything");

        Assert.Contains(
            store.ReadAudit(state.RunId),
            entry => entry.Event == "StageRolledBack" && entry.Stage == "good");
    }

    // ---------- safe stop ----------

    [Fact]
    public async Task StopHaltsBetweenStagesAndLeavesNothingHalfDone()
    {
        using var repo = new TempRepository();

        var second = new SimpleExecutor();

        var pipeline = new StagePipeline(
            [
                new("first", "First", [], RequiresApproval: true),
                new("second", "Second", ["first"], false)
            ],
            new Dictionary<string, IStageExecutor>
            {
                ["first"] = new SimpleExecutor(),
                ["second"] = second
            });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var started = await engine.StartAsync("anything");
        var stopped = engine.Stop(started.RunId, Actor);

        Assert.Equal(RunStatus.Stopped, stopped.Status);
        Assert.Equal(0, second.Calls);

        // No stage is left mid-flight.
        Assert.DoesNotContain(stopped.Stages, stage => stage.State == StageState.Running);
    }

    [Fact]
    public async Task AStopRequestPreventsFurtherStagesUntilResumed()
    {
        using var repo = new TempRepository();

        var second = new SimpleExecutor();

        var pipeline = new StagePipeline(
            [
                new("first", "First", [], RequiresApproval: true),
                new("second", "Second", ["first"], false)
            ],
            new Dictionary<string, IStageExecutor>
            {
                ["first"] = new SimpleExecutor(),
                ["second"] = second
            });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var started = await engine.StartAsync("anything");

        engine.Stop(started.RunId, Actor);

        // Approving while stopped must not quietly restart the pipeline.
        var afterApproval = await engine.ApproveAsync(started.RunId, "first", Actor);

        Assert.Equal(RunStatus.Stopped, afterApproval.Status);
        Assert.Equal(0, second.Calls);

        var resumed = await engine.ResumeAsync(started.RunId);

        Assert.Equal(RunStatus.Succeeded, resumed.Status);
        Assert.Equal(1, second.Calls);
    }

    // ---------- re-planning ----------

    [Fact]
    public async Task EditingAnUpstreamArtifactInvalidatesOnlyWhatDependedOnIt()
    {
        using var repo = new TempRepository();

        var upstream = new SimpleExecutor();
        var middle = new SimpleExecutor();
        var downstream = new SimpleExecutor();

        var pipeline = new StagePipeline(
            [
                new("upstream", "Upstream", [], false),
                new("middle", "Middle", ["upstream"], false),
                new("downstream", "Downstream", ["middle"], false)
            ],
            new Dictionary<string, IStageExecutor>
            {
                ["upstream"] = upstream,
                ["middle"] = middle,
                ["downstream"] = downstream
            });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.Succeeded, state.Status);

        // A human edits the approved upstream output.
        var artifactPath = Path.Combine(
            store.RunDirectory(state.RunId), "artifacts", "upstream.json");

        File.WriteAllText(artifactPath, JsonSerializer.Serialize(new Payload("edited")));

        var replanned = engine.Replan(state.RunId).State;

        // The edited stage keeps its output; everything downstream is invalidated.
        Assert.Equal(StageState.Succeeded, replanned.Stage("upstream")!.State);
        Assert.Equal(StageState.Pending, replanned.Stage("middle")!.State);
        Assert.Equal(StageState.Pending, replanned.Stage("downstream")!.State);

        var resumed = await engine.ResumeAsync(state.RunId);

        Assert.Equal(RunStatus.Succeeded, resumed.Status);
        Assert.Equal(1, upstream.Calls);
        Assert.Equal(2, middle.Calls);
        Assert.Equal(2, downstream.Calls);
    }

    [Fact]
    public async Task ReplanningIsANoOpWhenNothingChanged()
    {
        using var repo = new TempRepository();

        var only = new SimpleExecutor();

        var pipeline = new StagePipeline(
            [new("only", "Only", [], false)],
            new Dictionary<string, IStageExecutor> { ["only"] = only });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var state = await engine.StartAsync("anything");
        var replanned = engine.Replan(state.RunId).State;

        Assert.Equal(StageState.Succeeded, replanned.Stage("only")!.State);

        await engine.ResumeAsync(state.RunId);

        Assert.Equal(1, only.Calls);
    }

    [Fact]
    public async Task InvalidationIsAudited()
    {
        using var repo = new TempRepository();

        var pipeline = new StagePipeline(
            [
                new("upstream", "Upstream", [], false),
                new("downstream", "Downstream", ["upstream"], false)
            ],
            new Dictionary<string, IStageExecutor>
            {
                ["upstream"] = new SimpleExecutor(),
                ["downstream"] = new SimpleExecutor()
            });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var state = await engine.StartAsync("anything");

        File.WriteAllText(
            Path.Combine(store.RunDirectory(state.RunId), "artifacts", "upstream.json"),
            JsonSerializer.Serialize(new Payload("edited")));

        engine.Replan(state.RunId);

        Assert.Contains(
            store.ReadAudit(state.RunId),
            entry => entry.Event == "StageInvalidated" && entry.Stage == "downstream");
    }

    // ---------- metrics ----------

    [Fact]
    public async Task MetricsCountRetriesApprovalsAndSuccessRate()
    {
        using var repo = new TempRepository();

        var pipeline = new StagePipeline(
            [
                new("flaky", "Flaky", [], false),
                new("gated", "Gated", ["flaky"], RequiresApproval: true)
            ],
            new Dictionary<string, IStageExecutor>
            {
                ["flaky"] = new FlakyExecutor(failuresBeforeSuccess: 1),
                ["gated"] = new SimpleExecutor()
            });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root, new RetryPolicy(MaxAttempts: 3));

        var started = await engine.StartAsync("anything");
        var state = await engine.ApproveAsync(started.RunId, "gated", Actor);

        var metrics = RunMetrics.Calculate(state, store.ReadAudit(state.RunId));

        Assert.Equal(RunStatus.Succeeded, metrics.Status);
        Assert.Equal(2, metrics.StageCount);
        Assert.Equal(2, metrics.Succeeded);
        Assert.Equal(1.0, metrics.StageSuccessRate);
        Assert.Equal(1, metrics.RetryCount);
        Assert.Equal(1, metrics.ApprovalCount);
        Assert.Equal(0, metrics.RejectionCount);
        Assert.NotNull(metrics.MeanTimeToRecovery);
    }

    [Fact]
    public async Task MeanRecoveryIsNullWhenNothingEverFailed()
    {
        using var repo = new TempRepository();

        var pipeline = new StagePipeline(
            [new("only", "Only", [], false)],
            new Dictionary<string, IStageExecutor> { ["only"] = new SimpleExecutor() });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var state = await engine.StartAsync("anything");
        var metrics = RunMetrics.Calculate(state, store.ReadAudit(state.RunId));

        Assert.Null(metrics.MeanTimeToRecovery);
        Assert.Equal(0, metrics.RetryCount);
    }

    [Fact]
    public async Task MetricsReportRendersTheHeadlineFigures()
    {
        using var repo = new TempRepository();

        var pipeline = new StagePipeline(
            [new("only", "Only", [], false)],
            new Dictionary<string, IStageExecutor> { ["only"] = new SimpleExecutor() });

        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var state = await engine.StartAsync("anything");
        var text = RunMetricsRenderer.Render(
            RunMetrics.Calculate(state, store.ReadAudit(state.RunId)));

        Assert.Contains("RELIABILITY METRICS", text);
        Assert.Contains("Stage success rate", text);
        Assert.Contains("End-to-end latency", text);
        Assert.Contains("no stage failed then recovered", text);
    }
}
