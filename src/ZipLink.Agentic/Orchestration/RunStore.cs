using System.Text;
using System.Text.Json;

namespace ZipLink.Agentic.Orchestration;

/// <summary>
/// On-disk home for runs: <c>.ziplink/runs/&lt;runId&gt;/</c> holding run.json, an
/// append-only audit.jsonl, and one artifact file per stage.
///
/// State is written after every transition, so approving or resuming a run works across
/// separate process invocations and a killed run leaves a readable record rather than
/// nothing.
/// </summary>
public sealed class RunStore
{
    private const string RunFileName = "run.json";
    private const string AuditFileName = "audit.jsonl";
    private const string ArtifactsDirectoryName = "artifacts";

    private readonly Lock _auditGate = new();

    public RunStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        RootDirectory = Path.Combine(Path.GetFullPath(repositoryRoot), ".ziplink", "runs");
    }

    public string RootDirectory { get; }

    public static string NewRunId()
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
    }

    public string RunDirectory(string runId)
    {
        return Path.Combine(RootDirectory, runId);
    }

    public void Save(RunState state)
    {
        var directory = RunDirectory(state.RunId);

        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, RunFileName),
            JsonSerializer.Serialize(state, JsonDefaults.Options),
            Encoding.UTF8);
    }

    public RunState? Load(string runId)
    {
        var path = Path.Combine(RunDirectory(runId), RunFileName);

        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<RunState>(
            File.ReadAllText(path, Encoding.UTF8), JsonDefaults.Options);
    }

    public IReadOnlyList<string> ListRunIds()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateDirectories(RootDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    public string? LatestRunId()
    {
        return ListRunIds().LastOrDefault();
    }

    /// <summary>
    /// Appends one line to the audit log. Opening in append mode under a lock keeps the
    /// log append-only even while parallel stages are reporting.
    /// </summary>
    public void Append(AuditEvent auditEvent)
    {
        var directory = RunDirectory(auditEvent.RunId);

        Directory.CreateDirectory(directory);

        var line = JsonSerializer.Serialize(auditEvent, JsonDefaults.Compact);

        lock (_auditGate)
        {
            File.AppendAllText(
                Path.Combine(directory, AuditFileName), line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public IReadOnlyList<AuditEvent> ReadAudit(string runId)
    {
        var path = Path.Combine(RunDirectory(runId), AuditFileName);

        if (!File.Exists(path))
        {
            return Array.Empty<AuditEvent>();
        }

        var events = new List<AuditEvent>();

        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parsed = JsonSerializer.Deserialize<AuditEvent>(line, JsonDefaults.Options);

            if (parsed is not null)
            {
                events.Add(parsed);
            }
        }

        return events;
    }

    /// <summary>Writes a stage's output and returns the file name recorded in run.json.</summary>
    public string WriteArtifact(string runId, string stageId, object artifact)
    {
        var directory = Path.Combine(RunDirectory(runId), ArtifactsDirectoryName);

        Directory.CreateDirectory(directory);

        var fileName = $"{stageId}.json";

        File.WriteAllText(
            Path.Combine(directory, fileName),
            JsonSerializer.Serialize(artifact, artifact.GetType(), JsonDefaults.Options),
            Encoding.UTF8);

        return Path.Combine(ArtifactsDirectoryName, fileName);
    }

    /// <summary>
    /// Removes a stage's output. Used by rollback to undo work produced in a tick that
    /// ultimately failed, so a failed run does not leave partial results behind.
    /// </summary>
    public void DeleteArtifact(string runId, string stageId)
    {
        var path = Path.Combine(RunDirectory(runId), ArtifactsDirectoryName, $"{stageId}.json");

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Safe-stop is signalled through a file so that a stop issued from another process
    /// is visible to a run in flight. The engine only acts on it between stages.
    /// </summary>
    public void RequestStop(string runId)
    {
        Directory.CreateDirectory(RunDirectory(runId));

        File.WriteAllText(StopFilePath(runId), DateTime.UtcNow.ToString("O"), Encoding.UTF8);
    }

    public bool IsStopRequested(string runId)
    {
        return File.Exists(StopFilePath(runId));
    }

    public void ClearStopRequest(string runId)
    {
        var path = StopFilePath(runId);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string StopFilePath(string runId)
    {
        return Path.Combine(RunDirectory(runId), "STOP");
    }

    public IReadOnlyDictionary<string, string> ReadArtifacts(string runId)
    {
        var directory = Path.Combine(RunDirectory(runId), ArtifactsDirectoryName);

        if (!Directory.Exists(directory))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            artifacts[Path.GetFileNameWithoutExtension(file)] =
                File.ReadAllText(file, Encoding.UTF8);
        }

        return artifacts;
    }
}
