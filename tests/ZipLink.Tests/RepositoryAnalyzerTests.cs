using ZipLink.Agentic.Repository;

namespace ZipLink.Tests;

public class RepositoryAnalyzerTests
{
    [Fact]
    public void Analyze_ExtractsNamespaceTypesAndMethods()
    {
        using var repo = new TempRepository().AddFile(
            "src/Widgets/WidgetService.cs",
            """
            namespace Demo.Widgets;

            public class WidgetService
            {
                public WidgetService(int seed)
                {
                }

                public async Task<int> CountWidgetsAsync(string owner)
                {
                    await Task.Yield();
                    return 0;
                }

                private static string BuildKey() => "k";
            }
            """);

        var file = Assert.Single(new RepositoryAnalyzer().Analyze(repo.Root).Files);

        Assert.Equal("Demo.Widgets", file.Namespace);
        Assert.Equal(["WidgetService"], file.Types);
        Assert.Equal(["CountWidgetsAsync", "BuildKey"], file.Methods);
    }

    [Fact]
    public void Analyze_ExcludesBinAndObjDirectories()
    {
        using var repo = new TempRepository()
            .AddFile("src/Real.cs", "namespace Demo; public class Real { }")
            .AddFile("src/obj/Debug/Generated.cs", "namespace Demo; public class Generated { }")
            .AddFile("src/bin/Debug/Built.cs", "namespace Demo; public class Built { }");

        var analysis = new RepositoryAnalyzer().Analyze(repo.Root);

        var types = analysis.Files.SelectMany(file => file.Types).ToArray();

        Assert.Equal(["Real"], types);
        Assert.Equal(1, analysis.FileCount);
    }

    [Fact]
    public void Analyze_IgnoresDeclarationsInsideCommentsAndStrings()
    {
        using var repo = new TempRepository().AddFile(
            "src/Sample.cs",
            """
            namespace Demo;

            // public class CommentedOut { }
            /* public class BlockCommented { } */

            public class Real
            {
                private const string Snippet = "public class InsideString { }";
            }
            """);

        var file = Assert.Single(new RepositoryAnalyzer().Analyze(repo.Root).Files);

        Assert.Equal(["Real"], file.Types);
    }

    [Fact]
    public void Analyze_FindsInterfaceMembersThatHaveNoModifiers()
    {
        using var repo = new TempRepository().AddFile(
            "src/IStore.cs",
            """
            namespace Demo;

            public interface IStore
            {
                Task SaveAsync(string key);
                Task<string?> ReadAsync(string key);
            }
            """);

        var file = Assert.Single(new RepositoryAnalyzer().Analyze(repo.Root).Files);

        Assert.Equal(["IStore"], file.Types);
        Assert.Equal(["SaveAsync", "ReadAsync"], file.Methods);
    }

    [Fact]
    public void Analyze_ThrowsWhenRootDoesNotExist()
    {
        var analyzer = new RepositoryAnalyzer();

        Assert.Throws<DirectoryNotFoundException>(
            () => analyzer.Analyze(Path.Combine(Path.GetTempPath(), "ziplink-missing-dir")));
    }

    [Fact]
    public void Analyze_ThrowsWhenRootIsBlank()
    {
        Assert.Throws<ArgumentException>(() => new RepositoryAnalyzer().Analyze("  "));
    }
}
