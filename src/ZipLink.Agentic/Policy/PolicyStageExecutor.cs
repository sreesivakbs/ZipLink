using ZipLink.Agentic.Agents;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Agentic.Policy;

public sealed record PolicyArtifact(
    bool Passed,
    int FilesScanned,
    int Violations,
    IReadOnlyList<string> Findings);

/// <summary>
/// The policy gate: a deterministic security and change-control check over whatever the
/// implementation stage produced.
///
/// It runs in parallel with tests and docs and, like them, must succeed before release.
/// A violation fails the stage outright - there is no "approve anyway" path here, because
/// a guardrail an agent can argue past is not a guardrail.
/// </summary>
public sealed class PolicyStageExecutor : IStageExecutor
{
    public async Task<StageResult> ExecuteAsync(
        StageContext context,
        CancellationToken cancellationToken)
    {
        var implementation = context.Read<ImplementArtifact>(StagePipeline.Implement);

        var workspacePath = implementation?.WorkspacePath;

        var hasWorkspace = workspacePath is { Length: > 0 } && Directory.Exists(workspacePath);

        // Scan the agent's workspace when there is one; otherwise this repository, so the
        // gate still means something on a run with no implementation stage.
        var target = hasWorkspace ? workspacePath! : context.RepositoryRoot;

        var report = PolicyScanner.Scan(target);

        var allFindings = report.Findings.ToList();

        // Coverage can only shrink relative to something, so this rule needs the diff and
        // only applies when an agent actually changed code.
        if (hasWorkspace)
        {
            var diff = await DotnetCli.GitAsync(
                target, ["diff", "--cached", "--", "tests/"], cancellationToken);

            if (TestCoverageGuard.Inspect(diff.Output) is { } regression)
            {
                allFindings.Add(regression);
            }
        }

        report = report with { Findings = allFindings };

        var findings = report.Findings
            .Select(finding =>
                $"[{finding.Rule}] {finding.File}:{finding.Line} - {finding.Detail}")
            .ToList();

        var artifact = new PolicyArtifact(
            report.Passed, report.FilesScanned, report.Violations, findings);

        if (report.Passed)
        {
            return StageResult.Success(
                $"{report.FilesScanned} file(s) scanned; no policy violations.", artifact);
        }

        return new StageResult(
            StageOutcome.Failed,
            $"{report.Violations} policy violation(s) across {report.FilesScanned} file(s): "
            + string.Join("; ", findings.Take(3)),
            artifact);
    }
}
