using ZipLink.Agentic.Impact;
using ZipLink.Agentic.Requirements;

namespace ZipLink.Agentic.Orchestration;

/// <summary>
/// Stage outputs are deliberately small, flat records rather than the full analyzer
/// models: they are written to disk, read back by downstream stages, and meant to be
/// legible to a human reading the run folder.
/// </summary>
public sealed record RequirementsArtifact(
    string Normalized,
    string RiskLevel,
    bool RequiresClarification,
    string Rationale,
    IReadOnlyList<string> Ambiguities,
    IReadOnlyList<string> Questions,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> AcceptanceCriteria);

public sealed record ImpactArtifact(
    string Confidence,
    int FilesScanned,
    IReadOnlyList<string> TopFiles,
    IReadOnlyList<string> UnmatchedTerms,
    IReadOnlyList<string> Risks);

public sealed record StubArtifact(string Stage, string Status, string Note);

public sealed record TestsArtifact(bool Passed, string Summary);

/// <summary>
/// Runs requirement analysis and owns the clarification gate. A requirement judged too
/// ambiguous returns <see cref="StageOutcome.Blocked"/>, which stops the run here rather
/// than letting later stages invent an interpretation.
/// </summary>
public sealed class RequirementsStageExecutor : IStageExecutor
{
    private readonly RequirementAnalyzer _analyzer = new();

    public Task<StageResult> ExecuteAsync(
        StageContext context,
        CancellationToken cancellationToken)
    {
        var analysis = _analyzer.Analyze(context.Requirement);

        var artifact = new RequirementsArtifact(
            analysis.NormalizedRequirement,
            analysis.RiskLevel.ToString(),
            analysis.RequiresHumanClarification,
            analysis.ClarificationRationale,
            analysis.Ambiguities
                .Select(item => $"[{item.Category}] '{item.Trigger}': {item.Explanation}")
                .ToList(),
            analysis.ClarificationQuestions,
            analysis.Assumptions,
            analysis.AcceptanceCriteria);

        var summary =
            $"Risk {analysis.RiskLevel}, {analysis.Ambiguities.Count} ambiguity(ies) detected.";

        return Task.FromResult(
            analysis.RequiresHumanClarification
                ? StageResult.Blocked(summary, analysis.ClarificationRationale, artifact)
                : StageResult.Success(summary, artifact));
    }
}

/// <summary>
/// Ranks likely-impacted files. Reads the upstream requirements artifact rather than the
/// raw request, so the normalized wording flows through the pipeline.
/// </summary>
public sealed class ImpactStageExecutor : IStageExecutor
{
    private readonly ImpactAnalyzer _analyzer = new();

    public Task<StageResult> ExecuteAsync(
        StageContext context,
        CancellationToken cancellationToken)
    {
        var upstream = context.Read<RequirementsArtifact>("requirements");
        var requirement = upstream?.Normalized ?? context.Requirement;

        var report = _analyzer.Analyze(context.RepositoryRoot, requirement);

        var artifact = new ImpactArtifact(
            report.OverallConfidence.ToString(),
            report.FilesScanned,
            report.Items
                .Take(10)
                .Select(item => $"{item.Confidence,-6} {item.Path} (score {item.Score})")
                .ToList(),
            report.UnmatchedTerms.Select(term => term.Word).ToList(),
            report.Risks);

        return Task.FromResult(StageResult.Success(
            $"{report.Items.Count} file(s) implicated, confidence {report.OverallConfidence}.",
            artifact));
    }
}

/// <summary>
/// Placeholder for a stage whose real agent does not exist yet. The brief calls for the
/// engine to be proven with fake agents before real ones are plugged in, so these
/// deliberately do no work and say so in their artifact.
/// </summary>
public sealed class StubStageExecutor : IStageExecutor
{
    private readonly string _stageId;
    private readonly string _note;

    public StubStageExecutor(string stageId, string note)
    {
        _stageId = stageId;
        _note = note;
    }

    public Task<StageResult> ExecuteAsync(
        StageContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(StageResult.Success(
            $"STUB: {_note}",
            new StubArtifact(_stageId, "STUB", _note)));
    }
}

/// <summary>Delegates pass/fail to the real test suite.</summary>
public sealed class TestsStageExecutor : IStageExecutor
{
    private readonly ITestRunner _runner;

    public TestsStageExecutor(ITestRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
    }

    public async Task<StageResult> ExecuteAsync(
        StageContext context,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(context.RepositoryRoot, cancellationToken);

        var artifact = new TestsArtifact(result.Succeeded, result.Summary);

        return result.Succeeded
            ? StageResult.Success(result.Summary, artifact)
            : new StageResult(StageOutcome.Failed, result.Summary, artifact);
    }
}
