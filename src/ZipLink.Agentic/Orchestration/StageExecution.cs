using System.Text.Json;

namespace ZipLink.Agentic.Orchestration;

public enum StageOutcome
{
    Succeeded,

    Failed,

    /// <summary>
    /// The stage completed but a gate it owns refuses to let the run continue — for
    /// example a requirement too ambiguous to act on. Distinct from Failed: nothing
    /// went wrong, a human simply has to decide.
    /// </summary>
    Blocked
}

public sealed record StageResult(
    StageOutcome Outcome,
    string Summary,
    object? Artifact = null,
    string? GateReason = null)
{
    public static StageResult Success(string summary, object? artifact = null)
    {
        return new StageResult(StageOutcome.Succeeded, summary, artifact);
    }

    public static StageResult Failure(string summary)
    {
        return new StageResult(StageOutcome.Failed, summary);
    }

    public static StageResult Blocked(string summary, string gateReason, object? artifact = null)
    {
        return new StageResult(StageOutcome.Blocked, summary, artifact, gateReason);
    }
}

/// <summary>
/// What a stage is given when it runs. <see cref="Artifacts"/> carries the outputs of
/// every completed upstream stage, which is how cross-stage context and decision lineage
/// are preserved rather than re-derived.
/// </summary>
public sealed class StageContext
{
    public required string RunId { get; init; }

    public required string Requirement { get; init; }

    public required string RepositoryRoot { get; init; }

    /// <summary>Stage id to the raw JSON that stage produced.</summary>
    public required IReadOnlyDictionary<string, string> Artifacts { get; init; }

    public T? Read<T>(string stageId)
        where T : class
    {
        return Artifacts.TryGetValue(stageId, out var json)
            ? JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)
            : null;
    }
}

public interface IStageExecutor
{
    Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken);
}
