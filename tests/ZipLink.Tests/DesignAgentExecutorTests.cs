using System.Text.Json;
using ZipLink.Agentic.Agents;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Tests;

/// <summary>
/// The design agent, exercised entirely against a fake model. Nothing here touches the
/// network: unit and CI tests must never call a real language model.
/// </summary>
public class DesignAgentExecutorTests
{
    private const string ValidJson = """
        {
          "summary": "Add an ExpiresAtUtc property and reject expired links on resolve.",
          "steps": ["Add ExpiresAtUtc to ShortUrl", "Filter expired links in ResolveAsync"],
          "filesToChange": ["src/ZipLink.Core/Models/ShortUrl.cs"],
          "risks": ["Existing links have no expiry and must stay resolvable"],
          "openQuestions": ["What is the default lifetime?"]
        }
        """;

    private sealed class FakeModel : ILanguageModel
    {
        private readonly Queue<LanguageModelResult> _answers;

        public FakeModel(params LanguageModelResult[] answers)
        {
            _answers = new Queue<LanguageModelResult>(answers);
        }

        public int Calls { get; private set; }

        public string? LastPrompt { get; private set; }

        public IReadOnlyDictionary<string, JsonElement>? LastSchema { get; private set; }

        public Task<LanguageModelResult> CompleteJsonAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyDictionary<string, JsonElement> jsonSchema,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastPrompt = userPrompt;
            LastSchema = jsonSchema;

