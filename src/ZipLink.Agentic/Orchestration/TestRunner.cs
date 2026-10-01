using System.Diagnostics;
using System.Text;

namespace ZipLink.Agentic.Orchestration;

public sealed record TestRunResult(bool Succeeded, string Summary, string Output);

public interface ITestRunner
{
    Task<TestRunResult> RunAsync(string repositoryRoot, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the real test suite so a deterministic check decides pass/fail, rather than an
/// agent asserting that its work is correct.
///
/// Uses <c>--no-build</c> deliberately: the orchestrator is itself running from the
/// solution's output directory, so a rebuild would try to overwrite the executable that
/// is currently running. The caller is expected to have built already.
/// </summary>
public sealed class DotnetTestRunner : ITestRunner
{
    private readonly TimeSpan _timeout;

    public DotnetTestRunner(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<TestRunResult> RunAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var target = FindSolution(repositoryRoot);

        if (target is null)
        {
            return new TestRunResult(
                false, "No .slnx or .sln file found to test.", string.Empty);
        }

        var dotnet = ResolveDotnetPath();

        if (dotnet is null)
        {
            return new TestRunResult(
                false,
                "Could not locate the dotnet host. Set DOTNET_HOST_PATH or DOTNET_ROOT, "
                + "or put dotnet on PATH.",
                string.Empty);
        }

        var startInfo = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--nologo");

        using var process = new Process { StartInfo = startInfo };

        var output = new StringBuilder();

        process.OutputDataReceived += (_, args) => Append(output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);

            return new TestRunResult(
                false, $"Test run exceeded {_timeout.TotalMinutes:0} minutes.", output.ToString());
        }

        var text = output.ToString();

        return new TestRunResult(process.ExitCode == 0, Summarize(text, process.ExitCode), text);
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            builder.AppendLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// dotnet is not guaranteed to be on PATH, and DOTNET_HOST_PATH is only set in some
    /// launch contexts, so fall back to the documented install locations before trusting
    /// PATH. Returns null when no host can be found, which the caller reports as a
    /// readable failure instead of a Win32Exception.
    /// </summary>
    public static string? ResolveDotnetPath()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

        if (!string.IsNullOrWhiteSpace(host) && File.Exists(host))
        {
            return host;
        }

        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        foreach (var directory in CandidateDirectories())
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, executable);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Last resort: let the OS resolve it from PATH.
        return OnPath(executable) ? executable : null;
    }

    private static IEnumerable<string?> CandidateDirectories()
    {
        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT");

        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetEnvironmentVariable("ProgramW6432");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "dotnet");
        }
        else
        {
            yield return "/usr/share/dotnet";
            yield return "/usr/local/share/dotnet";
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        }
    }

    private static bool OnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory.Trim(), executable)));
    }

    private static string? FindSolution(string repositoryRoot)
    {
        return Directory
            .EnumerateFiles(repositoryRoot, "*.slnx", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(repositoryRoot, "*.sln", SearchOption.TopDirectoryOnly))
            .FirstOrDefault();
    }

    private static string Summarize(string output, int exitCode)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate =>
                candidate.StartsWith("Passed!", StringComparison.Ordinal)
                || candidate.StartsWith("Failed!", StringComparison.Ordinal));

        return line ?? $"dotnet test exited with code {exitCode}.";
    }
}
