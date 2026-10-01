using System.Globalization;
using System.Text;

namespace ZipLink.Agentic.Orchestration;

public sealed record StageTiming(
    string Stage,
    StageState State,
    int Attempts,
    TimeSpan? Duration);

/// <summary>
/// Reliability figures for one run, derived entirely from the persisted state and the
/// audit log. Nothing is measured in-memory, so a report can be produced for a run that
/// finished in an earlier process.
/// </summary>
public sealed record RunMetrics(
    string RunId,
    RunStatus Status,
    int StageCount,
    int Succeeded,
    int Failed,
    int RolledBack,
    int Invalidated,
    double StageSuccessRate,
    int RetryCount,
    int ApprovalCount,
    int RejectionCount,
    TimeSpan EndToEndLatency,
    TimeSpan TotalStageTime,
    TimeSpan ApprovalWaitTime,
    TimeSpan? MeanTimeToRecovery,
    IReadOnlyList<StageTiming> Stages)
{
    public static RunMetrics Calculate(RunState state, IReadOnlyList<AuditEvent> audit)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(audit);

        var succeeded = state.Stages.Count(stage => stage.State == StageState.Succeeded);
        var last = audit.Count > 0 ? audit[^1].TimestampUtc : state.CreatedAtUtc;

        return new RunMetrics(
            state.RunId,
            state.Status,
            state.Stages.Count,
            succeeded,
            state.Stages.Count(stage => stage.State == StageState.Failed),
            state.Stages.Count(stage => stage.State == StageState.RolledBack),
            audit.Count(entry => entry.Event == "StageInvalidated"),
            state.Stages.Count == 0 ? 0 : (double)succeeded / state.Stages.Count,
            audit.Count(entry => entry.Event == "StageRetrying"),
            audit.Count(entry => entry.Event == "StageApproved"),
            audit.Count(entry => entry.Event == "StageRejected"),
            last - state.CreatedAtUtc,
            state.Stages.Aggregate(
                TimeSpan.Zero, (total, stage) => total + (stage.Duration ?? TimeSpan.Zero)),
            ApprovalWait(audit),
            MeanRecovery(audit),
            state.Stages
                .Select(stage => new StageTiming(
                    stage.Id, stage.State, stage.Attempts, stage.Duration))
                .ToList());
    }

    /// <summary>
    /// How long the run sat waiting on a person. Separating this from execution time is
    /// what makes end-to-end latency interpretable: a slow run is usually a slow human.
    /// </summary>
    private static TimeSpan ApprovalWait(IReadOnlyList<AuditEvent> audit)
    {
        var total = TimeSpan.Zero;
        var pending = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var entry in audit)
        {
            if (entry.Stage is null)
            {
                continue;
            }

            if (entry.Event is "StageAwaitingApproval" or "StageBlocked")
            {
                pending[entry.Stage] = entry.TimestampUtc;
            }
            else if (entry.Event is "StageApproved" or "StageRejected"
                && pending.Remove(entry.Stage, out var since))
            {
                total += entry.TimestampUtc - since;
            }
        }

        return total;
    }

    /// <summary>
    /// Mean time from a stage's first failed attempt to it eventually succeeding. Null
    /// when nothing ever failed and recovered, which is honest: there is no recovery
    /// time to report rather than a misleading zero.
    /// </summary>
    private static TimeSpan? MeanRecovery(IReadOnlyList<AuditEvent> audit)
    {
        var firstFailure = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var recoveries = new List<TimeSpan>();

        foreach (var entry in audit)
        {
            if (entry.Stage is null)
            {
                continue;
            }

            if (entry.Event == "StageRetrying")
            {
                firstFailure.TryAdd(entry.Stage, entry.TimestampUtc);
            }
            else if (entry.Event == "StageSucceeded"
                && firstFailure.Remove(entry.Stage, out var since))
            {
                recoveries.Add(entry.TimestampUtc - since);
            }
        }

        return recoveries.Count == 0
            ? null
            : TimeSpan.FromTicks((long)recoveries.Average(span => span.Ticks));
    }
}

public static class RunMetricsRenderer
{
    public static string Render(RunMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var builder = new StringBuilder();

        builder.AppendLine("RELIABILITY METRICS");
        builder.AppendLine("-------------------");
        builder.AppendLine($"  Run                : {metrics.RunId} ({metrics.Status})");
        builder.AppendLine(
            $"  Stage success rate : {metrics.StageSuccessRate:P0} "
            + $"({metrics.Succeeded}/{metrics.StageCount} succeeded)");
        builder.AppendLine($"  Failed stages      : {metrics.Failed}");
        builder.AppendLine($"  Rolled back        : {metrics.RolledBack}");
        builder.AppendLine($"  Retries            : {metrics.RetryCount}");
        builder.AppendLine($"  Invalidated        : {metrics.Invalidated} (re-planning)");
        builder.AppendLine(
            $"  Human decisions    : {metrics.ApprovalCount} approved, "
            + $"{metrics.RejectionCount} rejected");
        builder.AppendLine($"  End-to-end latency : {Format(metrics.EndToEndLatency)}");
        builder.AppendLine($"  Time in stages     : {Format(metrics.TotalStageTime)}");
        builder.AppendLine($"  Waiting on humans  : {Format(metrics.ApprovalWaitTime)}");
        builder.AppendLine(
            "  Mean recovery time : "
            + (metrics.MeanTimeToRecovery is { } mttr
                ? Format(mttr)
                : "n/a (no stage failed then recovered)"));
        builder.AppendLine();
        builder.AppendLine("  stage          state             attempts   duration");

        foreach (var stage in metrics.Stages)
        {
            builder.AppendLine(
                $"  {stage.Stage,-14} {stage.State,-17} {stage.Attempts,8}   "
                + (stage.Duration is { } duration ? Format(duration) : "-"));
        }

        return builder.ToString();
    }

    private static string Format(TimeSpan span)
    {
        return span.TotalSeconds < 60
            ? span.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s"
            : span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }
}
