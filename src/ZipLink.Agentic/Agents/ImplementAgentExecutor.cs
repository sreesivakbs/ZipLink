using System.Text;
using System.Text.Json;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Agentic.Agents;

/// <summary>What the implement stage records, so later stages and a human can follow it.</summary>
public sealed record ImplementArtifact(
    string WorkspacePath,
    IReadOnlyList<string> FilesWritten,
    string DiffStat,
    int BuildAttempts,
    string Notes);

internal sealed record CodeEdit(string Path, string Contents);

internal sealed record CodeEditSet(IReadOnlyList<CodeEdit> Files, string? Notes);

/// <summary>
/// The first stage that writes code.
///
/// It writes into an isolated git worktree, never the developer's working tree, and only
/// to files the approved design named - the design's file list is an allow list enforced
/// before any write lands. It then compiles what it wrote and feeds compiler errors back
/// to itself, up to a bounded number of attempts, because an agent's claim that code is
/// correct is worth nothing next to a compiler saying so.
///
/// It does not decide that the change is good: the tests stage runs the real suite
/// afterwards, and the release gate puts a human in front of the diff. Nothing merges.
/// </summary>
public sealed class ImplementAgentExecutor : IStageExecutor
{
    private const int MaxBuildAttempts = 3;

    private const string SystemPrompt =
        "You are implementing an approved design in an existing C# repository.\n\n"
        + "Rules you must follow:\n"
        + "- Return the COMPLETE new contents of every file you change. Never return a "
        + "diff, a patch, or an excerpt with elisions.\n"
        + "- You may only write files listed as allowed. Writing anywhere else fails the "
        + "change.\n"
        + "- Preserve existing public behaviour unless the design says otherwise, and "
        + "keep existing call sites compiling.\n"
        + "- Match the surrounding code's style, nullability and naming.\n"
        + "- The build must have zero warnings.\n"
        + "- Add or update tests for the behaviour you change.\n\n"
        + "If a compiler error is supplied, fix exactly that error and return the full "
        + "contents of the files you had to touch to fix it.";

    private readonly ILanguageModel _model;

    public ImplementAgentExecutor(ILanguageModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
    }

    public async Task<StageResult> ExecuteAsync(
        StageContext context,
        CancellationToken cancellationToken)
    {
        var design = context.Read<DesignArtifact>(StagePipeline.Design);

        if (design is null || design.FilesToChange.Count == 0)
        {
            return StageResult.Failure(
                "No approved design with a file list is available, so there is nothing "
                + "the agent is permitted to write.");
        }

        var workspace = await CodeWorkspace.CreateAsync(
            context.RepositoryRoot, context.RunId, cancellationToken);

        var written = new List<string>();
        var notes = new StringBuilder();
        string? compilerErrors = null;

        for (var attempt = 1; attempt <= MaxBuildAttempts; attempt++)
        {
            var result = await _model.CompleteJsonAsync(
                SystemPrompt,
                BuildPrompt(context, design, workspace, compilerErrors),
                Schema,
                cancellationToken);

            if (result.Refused)
            {
                return StageResult.Blocked(
                    "The implementation agent declined to write this change.",
                    result.RefusalReason ?? "No reason was given.");
            }

            CodeEditSet? edits;

            try
            {
                edits = JsonSerializer.Deserialize<CodeEditSet>(
                    result.Json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                return StageResult.Failure(
                    $"The implementation agent returned invalid JSON: {ex.Message}");
            }

            if (edits is null || edits.Files.Count == 0)
            {
                return StageResult.Failure("The implementation agent returned no files.");
            }

            try
            {
                var applied = workspace.Apply(
                    edits.Files.Select(file => new WorkspaceWrite(file.Path, file.Contents)).ToList(),
                    design.FilesToChange);

                written = written.Union(applied, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (WorkspacePolicyException ex)
            {
                // A policy breach is not retried: the agent was told the rules and broke
                // them, and a human should see that rather than have it quietly re-rolled.
                await workspace.RollbackAsync(cancellationToken);

                return StageResult.Blocked(
                    "The implementation agent tried to write outside the approved scope.",
                    ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(edits.Notes))
            {
                notes.AppendLine(edits.Notes);
            }

            var build = await DotnetCli.DotnetAsync(
                workspace.Root,
                ["build", "--nologo", "-warnaserror"],
                cancellationToken,
                TimeSpan.FromMinutes(5));

            if (build.Succeeded)
            {
                return StageResult.Success(
                    $"{written.Count} file(s) written and compiling after {attempt} attempt(s).",
                    new ImplementArtifact(
                        workspace.Root,
                        written,
                        await workspace.DiffAsync(cancellationToken),
                        attempt,
                        notes.ToString().Trim()));
            }

            compilerErrors = Summarize(build.Output);
        }

        // Out of attempts: undo everything so the workspace matches the commit it started
        // from, rather than leaving half-working code for the tests stage to judge.
        await workspace.RollbackAsync(cancellationToken);

        return StageResult.Failure(
            $"The implementation did not compile after {MaxBuildAttempts} attempts; the "
            + $"workspace was rolled back. Last errors:\n{compilerErrors}");
    }

    private static string BuildPrompt(
        StageContext context,
        DesignArtifact design,
        CodeWorkspace workspace,
        string? compilerErrors)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine("REQUIREMENT");
        prompt.AppendLine(context.Requirement);
        prompt.AppendLine();
        prompt.AppendLine("APPROVED DESIGN");
        prompt.AppendLine(design.Summary);

        if (design.Steps.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Steps:");

            foreach (var step in design.Steps)
            {
                prompt.AppendLine($"- {step}");
            }
        }

        prompt.AppendLine();
        prompt.AppendLine("FILES YOU MAY WRITE (any other path is rejected)");

        foreach (var file in design.FilesToChange)
        {
            prompt.AppendLine($"- {file}");
        }

        prompt.AppendLine();
        prompt.AppendLine("CURRENT CONTENTS");

        foreach (var file in design.FilesToChange)
        {
            var existing = workspace.ReadExisting(file);

            prompt.AppendLine();
            prompt.AppendLine($"--- {file} ---");
            prompt.AppendLine(existing ?? "(this file does not exist yet; create it)");
        }

        if (compilerErrors is not null)
        {
            prompt.AppendLine();
            prompt.AppendLine("YOUR PREVIOUS ATTEMPT DID NOT COMPILE. Fix these errors:");
            prompt.AppendLine(compilerErrors);
        }

        return prompt.ToString();
    }

    /// <summary>Keeps the feedback to the lines that say what is wrong.</summary>
    private static string Summarize(string buildOutput)
    {
        var lines = buildOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains("error ", StringComparison.OrdinalIgnoreCase)
                || line.Contains("warning ", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToList();

        return lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : buildOutput.Trim();
    }

    private static IReadOnlyDictionary<string, JsonElement> Schema { get; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                files = new
                {
                    type = "array",
                    description = "Every file to write, with its complete new contents.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new
                            {
                                type = "string",
                                description = "Repository-relative path, forward slashes."
                            },
                            contents = new
                            {
                                type = "string",
                                description = "The entire file, not a diff or an excerpt."
                            }
                        },
                        required = new[] { "path", "contents" },
                        additionalProperties = false
                    }
                },
                notes = new
                {
                    type = "string",
                    description = "Anything a reviewer should know about the change."
                }
            }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "files", "notes" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false)
        };
}
