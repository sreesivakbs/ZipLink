using ZipLink.Agentic.Orchestration;
using ZipLink.Agentic.Policy;

namespace ZipLink.Tests;

/// <summary>
/// The policy gate. Deterministic by design: no model is consulted, so an agent cannot
/// talk its way past it.
/// </summary>
public class PolicyScannerTests
{
    [Fact]
    public void CleanSourcePasses()
    {
        using var repo = new TempRepository()
            .AddFile("src/App/Program.cs", "namespace App; public class Program { }");

        var report = PolicyScanner.Scan(repo.Root);

        Assert.True(report.Passed);
        Assert.Empty(report.Findings);
    }

    [Theory]
    [InlineData("var key = \"sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\";")] // policy:allow-secret
    [InlineData("var aws = \"AKIAIOSFODNN7EXAMPLE\";")] // policy:allow-secret
    [InlineData("var token = \"ghp_abcdefghijklmnopqrstuvwxyz0123456789\";")] // policy:allow-secret
    [InlineData("const string Conn = \"Server=db;User=sa;Password=SuperSecret123;\";")] // policy:allow-secret
    public void CommittedSecretsAreViolations(string line)
    {
        using var repo = new TempRepository().AddFile("src/App/Leak.cs", line);

        var report = PolicyScanner.Scan(repo.Root);

        Assert.False(report.Passed);
        Assert.Contains(report.Findings, finding => finding.Rule == PolicyScanner.SecretsRule);
    }

    [Fact]
    public void APrivateKeyBlockIsAViolation()
    {
        using var repo = new TempRepository()
            .AddFile("src/App/key.config", "-----BEGIN RSA PRIVATE KEY-----"); // policy:allow-secret

        Assert.False(PolicyScanner.Scan(repo.Root).Passed);
    }

    [Fact]
    public void AFindingNamesTheFileAndLine()
    {
        using var repo = new TempRepository().AddFile(
            "src/App/Leak.cs",
            "// fine\n// also fine\nvar k = \"sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ012345\";"); // policy:allow-secret

        var finding = Assert.Single(PolicyScanner.Scan(repo.Root).Findings);

        Assert.Equal("src/App/Leak.cs", finding.File);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void AnUnapprovedPackageIsAViolation()
    {
        using var repo = new TempRepository().AddFile(
            "src/App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        var report = PolicyScanner.Scan(repo.Root);

        Assert.False(report.Passed);
        Assert.Contains(
            report.Findings,
            finding => finding.Rule == PolicyScanner.DependenciesRule
                && finding.Detail.Contains("Newtonsoft.Json"));
    }

    [Fact]
    public void ApprovedPackagesPass()
    {
        using var repo = new TempRepository().AddFile(
            "src/App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="Anthropic" Version="12.53.0" />
                <!-- Approved so agents can write HTTP-level API tests, which the
                     testing notes record as this repository's largest gap. -->
                <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(PolicyScanner.Scan(repo.Root).Passed);
    }

    [Theory]
    [InlineData("bin/Debug/Leak.cs")]
    [InlineData("obj/Debug/Leak.cs")]
    [InlineData("docs/SETUP.md.cs")]
    [InlineData(".ziplink/runs/x/Leak.cs")]
    public void BuildOutputRunEvidenceAndDocsAreNotScanned(string path)
    {
        using var repo = new TempRepository().AddFile(
            path, "var k = \"sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ012345\";"); // policy:allow-secret

        Assert.True(PolicyScanner.Scan(repo.Root).Passed);
    }

    [Fact]
    public void ThisRepositoryPassesItsOwnPolicyGate()
    {
        // The gate has to hold against real source, not just fixtures - otherwise it
        // would be quietly disabled the first time it fired.
        var root = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var report = PolicyScanner.Scan(root);

        Assert.True(
            report.Passed,
            "ZipLink itself violates its policy gate: "
            + string.Join("; ", report.Findings.Select(f => $"{f.File}:{f.Line} {f.Detail}")));
    }

    // ---------- as a stage ----------

    private static StageContext Context(string root)
    {
        return new StageContext
        {
            RunId = "test-run",
            Requirement = "anything",
            RepositoryRoot = root,
            Artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    [Fact]
    public async Task TheStageSucceedsOnCleanSource()
    {
        using var repo = new TempRepository()
            .AddFile("src/App/Program.cs", "namespace App; public class Program { }");

        var result = await new PolicyStageExecutor()
            .ExecuteAsync(Context(repo.Root), CancellationToken.None);

        Assert.Equal(StageOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task TheStageFailsOnAViolationAndIsNotApprovable()
    {
        using var repo = new TempRepository().AddFile(
            "src/App/Leak.cs", "var k = \"sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ012345\";"); // policy:allow-secret

        var result = await new PolicyStageExecutor()
            .ExecuteAsync(Context(repo.Root), CancellationToken.None);

        // Failed, not Blocked: a security guardrail must not offer an "approve anyway".
        Assert.Equal(StageOutcome.Failed, result.Outcome);
        Assert.Contains("policy violation", result.Summary);
    }

    [Fact]
    public void ThePolicyStageIsInTheDefaultPipelineAndGatesRelease()
    {
        var pipeline = StagePipeline.CreateDefault(new NeverCalledTestRunner());

        var policy = pipeline.Stages.Single(stage => stage.Id == StagePipeline.Policy);
        var release = pipeline.Stages.Single(stage => stage.Id == StagePipeline.Release);

        Assert.Equal([StagePipeline.Implement], policy.DependsOn);
        Assert.Contains(StagePipeline.Policy, release.DependsOn);
        Assert.Equal(3, release.DependsOn.Count);
    }

    private sealed class NeverCalledTestRunner : ITestRunner
    {
        public Task<TestRunResult> RunAsync(string repositoryRoot, CancellationToken token)
        {
            throw new InvalidOperationException("The test stage must not run here.");
        }
    }
}
