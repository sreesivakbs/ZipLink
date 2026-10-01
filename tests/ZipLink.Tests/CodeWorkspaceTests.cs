using ZipLink.Agentic.Agents;

namespace ZipLink.Tests;

/// <summary>
/// The sandbox an agent writes into. These are the safety properties: everything here is
/// about what an agent must *not* be able to do.
/// </summary>
public class CodeWorkspaceTests : IDisposable
{
    private readonly string _root;
    private readonly CodeWorkspace _workspace;

    public CodeWorkspaceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "ziplink-ws-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        // A plain directory is enough: these tests exercise the write policy, which is
        // what stands between an agent and the rest of the machine.
        _workspace = CodeWorkspace.Open(_root, _root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly string[] Allowed =
    [
        "src/ZipLink.Core/Models/ShortUrl.cs",
        "tests/ZipLink.Tests/NewTests.cs"
    ];

    [Fact]
    public void AnAllowedFileIsWritten()
    {
        var written = _workspace.Apply(
            [new WorkspaceWrite("src/ZipLink.Core/Models/ShortUrl.cs", "// new contents")],
            Allowed);

        Assert.Single(written);
        Assert.Equal(
            "// new contents",
            File.ReadAllText(Path.Combine(_root, "src", "ZipLink.Core", "Models", "ShortUrl.cs")));
    }

    [Fact]
    public void AFileMissingFromTheApprovedDesignIsRejected()
    {
        var error = Assert.Throws<WorkspacePolicyException>(() =>
            _workspace.Apply(
                [new WorkspaceWrite("src/ZipLink.Api/Program.cs", "// sneaky")], Allowed));

        Assert.Contains("not in the approved design", error.Message);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../escape.cs")]
    public void PathTraversalIsRejected(string path)
    {
        Assert.Throws<WorkspacePolicyException>(
            () => _workspace.Apply([new WorkspaceWrite(path, "x")], [path]));
    }

    [Fact]
    public void AnAbsolutePathIsRejected()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "pwned.cs");

        var error = Assert.Throws<WorkspacePolicyException>(
            () => _workspace.Apply([new WorkspaceWrite(absolute, "x")], [absolute]));

        Assert.Contains("absolute path", error.Message);
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".ziplink/runs/x/run.json")]
    [InlineData("docs/ARCHITECTURE.md")]
    [InlineData("spikes/ZipLink.Spike.AgentFramework/Program.cs")]
    public void ProtectedDirectoriesAreRejectedEvenIfTheDesignListsThem(string path)
    {
        var error = Assert.Throws<WorkspacePolicyException>(
            () => _workspace.Apply([new WorkspaceWrite(path, "x")], [path]));

        Assert.Contains("protected", error.Message);
    }

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("PROJECT_BRIEF.md")]
    [InlineData(".gitignore")]
    public void GovernanceFilesAreRejectedEvenIfTheDesignListsThem(string path)
    {
        var error = Assert.Throws<WorkspacePolicyException>(
            () => _workspace.Apply([new WorkspaceWrite(path, "x")], [path]));

        Assert.Contains("protected", error.Message);
    }

    [Fact]
    public void ABatchContainingOneBadPathWritesNothingAtAll()
    {
        Assert.Throws<WorkspacePolicyException>(() =>
            _workspace.Apply(
                [
                    new WorkspaceWrite("src/ZipLink.Core/Models/ShortUrl.cs", "// fine"),
                    new WorkspaceWrite("src/ZipLink.Api/Program.cs", "// not fine")
                ],
                Allowed));

        // The legal write in the same batch must not have landed either.
        Assert.False(
            File.Exists(Path.Combine(_root, "src", "ZipLink.Core", "Models", "ShortUrl.cs")));
    }

    [Fact]
    public void BackslashAndForwardSlashPathsAreTreatedTheSame()
    {
        var written = _workspace.Apply(
            [new WorkspaceWrite(@"src\ZipLink.Core\Models\ShortUrl.cs", "// contents")],
            Allowed);

        Assert.Single(written);
    }

    [Fact]
    public void ReadExistingReturnsNullForAFileThatIsNotThereYet()
    {
        Assert.Null(_workspace.ReadExisting("tests/ZipLink.Tests/NewTests.cs"));
    }

    [Fact]
    public void ReadExistingReturnsContentsOnceWritten()
    {
        _workspace.Apply(
            [new WorkspaceWrite("tests/ZipLink.Tests/NewTests.cs", "// a test")], Allowed);

        Assert.Equal("// a test", _workspace.ReadExisting("tests/ZipLink.Tests/NewTests.cs"));
    }

    [Fact]
    public void WorkspacePathsAreScopedToTheRun()
    {
        Assert.Contains("run-a", CodeWorkspace.PathFor("run-a"));
        Assert.NotEqual(CodeWorkspace.PathFor("run-a"), CodeWorkspace.PathFor("run-b"));
    }
}
