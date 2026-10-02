using ZipLink.Core.Models;
using ZipLink.Infrastructure.Repositories;

namespace ZipLink.Tests;

/// <summary>
/// The file-backed link store. The point of it is surviving a restart, so most of these
/// tests throw the repository away and build a new one over the same file.
/// </summary>
public class JsonFileShortUrlRepositoryTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public JsonFileShortUrlRepositoryTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "ziplink-store-" + Guid.NewGuid().ToString("N"));

        _path = Path.Combine(_directory, "links.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private JsonFileShortUrlRepository Open() => new(_path);

    private static ShortUrl Link(string code, string url = "https://example.com/x")
    {
        return new ShortUrl { ShortCode = code, OriginalUrl = url };
    }

    [Fact]
    public async Task AStoredLinkIsReadBack()
    {
        var repository = Open();

        await repository.AddAsync(Link("abc1234"));

        var found = await repository.GetByShortCodeAsync("abc1234");

        Assert.NotNull(found);
        Assert.Equal("https://example.com/x", found!.OriginalUrl);
    }

    [Fact]
    public async Task LinksSurviveARestart()
    {
        var first = Open();

        await first.AddAsync(Link("keep123", "https://example.com/keep"));

        // A completely separate instance over the same file - the restart case.
        var second = Open();

        var found = await second.GetByShortCodeAsync("keep123");

        Assert.NotNull(found);
        Assert.Equal("https://example.com/keep", found!.OriginalUrl);
    }

    [Fact]
    public async Task ClickCountsSurviveARestart()
    {
        var first = Open();

        await first.AddAsync(Link("clicks1"));
        await first.IncrementClickCountAsync("clicks1");
        await first.IncrementClickCountAsync("clicks1");
        await first.IncrementClickCountAsync("clicks1");

        var reopened = await Open().GetByShortCodeAsync("clicks1");

        Assert.Equal(3, reopened!.ClickCount);
    }

    [Fact]
    public async Task CreationTimestampSurvivesARestart()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var first = Open();

        await first.AddAsync(new ShortUrl
        {
            ShortCode = "stamped",
            OriginalUrl = "https://example.com",
            CreatedAtUtc = created
        });

        var reopened = await Open().GetByShortCodeAsync("stamped");

        Assert.Equal(created, reopened!.CreatedAtUtc);
    }

    [Fact]
    public async Task AnUnknownCodeReturnsNull()
    {
        Assert.Null(await Open().GetByShortCodeAsync("nothere"));
    }

    [Fact]
    public async Task IncrementingAnUnknownCodeIsHarmless()
    {
        var repository = Open();

        await repository.IncrementClickCountAsync("nothere");

        Assert.Equal(0, repository.Count);
    }

    [Fact]
    public void AMissingFileStartsEmptyRatherThanThrowing()
    {
        Assert.Equal(0, Open().Count);
    }

    [Fact]
    public async Task ACorruptFileDoesNotStopTheServiceStarting()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, "{ this is not json");

        var repository = Open();

        Assert.Equal(0, repository.Count);

        // The bad file is kept for inspection rather than silently overwritten.
        Assert.NotEmpty(Directory.GetFiles(_directory, "*.corrupt-*"));

        // And the store still works afterwards.
        await repository.AddAsync(Link("after12"));
        Assert.NotNull(await Open().GetByShortCodeAsync("after12"));
    }

    [Fact]
    public async Task ConcurrentClicksAreAllCounted()
    {
        var repository = Open();

        await repository.AddAsync(Link("busy123"));

        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => repository.IncrementClickCountAsync("busy123")));

        Assert.Equal(50, (await repository.GetByShortCodeAsync("busy123"))!.ClickCount);
        Assert.Equal(50, (await Open().GetByShortCodeAsync("busy123"))!.ClickCount);
    }

    [Fact]
    public async Task ConcurrentWritesDoNotCorruptTheFile()
    {
        var repository = Open();

        await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => repository.AddAsync(Link($"code{i:D3}"))));

        // Re-reading proves the file is valid JSON and complete.
        Assert.Equal(40, Open().Count);
    }

    [Fact]
    public async Task NoTemporaryFileIsLeftBehind()
    {
        var repository = Open();

        await repository.AddAsync(Link("tidy123"));

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
}
