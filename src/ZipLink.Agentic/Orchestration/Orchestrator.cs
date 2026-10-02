using System.Security.Cryptography;
using System.Text;

namespace ZipLink.Agentic.Orchestration;

/// <summary>
/// How often a failing stage is re-attempted before the run gives up. Bounded by
/// construction: there is no "retry forever" setting.
/// </summary>
public sealed record RetryPolicy(int MaxAttempts = 3, TimeSpan Delay = default)
{
    public static readonly RetryPolicy Default = new();

    public static readonly RetryPolicy None = new(MaxAttempts: 1);
}

/// <summary>
/// Outcome of a re-plan: the run, plus exactly which stages were invalidated. The list
/// is empty when nothing changed, which callers must report honestly rather than
/// implying work was done.
/// </summary>
public sealed record ReplanResult(RunState State, IReadOnlyList<string> Invalidated);

/// <summary>
/// Walks the stage graph. On each tick it finds every stage whose dependencies are
/// satisfied and runs them together, so independent branches execute in parallel and a
/// stage with several dependencies naturally acts as a synchronization point.
///
/// Execution stops, rather than continuing on assumptions, whenever a stage lands in
/// <see cref="StageState.AwaitingApproval"/> — either because policy demands sign-off or
/// because the stage's own gate refused. Approval is a separate, human-initiated call.
/// </summary>
public sealed class Orchestrator
{
    public const string SystemActor = "system";

    // Unit separator: cannot occur in a requirement or in JSON, so hashed fields cannot
    // run together and collide.
    private const char FieldSeparator = (char)0x1F;

    private readonly StagePipeline _pipeline;
    private readonly RunStore _store;
    private readonly string _repositoryRoot;
    private readonly RetryPolicy _retryPolicy;

    public Orchestrator(
        StagePipeline pipeline,
        RunStore store,
        string repositoryRoot,
        RetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        _pipeline = pipeline;
        _store = store;
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _retryPolicy = retryPolicy ?? RetryPolicy.Default;
    }

    public async Task<RunState> StartAsync(
        string requirement,
        CancellationToken cancellationToken = default)
    {
        var state = CreateRun(requirement);

        return await ExecuteAsync(state, cancellationToken);
    }

    /// <summary>
    /// Creates and persists a run without executing it, so a caller can learn the run id
    /// immediately and watch progress rather than blocking for the minutes a run takes.
    /// Pair with <see cref="ExecuteAsync(string, CancellationToken)"/>.
    /// </summary>
    public RunState CreateRun(string requirement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);

        var state = new RunState
        {
            RunId = RunStore.NewRunId(),
            Requirement = requirement.Trim(),
            Stages = _pipeline.Stages
                .Select(definition => new StageRecord { Id = definition.Id })
                .ToList()
        };

        _store.Save(state);
        Audit(state, null, "RunStarted", SystemActor, state.Requirement);

