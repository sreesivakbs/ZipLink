using System.Collections.Concurrent;
using ZipLink.Agentic.Orchestration;

namespace ZipLink.Studio;

/// <summary>
/// Runs orchestration work in the background so an HTTP request never waits on it.
///
/// A run takes minutes - the design agent alone is around forty seconds - so starting a
/// run or approving a gate returns immediately and the browser polls the run state, which
/// the engine already persists after every transition.
/// </summary>
public sealed class RunHost
{
    private readonly ConcurrentDictionary<string, Task> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.Ordinal);

    /// <summary>True while work for this run is still in flight.</summary>
    public bool IsBusy(string runId)
    {
        return _active.TryGetValue(runId, out var task) && !task.IsCompleted;
    }

    public string? LastError(string runId)
    {
        return _errors.TryGetValue(runId, out var error) ? error : null;
    }

    /// <summary>
    /// Starts work for a run unless something is already running for it. Returns false
    /// when a second request arrives for a run that is already moving, which stops a
    /// double-click from launching two pipelines over the same state.
    /// </summary>
    public bool TryLaunch(string runId, Func<CancellationToken, Task> work)
    {
        if (IsBusy(runId))
        {
            return false;
        }

        _errors.TryRemove(runId, out _);

        _active[runId] = Task.Run(async () =>
        {
            try
            {
                await work(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The engine records stage failures itself; this catches anything that
                // escapes it, so a background crash surfaces in the UI rather than
                // leaving the page polling a run that will never move again.
                _errors[runId] = $"{ex.GetType().Name}: {ex.Message}";
            }
        });

        return true;
    }
}
