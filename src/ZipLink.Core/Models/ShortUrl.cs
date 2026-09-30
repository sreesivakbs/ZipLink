namespace ZipLink.Core.Models;

public class ShortUrl
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string OriginalUrl { get; set; } = string.Empty;

    public string ShortCode { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public int ClickCount { get; set; }
}
