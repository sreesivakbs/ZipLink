using System.Text.Json;
using ZipLink.Agentic.Agents;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Tests;

/// <summary>
/// Checking a design's file list against the rules the implementation agent is held to.
///
/// Written after a real run: the design agent asked a human to approve a plan naming
/// "docs/performance-baseline.md", a spikes/ file, and a garbled entry reading
/// "Medium spikes/ZipLink.Spike.AgentFramework/Program.cs". All three were silently
/// skipped at write time, so the approval was for a plan that could never be carried out
/// in full and nothing said so.
/// </summary>
public class DesignFileValidationTests
{
    // ---------- the path rules themselves ----------

    [Theory]
    [InlineData("src/ZipLink.Core/Models/ShortUrl.cs")]
    [InlineData("tests/ZipLink.Tests/NewTests.cs")]
    [InlineData("src/ZipLink.Api/appsettings.json")]
    public void OrdinaryProjectPathsAreWritable(string path)
    {
        Assert.Null(CodeWorkspace.DescribeWriteProblem(path));
    }

    [Theory]
    [InlineData("docs/performance-baseline.md")]
    [InlineData("spikes/ZipLink.Spike.AgentFramework/Program.cs")]
    [InlineData(".git/config")]
    [InlineData(".ziplink/runs/x/run.json")]
    public void ProtectedLocationsAreReported(string path)
    {
        Assert.Contains("protected location", CodeWorkspace.DescribeWriteProblem(path));
    }

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("PROJECT_BRIEF.md")]
    [InlineData(".gitignore")]
    public void GovernanceFilesAreReported(string path)
    {
        Assert.Contains("protected file", CodeWorkspace.DescribeWriteProblem(path));
    }

    [Fact]
    public void TraversalIsReported()
    {
        Assert.Contains("escapes the workspace", CodeWorkspace.DescribeWriteProblem("../outside.cs"));
    }

    [Fact]
    public void AnAbsolutePathIsReported()
    {
        Assert.Contains(
            "absolute path",
            CodeWorkspace.DescribeWriteProblem(@"C:\Windows\System32\drivers\etc\hosts"));
    }

    [Fact]
    public void TheGarbledEntryFromTheRealRunIsReported()
    {
        var problem = CodeWorkspace.DescribeWriteProblem(
            "Medium spikes/ZipLink.Spike.AgentFramework/Program.cs");

        Assert.Contains("garbled", problem);
    }

    [Fact]
    public void AnEmptyPathIsReported()
    {
        Assert.Contains("empty", CodeWorkspace.DescribeWriteProblem("  "));
    }

    // ---------- surfaced at the design gate ----------

    private sealed class CannedModel : ILanguageModel
    {
        private readonly string _json;

        public CannedModel(string json) => _json = json;

        public Task<LanguageModelResult> CompleteJsonAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyDictionary<string, JsonElement> jsonSchema,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(LanguageModelResult.Answer(_json));
        }
    }

    private static StageContext Context() => new()
    {
        RunId = "test-run",
        Requirement = "Make it handle more traffic",
        RepositoryRoot = Path.GetTempPath(),
        Artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
    };

    private static string DesignWith(params string[] files)
    {
        return JsonSerializer.Serialize(new
        {
            summary = "Add caching and a throughput benchmark.",
            steps = new[] { "one", "two" },
            filesToChange = files,
            risks = new[] { "a risk" },
            openQuestions = new[] { "a question" }
        });
    }

    [Fact]
    public async Task ACleanDesignReportsNothingUnwritable()
    {
        var executor = new DesignAgentExecutor(
            new CannedModel(DesignWith("src/ZipLink.Api/Program.cs")));

        var result = await executor.ExecuteAsync(Context(), CancellationToken.None);

        var design = Assert.IsType<DesignArtifact>(result.Artifact);

        Assert.Empty(design.UnwritableFiles);
        Assert.DoesNotContain("WARNING", result.Summary);
    }

    [Fact]
    public async Task TheDesignFromTheRealRunIsFlaggedBeforeApproval()
    {
        // Exactly the list a design agent produced and a human was asked to approve.
        var executor = new DesignAgentExecutor(new CannedModel(DesignWith(
            "src/ZipLink.Api/Program.cs",
            "src/ZipLink.Api/Services/CachedLinkResolver.cs",
            "docs/performance-baseline.md",
            "Medium spikes/ZipLink.Spike.AgentFramework/Program.cs")));

        var result = await executor.ExecuteAsync(Context(), CancellationToken.None);

        var design = Assert.IsType<DesignArtifact>(result.Artifact);

        Assert.Equal(2, design.UnwritableFiles.Count);
        Assert.Contains(design.UnwritableFiles, f => f.StartsWith("docs/performance-baseline.md"));
        Assert.Contains(design.UnwritableFiles, f => f.StartsWith("Medium spikes/"));
    }

    [Fact]
    public async Task TheWarningReachesTheStageSummaryAHumanReads()
    {
        var executor = new DesignAgentExecutor(new CannedModel(DesignWith(
            "src/ZipLink.Api/Program.cs",
            "docs/notes.md",
            "CLAUDE.md")));

        var result = await executor.ExecuteAsync(Context(), CancellationToken.None);

        // The approval banner shows this text, so the warning has to live here and not
        // only inside the artifact.
        Assert.Contains("WARNING: 2 of the listed file(s) cannot be written", result.Summary);
    }

    [Fact]
    public async Task FlaggingDoesNotBlockTheDesign()
    {
        var executor = new DesignAgentExecutor(
            new CannedModel(DesignWith("docs/everything.md")));

        var result = await executor.ExecuteAsync(Context(), CancellationToken.None);

        // Still a proposal for a human to judge - the design stage warns, it does not veto.
        Assert.Equal(StageOutcome.Succeeded, result.Outcome);
    }
}
