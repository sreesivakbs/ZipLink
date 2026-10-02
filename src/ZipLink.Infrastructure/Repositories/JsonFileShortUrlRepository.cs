using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ZipLink.Core.Interfaces;
using ZipLink.Core.Models;

namespace ZipLink.Infrastructure.Repositories;

/// <summary>
/// Stores short links in a JSON file so they survive a restart.
///
/// Reads are served from an in-memory dictionary loaded once at startup, because a
/// redirect is the hot path and must not touch the disk. Writes update that dictionary
/// and then rewrite the file, so the file is always the full current state rather than a
/// log that needs replaying.
///
/// The file is written to a temporary name and moved into place, so a crash halfway
/// through a save leaves the previous good file rather than a truncated one.
/// </summary>
public sealed class JsonFileShortUrlRepository : IShortUrlRepository
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, ShortUrl> _links =
        new(StringComparer.Ordinal);

    private readonly Lock _writeGate = new();
    private readonly string _path;

    public JsonFileShortUrlRepository(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);

        Load();
    }

    public string FilePath => _path;

    public int Count => _links.Count;

    public Task AddAsync(ShortUrl shortUrl)
    {
        ArgumentNullException.ThrowIfNull(shortUrl);

        _links[shortUrl.ShortCode] = shortUrl;

        Save();

        return Task.CompletedTask;
    }

    public Task<ShortUrl?> GetByShortCodeAsync(string shortCode)
    {
        _links.TryGetValue(shortCode, out var link);

        return Task.FromResult(link);
    }

    public Task IncrementClickCountAsync(string shortCode)
    {
        if (!_links.TryGetValue(shortCode, out var link))
        {
            return Task.CompletedTask;
        }

        // Counting is the only write on the redirect path. The increment happens under
        // the same lock as the save, so two simultaneous clicks cannot lose a count or
        // write a file that disagrees with memory.
        lock (_writeGate)
        {
            link.ClickCount++;

            Persist();
        }

        return Task.CompletedTask;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);

            var stored = JsonSerializer.Deserialize<List<ShortUrl>>(json, Options);

            foreach (var link in stored ?? [])
            {
                if (!string.IsNullOrWhiteSpace(link.ShortCode))
                {
                    _links[link.ShortCode] = link;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt or unreadable file must not stop the service from starting. The
            // bad file is kept alongside so it can be looked at rather than overwritten.
            TryQuarantine();
        }
    }

    private void TryQuarantine()
    {
        try
        {
            File.Move(_path, $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private void Save()
    {
        lock (_writeGate)
        {
            Persist();
        }
    }

    /// <summary>Writes the whole file. Callers must already hold <see cref="_writeGate"/>.</summary>
    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var snapshot = _links.Values
            .OrderBy(link => link.CreatedAtUtc)
            .ToList();

        var temporary = _path + ".tmp";

        File.WriteAllText(
            temporary, JsonSerializer.Serialize(snapshot, Options), Encoding.UTF8);

        // Move is atomic on the same volume, so a reader never sees a partial file.
        File.Move(temporary, _path, overwrite: true);
    }
}
