using System.Text;
using System.Text.Json;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Agentic.Agents;

public sealed record DocsArtifact
{
    public required string ChangeSummary { get; init; }

    public IReadOnlyList<string> ReleaseNotes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DocumentationGaps { get; init; } = Array.Empty<string>();

    public string Source { get; init; } = "agent";
}

/// <summary>
/// Writes the human-facing account of a change: release notes and the documentation it
/// leaves stale.
///
/// It runs in parallel with tests and the policy gate, and reads the design and
/// implementation artifacts rather than the code itself - the diff summary and the
/// approved design are what a reader of release notes actually needs.
///
/// It writes nothing to disk. Documentation is a proposal for a human, and the files it
/// would touch (docs/, README.md) are exactly the paths the workspace policy forbids
/// agents from editing.
/// </summary>
public sealed class DocsAgentExecutor : IStageExecutor
{
    private const string SystemPrompt =
        "You write release notes for a C# repository.\n\n"
        + "Describe what changed and why, in the words a user of the software would use, "
        + "not the words of the diff. Keep each note to one line.\n\n"
        + "Then list documentation that this change makes stale or missing - be specific "
        + "about which document and what is now wrong in it. If nothing is stale, return "
        + "an empty list rather than inventing work.";

    private readonly ILanguageModel _model;

    public DocsAgentExecutor(ILanguageModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
    }

    public async Task<StageResult> ExecuteAsync(
        StageContext context,
        CancellationToken cancellationToken)
    {
        var result = await _model.CompleteJsonAsync(
            SystemPrompt, BuildPrompt(context), Schema, cancellationToken);

        if (result.Refused)
        {
            return StageResult.Blocked(
                "The documentation agent declined.",
                result.RefusalReason ?? "No reason was given.");
        }

        DocsArtifact? docs;

        try
        {
            docs = JsonSerializer.Deserialize<DocsArtifact>(
                result.Json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return StageResult.Failure(
                $"The documentation agent returned invalid JSON: {ex.Message}");
        }

        if (docs is null || string.IsNullOrWhiteSpace(docs.ChangeSummary))
        {
            return StageResult.Failure("The documentation agent returned no summary.");
        }

        return StageResult.Success(
            $"{docs.ReleaseNotes.Count} release note(s); "
            + $"{docs.DocumentationGaps.Count} documentation gap(s) flagged.",
            docs);
    }

    private static string BuildPrompt(StageContext context)
    {
        var design = context.Read<DesignArtifact>(StagePipeline.Design);
        var implementation = context.Read<ImplementArtifact>(StagePipeline.Implement);

        var prompt = new StringBuilder();

        prompt.AppendLine("REQUIREMENT");
        prompt.AppendLine(context.Requirement);

        if (design is not null)
        {
            prompt.AppendLine();
            prompt.AppendLine("APPROVED DESIGN");
            prompt.AppendLine(design.Summary);
        }

        if (implementation is not null)
        {
            prompt.AppendLine();
            prompt.AppendLine("WHAT WAS ACTUALLY CHANGED");
            prompt.AppendLine(implementation.DiffStat);

            if (implementation.FilesWritten.Count > 0)
            {
                prompt.AppendLine();

                foreach (var file in implementation.FilesWritten)
                {
                    prompt.AppendLine($"- {file}");
                }
            }
        }
        else
        {
            prompt.AppendLine();
            prompt.AppendLine(
                "No implementation stage output is available, so describe the change the "
                + "design proposes rather than one that was made.");
        }

        return prompt.ToString();
    }

    private static IReadOnlyDictionary<string, JsonElement> Schema { get; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                changeSummary = new
                {
                    type = "string",
                    description = "One paragraph a release manager could paste verbatim."
                },
                releaseNotes = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "User-facing notes, one line each."
                },
                documentationGaps = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description =
                        "Documents this change makes stale, and what is now wrong in them."
                }
            }),
            ["required"] = JsonSerializer.SerializeToElement(
                new[] { "changeSummary", "releaseNotes", "documentationGaps" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false)
        };
}
