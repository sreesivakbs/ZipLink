using ZipLink.Agentic.Agents;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Tests;

/// <summary>
/// The handoff out of the agent's sandbox and into the developer's working tree.
///
/// This is the only code in the system that writes to the repository a human is working
/// in, so most of these tests are about refusal: code whose checks failed, a tree with
/// unsaved work in it, protected paths and deletions all have to be turned away. They run
/// against a real git repository and a real worktree, because the guarantees being tested
/// are git's.
/// </summary>
public class WorkspaceAdoptionTests : IDisposable
{
    private const string RunId = "run-adopt-1";
    private const string TrackedFile = "src/App/Program.cs";

    private readonly string _repository;
    private readonly string _workspace;
    private readonly RunStore _store;

    public WorkspaceAdoptionTests()
    {
        var unique = Guid.NewGuid().ToString("N");

        _repository = Path.Combine(Path.GetTempPath(), $"ziplink-adopt-{unique}");
        _workspace = Path.Combine(Path.GetTempPath(), $"ziplink-adopt-{unique}-ws");

        Directory.CreateDirectory(_repository);

        Git(_repository, "init", "-q");
        Git(_repository, "config", "user.email", "test@example.com");
        Git(_repository, "config", "user.name", "ZipLink Test");
        Git(_repository, "config", "commit.gpgsign", "false");

        Write(_repository, TrackedFile, "// original\n");

        // Mirrors the real repository: run state is ignored, so starting a run does not
        // make the working tree dirty and block its own adoption.
        Write(_repository, ".gitignore", ".ziplink/\n");

        Git(_repository, "add", "-A");
        Git(_repository, "commit", "-qm", "base");
        Git(_repository, "worktree", "add", "--detach", "-q", _workspace, "HEAD");

        _store = new RunStore(_repository);
    }

    public void Dispose()
    {
        TryGit(_repository, "worktree", "remove", "--force", _workspace);

        Delete(_workspace);
        Delete(_repository);
    }

    // ---------- the happy path ----------

    [Fact]
    public async Task AnApprovedRunsCodeReachesTheWorkingTree()
    {
        SeedRun();
        Write(_workspace, TrackedFile, "// the agent's version\n");

        var plan = await Adoption.ApplyAsync(RunId, "tester");

        Assert.True(plan.Applied);
        Assert.Equal("// the agent's version\n", Read(_repository, TrackedFile));
    }

    [Fact]
    public async Task AFileTheAgentCreatedIsCopiedToo()
    {
        SeedRun();
        Write(_workspace, "src/App/NewThing.cs", "// brand new\n");

        var plan = await Adoption.ApplyAsync(RunId, "tester");

        Assert.True(plan.Applied);
        Assert.Contains(plan.Changes, change => change.Path == "src/App/NewThing.cs");
        Assert.Equal("// brand new\n", Read(_repository, "src/App/NewThing.cs"));
    }

    [Fact]
    public async Task NothingIsCommitted()
    {
        SeedRun();
        Write(_workspace, TrackedFile, "// the agent's version\n");

        await Adoption.ApplyAsync(RunId, "tester");

        // The change has to land as ordinary uncommitted work, so a human can read it with
        // 'git diff' and undo it with 'git restore .'.
        var status = await DotnetCli.GitAsync(
            _repository, ["status", "--porcelain"], CancellationToken.None);

        Assert.Contains("Program.cs", status.Output);
    }

    [Fact]
    public async Task APreviewChangesNothing()
    {
        SeedRun();
        Write(_workspace, TrackedFile, "// the agent's version\n");

        var plan = await Adoption.PlanAsync(RunId);

        Assert.True(plan.CanApply);
        Assert.False(plan.Applied);
        Assert.Equal("// original\n", Read(_repository, TrackedFile));
    }

    // ---------- refusals ----------

    [Theory]
    [InlineData(StagePipeline.Tests)]
    [InlineData(StagePipeline.Policy)]
    [InlineData(StagePipeline.Implement)]
    public async Task CodeThatFailedItsChecksIsRefused(string failedStage)
    {
        SeedRun(failed: failedStage);
        Write(_workspace, TrackedFile, "// the agent's version\n");

        var plan = await Adoption.ApplyAsync(RunId, "tester");

        Assert.False(plan.Applied);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains($"'{failedStage}' stage"));

