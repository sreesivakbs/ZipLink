using System.Text;
using System.Text.RegularExpressions;

namespace ZipLink.Agentic.Repository;

public sealed record FileAnalysis
{
    public required string Path { get; init; }

    public string? Namespace { get; init; }

    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>();
}

public sealed record RepositoryAnalysis
{
    public required string RootPath { get; init; }

    public IReadOnlyList<FileAnalysis> Files { get; init; } = Array.Empty<FileAnalysis>();

    public int FileCount => Files.Count;

    public int TypeCount => Files.Sum(file => file.Types.Count);

    public int MethodCount => Files.Sum(file => file.Methods.Count);
}

public sealed class RepositoryAnalyzer
{
    private static readonly string[] ExcludedDirectories = ["bin", "obj"];

    private static readonly char[] PathSeparators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly Regex NamespacePattern = new(
        @"^[ \t]*namespace[ \t]+(?<name>[A-Za-z_][\w\.]*)[ \t]*[;{]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TypePattern = new(
        @"\b(?:class|interface|record(?:[ \t]+(?:struct|class))?)[ \t]+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex MethodPattern = new(
        @"^[ \t]*(?:\[[^\]\r\n]*\][ \t]*)*"
        + @"(?:(?:public|private|protected|internal|static|virtual|override|abstract"
        + @"|sealed|async|extern|unsafe|new|partial|readonly|required)[ \t]+)*"
        + @"(?<ret>[A-Za-z_][\w\.]*(?:<[^<>()\r\n]*>)?(?:\[[ \t,]*\])?\??)"
        + @"[ \t]+(?<name>[A-Za-z_]\w*)[ \t]*(?:<[^<>()\r\n]*>)?[ \t]*\(",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // A method declaration's return type is a real type. Anything in this set means
    // the pattern latched onto a modifier, a type declaration or a statement instead.
    private static readonly HashSet<string> NonReturnTypeTokens = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "virtual", "override",
        "abstract", "sealed", "async", "extern", "unsafe", "new", "partial", "readonly",
        "required", "const", "volatile", "event", "operator", "implicit", "explicit",
        "class", "interface", "record", "struct", "enum", "delegate", "namespace",
        "return", "throw", "await", "using", "if", "else", "for", "foreach", "while",
        "do", "switch", "case", "catch", "finally", "try", "lock", "fixed", "checked",
        "unchecked", "goto", "yield", "var", "in", "is", "as", "typeof", "nameof",
        "sizeof", "when", "where", "get", "set", "init", "base", "this", "params",
        "ref", "out", "default"
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "catch",
        "finally", "try", "lock", "fixed", "checked", "unchecked", "return", "throw",
        "new", "typeof", "nameof", "sizeof", "when", "using", "in", "is", "as",
        "await", "yield", "goto", "base", "this", "default"
    };

    public RepositoryAnalysis Analyze(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException(
                "Repository root path is required", nameof(rootPath));
        }

        var root = Path.GetFullPath(rootPath);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository root not found: {root}");
        }

        var files = new List<FileAnalysis>();

        foreach (var path in EnumerateSourceFiles(root))
        {
            var file = AnalyzeFile(root, path);

            if (file is not null)
            {
                files.Add(file);
            }
        }

        return new RepositoryAnalysis
        {
            RootPath = root,
            Files = files
        };
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsExcluded(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(string root, string path)
    {
        var segments = Path
            .GetRelativePath(root, path)
            .Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);

        // The last segment is the file name, never a directory.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (ExcludedDirectories.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static FileAnalysis? AnalyzeFile(string root, string path)
    {
        string source;

        try
        {
            source = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var code = StripCommentsAndLiterals(source);

        var namespaceMatch = NamespacePattern.Match(code);

        return new FileAnalysis
        {
            Path = Path.GetRelativePath(root, path),
            Namespace = namespaceMatch.Success
                ? namespaceMatch.Groups["name"].Value
                : null,
            Types = DistinctInOrder(
                TypePattern
                    .Matches(code)
                    .Select(match => match.Groups["name"].Value)),
            Methods = DistinctInOrder(
                MethodPattern
                    .Matches(code)
                    .Where(match => !NonReturnTypeTokens.Contains(match.Groups["ret"].Value))
                    .Where(match => !ReservedNames.Contains(match.Groups["name"].Value))
                    .Select(match => match.Groups["name"].Value))
        };
    }

    private static IReadOnlyList<string> DistinctInOrder(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var value in values)
        {
            if (seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    // Comments and literals are blanked out so their contents cannot be mistaken for
    // declarations. Newlines are preserved so the line-anchored patterns still line up.
    private static string StripCommentsAndLiterals(string source)
    {
        var builder = new StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (current == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (current == '/' && next == '*')
            {
                i += 2;

                while (i < source.Length
                    && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    if (source[i] == '\n')
                    {
                        builder.Append('\n');
                    }

                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                continue;
            }

            // Raw string literal: an opening fence of three or more quotes, closed by a
            // run of at least the same length. Without this, fixture code embedded in
            // test files is scanned as if it were real source.
            if (current == '"' && next == '"'
                && i + 2 < source.Length && source[i + 2] == '"')
            {
                var fence = 0;

                while (i + fence < source.Length && source[i + fence] == '"')
                {
                    fence++;
                }

                i += fence;
                builder.Append("\"\"");

                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        var run = 0;

                        while (i + run < source.Length && source[i + run] == '"')
                        {
                            run++;
                        }

                        i += run;

                        if (run >= fence)
                        {
                            break;
                        }

                        continue;
                    }

                    if (source[i] == '\n')
                    {
                        builder.Append('\n');
                    }

                    i++;
                }

                continue;
            }

            if (current == '@' && next == '"')
            {
                builder.Append("\"\"");
                i += 2;

                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    if (source[i] == '\n')
                    {
                        builder.Append('\n');
                    }

                    i++;
                }

                continue;
            }

            if (current == '"' || current == '\'')
            {
                builder.Append(current).Append(current);
                i++;

                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (source[i] == current)
                    {
                        i++;
                        break;
                    }

                    if (source[i] == '\n')
                    {
                        break;
                    }

                    i++;
                }

                continue;
            }

            builder.Append(current);
            i++;
        }

        return builder.ToString();
    }
}
