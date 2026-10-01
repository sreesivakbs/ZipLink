namespace ZipLink.Agentic.Requirements;

public enum RiskLevel
{
    Low,
    Medium,
    High
}

public enum AmbiguityCategory
{
    /// <summary>A quality word with no agreed definition: "secure", "fast", "better".</summary>
    VagueQualityAttribute,

    /// <summary>A comparative with no stated baseline: "more", "faster", "improve".</summary>
    UnquantifiedComparative,

    /// <summary>A time-dependent concept with no duration: "expiration", "timeout".</summary>
    UndefinedTimeframe,

    /// <summary>Open-ended scope: "etc", "appropriate", "edge cases".</summary>
    VagueScope,

    /// <summary>Several independent asks joined into one sentence.</summary>
    BundledRequests,

    /// <summary>No concrete domain noun to act on at all.</summary>
    MissingSubject
}

/// <summary>
/// One detected ambiguity, carrying the exact word that triggered it so a reviewer can
/// see why the requirement was flagged rather than having to trust a score.
/// </summary>
public sealed record Ambiguity
{
    public required AmbiguityCategory Category { get; init; }

    public required string Trigger { get; init; }

    public required string Explanation { get; init; }

    /// <summary>
    /// Contribution to the risk score. Security wording is weighted highest because
    /// guessing at an unstated security requirement is the costliest thing to get wrong.
    /// </summary>
    public int Weight { get; init; }
}

/// <summary>
/// The normalized, reviewable form of a raw engineering request. This is produced by
/// deterministic rules only: no language model is involved, so the output is stable
/// and reproducible for the same input.
/// </summary>
public sealed record RequirementAnalysis
{
    public required string OriginalText { get; init; }

    public required string NormalizedRequirement { get; init; }

    public IReadOnlyList<Ambiguity> Ambiguities { get; init; } = Array.Empty<Ambiguity>();

    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ClarificationQuestions { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = Array.Empty<string>();

    public int AmbiguityScore { get; init; }

    public RiskLevel RiskLevel { get; init; }

    public bool RequiresHumanClarification { get; init; }

    /// <summary>
    /// Why the gate is open or closed, stated in one line for the reader.
    /// </summary>
    public required string ClarificationRationale { get; init; }

    /// <summary>
    /// True when the acceptance criteria cannot be finalised because the requirement
    /// still contains unresolved ambiguity.
    /// </summary>
    public bool AcceptanceCriteriaProvisional => RequiresHumanClarification;
}
