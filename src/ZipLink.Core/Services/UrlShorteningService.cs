using ZipLink.Core.Interfaces;
using ZipLink.Core.Models;

namespace ZipLink.Core.Services;

public class UrlShorteningService
{
    private readonly IShortUrlRepository _repository;

    public UrlShorteningService(IShortUrlRepository repository)
    {
        _repository = repository;
    }

    public async Task<ShortUrl> CreateShortUrlAsync(string originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl))
        {
            throw new ArgumentException("URL is required");
        }

        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Invalid URL");
        }

        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            throw new ArgumentException("Only HTTP/HTTPS URLs allowed");
        }

        var shortUrl = new ShortUrl
        {
            OriginalUrl = originalUrl,
            ShortCode = GenerateCode()
        };

        await _repository.AddAsync(shortUrl);

        return shortUrl;
    }

    public async Task<ShortUrl?> GetAsync(string code)
    {
        return await _repository.GetByShortCodeAsync(code);
    }

    public async Task<ShortUrl?> ResolveAsync(string code)
    {
        var url = await _repository.GetByShortCodeAsync(code);

        if (url == null)
            return null;

        await _repository.IncrementClickCountAsync(code);

        return url;
    }

    private static string GenerateCode()
    {
        return Guid.NewGuid()
            .ToString("N")
            .Substring(0, 7);
    }
}