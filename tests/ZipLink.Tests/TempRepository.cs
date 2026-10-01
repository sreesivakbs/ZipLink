namespace ZipLink.Tests;

/// <summary>
/// A throwaway directory tree used to exercise the repository-scanning code against
/// known content instead of against ZipLink's own evolving source.
/// </summary>
public sealed class TempRepository : IDisposable
{
    public TempRepository()
    {
        Root = Path.Combine(
            Path.GetTempPath(), "ziplink-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public TempRepository AddFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(Root, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
