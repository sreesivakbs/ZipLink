using System.Text.Json;
using ZipLink.Agentic.Agents;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Tests;

public class BudgetAndDocsAgentTests
{
    private sealed class CountingModel : ILanguageModel
    {
        private readonly string _json;

        public CountingModel(string json)
        {
            _json = json;
        }

        public int Calls { get; private set; }

        public string? LastPrompt { get; private set; }

        public Task<LanguageModelResult> CompleteJsonAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyDictionary<string, JsonElement> jsonSchema,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastPrompt = userPrompt;

            return Task.FromResult(LanguageModelResult.Answer(_json));
        }
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> AnySchema =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    // ---------- budgets ----------

    [Fact]
    public async Task CallsUnderTheBudgetAreLetThrough()
    {
        var inner = new CountingModel("{}");
        var budgeted = new BudgetedLanguageModel(inner, new RunBudget(MaxModelCalls: 3));

        for (var i = 0; i < 3; i++)
        {
            await budgeted.CompleteJsonAsync("s", "u", AnySchema, CancellationToken.None);
        }

        Assert.Equal(3, inner.Calls);
        Assert.Equal(3, budgeted.Calls);
    }

    [Fact]
    public async Task ExceedingTheCallBudgetThrowsAndDoesNotReachTheModel()
    {
        var inner = new CountingModel("{}");
        var budgeted = new BudgetedLanguageModel(inner, new RunBudget(MaxModelCalls: 2));

        await budgeted.CompleteJsonAsync("s", "u", AnySchema, CancellationToken.None);
        await budgeted.CompleteJsonAsync("s", "u", AnySchema, CancellationToken.None);

        var error = await Assert.ThrowsAsync<BudgetExceededException>(
            () => budgeted.CompleteJsonAsync("s", "u", AnySchema, CancellationToken.None));

        Assert.Contains("budget of 2 model call", error.Message);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task AWallClockBudgetAlreadyExceededStopsTheRun()
    {
        var inner = new CountingModel("{}");

        var budgeted = new BudgetedLanguageModel(
            inner, new RunBudget(MaxModelCalls: 99, MaxWallClock: TimeSpan.FromTicks(1)));

        await Task.Delay(10);

        await Assert.ThrowsAsync<BudgetExceededException>(
            () => budgeted.CompleteJsonAsync("s", "u", AnySchema, CancellationToken.None));

        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task ABudgetBreachFailsTheStageRatherThanCrashingTheRun()
    {
        using var repo = new TempRepository();

        var budgeted = new BudgetedLanguageModel(
            new CountingModel("{}"), new RunBudget(MaxModelCalls: 0));

        var pipeline = new StagePipeline(
            [new("docs", "Docs", [], false)],
            new Dictionary<string, IStageExecutor>
            {
                ["docs"] = new DocsAgentExecutor(budgeted)
            });

        var engine = new Orchestrator(
            pipeline, new RunStore(repo.Root), repo.Root, RetryPolicy.None);

        var state = await engine.StartAsync("anything");

        // The engine's own exception handling turns it into a recorded failure.
        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Contains("BudgetExceededException", state.Stage("docs")!.Summary);
    }

    [Fact]
    public void TheDefaultBudgetIsBoundedNotUnlimited()
    {
        Assert.True(RunBudget.Default.MaxModelCalls > 0);
        Assert.True(RunBudget.Default.EffectiveWallClock > TimeSpan.Zero);
    }

    // ---------- docs agent ----------

    private const string DocsJson = """
        {
          "changeSummary": "Short links now refuse private and internal addresses.",
          "releaseNotes": ["Shortening a link to an internal IP address is now rejected."],
          "documentationGaps": ["docs/SETUP.md does not mention the new rejection reason."]
        }
        """;

    private static StageContext Context(IReadOnlyDictionary<string, string>? artifacts = null)
    {
        return new StageContext
        {
            RunId = "test-run",
            Requirement = "Block private addresses",
            RepositoryRoot = Path.GetTempPath(),
            Artifacts = artifacts ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    [Fact]
    public async Task TheDocsAgentReturnsATypedArtifact()
    {
        var result = await new DocsAgentExecutor(new CountingModel(DocsJson))
            .ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(StageOutcome.Succeeded, result.Outcome);

        var docs = Assert.IsType<DocsArtifact>(result.Artifact);

        Assert.Single(docs.ReleaseNotes);
        Assert.Single(docs.DocumentationGaps);
        Assert.Equal("agent", docs.Source);
    }

    [Fact]
    public async Task MalformedDocsJsonIsAFailure()
    {
        var result = await new DocsAgentExecutor(new CountingModel("{ broken"))
            .ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(StageOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task TheDocsAgentIsGivenWhatActuallyChanged()
    {
        var implement = JsonSerializer.Serialize(new ImplementArtifact(
            "/tmp/ws",
            ["src/ZipLink.Core/Validation/PrivateAddressGuard.cs"],
            "3 files changed, 269 insertions(+)",
            1,
            "notes"));

        var model = new CountingModel(DocsJson);

        await new DocsAgentExecutor(model).ExecuteAsync(
            Context(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StagePipeline.Implement] = implement
            }),
            CancellationToken.None);

        Assert.Contains("269 insertions", model.LastPrompt);
        Assert.Contains("PrivateAddressGuard.cs", model.LastPrompt);
    }

    [Fact]
    public async Task WithNoImplementationTheAgentIsToldToDescribeTheProposal()
    {
        var model = new CountingModel(DocsJson);

        await new DocsAgentExecutor(model).ExecuteAsync(Context(), CancellationToken.None);

        Assert.Contains("rather than one that was made", model.LastPrompt);
    }

    [Fact]
    public void TheDefaultPipelineUsesTheDocsAgentWhenSupplied()
    {
        var pipeline = StagePipeline.CreateDefault(
            new NeverCalledTestRunner(),
            docsAgent: new DocsAgentExecutor(new CountingModel(DocsJson)));

        Assert.IsType<DocsAgentExecutor>(pipeline.Executor(StagePipeline.Docs));
    }

    private sealed class NeverCalledTestRunner : ITestRunner
    {
        public Task<TestRunResult> RunAsync(string repositoryRoot, CancellationToken token)
        {
            throw new InvalidOperationException("The test stage must not run here.");
        }
    }
}
