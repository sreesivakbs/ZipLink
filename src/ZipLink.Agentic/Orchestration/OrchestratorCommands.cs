using ZipLink.Agentic.Agents;

namespace ZipLink.Agentic.Orchestration;

/// <summary>
/// CLI verbs for the orchestrator. Each verb loads state from disk, acts, and saves, so
/// starting a run, approving a gate and resuming are independent invocations.
/// </summary>
public sealed class OrchestratorCommands
{
    public const int ExitOk = 0;
    public const int ExitInvalidInput = 1;
    public const int ExitAwaitingApproval = 2;

    private readonly string _repositoryRoot;
    private readonly RunStore _store;
    private readonly StagePipeline _pipeline;
    private readonly Orchestrator _orchestrator;
    private readonly TextWriter _out;

    public OrchestratorCommands(
        string repositoryRoot,
        ITestRunner? testRunner = null,
        TextWriter? output = null,
        IStageExecutor? designAgent = null,
        IStageExecutor? implementAgent = null,
        IStageExecutor? docsAgent = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _store = new RunStore(_repositoryRoot);
        _out = output ?? Console.Out;

        // Real agents are wired only when a credential exists; otherwise the stubs keep
        // the pipeline runnable offline. Which mode is active is always announced,
        // because "the design stage ran" means different things in each.
        var configured = ClaudeLanguageModel.IsConfigured;

        if (configured && designAgent is null && implementAgent is null && docsAgent is null)
        {
            _out.WriteLine(
                $"Agents enabled ({ClaudeLanguageModel.DefaultModel}), budget "
                + $"{RunBudget.Default.MaxModelCalls} model calls / "
                + $"{RunBudget.Default.EffectiveWallClock.TotalMinutes:0} min per run. The "
                + "implementation agent writes to an isolated git worktree, never this "
                + "working tree, and every proposal still requires human approval.");
            _out.WriteLine();
        }

        // One budgeted model shared by every agent, so the cap is per run rather than
        // per agent and a new agent cannot quietly escape it.
        var model = configured
            ? new BudgetedLanguageModel(new ClaudeLanguageModel(), RunBudget.Default)
            : null;

        var design = designAgent
            ?? (model is null ? null : new DesignAgentExecutor(model));

        var implement = implementAgent
            ?? (model is null ? null : new ImplementAgentExecutor(model));

        var docs = docsAgent
            ?? (model is null ? null : new DocsAgentExecutor(model));

        _pipeline = StagePipeline.CreateDefault(
            testRunner ?? new DotnetTestRunner(), design, implement, docs);

        _orchestrator = new Orchestrator(_pipeline, _store, _repositoryRoot);
    }

