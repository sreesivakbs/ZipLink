using System.Collections.Concurrent;
using ZipLink.Core.Interfaces;
using ZipLink.Core.Models;

namespace ZipLink.Infrastructure.Repositories;

public class InMemoryShortUrlRepository : IShortUrlRepository
{
    private readonly ConcurrentDictionary<string, ShortUrl> _data = new();

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
