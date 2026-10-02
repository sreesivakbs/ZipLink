using ZipLink.Agentic.Policy;

namespace ZipLink.Tests;

/// <summary>
/// The rule that catches an agent deleting tests. Written after an agent did exactly
/// that: it rewrote a test file, added cases for the new feature and silently dropped the
/// parameterised cases covering private and internal addresses. The suite stayed green.
/// </summary>
public class TestCoverageGuardTests
{
    [Fact]
    public void AddingTestsIsFine()
    {
        var diff = """
            --- a/tests/Foo.cs
            +++ b/tests/Foo.cs
            +    [Fact]
            +    public void NewThing() { }
            """;

        Assert.Null(TestCoverageGuard.Inspect(diff));
    }

    [Fact]
    public void RewritingTestsWithoutLosingCasesIsFine()
    {
        var diff = """
            --- a/tests/Foo.cs
            +++ b/tests/Foo.cs
            -    [Fact]
            -    public void OldName() { }
            +    [Fact]
            +    public void BetterName() { }
            """;

        Assert.Null(TestCoverageGuard.Inspect(diff));
    }

    [Fact]
    public void ANetLossOfTestCasesIsAViolation()
    {
        var diff = """
            --- a/tests/Foo.cs
            +++ b/tests/Foo.cs
            -    [InlineData("http://169.254.169.254/latest/meta-data")]
            -    [InlineData("http://10.1.2.3")]
            -    [InlineData("http://192.168.1.1")]
            +    [Fact]
            +    public void SomethingElse() { }
            """;

        var finding = TestCoverageGuard.Inspect(diff);

        Assert.NotNull(finding);
        Assert.Equal(TestCoverageGuard.Rule, finding!.Rule);
        Assert.Equal(PolicySeverity.Violation, finding.Severity);
        Assert.Contains("net loss of 2", finding.Detail);
    }

    [Fact]
    public void TheRealRegressionThatPromptedThisRuleIsCaught()
    {
        // Shape of the diff an agent actually produced: twelve new methods for the
        // feature it was asked for, and the private-address theory deleted.
        var removed = string.Join(
            "\n",
            Enumerable.Range(0, 15).Select(i => $"-    [InlineData(\"http://10.0.0.{i}\")]"));

        var added = string.Join(
            "\n",
            Enumerable.Range(0, 12).Select(i => $"+    [Fact] public void New{i}() {{ }}"));

        var finding = TestCoverageGuard.Inspect($"--- a/tests/X.cs\n+++ b/tests/X.cs\n{removed}\n{added}");

        Assert.NotNull(finding);
        Assert.Contains("removes 15 test case(s) and adds 12", finding!.Detail);
    }

    [Fact]
    public void FileHeadersAreNotCountedAsRemovedTests()
    {
        // "--- a/tests/[Fact]Something.cs" must not read as a deleted test.
        var diff = """
            --- a/tests/[Fact]Weird.cs
            +++ b/tests/[Fact]Weird.cs
            +    [Fact]
            +    public void Thing() { }
            """;

        Assert.Null(TestCoverageGuard.Inspect(diff));
    }

    [Fact]
    public void AnEmptyDiffIsNotAViolation()
    {
        Assert.Null(TestCoverageGuard.Inspect(string.Empty));
        Assert.Null(TestCoverageGuard.Inspect("   "));
    }

    [Theory]
    [InlineData("Theory")]
    [InlineData("MemberData")]
    [InlineData("ClassData")]
    public void AllTestCaseAttributesCount(string attribute)
    {
        var diff = $"--- a/tests/X.cs\n+++ b/tests/X.cs\n-    [{attribute}]\n-    [{attribute}]\n+    [{attribute}]";

        Assert.NotNull(TestCoverageGuard.Inspect(diff));
    }
}
