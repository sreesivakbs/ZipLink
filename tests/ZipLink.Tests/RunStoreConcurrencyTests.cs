using ZipLink.Agentic.Orchestration;

namespace ZipLink.Tests;

/// <summary>
/// Reproduces the failure the Studio page exposed: the engine saves run state while the
/// browser polls it, and on Windows an unshared read locks out the writer, so the save
/// died with "the process cannot access the file because it is being used by another
/// process". The CLI never hit it because nothing read while the engine wrote.
/// </summary>
public class RunStoreConcurrencyTests
{
    private static RunState NewState(string runId)
    {
        return new RunState
        {
            RunId = runId,
            Requirement = "concurrent access",
            Stages = [new StageRecord { Id = "only" }]
        };
    }

    [Fact]
    public async Task SavingWhileAnotherReaderPollsDoesNotThrow()
    {
        using var repo = new TempRepository();

        var store = new RunStore(repo.Root);
        var state = NewState(RunStore.NewRunId());

        store.Save(state);

        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Stands in for the page polling every second or two, only far harder.
        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                store.Load(state.RunId);
            }
        });

        // And the engine persisting after every transition.
        for (var i = 0; i < 300; i++)
        {
            state.Stages[0].Attempts = i;
            store.Save(state);
        }

        done.Cancel();
        await reader;

        Assert.Equal(299, store.Load(state.RunId)!.Stages[0].Attempts);
    }

    [Fact]
    public async Task AReaderNeverSeesAPartiallyWrittenFile()
    {
        using var repo = new TempRepository();

        var store = new RunStore(repo.Root);
        var state = NewState(RunStore.NewRunId());

        // A summary long enough that a non-atomic write would be caught mid-flight.
        state.Stages[0].Summary = new string('x', 20_000);
        store.Save(state);

        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var reads = 0;

        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                // A torn read would fail to deserialize and throw.
                var loaded = store.Load(state.RunId);

                Assert.NotNull(loaded);
                Assert.Equal(20_000, loaded!.Stages[0].Summary!.Length);

                reads++;
            }
        });

        for (var i = 0; i < 200; i++)
        {
            state.Stages[0].Attempts = i;
            store.Save(state);
        }

        done.Cancel();
        await reader;

        Assert.True(reads > 0, "the reader never ran, so the test proved nothing");
    }

    [Fact]
    public async Task AppendingAuditWhileItIsBeingReadDoesNotThrow()
    {
        using var repo = new TempRepository();

        var store = new RunStore(repo.Root);
        var runId = RunStore.NewRunId();

        store.Append(new AuditEvent(DateTime.UtcNow, runId, null, "RunStarted", "system", null));

        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                store.ReadAudit(runId);
            }
        });

        for (var i = 0; i < 200; i++)
        {
            store.Append(new AuditEvent(
                DateTime.UtcNow, runId, "only", "StageStarted", "system", $"event {i}"));
        }

        done.Cancel();
        await reader;

        // The first event plus every appended one, with nothing lost or duplicated.
        Assert.Equal(201, store.ReadAudit(runId).Count);
    }

    [Fact]
    public async Task ArtifactsCanBeReadWhileTheyAreBeingWritten()
    {
        using var repo = new TempRepository();

        var store = new RunStore(repo.Root);
        var runId = RunStore.NewRunId();

        store.WriteArtifact(runId, "design", new { Summary = "first" });

        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                store.ReadArtifacts(runId);
            }
        });

        for (var i = 0; i < 200; i++)
        {
            store.WriteArtifact(runId, "design", new { Summary = $"revision {i}" });
        }

        done.Cancel();
        await reader;

        Assert.Contains("revision 199", store.ReadArtifacts(runId)["design"]);
    }

    [Fact]
    public async Task TwoStoresOverTheSameFolderDoNotLockEachOtherOut()
    {
        using var repo = new TempRepository();

        // Separate instances stand in for separate processes - Studio and the CLI both
        // pointed at the same repository.
        var engine = new RunStore(repo.Root);
        var viewer = new RunStore(repo.Root);

        var state = NewState(RunStore.NewRunId());

        engine.Save(state);

        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                viewer.Load(state.RunId);
                viewer.ReadAudit(state.RunId);
            }
        });

        for (var i = 0; i < 200; i++)
        {
            state.Stages[0].Attempts = i;
            engine.Save(state);
            engine.Append(new AuditEvent(
                DateTime.UtcNow, state.RunId, "only", "StageStarted", "system", null));
        }

        done.Cancel();
        await reader;

        Assert.Equal(199, viewer.Load(state.RunId)!.Stages[0].Attempts);
    }

    [Fact]
    public void NoTemporaryFilesAreLeftBehind()
    {
        using var repo = new TempRepository();

        var store = new RunStore(repo.Root);
        var state = NewState(RunStore.NewRunId());

        store.Save(state);
        store.WriteArtifact(state.RunId, "design", new { Summary = "x" });

        Assert.Empty(Directory.GetFiles(
            store.RunDirectory(state.RunId), "*.tmp", SearchOption.AllDirectories));
    }
}
