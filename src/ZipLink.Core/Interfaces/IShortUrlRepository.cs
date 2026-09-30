using ZipLink.Core.Models;

namespace ZipLink.Core.Interfaces;

public interface IShortUrlRepository
{
    Task AddAsync(ShortUrl shortUrl);
    Task<ShortUrl?> GetByShortCodeAsync(string shortCode);
    Task IncrementClickCountAsync(string shortCode);
}
