using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Reflection;
using ZipLink.Agentic.Impact;
using ZipLink.Agentic.Requirements;

// Spike: the same requirements -> impact -> design sub-graph that ZipLink.Agentic
// orchestrates itself, expressed instead on Microsoft Agent Framework Workflows.
//
// The stage logic is deliberately identical - it calls the very same RequirementAnalyzer
// and ImpactAnalyzer - so that any difference observed is a difference in orchestration,
// not in analysis. No model is called anywhere.

var repoRoot = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

var requirement = args.Length > 0
    ? string.Join(' ', args).Trim()
    : "Add custom short names and link expiration";

Console.WriteLine("ZipLink spike - Microsoft Agent Framework Workflows");
Console.WriteLine("==================================================");
Console.WriteLine($"Requirement : {requirement}");
Console.WriteLine($"Repository  : {repoRoot}");
Console.WriteLine();

// --- the graph -------------------------------------------------------------

var requirements = new RequirementsExecutor();
var impact = new ImpactExecutor(repoRoot);
var clarified = new ClarificationExecutor();
var design = new DesignExecutor();
var tests = new TestsExecutor();
var docs = new DocsExecutor();
var release = new ReleaseExecutor();

// Human interrupts are first-class: a RequestPort suspends the run and surfaces an
// ExternalRequest to the host, which answers with an ExternalResponse.
var clarificationPort = RequestPort.Create<RequirementSummary, string>("clarification");
var approvalPort = RequestPort.Create<ImpactSummary, bool>("design-approval");

var workflow = new WorkflowBuilder(requirements)
    // Conditional edges express the dynamic clarification gate: an ambiguous
    // requirement is routed to a human instead of onward to analysis.
    .AddEdge<RequirementSummary>(
        requirements, clarificationPort, s => s is { NeedsClarification: true })
    .AddEdge<RequirementSummary>(
        requirements, impact, s => s is { NeedsClarification: false })
    .AddEdge(clarificationPort, clarified)
    // Policy gate: design always needs sign-off.
    .AddEdge(impact, approvalPort)
    .AddEdge(approvalPort, design)
    // Parallel fan-out, then a barrier that waits for both branches.
    .AddFanOutEdge(design, [tests, docs])
    .AddFanInBarrierEdge([tests, docs], release)
    .Build();

// --- run it ----------------------------------------------------------------

var run = await InProcessExecution.RunStreamingAsync(workflow, requirement);

await foreach (var workflowEvent in run.WatchStreamAsync())
{
    switch (workflowEvent)
    {
        case ExecutorInvokedEvent invoked:
            Console.WriteLine($"  -> {invoked.ExecutorId} started");
            break;

        case ExecutorCompletedEvent completed:
            Console.WriteLine($"  <- {completed.ExecutorId} completed");
            break;

        case ExecutorFailedEvent failed:
            Console.WriteLine($"  !! {failed.ExecutorId} FAILED: {failed.Data}");
            break;

        case RequestInfoEvent request:
            await AnswerAsync(run, request);
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine();
            Console.WriteLine($"OUTPUT: {output.Data}");
            break;
    }
}

Console.WriteLine();
Console.WriteLine("Run finished.");

return 0;

// The host owns the human decision. This spike auto-answers so it can run unattended;
// a real host would prompt here. Either way the workflow is genuinely suspended until
// a response is sent, which is the behaviour being compared.
async Task AnswerAsync(StreamingRun activeRun, RequestInfoEvent request)
{
    Console.WriteLine();
    Console.WriteLine($"  [GATE] {request.Request.PortInfo.PortId} is waiting on a human.");

    if (request.Request.TryGetDataAs<ImpactSummary>(out var impactSummary)
        && impactSummary is not null)
    {
        Console.WriteLine(
            $"         impact: {impactSummary.FileCount} file(s), "
            + $"confidence {impactSummary.Confidence}");

        foreach (var file in impactSummary.TopFiles.Take(3))
        {
            Console.WriteLine($"           {file}");
        }

        Console.WriteLine("         auto-approving (spike runs unattended)");
        await activeRun.SendResponseAsync(request.Request.CreateResponse(true));

        return;
    }

    if (request.Request.TryGetDataAs<RequirementSummary>(out var summary)
        && summary is not null)
    {
        Console.WriteLine($"         risk {summary.Risk}: {summary.Rationale}");

        foreach (var question in summary.Questions)
        {
            Console.WriteLine($"           Q: {question}");
        }

        Console.WriteLine("         answering with a placeholder clarification");
        await activeRun.SendResponseAsync(
            request.Request.CreateResponse("Target 5000 requests per second."));

        return;
    }

    await activeRun.SendResponseAsync(request.Request.CreateResponse(true));
}

// --- messages --------------------------------------------------------------

internal sealed record RequirementSummary(
    string Normalized,
    string Risk,
    bool NeedsClarification,
    string Rationale,
    IReadOnlyList<string> Questions);

internal sealed record ImpactSummary(
    string Confidence,
    int FileCount,
    IReadOnlyList<string> TopFiles);

internal sealed record StageNote(string Stage, string Text);

