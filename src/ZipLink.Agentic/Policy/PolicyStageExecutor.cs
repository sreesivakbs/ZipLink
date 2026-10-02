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
    public Task<StageResult> ExecuteAsync(
        StageContext context,
        CancellationToken cancellationToken)
    {
        var implementation = context.Read<ImplementArtifact>(StagePipeline.Implement);

        // Scan the agent's workspace when there is one; otherwise this repository, so the
        // gate still means something on a run with no implementation stage.
        var target = implementation?.WorkspacePath is { Length: > 0 } workspace
            && Directory.Exists(workspace)
                ? workspace
                : context.RepositoryRoot;

        var report = PolicyScanner.Scan(target);

        var findings = report.Findings
            .Select(finding =>
                $"[{finding.Rule}] {finding.File}:{finding.Line} - {finding.Detail}")
            .ToList();

        var artifact = new PolicyArtifact(
            report.Passed, report.FilesScanned, report.Violations, findings);

        if (report.Passed)
        {
            return Task.FromResult(StageResult.Success(
                $"{report.FilesScanned} file(s) scanned; no policy violations.", artifact));
        }

        return Task.FromResult(new StageResult(
            StageOutcome.Failed,
            $"{report.Violations} policy violation(s) across {report.FilesScanned} file(s): "
            + string.Join("; ", findings.Take(3)),
            artifact));
    }
}
