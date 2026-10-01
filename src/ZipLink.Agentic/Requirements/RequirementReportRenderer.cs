using System.Text;

namespace ZipLink.Agentic.Requirements;

/// <summary>
/// Renders a <see cref="RequirementAnalysis"/> as plain text, kept separate from the
/// entry point so the output can be asserted in tests.
/// </summary>
public static class RequirementReportRenderer
{
    public static string Render(RequirementAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var builder = new StringBuilder();

        builder.AppendLine("ZipLink Requirement Analysis");
        builder.AppendLine("============================");
        builder.AppendLine();
        builder.AppendLine($"Raw input  : {analysis.OriginalText}");
        builder.AppendLine($"Normalized : {analysis.NormalizedRequirement}");
        builder.AppendLine(
            $"Risk       : {analysis.RiskLevel} (ambiguity score {analysis.AmbiguityScore})");
        builder.AppendLine(
            $"Gate       : {(analysis.RequiresHumanClarification
                ? "CLARIFICATION REQUIRED"
                : "clear to proceed")}");
        builder.AppendLine($"Rationale  : {analysis.ClarificationRationale}");
        builder.AppendLine();

        WriteAmbiguities(builder, analysis);

        WriteSection(builder, "ASSUMPTIONS", analysis.Assumptions);
        WriteSection(builder, "CLARIFICATION QUESTIONS", analysis.ClarificationQuestions);

        WriteSection(
            builder,
            analysis.AcceptanceCriteriaProvisional
                ? "ACCEPTANCE CRITERIA (provisional - cannot be finalised until the "
                    + "questions above are answered)"
                : "ACCEPTANCE CRITERIA",
            analysis.AcceptanceCriteria);

        return builder.ToString();
    }

    private static void WriteAmbiguities(StringBuilder builder, RequirementAnalysis analysis)
    {
        builder.AppendLine("DETECTED AMBIGUITIES");
        builder.AppendLine("--------------------");

        if (analysis.Ambiguities.Count == 0)
        {
            builder.AppendLine("  (none)");
            builder.AppendLine();
            return;
        }

        foreach (var ambiguity in analysis.Ambiguities)
        {
            builder.AppendLine(
                $"  [{ambiguity.Category}] '{ambiguity.Trigger}' (weight {ambiguity.Weight})");
            builder.AppendLine($"      {ambiguity.Explanation}");
        }

        builder.AppendLine();
    }

    private static void WriteSection(
        StringBuilder builder,
        string title,
        IReadOnlyList<string> entries)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', Math.Min(title.Length, 60)));

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
}
