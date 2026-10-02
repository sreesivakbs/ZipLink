using System.Text;
using System.Text.Json;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Agentic.Agents;

/// <summary>
/// What the design agent must return. Deserializing into this record is the validation
/// step: anything that does not fit is a stage failure, not a "best effort" answer.
/// </summary>
public sealed record DesignArtifact
{
    public required string Summary { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FilesToChange { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> OpenQuestions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Files the design names that the implementation agent would be refused - protected
    /// paths, traversal, garbled entries. Surfaced here so a human sees them while
    /// deciding, rather than discovering afterwards that part of the plan was skipped.
    /// </summary>
    public IReadOnlyList<string> UnwritableFiles { get; init; } = Array.Empty<string>();

    /// <summary>Records that this came from a model, so no reader mistakes it for fact.</summary>
    public string Source { get; init; } = "agent";
}

/// <summary>
/// The first stage with real judgment in it: it proposes a design from the upstream
/// requirement and impact analysis.
///
/// Three properties are deliberate. It only ever *proposes* - the stage still sits behind
/// the design approval gate, so nothing it writes is acted on without a human. Its output
/// is schema-constrained and parsed, so a malformed answer is an ordinary failure that
/// the existing bounded-retry policy handles. And a refusal becomes a gate rather than a
/// failure, because a model declining to answer is something a person should read.
/// </summary>
public sealed class DesignAgentExecutor : IStageExecutor
{
    private const string SystemPrompt =
        "You are a senior software engineer proposing a design for a change to an "
        + "existing C# repository. You are given a normalized requirement and a "
        + "lexical impact analysis that ranks files by vocabulary overlap.\n\n"
        + "Treat the impact analysis as a hint, not as truth: it cannot see indirect "
        + "coupling, and terms it reports as unmatched usually mean the concept is not "
        + "modelled yet and new code is needed.\n\n"
        + "Propose the smallest change that satisfies the requirement. Do not write code. "
        + "State real risks rather than generic ones, and list any question whose answer "
        + "would change the design.";

    private readonly ILanguageModel _model;

    public DesignAgentExecutor(ILanguageModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
    }

    public async Task<StageResult> ExecuteAsync(
        StageContext context,
        CancellationToken cancellationToken)
    {
        var requirement = context.Read<RequirementsArtifact>(StagePipeline.Requirements);
        var impact = context.Read<ImpactArtifact>(StagePipeline.Impact);

        var result = await _model.CompleteJsonAsync(
            SystemPrompt,
            BuildPrompt(context.Requirement, requirement, impact),
            Schema,
            cancellationToken);

        if (result.Refused)
        {
            return StageResult.Blocked(
                "The design agent declined to answer.",
                result.RefusalReason ?? "No reason was given.");
        }

        DesignArtifact? design;

        try
        {
            design = JsonSerializer.Deserialize<DesignArtifact>(
                result.Json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            // Deliberately a failure, not a salvage attempt: the retry policy gets a
            // clean second try rather than the pipeline carrying a half-parsed design.
            return StageResult.Failure($"The design agent returned invalid JSON: {ex.Message}");
        }

        if (design is null || string.IsNullOrWhiteSpace(design.Summary))
        {
            return StageResult.Failure(
                "The design agent returned JSON with no summary, so there is nothing to review.");
        }

        // Check the plan against the rules the implementation agent will be held to, so
        // the approval banner says "3 of these 10 cannot be written" rather than leaving
        // a human to find out after approving.
        var unwritable = design.FilesToChange
            .Select(file => new { File = file, Problem = CodeWorkspace.DescribeWriteProblem(file) })
            .Where(entry => entry.Problem is not null)
            .Select(entry => $"{entry.File} - {entry.Problem}")
            .ToList();

        design = design with { UnwritableFiles = unwritable };

        var summary =
            $"{design.Steps.Count} step(s) proposed across {design.FilesToChange.Count} file(s); "
            + $"{design.OpenQuestions.Count} open question(s).";

        if (unwritable.Count > 0)
        {
            summary +=
                $" WARNING: {unwritable.Count} of the listed file(s) cannot be written and "
                + "would be skipped.";
        }

        return StageResult.Success(summary, design);
    }

    private static string BuildPrompt(
        string rawRequirement,
        RequirementsArtifact? requirement,
        ImpactArtifact? impact)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine("REQUIREMENT");
        prompt.AppendLine(requirement?.Normalized ?? rawRequirement);
        prompt.AppendLine();

        if (requirement is not null)
        {
            prompt.AppendLine($"Assessed risk: {requirement.RiskLevel}");

            AppendList(prompt, "Known assumptions", requirement.Assumptions);
            AppendList(prompt, "Questions already raised", requirement.Questions);
            AppendList(prompt, "Agreed acceptance criteria", requirement.AcceptanceCriteria);
        }

        if (impact is not null)
        {
            prompt.AppendLine(
                $"IMPACT ANALYSIS (confidence {impact.Confidence}, "
                + $"{impact.FilesScanned} files scanned)");

            AppendList(prompt, "Most likely impacted", impact.TopFiles);
            AppendList(
                prompt,
                "Terms matching nothing in the codebase (probably new concepts)",
                impact.UnmatchedTerms);
            AppendList(prompt, "Reported risks", impact.Risks);
        }

        return prompt.ToString();
    }

    private static void AppendList(
        StringBuilder prompt, string title, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        prompt.AppendLine();
        prompt.AppendLine($"{title}:");

        foreach (var value in values)
        {
            prompt.AppendLine($"- {value}");
        }

        prompt.AppendLine();
    }

    private static IReadOnlyDictionary<string, JsonElement> Schema { get; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                summary = new
                {
                    type = "string",
                    description = "One paragraph describing the proposed change."
                },
                steps = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Ordered implementation steps."
                },
                filesToChange = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Repository-relative paths, including files to create."
                },
                risks = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Specific risks of this design, not generic ones."
                },
                openQuestions = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Questions whose answers would change the design."
                }
            }),
            ["required"] = JsonSerializer.SerializeToElement(
                new[] { "summary", "steps", "filesToChange", "risks", "openQuestions" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false)
        };
}
