using ZipLink.Agentic.Impact;

namespace ZipLink.Tests;

public class ImpactAnalyzerTests
{
    private const string Requirement = "Add expiration support to shortened URLs";

    private static TempRepository BuildShortenerRepository()
    {
        return new TempRepository()
            .AddFile(
                "src/Core/Models/ShortUrl.cs",
                """
                namespace Demo.Core.Models;

                public class ShortUrl
                {
                    public string ShortCode { get; set; } = string.Empty;
                }
                """)
            .AddFile(
                "src/Core/Services/UrlShorteningService.cs",
                """
                namespace Demo.Core.Services;

                public class UrlShorteningService
                {
                    public Task<ShortUrl> CreateShortUrlAsync(string originalUrl) => null!;
                    public Task<ShortUrl?> ResolveAsync(string code) => null!;
                }
                """)
            .AddFile(
                "src/Api/HealthEndpoint.cs",
                """
                namespace Demo.Api;

                public class HealthEndpoint
                {
                    public void MapHealth() { }
                }
                """)
            .AddFile(
                "src/Core/obj/Debug/Generated.cs",
                "namespace Demo; public class ShortUrlGenerated { }");
    }

    [Fact]
    public void Analyze_RanksTheShorteningServiceAboveUnrelatedFiles()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        Assert.NotEmpty(report.Items);

        var top = report.Items[0];

        Assert.Contains("UrlShorteningService", top.Path);
        Assert.Equal(report.Items.Max(item => item.Score), top.Score);
    }

    [Fact]
    public void Analyze_RanksProductionCodeAboveItsTests()
    {
        using var repo = BuildShortenerRepository().AddFile(
            "tests/Demo.Tests/UrlShorteningServiceTests.cs",
            """
            namespace Demo.Tests;

            public class UrlShorteningServiceTests
            {
                public void CreateShortUrl_WithValidUrl_CreatesShortUrl() { }
                public void CreateShortUrl_WithEmptyUrl_Throws() { }
                public void ResolveAsync_WithShortCode_ReturnsShortUrl() { }
            }
            """);

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        var service = report.Items.First(item => item.Path.Contains("UrlShorteningService.cs"));
        var tests = report.Items.First(item => item.Path.Contains("UrlShorteningServiceTests"));

        Assert.True(tests.IsTestFile);
        Assert.False(service.IsTestFile);
        Assert.True(
            service.Score > tests.Score,
            $"production {service.Score} should outrank tests {tests.Score}");
    }

    [Fact]
    public void Analyze_IgnoresCodeEmbeddedInRawStringLiterals()
    {
        using var repo = new TempRepository().AddFile(
            "src/Fixtures.cs",
            "namespace Demo;\n\npublic class Fixtures\n{\n"
            + "    private const string Sample = \"\"\"\n"
            + "        public class ExpirationPolicy { }\n"
            + "        \"\"\";\n}\n");

        var report = new ImpactAnalyzer().Analyze(repo.Root, "Add an expiration policy");

        Assert.DoesNotContain(
            report.Items.SelectMany(item => item.MatchedTypes), type => type == "ExpirationPolicy");
    }

    [Fact]
    public void Analyze_ExcludesFilesWithNoVocabularyOverlap()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        Assert.DoesNotContain(
            report.Items, item => item.Path.Contains("HealthEndpoint"));
    }

    [Fact]
    public void Analyze_NeverReportsGeneratedBuildOutput()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        Assert.DoesNotContain(report.Items, item => item.Path.Contains("obj"));
    }

    [Fact]
    public void Analyze_ReportsVocabularyThatIsAbsentFromTheCodebase()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        Assert.Contains(report.UnmatchedTerms, term => term.Word == "expiration");
    }

    [Fact]
    public void Analyze_CapsOverallConfidenceWhenPartOfTheRequirementIsUnmatched()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        Assert.NotEmpty(report.UnmatchedTerms);
        Assert.NotEqual(ConfidenceLevel.High, report.OverallConfidence);
    }

    [Fact]
    public void Analyze_ReachesHighConfidenceWhenEveryTermIsMatched()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(
            repo.Root, "Add support for the shortening service");

        Assert.Empty(report.UnmatchedTerms);
        Assert.Equal(ConfidenceLevel.High, report.OverallConfidence);
    }

    [Fact]
    public void Analyze_ExplainsEveryRankedFile()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        Assert.All(report.Items, item => Assert.NotEmpty(item.Reasons));
    }

    [Fact]
    public void Analyze_NamesTheMatchedTypesAndMethods()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        var service = report.Items.Single(item => item.Path.Contains("UrlShorteningService"));

        Assert.Contains("UrlShorteningService", service.MatchedTypes);
        Assert.Contains("CreateShortUrlAsync", service.MatchedMethods);
    }

    [Fact]
    public void Analyze_ReturnsNoItemsAndLowConfidenceForUnrelatedRequirements()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(
            repo.Root, "Migrate the payroll ledger to quarterly accruals");

        Assert.Empty(report.Items);
        Assert.Equal(ConfidenceLevel.Low, report.OverallConfidence);
        Assert.Contains(report.Risks, risk => risk.Contains("No file matched"));
    }

    [Fact]
    public void Analyze_AlwaysReportsAssumptionsRisksAndLimitations()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        Assert.NotEmpty(report.Assumptions);
        Assert.NotEmpty(report.Risks);
        Assert.NotEmpty(report.Limitations);
    }

    [Fact]
    public void Analyze_ThrowsWhenRequirementIsBlank()
    {
        using var repo = BuildShortenerRepository();

        var analyzer = new ImpactAnalyzer();

        Assert.Throws<ArgumentException>(() => analyzer.Analyze(repo.Root, "   "));
    }

    [Fact]
    public void Render_ProducesTheReportSections()
    {
        using var repo = BuildShortenerRepository();

        var report = new ImpactAnalyzer().Analyze(repo.Root, Requirement);

        var text = ImpactReportRenderer.Render(report);

        Assert.Contains("ZipLink Impact Analysis", text);
        Assert.Contains("REQUIREMENT TERMS", text);
        Assert.Contains("LIKELY IMPACTED FILES", text);
        Assert.Contains("ASSUMPTIONS", text);
        Assert.Contains("RISKS", text);
        Assert.Contains("LIMITATIONS", text);
        Assert.Contains("UrlShorteningService", text);
    }
}