        // The whole point: the file never moved.
        Assert.Equal("// original\n", Read(_repository, TrackedFile));
    }

    [Fact]
    public async Task ADirtyWorkingTreeIsRefused()
    {
        SeedRun();
        Write(_workspace, TrackedFile, "// the agent's version\n");

        // Someone's unsaved work. Overwriting it would be unrecoverable, so adoption stops.
        Write(_repository, "src/App/MyWork.cs", "// half-finished\n");

        var plan = await Adoption.ApplyAsync(RunId, "tester");

        Assert.False(plan.Applied);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("uncommitted changes"));
        Assert.Equal("// original\n", Read(_repository, TrackedFile));
    }

    [Fact]
    public async Task AProtectedFileIsRefused()
    {
        SeedRun();

        // Even reaching the working tree, the sandbox's rules still hold: governance files
        // are never the agent's to edit.
        Write(_workspace, "CLAUDE.md", "# rewritten by an agent\n");

        var plan = await Adoption.ApplyAsync(RunId, "tester");

        Assert.False(plan.Applied);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("CLAUDE.md"));
        Assert.False(File.Exists(Path.Combine(_repository, "CLAUDE.md")));
    }

    [Fact]
    public async Task ADeletionIsRefusedRatherThanSilentlySkipped()
    {
        SeedRun();
        File.Delete(Path.Combine(_workspace, TrackedFile.Replace('/', Path.DirectorySeparatorChar)));

        var plan = await Adoption.ApplyAsync(RunId, "tester");

        Assert.False(plan.Applied);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("deletes or renames"));

        // Skipping it quietly would have adopted a different change than the one shown.
        Assert.True(File.Exists(Path.Combine(_repository, "src", "App", "Program.cs")));
    }

    [Fact]
    public async Task ARunThatChangedNothingSaysSo()
    {
        SeedRun();

        var plan = await Adoption.PlanAsync(RunId);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Warnings, warning => warning.Contains("changed no files"));
    }

    [Fact]
    public async Task AnUnknownRunIsRefused()
    {
        var plan = await Adoption.PlanAsync("no-such-run");

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("no run with id"));
    }

    [Fact]
    public async Task ARunWithNoWorkspaceIsRefused()
    {
        var state = new RunState { RunId = RunId, Requirement = "anything" };

        state.Stages.Add(new StageRecord { Id = StagePipeline.Implement, State = StageState.Succeeded });

        _store.Save(state);

        var plan = await Adoption.PlanAsync(RunId);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("no agent workspace"));
    }

    [Fact]
    public async Task AMissingWorkspaceDirectoryIsRefused()
    {
        SeedRun(workspacePath: Path.Combine(Path.GetTempPath(), "ziplink-gone-" + Guid.NewGuid()));

        var plan = await Adoption.PlanAsync(RunId);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("no longer exists"));
    }

    // ---------- warnings ----------

    [Fact]
    public async Task AStaleWorkspaceIsFlaggedButStillAdoptable()
    {
        SeedRun();
        Write(_workspace, TrackedFile, "// the agent's version\n");

        // The repository moved on after the run started, so the agent's copy is behind.
        Write(_repository, "src/App/Later.cs", "// added after the run began\n");
        Git(_repository, "add", "-A");
        Git(_repository, "commit", "-qm", "later work");

        var plan = await Adoption.PlanAsync(RunId);

        Assert.Contains(plan.Warnings, warning => warning.Contains("older commit"));
        Assert.True(plan.CanApply);
    }

    // ---------- audit ----------

    [Fact]
    public async Task AnAdoptionIsRecordedInTheAuditLog()
    {
        SeedRun();
        Write(_workspace, TrackedFile, "// the agent's version\n");

        await Adoption.ApplyAsync(RunId, "sree");

        var entry = Assert.Single(
            _store.ReadAudit(RunId), e => e.Event == "adoption_applied");

        Assert.Equal("sree", entry.Actor);
        Assert.Contains(TrackedFile, entry.Detail);
        Assert.Contains("Nothing was committed", entry.Detail);
    }

    [Fact]
    public async Task ARefusalIsRecordedInTheAuditLog()
    {
        SeedRun(failed: StagePipeline.Tests);
        Write(_workspace, TrackedFile, "// the agent's version\n");

        await Adoption.ApplyAsync(RunId, "sree");

        var entry = Assert.Single(
            _store.ReadAudit(RunId), e => e.Event == "adoption_refused");

        Assert.Contains("'tests' stage", entry.Detail);
    }

    // ---------- fixture ----------

    private WorkspaceAdoption Adoption => new(_store, _repository);

    private void SeedRun(string? failed = null, string? workspacePath = null)
    {
        StageState StateFor(string stage) =>
            string.Equals(stage, failed, StringComparison.Ordinal)
                ? StageState.Failed
                : StageState.Succeeded;

        var state = new RunState
        {
            RunId = RunId,
            Requirement = "Set the generated short code length to 10 characters"
        };

        foreach (var stage in new[] { StagePipeline.Implement, StagePipeline.Tests, StagePipeline.Policy })
        {
            state.Stages.Add(new StageRecord { Id = stage, State = StateFor(stage) });
        }

        _store.Save(state);

        _store.WriteArtifact(
            RunId,
            StagePipeline.Implement,
            new ImplementArtifact(
                workspacePath ?? _workspace,
                [TrackedFile],
                "1 file changed",
                1,
                "seeded by a test"));
    }

    private static void Write(string root, string relativePath, string contents)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    private static string Read(string root, string relativePath)
    {
        return File.ReadAllText(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var result = DotnetCli
            .GitAsync(workingDirectory, arguments, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {result.Output}");
        }
    }

    private static void TryGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            DotnetCli
                .GitAsync(workingDirectory, arguments, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
            // Fixture teardown must never fail a test run.
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
