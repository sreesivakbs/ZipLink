using System.Text.RegularExpressions;

namespace ZipLink.Agentic.Policy;

/// <summary>
/// Catches an agent deleting tests.
///
/// This rule exists because it happened. An implementation agent asked to add custom
/// aliases and expiration rewrote the service's test file wholesale: it added twelve test
/// methods for the new behaviour and silently dropped the parameterised cases covering
/// private and internal addresses - fifteen assertions of security behaviour. The suite
/// still passed, because deleted tests do not fail, and the test stage reported green.
///
/// A passing suite therefore proves nothing on its own about whether coverage survived.
/// Counting test attributes across the diff does, deterministically and without asking
/// anyone's opinion.
/// </summary>
public static class TestCoverageGuard
{
    public const string Rule = "no-test-deletion";

    private static readonly Regex TestAttribute = new(
        @"\[\s*(?:Fact|Theory|InlineData|MemberData|ClassData)\b", RegexOptions.Compiled);

    /// <summary>
    /// Inspects a unified diff. Returns a violation when the change removes more test
    /// cases than it adds - a net reduction in coverage, whatever else it does.
    /// </summary>
    public static PolicyFinding? Inspect(string unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return null;
        }

        var removed = 0;
        var added = 0;

        foreach (var line in unifiedDiff.Split('\n'))
        {
            // Skip the +++/--- file headers, which are not content lines.
            if (line.StartsWith("+++", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('-') && TestAttribute.IsMatch(line))
            {
                removed++;
            }
            else if (line.StartsWith('+') && TestAttribute.IsMatch(line))
            {
                added++;
            }
        }

        if (removed <= added)
        {
            return null;
        }

        return new PolicyFinding(
            Rule,
            PolicySeverity.Violation,
            "tests",
            0,
            $"The change removes {removed} test case(s) and adds {added}, a net loss of "
            + $"{removed - added}. Tests may be rewritten, but coverage may not shrink - "
            + "a suite that passes because its tests were deleted proves nothing.");
    }
}
