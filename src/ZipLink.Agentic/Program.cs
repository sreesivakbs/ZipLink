using System.Text;

var repoRoot = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

Console.WriteLine("ZipLink Repository Intelligence");
Console.WriteLine($"Repository: {repoRoot}");
Console.WriteLine();

var extensions = new[] { ".cs", ".csproj" };

var files = Directory
    .EnumerateFiles(repoRoot, "*.*", SearchOption.AllDirectories)
    .Where(f => extensions.Contains(Path.GetExtension(f)))
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
    .OrderBy(f => f)
    .ToList();

Console.WriteLine($"Found {files.Count} source/project files.");
Console.WriteLine();

foreach (var file in files)
{
    var relative = Path.GetRelativePath(repoRoot, file);
    Console.WriteLine(relative);
}