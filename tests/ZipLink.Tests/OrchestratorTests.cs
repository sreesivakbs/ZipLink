using ZipLink.Agentic.Orchestration;

namespace ZipLink.Tests;

public class OrchestratorTests
{
    private const string Actor = "tester";

    // ---------- helpers ----------

    private sealed class RecordingExecutor : IStageExecutor
    {
        private readonly string _id;
        private readonly List<string> _log;
        private readonly Func<StageResult> _result;
        private readonly int _delayMs;

        public RecordingExecutor(
            string id,
            List<string> log,
            Func<StageResult>? result = null,
            int delayMs = 0)
        {
            _id = id;
            _log = log;
            _result = result ?? (() => StageResult.Success($"{id} ok"));
            _delayMs = delayMs;
        }

        public async Task<StageResult> ExecuteAsync(
            StageContext context, CancellationToken cancellationToken)
        {
            lock (_log)
            {
                _log.Add($"start:{_id}");
            }

            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, cancellationToken);
            }

            lock (_log)
            {
                _log.Add($"end:{_id}");
            }

            return _result();
        }
    }

    private sealed class FakeTestRunner : ITestRunner
    {
        private readonly bool _succeeds;

        public FakeTestRunner(bool succeeds = true)
        {
            _succeeds = succeeds;
        }

        public Task<TestRunResult> RunAsync(string repositoryRoot, CancellationToken token)
        {
            return Task.FromResult(new TestRunResult(
                _succeeds, _succeeds ? "Passed! - Failed: 0" : "Failed! - Failed: 1", string.Empty));
        }
    }

    private static (Orchestrator Engine, StagePipeline Pipeline, RunStore Store) Build(
        TempRepository repo,
        IReadOnlyList<StageDefinition> stages,
        IReadOnlyDictionary<string, IStageExecutor> executors)
    {
        var pipeline = new StagePipeline(stages, executors);
        var store = new RunStore(repo.Root);

        return (new Orchestrator(pipeline, store, repo.Root), pipeline, store);
    }

    // ---------- graph scheduling ----------

    [Fact]
    public async Task Run_ExecutesStagesInDependencyOrder()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages =
        [
            new("a", "A", [], false),
            new("b", "B", ["a"], false),
            new("c", "C", ["b"], false)
        ];

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["a"] = new RecordingExecutor("a", log),
            ["b"] = new RecordingExecutor("b", log),
            ["c"] = new RecordingExecutor("c", log)
        });

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.Equal(["start:a", "end:a", "start:b", "end:b", "start:c", "end:c"], log);
    }

    [Fact]
    public async Task Run_ExecutesIndependentStagesInParallelAndJoins()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages =
        [
            new("root", "Root", [], false),
            new("left", "Left", ["root"], false),
            new("right", "Right", ["root"], false),
            new("join", "Join", ["left", "right"], false)
        ];

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["root"] = new RecordingExecutor("root", log),
            ["left"] = new RecordingExecutor("left", log, delayMs: 60),
            ["right"] = new RecordingExecutor("right", log, delayMs: 10),
            ["join"] = new RecordingExecutor("join", log)
        });

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.Succeeded, state.Status);

        // Both branches start before either finishes: they really overlap.
        var firstEnd = log.FindIndex(entry => entry.StartsWith("end:left") || entry.StartsWith("end:right"));

        Assert.True(log.IndexOf("start:left") < firstEnd);
        Assert.True(log.IndexOf("start:right") < firstEnd);

        // The join waits for both.
        Assert.True(log.IndexOf("start:join") > log.IndexOf("end:left"));
        Assert.True(log.IndexOf("start:join") > log.IndexOf("end:right"));
    }

    // ---------- gates ----------

    [Fact]
    public async Task Run_HaltsAtAnApprovalGateWithoutRunningDownstream()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages =
        [
            new("gated", "Gated", [], RequiresApproval: true),
            new("after", "After", ["gated"], false)
        ];

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["gated"] = new RecordingExecutor("gated", log),
            ["after"] = new RecordingExecutor("after", log)
        });

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.AwaitingApproval, state.Status);
        Assert.Equal(StageState.AwaitingApproval, state.Stage("gated")!.State);
        Assert.Equal(StageState.Pending, state.Stage("after")!.State);
        Assert.DoesNotContain("start:after", log);
    }

    [Fact]
    public async Task Approve_ResumesTheRunAndCompletesIt()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages =
        [
            new("gated", "Gated", [], RequiresApproval: true),
            new("after", "After", ["gated"], false)
        ];

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["gated"] = new RecordingExecutor("gated", log),
            ["after"] = new RecordingExecutor("after", log)
        });

        var started = await engine.StartAsync("anything");
        var state = await engine.ApproveAsync(started.RunId, "gated", Actor);

        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.Equal(Actor, state.Stage("gated")!.DecidedBy);
        Assert.Contains("start:after", log);
    }

    [Fact]
    public async Task Reject_FailsTheRunAndLeavesDownstreamUnrun()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages =
        [
            new("gated", "Gated", [], RequiresApproval: true),
            new("after", "After", ["gated"], false)
        ];

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["gated"] = new RecordingExecutor("gated", log),
            ["after"] = new RecordingExecutor("after", log)
        });

        var started = await engine.StartAsync("anything");
        var state = engine.Reject(started.RunId, "gated", Actor, "not good enough");

        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Contains("not good enough", state.FailureReason);
        Assert.DoesNotContain("start:after", log);
    }

    [Fact]
    public async Task ApprovingAStageThatIsNotWaitingIsRejected()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages = [new("only", "Only", [], false)];

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["only"] = new RecordingExecutor("only", log)
        });

        var started = await engine.StartAsync("anything");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ApproveAsync(started.RunId, "only", Actor));
    }

    [Fact]
    public async Task ABlockedGateStopsTheRunJustLikeAPolicyGate()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages =
        [
            new("gate", "Gate", [], false),
            new("after", "After", ["gate"], false)
        ];

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["gate"] = new RecordingExecutor(
                "gate", log, () => StageResult.Blocked("too vague", "needs clarification")),
            ["after"] = new RecordingExecutor("after", log)
        });

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.AwaitingApproval, state.Status);
        Assert.Equal("needs clarification", state.Stage("gate")!.GateReason);
        Assert.DoesNotContain("start:after", log);
    }

    // ---------- failure ----------

    [Fact]
    public async Task AFailedStageFailsTheRunAndStopsDownstream()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages =
        [
            new("bad", "Bad", [], false),
            new("after", "After", ["bad"], false)
        ];

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["bad"] = new RecordingExecutor("bad", log, () => StageResult.Failure("boom")),
            ["after"] = new RecordingExecutor("after", log)
        });

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Equal(StageState.Failed, state.Stage("bad")!.State);
        Assert.DoesNotContain("start:after", log);
    }

    [Fact]
    public async Task AnExecutorThatThrowsIsRecordedAsAFailureNotACrash()
    {
        using var repo = new TempRepository();

        StageDefinition[] stages = [new("boom", "Boom", [], false)];

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["boom"] = new ThrowingExecutor()
        });

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Contains("InvalidOperationException", state.Stage("boom")!.Summary);
    }

    private sealed class ThrowingExecutor : IStageExecutor
    {
        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken token)
        {
            throw new InvalidOperationException("executor exploded");
        }
    }

    // ---------- persistence and audit ----------

    [Fact]
    public async Task RunStateSurvivesAReloadFromDisk()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages = [new("gated", "Gated", [], RequiresApproval: true)];

        var (engine, _, store) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["gated"] = new RecordingExecutor("gated", log)
        });

        var started = await engine.StartAsync("remember me");
        var reloaded = store.Load(started.RunId);

        Assert.NotNull(reloaded);
        Assert.Equal("remember me", reloaded!.Requirement);
        Assert.Equal(RunStatus.AwaitingApproval, reloaded.Status);
        Assert.Equal(StageState.AwaitingApproval, reloaded.Stage("gated")!.State);
    }

    [Fact]
    public async Task AuditLogRecordsTheRunAndOnlyGrows()
    {
        using var repo = new TempRepository();

        var log = new List<string>();

        StageDefinition[] stages = [new("gated", "Gated", [], RequiresApproval: true)];

        var (engine, _, store) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["gated"] = new RecordingExecutor("gated", log)
        });

        var started = await engine.StartAsync("anything");
        var afterStart = store.ReadAudit(started.RunId);

        Assert.Contains(afterStart, entry => entry.Event == "RunStarted");
        Assert.Contains(afterStart, entry => entry.Event == "StageStarted");
        Assert.Contains(afterStart, entry => entry.Event == "StageAwaitingApproval");

        await engine.ApproveAsync(started.RunId, "gated", Actor);

        var afterApproval = store.ReadAudit(started.RunId);

        Assert.True(afterApproval.Count > afterStart.Count);
        Assert.Equal(
            afterStart.Select(entry => entry.Event),
            afterApproval.Take(afterStart.Count).Select(entry => entry.Event));
        Assert.Contains(
            afterApproval, entry => entry.Event == "StageApproved" && entry.Actor == Actor);
    }

    [Fact]
    public async Task ArtifactsFlowFromOneStageToTheNext()
    {
        using var repo = new TempRepository();

        StageDefinition[] stages =
        [
            new("producer", "Producer", [], false),
            new("consumer", "Consumer", ["producer"], false)
        ];

        var seen = new List<string>();

        var (engine, _, _) = Build(repo, stages, new Dictionary<string, IStageExecutor>
        {
            ["producer"] = new ProducerExecutor(),
            ["consumer"] = new ConsumerExecutor(seen)
        });

        var state = await engine.StartAsync("anything");

        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.Equal(["handed-over"], seen);
    }

    private sealed record Payload(string Value);

    private sealed class ProducerExecutor : IStageExecutor
    {
        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken token)
        {
            return Task.FromResult(StageResult.Success("made it", new Payload("handed-over")));
        }
    }

    private sealed class ConsumerExecutor : IStageExecutor
    {
        private readonly List<string> _seen;

        public ConsumerExecutor(List<string> seen)
        {
            _seen = seen;
        }

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken token)
        {
            var payload = context.Read<Payload>("producer");

            if (payload is not null)
            {
                _seen.Add(payload.Value);
            }

            return Task.FromResult(StageResult.Success("read it"));
        }
    }

    // ---------- graph validation ----------

    [Fact]
    public void ACyclicGraphIsRejected()
    {
        var executors = new Dictionary<string, IStageExecutor>
        {
            ["a"] = new ProducerExecutor(),
            ["b"] = new ProducerExecutor()
        };

        StageDefinition[] cyclic =
        [
            new("a", "A", ["b"], false),
            new("b", "B", ["a"], false)
        ];

        var error = Assert.Throws<ArgumentException>(() => new StagePipeline(cyclic, executors));

        Assert.Contains("cycle", error.Message);
    }

    [Fact]
    public void AnUnknownDependencyIsRejected()
    {
        var executors = new Dictionary<string, IStageExecutor> { ["a"] = new ProducerExecutor() };

        StageDefinition[] stages = [new("a", "A", ["ghost"], false)];

        Assert.Throws<ArgumentException>(() => new StagePipeline(stages, executors));
    }

    [Fact]
    public void AStageWithNoExecutorIsRejected()
    {
        var executors = new Dictionary<string, IStageExecutor>();

        StageDefinition[] stages = [new("a", "A", [], false)];

        Assert.Throws<ArgumentException>(() => new StagePipeline(stages, executors));
    }

    // ---------- host resolution ----------

    [Fact]
    public void DotnetHostIsResolvableOnThisMachine()
    {
        // The tests stage shells out to dotnet, which is not on PATH everywhere.
        Assert.NotNull(DotnetTestRunner.ResolveDotnetPath());
    }

    // ---------- the real default pipeline ----------

    [Fact]
    public async Task DefaultPipeline_BlocksAnAmbiguousRequirementAtRequirements()
    {
        using var repo = new TempRepository().AddFile(
            "src/Core/UrlShorteningService.cs",
            "namespace Demo; public class UrlShorteningService { public void Shorten() { } }");

        var pipeline = StagePipeline.CreateDefault(new FakeTestRunner());
        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var state = await engine.StartAsync("Make the URL shortener more secure");

        Assert.Equal(RunStatus.AwaitingApproval, state.Status);
        Assert.Equal(StageState.AwaitingApproval, state.Stage(StagePipeline.Requirements)!.State);
        Assert.Equal(StageState.Pending, state.Stage(StagePipeline.Impact)!.State);
        Assert.Contains("secure", state.Stage(StagePipeline.Requirements)!.GateReason);
    }

    [Fact]
    public async Task DefaultPipeline_ClearRequirementReachesTheDesignApprovalGate()
    {
        using var repo = new TempRepository().AddFile(
            "src/Core/UrlShorteningService.cs",
            "namespace Demo; public class UrlShorteningService { public void Shorten() { } }");

        var pipeline = StagePipeline.CreateDefault(new FakeTestRunner());
        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var state = await engine.StartAsync("Add a ClickCount property to the ShortUrl model");

        Assert.Equal(RunStatus.AwaitingApproval, state.Status);
        Assert.Equal(StageState.Succeeded, state.Stage(StagePipeline.Requirements)!.State);
        Assert.Equal(StageState.Succeeded, state.Stage(StagePipeline.Impact)!.State);
        Assert.Equal(StageState.AwaitingApproval, state.Stage(StagePipeline.Design)!.State);
    }

    [Fact]
    public async Task DefaultPipeline_FailsTheRunWhenTheTestSuiteFails()
    {
        using var repo = new TempRepository().AddFile(
            "src/Core/UrlShorteningService.cs",
            "namespace Demo; public class UrlShorteningService { public void Shorten() { } }");

        var pipeline = StagePipeline.CreateDefault(new FakeTestRunner(succeeds: false));
        var store = new RunStore(repo.Root);
        var engine = new Orchestrator(pipeline, store, repo.Root);

        var started = await engine.StartAsync("Add a ClickCount property to the ShortUrl model");
        var state = await engine.ApproveAsync(started.RunId, StagePipeline.Design, Actor);

        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Equal(StageState.Failed, state.Stage(StagePipeline.Tests)!.State);
        Assert.Equal(StageState.Pending, state.Stage(StagePipeline.Release)!.State);
    }
}
