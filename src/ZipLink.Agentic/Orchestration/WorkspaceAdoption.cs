using System.Text.Json;
using ZipLink.Agentic.Agents;

namespace ZipLink.Agentic.Orchestration;

/// <summary>One file a run changed, with the git status letter describing the change.</summary>
public sealed record AdoptionChange(string Path, char Status)
{
    public string Description => Status switch
    {
        'A' => "new file",
        'M' => "modified",
        'D' => "deleted",
        'R' => "renamed",
        _ => Status.ToString()
    };
}

/// <summary>What adopting a run's work into the working tree would do - or did.</summary>
public sealed record AdoptionPlan
{
    public required string RunId { get; init; }

    public string? WorkspacePath { get; init; }

    public IReadOnlyList<AdoptionChange> Changes { get; init; } = Array.Empty<AdoptionChange>();

    /// <summary>Reasons this cannot proceed. Non-empty means nothing was written.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();

    /// <summary>Worth knowing, but not disqualifying.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>True only when files were actually copied into the working tree.</summary>
    public bool Applied { get; init; }

    public bool CanApply => Blockers.Count == 0 && Changes.Count > 0;
}

/// <summary>
/// Moves a finished run's code out of its sandbox and into the developer's working tree.
///
/// This is the only place in the system that writes to the repository a human is working
/// in, so it is deliberately not something a run can do to itself: a person asks for it,
/// by run id, after the deterministic checks have already passed. The agent proposes, the
/// checks judge, a human adopts.
///
/// Five rules make it safe to press:
/// <list type="bullet">
/// <item>the run's implement, tests and policy stages must all have succeeded, so code
/// that failed its checks can never be adopted;</item>
/// <item>the working tree must be clean, so <c>git restore .</c> is a complete undo and
/// none of the developer's uncommitted work can be buried;</item>
/// <item>every path is re-checked against
/// <see cref="CodeWorkspace.DescribeWriteProblem"/>, so the protections that held inside
/// the sandbox hold here too;</item>
/// <item>deletions and renames are refused rather than skipped, because removing a file
/// is a change-control decision a human makes explicitly;</item>
/// <item>nothing is committed. The work lands as ordinary uncommitted edits for review.</item>
/// </list>
/// </summary>
public sealed class WorkspaceAdoption
{
    /// <summary>Gates whose success is a precondition for adopting anything.</summary>
    private static readonly string[] RequiredStages =
        [StagePipeline.Implement, StagePipeline.Tests, StagePipeline.Policy];

    private readonly RunStore _store;
    private readonly string _repositoryRoot;

    public WorkspaceAdoption(RunStore store, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        _store = store;
        _repositoryRoot = repositoryRoot;
    }

    /// <summary>
    /// Works out what adopting this run would change, without touching anything. The UI
    /// shows this before a human commits to it.
    /// </summary>
    public async Task<AdoptionPlan> PlanAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var state = _store.Load(runId);

        if (state is null)
        {
            return new AdoptionPlan
            {
                RunId = runId,
                Blockers = [$"There is no run with id '{runId}'."]
            };
        }

        var blockers = new List<string>();
        var warnings = new List<string>();

        // The deterministic gates decide whether this code is fit to adopt. Skipping this
        // check would make every other check in the system decorative.
        foreach (var required in RequiredStages)
        {
            var stage = state.Stage(required);

            if (stage?.State != StageState.Succeeded)
            {
                blockers.Add(
                    $"The '{required}' stage is {stage?.State.ToString() ?? "missing"}, "
                    + "not Succeeded.");
            }
        }

        var workspace = ReadWorkspacePath(runId);

        if (workspace is null)
        {
            blockers.Add("This run recorded no agent workspace, so it produced no code.");

            return new AdoptionPlan
            {
                RunId = runId,
                Blockers = blockers,
                Warnings = warnings
            };
        }

        if (!Directory.Exists(workspace))
        {
            blockers.Add($"The agent workspace no longer exists at {workspace}.");

            return new AdoptionPlan
            {
                RunId = runId,
                WorkspacePath = workspace,
                Blockers = blockers,
                Warnings = warnings
            };
        }

        // A dirty tree is the one way adopting could destroy work that git cannot recover.
        // Refusing keeps "undo" as simple as `git restore .`.
        var dirty = await DotnetCli.GitAsync(
            _repositoryRoot, ["status", "--porcelain"], cancellationToken);