        return state;
    }

    /// <summary>
    /// Drives an existing run forward. Unlike <see cref="ResumeAsync"/> this does not
    /// re-plan or record a resume, because the run has not been paused - it is simply
    /// being executed by a different caller than the one that created it.
    /// </summary>
    public async Task<RunState> ExecuteAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(Require(runId), cancellationToken);
    }

    public async Task<RunState> ResumeAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var state = Require(runId);

        if (state.Status is RunStatus.Succeeded or RunStatus.Failed)
        {
            return state;
        }

        // A resume is also the moment to notice that an upstream artifact changed while
        // the run was paused.
        _store.ClearStopRequest(runId);
        state = Replan(state);

        if (state.Status == RunStatus.Stopped)
        {
            state.Status = RunStatus.Running;
        }

        Audit(state, null, "RunResumed", SystemActor, null);

        return await ExecuteAsync(state, cancellationToken);
    }

    /// <summary>
    /// Safe-stop. The request is recorded immediately and the engine acts on it between
    /// stages, never mid-stage, so a stopped run never leaves a half-finished stage.
    /// </summary>
    public RunState Stop(string runId, string actor)
    {
        var state = Require(runId);

        _store.RequestStop(runId);

        if (state.Status is RunStatus.Running or RunStatus.AwaitingApproval)
        {
            state.Status = RunStatus.Stopped;

            _store.Save(state);
            Audit(state, null, "RunStopped", actor, null);
        }

        return state;
    }

    /// <summary>
    /// Compares each completed stage's recorded input fingerprint against its inputs as
    /// they are now. A stage whose inputs changed is invalidated along with everything
    /// downstream of it, so editing an approved output re-runs only what depended on it.
    /// The edited stage itself is untouched: its own inputs did not change.
    /// </summary>
    public ReplanResult Replan(string runId)
    {
        var state = Require(runId);
        var invalidated = ReplanCore(state);

        return new ReplanResult(state, invalidated);
    }

    private RunState Replan(RunState state)
    {
        ReplanCore(state);

        return state;
    }

    /// <summary>Returns the stages that were invalidated, in pipeline order.</summary>
    private IReadOnlyList<string> ReplanCore(RunState state)
    {
        var artifacts = _store.ReadArtifacts(state.RunId);
        var stale = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in _pipeline.Stages)
        {
            var record = state.Stage(definition.Id);

            if (record?.InputHash is null)
            {
                continue;
            }

            if (record.State is not (StageState.Succeeded or StageState.AwaitingApproval))
            {
                continue;
            }

            if (!string.Equals(
                record.InputHash,
                ComputeInputHash(definition, state.Requirement, artifacts),
                StringComparison.Ordinal))
            {
                stale.Add(definition.Id);
            }
        }

        if (stale.Count == 0)
        {
            return Array.Empty<string>();
        }

        ExpandDownstream(stale);

        var invalidated = new List<string>();

        foreach (var definition in _pipeline.Stages.Where(stage => stale.Contains(stage.Id)))
        {
            var record = state.Stage(definition.Id)!;

            if (record.State == StageState.Pending)
            {
                continue;
            }

            invalidated.Add(definition.Id);

            record.State = StageState.Pending;
            record.Attempts = 0;
            record.StartedAtUtc = null;
            record.CompletedAtUtc = null;
            record.Summary = null;
            record.GateReason = null;
            record.InputHash = null;
            record.DecidedBy = null;
            record.DecidedAtUtc = null;

            if (record.ArtifactFile is not null)
            {
                _store.DeleteArtifact(state.RunId, definition.Id);
                record.ArtifactFile = null;
            }

            Audit(state, definition.Id, "StageInvalidated", SystemActor, "inputs changed");
        }

        state.Status = RunStatus.Running;
        state.FailureReason = null;

        _store.Save(state);

        return invalidated;
    }

    private void ExpandDownstream(HashSet<string> stale)
    {
        bool grew;

        do
        {
            grew = false;

            foreach (var definition in _pipeline.Stages)
            {
                if (!stale.Contains(definition.Id)
                    && definition.DependsOn.Any(stale.Contains))
                {
                    stale.Add(definition.Id);
                    grew = true;
                }
            }
        }
        while (grew);
    }

    private static string ComputeInputHash(
        StageDefinition definition,
        string requirement,
        IReadOnlyDictionary<string, string> artifacts)
    {
        var builder = new StringBuilder()
            .Append(definition.Id)
            .Append(FieldSeparator)
            .Append(requirement);

        foreach (var dependency in definition.DependsOn.Order(StringComparer.Ordinal))
        {
            builder
                .Append(FieldSeparator)
                .Append(dependency)
                .Append('=')
                .Append(artifacts.TryGetValue(dependency, out var json) ? json : string.Empty);
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    public async Task<RunState> ApproveAsync(
        string runId,
        string stageId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var state = Require(runId);
        var record = RequireAwaitingStage(state, stageId);

        record.State = StageState.Succeeded;
        record.DecidedBy = actor;
        record.DecidedAtUtc = DateTime.UtcNow;
        record.GateReason = null;

        state.Status = RunStatus.Running;

        _store.Save(state);
        Audit(state, stageId, "StageApproved", actor, null);

        return await ExecuteAsync(state, cancellationToken);
    }

    public RunState Reject(string runId, string stageId, string actor, string? reason)
    {
        var state = Require(runId);
        var record = RequireAwaitingStage(state, stageId);

        record.State = StageState.Failed;
        record.DecidedBy = actor;
        record.DecidedAtUtc = DateTime.UtcNow;
        record.Summary = reason ?? "Rejected by reviewer.";

        state.Status = RunStatus.Failed;
        state.FailureReason = $"Stage '{stageId}' was rejected: {record.Summary}";

        _store.Save(state);
        Audit(state, stageId, "StageRejected", actor, reason);
        Audit(state, null, "RunFailed", SystemActor, state.FailureReason);

        return state;
    }

    private async Task<RunState> ExecuteAsync(RunState state, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (state.Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Stopped)
            {
                break;
            }

            // Safe-stop is honoured between stages only, so a stop never interrupts work
            // that is already in flight.
            if (_store.IsStopRequested(state.RunId))
            {
                state.Status = RunStatus.Stopped;

                _store.Save(state);
                Audit(state, null, "RunStopped", SystemActor, "stop requested");

                break;
            }

            var ready = _pipeline.Stages
                .Where(definition => state.Stage(definition.Id)?.State == StageState.Pending)
                .Where(definition => definition.DependsOn.All(IsSatisfied))
                .ToList();

            if (ready.Count == 0)
            {
                Settle(state);
                break;
            }

            foreach (var definition in ready)
            {
                state.Stage(definition.Id)!.State = StageState.Ready;
                Audit(state, definition.Id, "StageReady", SystemActor, null);
            }

            _store.Save(state);

            // Every ready stage runs together; a stage with multiple dependencies only
            // becomes ready once all of them have succeeded, which is the join.
            var artifacts = _store.ReadArtifacts(state.RunId);

            await Task.WhenAll(
                ready.Select(definition =>
                    RunStageAsync(state, definition, artifacts, cancellationToken)));

            _store.Save(state);

            if (state.Stages.Any(stage => stage.State == StageState.Failed))
            {
                RollBack(state, ready);

                state.Status = RunStatus.Failed;
                state.FailureReason ??= "A stage failed.";

                _store.Save(state);
                Audit(state, null, "RunFailed", SystemActor, state.FailureReason);

                break;
            }
        }

        return state;

        bool IsSatisfied(string dependencyId)
        {
            return state.Stage(dependencyId)?.State
                is StageState.Succeeded or StageState.Skipped;
        }
    }

    private async Task RunStageAsync(
        RunState state,
        StageDefinition definition,
        IReadOnlyDictionary<string, string> artifacts,
        CancellationToken cancellationToken)
    {
        var record = state.Stage(definition.Id)!;

        record.State = StageState.Running;
        record.StartedAtUtc = DateTime.UtcNow;

        Audit(state, definition.Id, "StageStarted", SystemActor, null);

        var context = new StageContext
        {
            RunId = state.RunId,
            Requirement = state.Requirement,
            RepositoryRoot = _repositoryRoot,
            Artifacts = artifacts
        };

        StageResult result;

        // Bounded by RetryPolicy.MaxAttempts: a stage can fail repeatedly but it cannot
        // spin forever.
        while (true)
        {
            record.Attempts++;

            try
            {
                result = await _pipeline.Executor(definition.Id)
                    .ExecuteAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = StageResult.Failure($"{ex.GetType().Name}: {ex.Message}");
            }

            if (result.Outcome != StageOutcome.Failed
                || record.Attempts >= _retryPolicy.MaxAttempts)
            {
                break;
            }

            record.State = StageState.Retrying;

            Audit(
                state,
                definition.Id,
                "StageRetrying",
                SystemActor,
                $"attempt {record.Attempts} of {_retryPolicy.MaxAttempts} failed: {result.Summary}");

            if (_retryPolicy.Delay > TimeSpan.Zero)
            {
                await Task.Delay(_retryPolicy.Delay, cancellationToken);
            }
        }

        record.CompletedAtUtc = DateTime.UtcNow;
        record.InputHash = ComputeInputHash(definition, state.Requirement, artifacts);
        record.Summary = result.Summary;

        if (result.Artifact is not null)
        {
            record.ArtifactFile = _store.WriteArtifact(state.RunId, definition.Id, result.Artifact);
        }

        switch (result.Outcome)
        {
            case StageOutcome.Failed:
                record.State = StageState.Failed;
                state.FailureReason ??= $"Stage '{definition.Id}' failed: {result.Summary}";
                Audit(state, definition.Id, "StageFailed", SystemActor, result.Summary);
                break;

            case StageOutcome.Blocked:
                record.State = StageState.AwaitingApproval;
                record.GateReason = result.GateReason;
                Audit(state, definition.Id, "StageBlocked", SystemActor, result.GateReason);
                break;

            default:
                if (definition.RequiresApproval)
                {
                    record.State = StageState.AwaitingApproval;
                    record.GateReason =
                        "Policy gate: this stage is high-impact and needs human approval.";
                    Audit(state, definition.Id, "StageAwaitingApproval", SystemActor, null);
                }
                else
                {
                    record.State = StageState.Succeeded;
                    Audit(state, definition.Id, "StageSucceeded", SystemActor, result.Summary);
                }

                break;
        }
    }

    /// <summary>
    /// Undoes the work of a tick in which something failed. Stages that succeeded
    /// alongside the failure are marked <see cref="StageState.RolledBack"/> and their
    /// artifacts deleted, so a failed run does not leave half a tick's output behind.
    ///
    /// The failed stage keeps its own artifact: that is diagnostic evidence, not a side
    /// effect to undo. Nothing writes application code yet, so rollback is confined to
    /// run artifacts — once an implementation stage exists this becomes a git reset to
    /// the previous stage's commit.
    /// </summary>
    private void RollBack(RunState state, IReadOnlyList<StageDefinition> tick)
    {
        foreach (var definition in tick)
        {
            var record = state.Stage(definition.Id)!;

            if (record.State is not (StageState.Succeeded or StageState.AwaitingApproval))
            {
                continue;
            }

            record.State = StageState.RolledBack;
            record.GateReason = null;

            if (record.ArtifactFile is not null)
            {
                _store.DeleteArtifact(state.RunId, definition.Id);
                record.ArtifactFile = null;
            }

            Audit(
                state,
                definition.Id,
                "StageRolledBack",
                SystemActor,
                "a stage in the same step failed");
        }
    }

    /// <summary>
    /// Nothing is runnable: decide whether that means finished, waiting on a human, or
    /// a graph that cannot make progress.
    /// </summary>
    private void Settle(RunState state)
    {
        if (state.Stages.All(stage =>
            stage.State is StageState.Succeeded or StageState.Skipped))
        {
            state.Status = RunStatus.Succeeded;
            _store.Save(state);
            Audit(state, null, "RunSucceeded", SystemActor, null);

            return;
        }

        if (state.Stages.Any(stage => stage.State == StageState.AwaitingApproval))
        {
            state.Status = RunStatus.AwaitingApproval;
            _store.Save(state);
            Audit(state, null, "RunAwaitingApproval", SystemActor, null);

            return;
        }

        state.Status = RunStatus.Failed;
        state.FailureReason ??= "No stage is runnable and the run is not complete.";

        _store.Save(state);
        Audit(state, null, "RunFailed", SystemActor, state.FailureReason);
    }

    private RunState Require(string runId)
    {
        return _store.Load(runId)
            ?? throw new InvalidOperationException($"Run '{runId}' was not found.");
    }

    private static StageRecord RequireAwaitingStage(RunState state, string stageId)
    {
        var record = state.Stage(stageId)
            ?? throw new ArgumentException(
                $"Run '{state.RunId}' has no stage '{stageId}'.", nameof(stageId));

        if (record.State != StageState.AwaitingApproval)
        {
            throw new InvalidOperationException(
                $"Stage '{stageId}' is {record.State}, not awaiting a decision.");
        }

        return record;
    }

    private void Audit(
        RunState state, string? stage, string name, string actor, string? detail)
    {
        _store.Append(new AuditEvent(DateTime.UtcNow, state.RunId, stage, name, actor, detail));
    }
}
