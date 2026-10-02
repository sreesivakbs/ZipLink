namespace ZipLink.Agentic.Agents;

/// <summary>One file the agent wants to write, with its complete new contents.</summary>
public sealed record WorkspaceWrite(string Path, string Contents);

/// <summary>
/// Thrown when an agent tries to write somewhere the approved design did not sanction.
/// A policy violation is a stage failure, never a warning.
/// </summary>
public sealed class WorkspacePolicyException : Exception
{
    public WorkspacePolicyException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// An isolated checkout the implementation agent writes into.
///
/// It is a <c>git worktree</c> in detached HEAD state, created outside the repository, so
/// the developer's working tree is never touched and **no branch is created**. Rollback
/// is a real <c>git reset --hard</c> plus <c>git clean -fd</c> rather than deleting
/// artifacts, which is what the project brief means by "rollback = reset to the previous
/// stage's commit".
///
/// Nothing here merges or pushes. The workspace is left for a human to inspect.
/// </summary>
public sealed class CodeWorkspace
{
    // Paths an agent may never write, whatever the design says. Governance, evidence and
    // git internals are not the agent's to edit.
    private static readonly string[] ProtectedSegments =
        [".git", ".ziplink", "docs", "spikes"];

    private static readonly string[] ProtectedFiles =
        ["CLAUDE.md", "PROJECT_BRIEF.md", "README.md", ".gitignore"];

    private CodeWorkspace(string repositoryRoot, string root)
    {
        RepositoryRoot = repositoryRoot;
        Root = root;
    }

    public string RepositoryRoot { get; }

    public string Root { get; }

    public static string PathFor(string runId)
    {
        return Path.Combine(Path.GetTempPath(), "ziplink-work", runId);
    }

    /// <summary>
    /// Creates the workspace, or reopens it if a previous attempt in this run already
    /// made one - a retry must not fail because the directory exists.
    /// </summary>
    public static async Task<CodeWorkspace> CreateAsync(
        string repositoryRoot,
        string runId,
        CancellationToken cancellationToken)
    {
        var root = PathFor(runId);

        if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
        {
            return new CodeWorkspace(repositoryRoot, root);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(root)!);

        // --detach keeps this to a single branch in the repository: the workspace sits on
        // a detached HEAD rather than creating agent/* branches.
        var result = await DotnetCli.GitAsync(
            repositoryRoot,
            ["worktree", "add", "--detach", root, "HEAD"],
            cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not create an agent workspace: {result.Output.Trim()}");
        }

        return new CodeWorkspace(repositoryRoot, root);
    }

    public static CodeWorkspace Open(string repositoryRoot, string root)
    {
        return new CodeWorkspace(repositoryRoot, root);
    }

    public static bool Exists(string root)
    {
        return Directory.Exists(root);
    }

    /// <summary>
    /// Writes the agent's files after checking every one against the allow list taken
    /// from the approved design. Validation happens for all writes before any is applied,
    /// so a rejected batch leaves nothing half-written.
    /// </summary>
    public IReadOnlyList<string> Apply(
        IReadOnlyList<WorkspaceWrite> writes,
        IReadOnlyCollection<string> allowedPaths)
    {
        var allowed = new HashSet<string>(
            allowedPaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);

        var planned = new List<(string Relative, string Full, string Contents)>();

        foreach (var write in writes)
        {
            var relative = Normalize(write.Path);

            Guard(relative, allowed);

            planned.Add((relative, ResolveInsideWorkspace(relative), write.Contents));
        }

        foreach (var (_, full, contents) in planned)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
        }

        return planned.Select(item => item.Relative).ToList();
    }

    /// <summary>Current contents of a tracked file, or null when it does not exist yet.</summary>
    public string? ReadExisting(string relativePath)
    {
        var full = Path.Combine(Root, Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    public async Task<string> DiffAsync(CancellationToken cancellationToken)
    {
        // Stage first so newly created files appear in the diff at all.
        await DotnetCli.GitAsync(Root, ["add", "-A"], cancellationToken);

        var result = await DotnetCli.GitAsync(
            Root, ["diff", "--cached", "--stat"], cancellationToken);

        return result.Output.Trim();
    }

    /// <summary>
    /// Discards everything the agent did. This is the real rollback the brief asks for:
    /// the workspace returns to the commit it was created from.
    /// </summary>
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        await DotnetCli.GitAsync(Root, ["reset", "--hard"], cancellationToken);
        await DotnetCli.GitAsync(Root, ["clean", "-fd"], cancellationToken);
    }

    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        await DotnetCli.GitAsync(
            RepositoryRoot, ["worktree", "remove", "--force", Root], cancellationToken);
    }

    /// <summary>
    /// Why this path could never be written, or null when it is acceptable. Shared with
    /// the design stage so a human is not asked to approve a plan that names files the
    /// implementation agent would be refused - the alternative is discovering it only
    /// after approval, when the agent silently skips them.
    ///
    /// This deliberately does not consider the approved file list: that is a property of
    /// a particular design, not of the path itself.
    /// </summary>
    public static string? DescribeWriteProblem(string path)
    {
        var relative = Normalize(path);

        if (relative.Length == 0)
        {
            return "the path is empty";
        }

        if (System.IO.Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            return "it is an absolute path, and agents may only write inside the workspace";
        }

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Contains(".."))
        {
            return "it escapes the workspace with '..'";
        }

        if (segments.Any(segment =>
            ProtectedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            return "it is in a protected location that agents may never write";
        }

        if (ProtectedFiles.Contains(segments[^1], StringComparer.OrdinalIgnoreCase))
        {
            return "it is a protected file that agents may never write";
        }

        // A space inside a path segment is almost always a garbled answer rather than a
        // real file name - a confidence word or a stray fragment that leaked into the
        // list. Worth surfacing before a human approves it.
        if (segments.Any(segment => segment.Contains(' ')))
        {
            return "a path segment contains a space, which usually means the answer was garbled";
        }

        return null;
    }

    private void Guard(string relative, HashSet<string> allowed)
    {
        if (DescribeWriteProblem(relative) is { } problem)
        {
            throw new WorkspacePolicyException($"'{relative}' cannot be written: {problem}.");
        }

        if (!allowed.Contains(relative))
        {
            throw new WorkspacePolicyException(
                $"'{relative}' is not in the approved design's list of files to change. "
                + "The agent may only touch files a human signed off.");
        }
    }

    private string ResolveInsideWorkspace(string relative)
    {
        var full = Path.GetFullPath(
            Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

        var root = Path.GetFullPath(Root);

        // Belt and braces: even after the segment checks, the resolved path must land
        // inside the workspace.
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspacePolicyException(
                $"'{relative}' resolves outside the workspace.");
        }

        return full;
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').Trim().TrimStart('/');
    }
}