// --- executors -------------------------------------------------------------
//
// FINDING: ReflectingExecutor<T> / IMessageHandler<,> are marked [Obsolete] at 1.23.0,
// directing callers to "[MessageHandler] attribute on methods in a partial class
// deriving from Executor". That pattern needs a source generator to implement the
// abstract Executor.ConfigureProtocol - and no analyzer ships in
// Microsoft.Agents.AI.Workflows, .AI, or .Abstractions at this version, so the
// replacement does not compile. Deriving from Executor directly fails with CS0534.
//
// The obsolete API is therefore the only working option here. Suppressed narrowly and
// deliberately rather than project-wide, so the day it is removed this stops compiling
// and someone has to look at it.
#pragma warning disable CS0618 // Type or member is obsolete

internal sealed class RequirementsExecutor()
    : ReflectingExecutor<RequirementsExecutor>("requirements"),
      IMessageHandler<string, RequirementSummary>
{
    public ValueTask<RequirementSummary> HandleAsync(
        string requirement, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var analysis = new RequirementAnalyzer().Analyze(requirement);

        return ValueTask.FromResult(new RequirementSummary(
            analysis.NormalizedRequirement,
            analysis.RiskLevel.ToString(),
            analysis.RequiresHumanClarification,
            analysis.ClarificationRationale,
            analysis.ClarificationQuestions));
    }
}

internal sealed class ImpactExecutor
    : ReflectingExecutor<ImpactExecutor>, IMessageHandler<RequirementSummary, ImpactSummary>
{
    private readonly string _repositoryRoot;

    public ImpactExecutor(string repositoryRoot)
        : base("impact")
    {
        _repositoryRoot = repositoryRoot;
    }

    public ValueTask<ImpactSummary> HandleAsync(
        RequirementSummary summary,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var report = new ImpactAnalyzer().Analyze(_repositoryRoot, summary.Normalized);

        return ValueTask.FromResult(new ImpactSummary(
            report.OverallConfidence.ToString(),
            report.Items.Count,
            report.Items
                .Take(5)
                .Select(item => $"{item.Confidence,-6} {item.Path}")
                .ToList()));
    }
}

internal sealed class ClarificationExecutor()
    : ReflectingExecutor<ClarificationExecutor>("clarified"), IMessageHandler<string>
{
    public async ValueTask HandleAsync(
        string clarification, IWorkflowContext context, CancellationToken cancellationToken)
    {
        // FINDING: calling context.YieldOutputAsync here fails at runtime with
        // "Cannot output object of type String. Expecting one of []". An executor must
        // declare its output types first - via ExecutorOptions
        // (AutoYieldOutputHandlerResultObject) or protocol configuration - and a
        // void-returning handler declares none. Reporting directly keeps the spike on
        // the orchestration comparison rather than on output plumbing.
        Console.WriteLine();
        Console.WriteLine(
            $"RESULT: blocked for clarification; human answered: \"{clarification}\". "
            + "A real run would re-plan from here.");

        await ValueTask.CompletedTask;
    }
}

internal sealed class DesignExecutor()
    : ReflectingExecutor<DesignExecutor>("design"), IMessageHandler<bool, StageNote>
{
    public ValueTask<StageNote> HandleAsync(
        bool approved, IWorkflowContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new StageNote(
            "design",
            approved
                ? "STUB: design approved by a human; no design agent in this spike."
                : "Design rejected."));
    }
}

internal sealed class TestsExecutor()
    : ReflectingExecutor<TestsExecutor>("tests"), IMessageHandler<StageNote, StageNote>
{
    public ValueTask<StageNote> HandleAsync(
        StageNote incoming, IWorkflowContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(
            new StageNote("tests", "STUB: no test agent in this spike."));
    }
}

internal sealed class DocsExecutor()
    : ReflectingExecutor<DocsExecutor>("docs"), IMessageHandler<StageNote, StageNote>
{
    public ValueTask<StageNote> HandleAsync(
        StageNote incoming, IWorkflowContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(
            new StageNote("docs", "STUB: no docs agent in this spike."));
    }
}

// FINDING: AddFanInBarrierEdge gates *timing* - it holds messages until every source has
// produced one - but then streams them to the target individually. So this executor runs
// once per branch, not once with both results. Aggregating is the caller's job (the
// framework offers AggregatingExecutor<,> for it). ZipLink.Agentic's join differs: a
// stage with several dependencies runs exactly once, after all of them succeed.
//
// The counter below is instance state, which is adequate for a single-process spike but
// would not survive a checkpoint restore - another thing the caller must think about.
internal sealed class ReleaseExecutor()
    : ReflectingExecutor<ReleaseExecutor>("release"), IMessageHandler<StageNote>
{
    private const int ExpectedBranches = 2;

    private readonly List<string> _arrived = [];

    public async ValueTask HandleAsync(
        StageNote note, IWorkflowContext context, CancellationToken cancellationToken)
    {
        _arrived.Add(note.Stage);

        Console.WriteLine(
            $"     release received '{note.Stage}' ({_arrived.Count}/{ExpectedBranches})");

        if (_arrived.Count < ExpectedBranches)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"RESULT: release readiness reached after joining "
            + $"[{string.Join(" + ", _arrived)}].");

        await ValueTask.CompletedTask;
    }
}

#pragma warning restore CS0618