        if (dirty.Succeeded && dirty.Output.Trim().Length > 0)
        {
            blockers.Add(
                "The repository has uncommitted changes. Commit or stash them first, so "
                + "that adopting this run can be undone with 'git restore .'.");
        }

        if (await BaseCommitMovedAsync(workspace, cancellationToken))
        {
            warnings.Add(
                "This run started from an older commit than the repository is on now, so "
                + "its files may not include later changes.");
        }

        var changes = await ReadChangesAsync(workspace, cancellationToken);

        if (changes.Count == 0)
        {
            warnings.Add("This run changed no files, so there is nothing to adopt.");
        }

        // The sandbox's path rules are not a property of the sandbox - they are the rules.
        // They apply just as much when writing into the real repository.
        foreach (var change in changes)
        {
            if (CodeWorkspace.DescribeWriteProblem(change.Path) is { } problem)
            {
                blockers.Add($"'{change.Path}' cannot be adopted: {problem}.");
            }
        }

        // Surfaced rather than silently skipped: a removal is a change-control decision,
        // and quietly dropping it would adopt a different change than the one displayed.
        var removals = changes.Where(change => change.Status is 'D' or 'R').ToList();

        if (removals.Count > 0)
        {
            blockers.Add(
                "This run deletes or renames "
                + $"{string.Join(", ", removals.Select(removal => $"'{removal.Path}'"))}. "
                + "Removing files needs a human decision, so adopt those by hand.");
        }

        return new AdoptionPlan
        {
            RunId = runId,
            WorkspacePath = workspace,
            Changes = changes,
            Blockers = blockers,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Copies the run's changed files into the working tree, if and only if the plan is
    /// clear. Re-plans first, so a tree that went dirty between preview and press is caught.
    /// </summary>
    public async Task<AdoptionPlan> ApplyAsync(
        string runId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var plan = await PlanAsync(runId, cancellationToken);

        if (!plan.CanApply)
        {
            _store.Append(new AuditEvent(
                DateTime.UtcNow,
                runId,
                null,
                "adoption_refused",
                actor,
                plan.Blockers.Count > 0
                    ? string.Join(" ", plan.Blockers)
                    : "Nothing to adopt."));

            return plan;
        }

        foreach (var change in plan.Changes)
        {
            var relative = change.Path.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(plan.WorkspacePath!, relative);
            var destination = Path.Combine(_repositoryRoot, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        _store.Append(new AuditEvent(
            DateTime.UtcNow,
            runId,
            null,
            "adoption_applied",
            actor,
            $"Copied {plan.Changes.Count} file(s) into the working tree: "
            + string.Join(", ", plan.Changes.Select(change => change.Path))
            + ". Nothing was committed."));

        return plan with { Applied = true };
    }

    private string? ReadWorkspacePath(string runId)
    {
        if (!_store.ReadArtifacts(runId).TryGetValue(StagePipeline.Implement, out var json))
        {
            return null;
        }

        try
        {
            var path = JsonSerializer
                .Deserialize<ImplementArtifact>(json, JsonDefaults.Options)
                ?.WorkspacePath;

            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> BaseCommitMovedAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        var theirs = await DotnetCli.GitAsync(
            workspace, ["rev-parse", "HEAD"], cancellationToken);

        var ours = await DotnetCli.GitAsync(
            _repositoryRoot, ["rev-parse", "HEAD"], cancellationToken);

        return theirs.Succeeded
            && ours.Succeeded
            && !string.Equals(theirs.Output.Trim(), ours.Output.Trim(), StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<AdoptionChange>> ReadChangesAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        // Staging is what makes files the agent created visible to 'diff'. It touches only
        // the throwaway worktree's index, never its contents, and the implement stage
        // already does exactly this to produce its diffstat.
        await DotnetCli.GitAsync(workspace, ["add", "-A"], cancellationToken);

        var result = await DotnetCli.GitAsync(
            workspace, ["diff", "--cached", "--name-status", "HEAD"], cancellationToken);

        if (!result.Succeeded)
        {
            return Array.Empty<AdoptionChange>();
        }

        var changes = new List<AdoptionChange>();

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2 || parts[0].Length == 0)
            {
                continue;
            }

            // Renames arrive as "R100\told\tnew"; the last field is always the path that
            // would be written.
            changes.Add(new AdoptionChange(parts[^1].Replace('\\', '/'), parts[0][0]));
        }

        return changes;
    }
}