    public async Task<int> RunAsync(string requirement, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requirement))
        {
            _out.WriteLine("error: a requirement is required, e.g. run \"Add link expiry\".");

            return ExitInvalidInput;
        }

        var state = await _orchestrator.StartAsync(requirement, cancellationToken);

        return Report(state);
    }

    public async Task<int> ResumeAsync(string? runId, CancellationToken cancellationToken = default)
    {
        if (!TryResolve(runId, out var resolved))
        {
            return ExitInvalidInput;
        }

        return Report(await _orchestrator.ResumeAsync(resolved, cancellationToken));
    }

    public async Task<int> ApproveAsync(
        string? runId,
        string stageId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolve(runId, out var resolved))
        {
            return ExitInvalidInput;
        }

        try
        {
            return Report(
                await _orchestrator.ApproveAsync(resolved, stageId, actor, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _out.WriteLine($"error: {ex.Message}");

            return ExitInvalidInput;
        }
    }

    public int Reject(string? runId, string stageId, string actor, string? reason)
    {
        if (!TryResolve(runId, out var resolved))
        {
            return ExitInvalidInput;
        }

        try
        {
            return Report(_orchestrator.Reject(resolved, stageId, actor, reason));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _out.WriteLine($"error: {ex.Message}");

            return ExitInvalidInput;
        }
    }

    public int Status(string? runId, bool includeAudit)
    {
        if (!TryResolve(runId, out var resolved))
        {
            return ExitInvalidInput;
        }

        var state = _store.Load(resolved);

        if (state is null)
        {
            _out.WriteLine($"error: run '{resolved}' was not found.");

            return ExitInvalidInput;
        }

        _out.WriteLine(RunReportRenderer.Render(state, _pipeline));

        if (includeAudit)
        {
            _out.WriteLine(RunReportRenderer.RenderAudit(_store.ReadAudit(resolved)));
        }

        return state.Status == RunStatus.AwaitingApproval ? ExitAwaitingApproval : ExitOk;
    }

    public int Stop(string? runId, string actor)
    {
        if (!TryResolve(runId, out var resolved))
        {
            return ExitInvalidInput;
        }

        var state = _orchestrator.Stop(resolved, actor);

        _out.WriteLine($"Run {resolved} stopped. Status is now {state.Status}.");
        _out.WriteLine("No stage was interrupted; resume with: resume " + resolved);

        return ExitOk;
    }

    public int Report(string? runId)
    {
        if (!TryResolve(runId, out var resolved))
        {
            return ExitInvalidInput;
        }

        var state = _store.Load(resolved);

        if (state is null)
        {
            _out.WriteLine($"error: run '{resolved}' was not found.");

            return ExitInvalidInput;
        }

        var audit = _store.ReadAudit(resolved);

        _out.WriteLine(RunReportRenderer.Render(state, _pipeline));
        _out.WriteLine(RunMetricsRenderer.Render(RunMetrics.Calculate(state, audit)));
        _out.WriteLine(RunReportRenderer.RenderAudit(audit));

        return state.Status == RunStatus.AwaitingApproval ? ExitAwaitingApproval : ExitOk;
    }

    public int Replan(string? runId)
    {
        if (!TryResolve(runId, out var resolved))
        {
            return ExitInvalidInput;
        }

        var result = _orchestrator.Replan(resolved);

        _out.WriteLine(RunReportRenderer.Render(result.State, _pipeline));

        if (result.Invalidated.Count == 0)
        {
            _out.WriteLine(
                "No stage inputs changed, so nothing was invalidated. "
                + "(Runs created before input fingerprinting have no hash to compare.)");
        }
        else
        {
            _out.WriteLine(
                $"Invalidated {result.Invalidated.Count} stage(s): "
                + string.Join(", ", result.Invalidated));
            _out.WriteLine("Run 'resume' to re-execute them.");
        }

        return ExitOk;
    }

    public int List()
    {
        var runs = _store.ListRunIds();

        if (runs.Count == 0)
        {
            _out.WriteLine("No runs yet. Start one with: run \"<requirement>\"");

            return ExitOk;
        }

        _out.WriteLine("RUNS");
        _out.WriteLine("----");

        foreach (var runId in runs)
        {
            var state = _store.Load(runId);

            _out.WriteLine($"  {runId}  {state?.Status,-18} {state?.Requirement}");
        }

        return ExitOk;
    }

    private int Report(RunState state)
    {
        _out.WriteLine(RunReportRenderer.Render(state, _pipeline));

        return state.Status switch
        {
            RunStatus.AwaitingApproval => ExitAwaitingApproval,
            RunStatus.Failed => ExitInvalidInput,
            _ => ExitOk
        };
    }

    private bool TryResolve(string? runId, out string resolved)
    {
        if (!string.IsNullOrWhiteSpace(runId)
            && !string.Equals(runId, "latest", StringComparison.OrdinalIgnoreCase))
        {
            resolved = runId;

            return true;
        }

        var latest = _store.LatestRunId();

        if (latest is null)
        {
            _out.WriteLine("error: no runs exist yet.");
            resolved = string.Empty;

            return false;
        }

        resolved = latest;

        return true;
    }
}
