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
        Assert.Equal(7, result.ShortCode.Length);
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