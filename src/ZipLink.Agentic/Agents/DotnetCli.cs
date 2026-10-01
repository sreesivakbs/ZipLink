using System.Diagnostics;
using System.Text;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Agentic.Agents;

public sealed record CommandResult(bool Succeeded, int ExitCode, string Output);

/// <summary>
/// Runs an external command in a given directory and captures its combined output.
/// Used to drive git and dotnet inside an agent workspace, where the agent's work has to
/// be judged by tools rather than by its own account of itself.
/// </summary>
public static class DotnetCli
{
    public static Task<CommandResult> DotnetAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var dotnet = DotnetTestRunner.ResolveDotnetPath();

        return dotnet is null
            ? Task.FromResult(new CommandResult(
                false,
                -1,
                "Could not locate the dotnet host. Set DOTNET_HOST_PATH or DOTNET_ROOT."))
            : RunAsync(dotnet, workingDirectory, arguments, cancellationToken, timeout);
    }

    public static Task<CommandResult> GitAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        return RunAsync("git", workingDirectory, arguments, cancellationToken, timeout);
    }

    private static async Task<CommandResult> RunAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        var output = new StringBuilder();

        process.OutputDataReceived += (_, args) => Append(output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, args.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandResult(false, -1, $"Could not start '{fileName}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);

            return new CommandResult(false, -1, $"'{fileName}' timed out.\n{output}");
        }

        return new CommandResult(process.ExitCode == 0, process.ExitCode, output.ToString());
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
}
