using Xunit;
using ZipLink.Core.Interfaces;
using ZipLink.Core.Models;
using ZipLink.Core.Services;

namespace ZipLink.Tests;

public class UrlShorteningServiceTests
{
    [Fact]
    public async Task CreateShortUrl_WithValidUrl_CreatesShortUrl()
    {
        var repository = new FakeShortUrlRepository();
        var service = new UrlShorteningService(repository);

        var result = await service.CreateShortUrlAsync(
            "https://www.example.com");

        Assert.NotNull(result);
        Assert.Equal("https://www.example.com", result.OriginalUrl);
        Assert.False(string.IsNullOrWhiteSpace(result.ShortCode));
        Assert.Equal(10, result.ShortCode.Length);
    }

    [Fact]
    public async Task CreateShortUrl_ThenResolve_ReturnsOriginalUrlForTenCharacterCode()
    {
        var repository = new FakeShortUrlRepository();
        var service = new UrlShorteningService(repository);

        var created = await service.CreateShortUrlAsync("https://www.example.com/docs?page=2");

        Assert.Equal(10, created.ShortCode.Length);

        var resolved = await service.ResolveAsync(created.ShortCode);

        Assert.NotNull(resolved);
        Assert.Equal("https://www.example.com/docs?page=2", resolved!.OriginalUrl);
    }

    [Fact]
    public async Task CreateShortUrl_WithEmptyUrl_ThrowsException()
    {
        var repository = new FakeShortUrlRepository();
        var service = new UrlShorteningService(repository);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateShortUrlAsync(""));
    }

    [Fact]
    public async Task CreateShortUrl_WithInvalidUrl_ThrowsException()
    {
        var repository = new FakeShortUrlRepository();
        var service = new UrlShorteningService(repository);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateShortUrlAsync("not-a-url"));
    }

    [Theory]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://10.1.2.3")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://192.168.1.1")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[::ffff:192.168.0.1]/")]
    [InlineData("http://localhost:8080/x")]
    [InlineData("http://LOCALHOST/")]
    [InlineData("http://db.localhost/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://foo.internal/")]
    [InlineData("http://router.home.arpa/")]
    public async Task CreateShortUrl_WithPrivateOrInternalHost_ThrowsException(string url)
    {
        var repository = new FakeShortUrlRepository();
        var service = new UrlShorteningService(repository);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateShortUrlAsync(url));

        Assert.Contains("private or internal", exception.Message);
        Assert.Equal(0, repository.Count);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://8.8.8.8")]
    [InlineData("http://172.32.0.1")]
    [InlineData("http://100.128.0.1")]
    [InlineData("https://docs.example.co.uk/a/b?q=1")]
    public async Task CreateShortUrl_WithPublicHost_CreatesAndIsRetrievable(string url)
    {
        var repository = new FakeShortUrlRepository();
        var service = new UrlShorteningService(repository);

        var created = await service.CreateShortUrlAsync(url);

        Assert.Equal(url, created.OriginalUrl);

        var resolved = await service.ResolveAsync(created.ShortCode);

        Assert.NotNull(resolved);
        Assert.Equal(url, resolved!.OriginalUrl);
        Assert.Equal(1, repository.Count);
    }

    [Fact]
    public async Task ResolveAsync_WithExistingCode_ReturnsUrl()
    {
        var repository = new FakeShortUrlRepository();

        var shortUrl = new ShortUrl
        {
            OriginalUrl = "https://www.example.com",
            ShortCode = "abc1234"
        };

        await repository.AddAsync(shortUrl);

        var service = new UrlShorteningService(repository);

        var result = await service.ResolveAsync("abc1234");

        Assert.NotNull(result);
        Assert.Equal("https://www.example.com", result!.OriginalUrl);
    }

    [Fact]
    public async Task ResolveAsync_WithLegacySevenCharacterCode_StillResolves()
    {
        var repository = new FakeShortUrlRepository();

        await repository.AddAsync(new ShortUrl
        {
            OriginalUrl = "https://legacy.example.com/page",
            ShortCode = "old1234"
        });

        var service = new UrlShorteningService(repository);

        var result = await service.ResolveAsync("old1234");

        Assert.NotNull(result);
        Assert.Equal(7, result!.ShortCode.Length);
        Assert.Equal("https://legacy.example.com/page", result.OriginalUrl);
    }

    [Fact]
    public async Task ResolveAsync_WithUnknownCode_ReturnsNull()
    {
        var repository = new FakeShortUrlRepository();
        var service = new UrlShorteningService(repository);

        var result = await service.ResolveAsync("missing");

        Assert.Null(result);
    }
}

public class FakeShortUrlRepository : IShortUrlRepository
{
    private readonly Dictionary<string, ShortUrl> _data = new();

    public int Count => _data.Count;

    public Task AddAsync(ShortUrl shortUrl)
    {
        _data[shortUrl.ShortCode] = shortUrl;
        return Task.CompletedTask;
    }

    public Task<ShortUrl?> GetByShortCodeAsync(string shortCode)
    {
        _data.TryGetValue(shortCode, out var result);
        return Task.FromResult(result);
    }

    public Task IncrementClickCountAsync(string shortCode)
    {
        if (_data.TryGetValue(shortCode, out var url))
        {
            url.ClickCount++;
        }

        return Task.CompletedTask;
    }
}