            return Task.FromResult(
                _answers.Count > 0 ? _answers.Dequeue() : LanguageModelResult.Answer(ValidJson));
        }
    }

    private static StageContext Context(
        IReadOnlyDictionary<string, string>? artifacts = null)
    {
        return new StageContext
        {
            RunId = "test-run",
            Requirement = "Add link expiration",
            RepositoryRoot = Path.GetTempPath(),
            Artifacts = artifacts ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    // ---------- the happy path ----------

    [Fact]
    public async Task AValidAnswerBecomesAStageSuccessWithATypedArtifact()
    {
        var executor = new DesignAgentExecutor(
            new FakeModel(LanguageModelResult.Answer(ValidJson)));

        var result = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(StageOutcome.Succeeded, result.Outcome);

        var design = Assert.IsType<DesignArtifact>(result.Artifact);

        Assert.Contains("ExpiresAtUtc", design.Summary);
        Assert.Equal(2, design.Steps.Count);
        Assert.Single(design.OpenQuestions);
        Assert.Equal("agent", design.Source);
    }

    [Fact]
    public async Task TheSummaryReportsWhatTheAgentProduced()
    {
        var executor = new DesignAgentExecutor(
            new FakeModel(LanguageModelResult.Answer(ValidJson)));

        var result = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Contains("2 step(s)", result.Summary);
        Assert.Contains("1 open question(s)", result.Summary);
    }

    // ---------- validation failures feed the retry policy ----------

    [Fact]
    public async Task MalformedJsonIsAFailureRatherThanASalvagedAnswer()
    {
        var executor = new DesignAgentExecutor(
            new FakeModel(LanguageModelResult.Answer("{ not json at all")));

        var result = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(StageOutcome.Failed, result.Outcome);
        Assert.Contains("invalid JSON", result.Summary);
    }

    [Fact]
    public async Task AnAnswerWithNoSummaryIsRejected()
    {
        var executor = new DesignAgentExecutor(
            new FakeModel(LanguageModelResult.Answer("""{"summary":"  ","steps":[]}""")));

        var result = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(StageOutcome.Failed, result.Outcome);
        Assert.Contains("nothing to review", result.Summary);
    }

    [Fact]
    public async Task AMalformedAnswerIsRetriedByTheEngineAndCanRecover()
    {
        using var repo = new TempRepository();

        var model = new FakeModel(
            LanguageModelResult.Answer("{ broken"),
            LanguageModelResult.Answer(ValidJson));

        var pipeline = new StagePipeline(
            [new("design", "Design", [], RequiresApproval: false)],
            new Dictionary<string, IStageExecutor>
            {
                ["design"] = new DesignAgentExecutor(model)
            });

        var engine = new Orchestrator(
            pipeline, new RunStore(repo.Root), repo.Root, new RetryPolicy(MaxAttempts: 3));

        var state = await engine.StartAsync("Add link expiration");

        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.Equal(2, model.Calls);
        Assert.Equal(2, state.Stage("design")!.Attempts);
    }

    // ---------- refusal is a gate, not a failure ----------

    [Fact]
    public async Task ARefusalBlocksForAHumanInsteadOfFailing()
    {
        var executor = new DesignAgentExecutor(
            new FakeModel(LanguageModelResult.Refusal("Declined for safety reasons.")));

        var result = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(StageOutcome.Blocked, result.Outcome);
        Assert.Contains("Declined for safety", result.GateReason);
    }

    [Fact]
    public async Task ARefusalIsNotRetried()
    {
        using var repo = new TempRepository();

        var model = new FakeModel(
            LanguageModelResult.Refusal("Declined."),
            LanguageModelResult.Answer(ValidJson));

        var pipeline = new StagePipeline(
            [new("design", "Design", [], RequiresApproval: false)],
            new Dictionary<string, IStageExecutor>
            {
                ["design"] = new DesignAgentExecutor(model)
            });

        var engine = new Orchestrator(
            pipeline, new RunStore(repo.Root), repo.Root, new RetryPolicy(MaxAttempts: 3));

        var state = await engine.StartAsync("Add link expiration");

        // Blocked is a governance outcome: retrying it would just annoy the classifier.
        Assert.Equal(RunStatus.AwaitingApproval, state.Status);
        Assert.Equal(1, model.Calls);
    }

    // ---------- the agent is given the upstream context ----------

    [Fact]
    public async Task UpstreamArtifactsArePassedToTheModel()
    {
        var requirements = JsonSerializer.Serialize(new RequirementsArtifact(
            "Add link expiration",
            "Medium",
            RequiresClarification: false,
            "No blocking ambiguity.",
            ["[UndefinedTimeframe] 'expiration'"],
            ["What duration applies?"],
            ["A default will be needed."],
            ["Covered by tests for both cases."]));

        var impact = JsonSerializer.Serialize(new ImpactArtifact(
            "Medium",
            32,
            ["High   src/ZipLink.Core/Models/ShortUrl.cs"],
            ["expiration"],
            ["Indirect coupling is invisible to this analysis."]));

        var model = new FakeModel(LanguageModelResult.Answer(ValidJson));

        var executor = new DesignAgentExecutor(model);

        await executor.ExecuteAsync(
            Context(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StagePipeline.Requirements] = requirements,
                [StagePipeline.Impact] = impact
            }),
            CancellationToken.None);

        Assert.NotNull(model.LastPrompt);
        Assert.Contains("Add link expiration", model.LastPrompt);
        Assert.Contains("What duration applies?", model.LastPrompt);
        Assert.Contains("ShortUrl.cs", model.LastPrompt);

        // The unmatched term is the most useful signal the agent receives.
        Assert.Contains("expiration", model.LastPrompt);
    }

    [Fact]
    public async Task TheRequestIsConstrainedByASchema()
    {
        var model = new FakeModel(LanguageModelResult.Answer(ValidJson));

        await new DesignAgentExecutor(model).ExecuteAsync(Context(), CancellationToken.None);

        Assert.NotNull(model.LastSchema);
        Assert.True(model.LastSchema!.ContainsKey("properties"));
        Assert.True(model.LastSchema.ContainsKey("required"));
        Assert.Equal("object", model.LastSchema["type"].GetString());
    }

    [Fact]
    public async Task TheAgentFallsBackToTheRawRequirementWhenUpstreamIsMissing()
    {
        var model = new FakeModel(LanguageModelResult.Answer(ValidJson));

        await new DesignAgentExecutor(model).ExecuteAsync(Context(), CancellationToken.None);

        Assert.Contains("Add link expiration", model.LastPrompt);
    }

    // ---------- wiring ----------

    [Fact]
    public void TheDefaultPipelineUsesTheStubWhenNoAgentIsSupplied()
    {
        var pipeline = StagePipeline.CreateDefault(new NeverCalledTestRunner());

        Assert.IsType<StubStageExecutor>(pipeline.Executor(StagePipeline.Design));
    }

    [Fact]
    public void TheDefaultPipelineUsesTheAgentWhenOneIsSupplied()
    {
        var pipeline = StagePipeline.CreateDefault(
            new NeverCalledTestRunner(),
            new DesignAgentExecutor(new FakeModel()));

        Assert.IsType<DesignAgentExecutor>(pipeline.Executor(StagePipeline.Design));
    }

    [Fact]
    public void TheDesignStageStillRequiresApprovalWhenAnAgentIsWired()
    {
        var pipeline = StagePipeline.CreateDefault(
            new NeverCalledTestRunner(),
            new DesignAgentExecutor(new FakeModel()));

        var design = pipeline.Stages.Single(stage => stage.Id == StagePipeline.Design);

        Assert.True(design.RequiresApproval);
    }

    private sealed class NeverCalledTestRunner : ITestRunner
    {
        public Task<TestRunResult> RunAsync(string repositoryRoot, CancellationToken token)
        {
            throw new InvalidOperationException("The test stage must not run here.");
        }
    }
}
