using System.Globalization;
using System.Text;

namespace ZipLink.Agentic.Orchestration;

public static class RunReportRenderer
{
    public static string Render(RunState state, StagePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pipeline);

        var builder = new StringBuilder();

        builder.AppendLine("ZipLink Orchestration Run");
        builder.AppendLine("=========================");
        builder.AppendLine();
        builder.AppendLine($"Run         : {state.RunId}");
        builder.AppendLine($"Requirement : {state.Requirement}");
        builder.AppendLine($"Status      : {state.Status}");
        builder.AppendLine(
            $"Started     : {state.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");

        if (!string.IsNullOrWhiteSpace(state.FailureReason))
        {
            builder.AppendLine($"Failure     : {state.FailureReason}");
        }

        builder.AppendLine();
        builder.AppendLine("STAGES");
        builder.AppendLine("------");

        foreach (var definition in pipeline.Stages)
        {
            var record = state.Stage(definition.Id);

            if (record is null)
            {
                continue;
            }

            var dependencies = definition.DependsOn.Count == 0
                ? "-"
                : string.Join("+", definition.DependsOn);

            var duration = record.Duration is { } elapsed
                ? $"{elapsed.TotalSeconds:0.00}s"
                : "-";

            builder.AppendLine(
                $"  {Marker(record.State)} {definition.Id,-13} {record.State,-17} "
                + $"after:{dependencies,-16} {duration,8}");

            if (!string.IsNullOrWhiteSpace(record.Summary))
            {
                builder.AppendLine($"       {record.Summary}");
            }

            if (!string.IsNullOrWhiteSpace(record.GateReason))
            {
                builder.AppendLine($"       GATE: {record.GateReason}");
            }

            if (!string.IsNullOrWhiteSpace(record.DecidedBy))
            {
                builder.AppendLine($"       decided by {record.DecidedBy}");
            }
        }

        builder.AppendLine();
        AppendNextAction(builder, state);

        return builder.ToString();
    }

    public static string RenderAudit(IReadOnlyList<AuditEvent> events)
    {
        var builder = new StringBuilder();

        builder.AppendLine("AUDIT LOG");
        builder.AppendLine("---------");

        foreach (var entry in events)
        {
            builder.AppendLine(
                $"  {entry.TimestampUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} "
                + $"{entry.Event,-22} {entry.Stage ?? "-",-13} {entry.Actor,-8} "
                + $"{entry.Detail ?? string.Empty}");
        }

        return builder.ToString();
    }

    private static void AppendNextAction(StringBuilder builder, RunState state)
    {
        switch (state.Status)
        {
            case RunStatus.AwaitingApproval:
                var waiting = state.Stages
                    .Where(stage => stage.State == StageState.AwaitingApproval)
                    .Select(stage => stage.Id)
                    .ToList();

                builder.AppendLine(
                    $"Waiting on a human decision for: {string.Join(", ", waiting)}");
                builder.AppendLine(
                    $"  approve:  run --project src/ZipLink.Agentic -- approve {state.RunId} "
                    + $"{waiting.FirstOrDefault()}");
                builder.AppendLine(
                    $"  reject :  run --project src/ZipLink.Agentic -- reject {state.RunId} "
                    + $"{waiting.FirstOrDefault()} \"reason\"");
                break;

            case RunStatus.Succeeded:
                builder.AppendLine("Run complete. Every stage succeeded.");
                break;

            case RunStatus.Failed:
                builder.AppendLine("Run failed. Nothing downstream of the failure was executed.");
                break;

            default:
                builder.AppendLine("Run is still in progress.");
                break;
        }
    }

    private static string Marker(StageState state)
    {
        return state switch
        {
            StageState.Succeeded => "[x]",
            StageState.Failed => "[!]",
            StageState.AwaitingApproval => "[?]",
            StageState.Running => "[>]",
            StageState.Skipped => "[-]",
            _ => "[ ]"
        };
    }
}
