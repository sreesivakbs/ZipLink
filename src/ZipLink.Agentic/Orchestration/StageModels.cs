using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipLink.Agentic.Orchestration;

/// <summary>
/// Stage lifecycle from the project brief. <c>Retrying</c> and <c>RolledBack</c> are
/// declared here but not yet reachable: bounded retries and rollback are the next slice.
/// </summary>
public enum StageState
{
    Pending,
    Ready,
    Running,
    AwaitingApproval,
    Succeeded,
    Retrying,
    Failed,
    RolledBack,
    Skipped
}

public enum RunStatus
{
    Running,
    AwaitingApproval,
    Succeeded,
    Failed,
    Stopped
}

/// <summary>
/// One node in the pipeline graph. <see cref="DependsOn"/> is the edge set, which is what
/// makes execution a DAG rather than a list: a stage becomes runnable only once every
/// dependency has succeeded, and independent stages run together.
/// </summary>
public sealed record StageDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> DependsOn,
    bool RequiresApproval);

/// <summary>Mutable per-run record for one stage. Serialized into run.json.</summary>
public sealed class StageRecord
{
    public required string Id { get; set; }

    public StageState State { get; set; } = StageState.Pending;

    public int Attempts { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string? Summary { get; set; }

    /// <summary>Why this stage is waiting on a human.</summary>
    public string? GateReason { get; set; }

    public string? ArtifactFile { get; set; }

    /// <summary>
    /// Fingerprint of everything this stage consumed: the requirement plus the artifacts
    /// of its dependencies. Re-planning compares this against the current inputs to find
    /// work that is no longer valid.
    /// </summary>
    public string? InputHash { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    [JsonIgnore]
    public TimeSpan? Duration => StartedAtUtc is not null && CompletedAtUtc is not null
        ? CompletedAtUtc - StartedAtUtc
        : null;
}

/// <summary>
/// The complete state of one run. Persisted after every transition so a run can be
/// inspected, approved and resumed in separate processes.
/// </summary>
public sealed class RunState
{
    public required string RunId { get; set; }

    public required string Requirement { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public RunStatus Status { get; set; } = RunStatus.Running;

    public List<StageRecord> Stages { get; set; } = [];

    public string? FailureReason { get; set; }

    public StageRecord? Stage(string id)
    {
        return Stages.FirstOrDefault(stage => string.Equals(stage.Id, id, StringComparison.Ordinal));
    }
}

/// <summary>One immutable line in the append-only audit log.</summary>
public sealed record AuditEvent(
    DateTime TimestampUtc,
    string RunId,
    string? Stage,
    string Event,
    string Actor,
    string? Detail);

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
