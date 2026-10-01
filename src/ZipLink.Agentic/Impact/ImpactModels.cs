using ZipLink.Agentic.Text;

namespace ZipLink.Agentic.Impact;

public enum ConfidenceLevel
{
    Low,
    Medium,
    High
}

/// <summary>
/// One file the requirement is likely to touch, with the evidence that put it there.
/// Every item carries its own reasons so a reviewer can disagree with the ranking
/// without having to re-derive it.
/// </summary>
public sealed record ImpactItem
{
    public required string Path { get; init; }

    public string? Namespace { get; init; }

    public double Score { get; init; }

    public ConfidenceLevel Confidence { get; init; }

    /// <summary>
    /// Test files are usually impacted by a change rather than the place the change is
    /// made, so they are scored lower and flagged rather than hidden.
    /// </summary>
    public bool IsTestFile { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MatchedTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MatchedMethods { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The full impact analysis for one requirement. This is a read-only recommendation:
/// nothing in this pipeline edits application code.
/// </summary>
public sealed record ImpactReport
{
    public required string Requirement { get; init; }

    public required string RepositoryRoot { get; init; }

    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;

    public int FilesScanned { get; init; }

    public IReadOnlyList<RequirementTerm> Terms { get; init; } =
        Array.Empty<RequirementTerm>();

    /// <summary>
    /// Terms with no match anywhere in the repository. These are the most informative
    /// part of the report: they usually mean the requirement introduces a concept the
    /// codebase does not model yet, so no existing file can be credited for it.
    /// </summary>
    public IReadOnlyList<RequirementTerm> UnmatchedTerms { get; init; } =
        Array.Empty<RequirementTerm>();

    /// <summary>
    /// Terms so widespread that they cannot discriminate between files. Their score
    /// contribution is damped and they are reported for transparency.
    /// </summary>
    public IReadOnlyList<RequirementTerm> CommonTerms { get; init; } =
        Array.Empty<RequirementTerm>();

    public IReadOnlyList<ImpactItem> Items { get; init; } = Array.Empty<ImpactItem>();

    public ConfidenceLevel OverallConfidence { get; init; }

    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
}
