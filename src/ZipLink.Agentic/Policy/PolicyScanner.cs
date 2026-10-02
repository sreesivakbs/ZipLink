using System.Text.RegularExpressions;

namespace ZipLink.Agentic.Policy;

public enum PolicySeverity
{
    Warning,
    Violation
}

public sealed record PolicyFinding(
    string Rule,
    PolicySeverity Severity,
    string File,
    int Line,
    string Detail);

public sealed record PolicyReport(
    int FilesScanned,
    IReadOnlyList<PolicyFinding> Findings)
{
    public bool Passed => Findings.All(finding => finding.Severity != PolicySeverity.Violation);

    public int Violations =>
        Findings.Count(finding => finding.Severity == PolicySeverity.Violation);
}

/// <summary>
/// Deterministic policy guardrails run over a workspace after code is written.
///
/// Nothing here asks a model anything. A policy gate whose verdict came from an LLM would
/// be worthless: the point is that an agent cannot talk its way past it.
///
/// Two rules, both from the project brief's policy gate: no secrets, and no dependency a
/// human has not approved.
/// </summary>
public static class PolicyScanner
{
    public const string SecretsRule = "no-secrets";
    public const string DependenciesRule = "no-unapproved-dependencies";

    /// <summary>
    /// Packages a human has approved. A NuGet addition is a change-control decision, so
    /// an agent adding one to a csproj is a violation rather than a convenience.
    /// </summary>
    private static readonly HashSet<string> ApprovedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Anthropic",
        "Microsoft.AspNetCore.Mvc.Testing",
        "Microsoft.AspNetCore.OpenApi",
        "Microsoft.NET.Test.Sdk",
        "Microsoft.Agents.AI.Workflows",
        "coverlet.collector",
        "xunit",
        "xunit.runner.visualstudio"
    };

    private static readonly string[] ScannedExtensions =
        [".cs", ".csproj", ".json", ".config", ".yml", ".yaml", ".props", ".targets"];

    // Directories that are not the agent's output: build artefacts, run evidence, git
    // internals, and our own documentation (which legitimately shows placeholder keys).
    private static readonly string[] SkippedDirectories =
        ["bin", "obj", ".git", ".ziplink", "docs", "node_modules"];

    private static readonly (string Name, Regex Pattern)[] SecretPatterns =
    [
        ("Anthropic API key", new Regex(@"sk-ant-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled)),
        ("AWS access key id", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("Private key block", new Regex(
            @"-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("GitHub token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{30,}\b", RegexOptions.Compiled)),
        ("Password in connection string", new Regex(
            @"(?i)\b(?:password|pwd)\s*=\s*[^;\s""']{6,}", RegexOptions.Compiled)),
        ("Hard-coded API key", new Regex(
            @"(?i)\bapi[_-]?key\b\s*[:=]\s*[""'][A-Za-z0-9_\-]{20,}[""']", RegexOptions.Compiled))
    ];

    private static readonly Regex PackageReference = new(
        @"<PackageReference\s+Include\s*=\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

    public static PolicyReport Scan(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var findings = new List<PolicyFinding>();
        var scanned = 0;

        foreach (var file in EnumerateFiles(root))
        {
            scanned++;

            string[] lines;

            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            ScanForSecrets(relative, lines, findings);

            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                ScanForDependencies(relative, lines, findings);
            }
        }

        return new PolicyReport(scanned, findings);
    }

    /// <summary>
    /// Inline suppression for lines that legitimately contain a secret-shaped string -
    /// test fixtures, mostly. Deliberately narrow: it must sit on the offending line, so
    /// it cannot silence a file or a directory, and it shows up in review.
    /// </summary>
    public const string AllowMarker = "policy:allow-secret";

    private static void ScanForSecrets(
        string relative, string[] lines, List<PolicyFinding> findings)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains(AllowMarker, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (name, pattern) in SecretPatterns)
            {
                if (!pattern.IsMatch(lines[index]))
                {
                    continue;
                }

                findings.Add(new PolicyFinding(
                    SecretsRule,
                    PolicySeverity.Violation,
                    relative,
                    index + 1,
                    $"{name} appears to be committed in source."));
            }
        }
    }

    private static void ScanForDependencies(
        string relative, string[] lines, List<PolicyFinding> findings)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            var match = PackageReference.Match(lines[index]);

            if (!match.Success)
            {
                continue;
            }

            var package = match.Groups["name"].Value;

            if (ApprovedPackages.Contains(package))
            {
                continue;
            }

            findings.Add(new PolicyFinding(
                DependenciesRule,
                PolicySeverity.Violation,
                relative,
                index + 1,
                $"'{package}' is not on the approved dependency list. Adding a package is "
                + "a change-control decision a human must make."));
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => ScannedExtensions.Contains(
                Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => !IsSkipped(root, file));
    }

    private static bool IsSkipped(string root, string file)
    {
        var segments = Path
            .GetRelativePath(root, file)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        return segments
            .Take(segments.Length - 1)
            .Any(segment => SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}
