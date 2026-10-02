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

    private const int IoAttempts = 12;

    private static readonly TimeSpan IoBackoff = TimeSpan.FromMilliseconds(25);

    private readonly Lock _auditGate = new();

    // ---- shared file access -------------------------------------------------
    //
    // Run state is written by the engine while something else - the Studio page polling
    // every second or two, or a second CLI invocation - is reading it. The default
    // File.ReadAllText/WriteAllText helpers open without sharing, so a reader locks out
    // the writer and the save fails with "the process cannot access the file".
    //
    // Every access therefore opens with FileShare.ReadWrite | Delete, writes go through a
    // temporary file that is moved into place so a reader never sees half a document, and
    // anything that still collides is retried briefly rather than failing the run.

    private static string ReadText(string path)
    {
        return Retry(() =>
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream, Encoding.UTF8);

            return reader.ReadToEnd();
        });
    }

    private static void WriteTextAtomic(string path, string contents)
    {
        var temporary = path + ".tmp";

        Retry<object?>(() =>
        {
            using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete))
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                writer.Write(contents);
            }

            // Move is atomic on one volume: a reader sees either the old file or the new
            // one, never a partially written document.
            File.Move(temporary, path, overwrite: true);

            return null;
        });
    }

    private static void AppendText(string path, string contents)
    {
        Retry<object?>(() =>
        {
            using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            using var writer = new StreamWriter(stream, Encoding.UTF8);

            writer.Write(contents);

            return null;
        });
    }

    /// <summary>
    /// Briefly retries an IO operation. Sharing flags remove most contention, but a file
    /// being replaced at the exact moment another process opens it can still fail, and a
    /// run should not die because of a timing coincidence.
    /// </summary>
    private static T Retry<T>(Func<T> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (attempt < IoAttempts)
            {
                Thread.Sleep(IoBackoff);
            }
            catch (UnauthorizedAccessException) when (attempt < IoAttempts)
            {
                Thread.Sleep(IoBackoff);
            }
        }
    }

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

        WriteTextAtomic(
            Path.Combine(directory, RunFileName),
            JsonSerializer.Serialize(state, JsonDefaults.Options));
    }

    public RunState? Load(string runId)
    {
        var path = Path.Combine(RunDirectory(runId), RunFileName);

        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<RunState>(ReadText(path), JsonDefaults.Options);
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
            AppendText(
                Path.Combine(directory, AuditFileName), line + Environment.NewLine);
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

        foreach (var line in ReadText(path).Split('\n'))
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

        WriteTextAtomic(
            Path.Combine(directory, fileName),
            JsonSerializer.Serialize(artifact, artifact.GetType(), JsonDefaults.Options));

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

        WriteTextAtomic(StopFilePath(runId), DateTime.UtcNow.ToString("O"));
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
            artifacts[Path.GetFileNameWithoutExtension(file)] = ReadText(file);
        }

        return artifacts;
    }
}
