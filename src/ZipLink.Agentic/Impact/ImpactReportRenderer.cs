using System.Text;

namespace ZipLink.Agentic.Impact;

/// <summary>
/// Renders an <see cref="ImpactReport"/> as plain text. Kept separate from the entry
/// point so the output can be asserted in tests.
/// </summary>
public static class ImpactReportRenderer
{
    private const int MaxReasonsPerItem = 6;

    public static string Render(ImpactReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();

        WriteHeader(builder, report);
        WriteTerms(builder, report);
        WriteItems(builder, report);
        WriteSection(builder, "ASSUMPTIONS", report.Assumptions);
        WriteSection(builder, "RISKS", report.Risks);
        WriteSection(builder, "LIMITATIONS", report.Limitations);
        WriteFooter(builder);

        return builder.ToString();
    }

    private static void WriteHeader(StringBuilder builder, ImpactReport report)
    {
        builder.AppendLine("ZipLink Impact Analysis");
        builder.AppendLine("=======================");
        builder.AppendLine();
        builder.AppendLine($"Requirement : {report.Requirement}");
        builder.AppendLine($"Repository  : {report.RepositoryRoot}");
        builder.AppendLine($"Files seen  : {report.FilesScanned}");
        builder.AppendLine(
            $"Generated   : {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        builder.AppendLine($"Confidence  : {report.OverallConfidence} (overall)");
        builder.AppendLine();
    }

    private static void WriteTerms(StringBuilder builder, ImpactReport report)
    {
        builder.AppendLine("REQUIREMENT TERMS");
        builder.AppendLine("-----------------");

        if (report.Terms.Count == 0)
        {
            builder.AppendLine(
                "  (none - the requirement contained no distinctive words)");
            builder.AppendLine();
            return;
        }

        builder.AppendLine(
            $"  Searched : {string.Join(", ", report.Terms.Select(term => term.Word))}");

        if (report.UnmatchedTerms.Count > 0)
        {
            builder.AppendLine(
                "  Unmatched: "
                + string.Join(", ", report.UnmatchedTerms.Select(term => term.Word))
                + "  <-- not present anywhere in the codebase");
        }

        if (report.CommonTerms.Count > 0)
        {
            builder.AppendLine(
                "  Common   : "
                + string.Join(", ", report.CommonTerms.Select(term => term.Word))
                + "  <-- too widespread to discriminate, down-weighted");
        }

        builder.AppendLine();
    }

    private static void WriteItems(StringBuilder builder, ImpactReport report)
    {
        builder.AppendLine("LIKELY IMPACTED FILES");
        builder.AppendLine("---------------------");

        if (report.Items.Count == 0)
        {
            builder.AppendLine("  (none identified)");
            builder.AppendLine();
            return;
        }

        var rank = 1;

        foreach (var item in report.Items)
        {
            var kind = item.IsTestFile ? " [test]" : string.Empty;

            builder.AppendLine(
                $"{rank,2}. [{item.Confidence,-6}] {item.Path}{kind}  (score {item.Score})");

            if (!string.IsNullOrWhiteSpace(item.Namespace))
            {
                builder.AppendLine($"      namespace: {item.Namespace}");
            }

            if (item.MatchedTypes.Count > 0)
            {
                builder.AppendLine($"      types   : {string.Join(", ", item.MatchedTypes)}");
            }

            if (item.MatchedMethods.Count > 0)
            {
                builder.AppendLine($"      methods : {string.Join(", ", item.MatchedMethods)}");
            }

            builder.AppendLine("      why:");

            foreach (var reason in item.Reasons.Take(MaxReasonsPerItem))
            {
                builder.AppendLine($"        - {reason}");
            }

            if (item.Reasons.Count > MaxReasonsPerItem)
            {
                builder.AppendLine(
                    $"        - ... and {item.Reasons.Count - MaxReasonsPerItem} more");
            }

            builder.AppendLine();
            rank++;
        }
    }

    private static void WriteSection(
        StringBuilder builder,
        string title,
        IReadOnlyList<string> entries)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));

        if (entries.Count == 0)
        {
            builder.AppendLine("  (none)");
            builder.AppendLine();
            return;
        }

        foreach (var entry in entries)
        {
            builder.AppendLine($"  - {entry}");
        }

        builder.AppendLine();
    }

    private static void WriteFooter(StringBuilder builder)
    {
        builder.AppendLine(
            "This report is advisory. No application code was read for modification, "
            + "and nothing was changed.");
    }
}
