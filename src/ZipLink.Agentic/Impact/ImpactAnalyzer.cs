using ZipLink.Agentic.Repository;
using ZipLink.Agentic.Text;

namespace ZipLink.Agentic.Impact;

/// <summary>
/// Ranks the files a requirement is likely to touch by matching requirement vocabulary
/// against the identifiers already present in the repository.
///
/// This is lexical evidence, not comprehension. It answers "where does this repository
/// already talk about these words", which is a useful starting point for a human, and
/// it is explicitly not a decision to change anything. Nothing here writes to disk.
/// </summary>
public sealed class ImpactAnalyzer
{
    private const double TypeNameWeight = 5.0;
    private const double MethodNameWeight = 4.0;
    private const double FileNameWeight = 3.0;
    private const double NamespaceWeight = 2.0;

    // A term present in more than this share of files cannot discriminate between them.
    private const double CommonTermFileShare = 0.5;
    private const double CommonTermDamping = 0.4;

    // Repeated matches of the same term in one file are evidence of verbosity more than
    // of impact, so additional hits past the first contribute at a reduced rate.
    private const double RepeatMatchRate = 0.25;

    // A test file is normally updated because production code changed, not instead of
    // it, so it should not outrank the code it covers.
    private const double TestFileDamping = 0.5;

    private const double HighConfidenceScore = 8.0;
    private const double MediumConfidenceScore = 4.0;

    private readonly RepositoryAnalyzer _repositoryAnalyzer;

    public ImpactAnalyzer()
        : this(new RepositoryAnalyzer())
    {
    }

    public ImpactAnalyzer(RepositoryAnalyzer repositoryAnalyzer)
    {
        ArgumentNullException.ThrowIfNull(repositoryAnalyzer);

        _repositoryAnalyzer = repositoryAnalyzer;
    }

    public ImpactReport Analyze(string repositoryRoot, string requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
        {
            throw new ArgumentException(
                "A requirement description is required", nameof(requirement));
        }

        var repository = _repositoryAnalyzer.Analyze(repositoryRoot);
        var terms = TextNormalizer.ExtractTerms(requirement);

        var profiles = repository.Files.Select(FileProfile.From).ToList();

        var matchedTermStems = new HashSet<string>(StringComparer.Ordinal);
        var commonTerms = DetermineCommonTerms(terms, profiles, matchedTermStems);

        var items = profiles
            .Select(profile => ScoreFile(profile, terms, commonTerms))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unmatchedTerms = terms
            .Where(term => !matchedTermStems.Contains(term.Stem))
            .ToList();

        return new ImpactReport
        {
            Requirement = requirement.Trim(),
            RepositoryRoot = repository.RootPath,
            FilesScanned = repository.FileCount,
            Terms = terms,
            UnmatchedTerms = unmatchedTerms,
            CommonTerms = commonTerms.Values.ToList(),
            Items = items,
            OverallConfidence = DetermineOverallConfidence(items, unmatchedTerms),
            Assumptions = BuildAssumptions(),
            Risks = BuildRisks(items, unmatchedTerms, commonTerms.Values.ToList(), repository),
            Limitations = BuildLimitations()
        };
    }

    /// <summary>
    /// Finds the terms that appear in so many files they stop being evidence, and
    /// records which terms matched anything at all.
    /// </summary>
    private static Dictionary<string, RequirementTerm> DetermineCommonTerms(
        IReadOnlyList<RequirementTerm> terms,
        IReadOnlyList<FileProfile> profiles,
        HashSet<string> matchedTermStems)
    {
        var common = new Dictionary<string, RequirementTerm>(StringComparer.Ordinal);

        if (profiles.Count == 0)
        {
            return common;
        }

        foreach (var term in terms)
        {
            var fileCount = profiles.Count(profile => profile.Contains(term.Stem));

            if (fileCount > 0)
            {
                matchedTermStems.Add(term.Stem);
            }

            if (fileCount > profiles.Count * CommonTermFileShare)
            {
                common[term.Stem] = term;
            }
        }

        return common;
    }

    private static ImpactItem? ScoreFile(
        FileProfile profile,
        IReadOnlyList<RequirementTerm> terms,
        IReadOnlyDictionary<string, RequirementTerm> commonTerms)
    {
        var score = 0.0;
        var reasons = new List<string>();
        var matchedTypes = new List<string>();
        var matchedMethods = new List<string>();

        foreach (var term in terms)
        {
            var damped = commonTerms.ContainsKey(term.Stem);
            var weighting = damped ? CommonTermDamping : 1.0;
            var note = damped ? " (common term, down-weighted)" : string.Empty;

            var typeHits = profile.Types
                .Where(entry => Matches(entry.Stems, term.Stem))
                .ToList();

            if (typeHits.Count > 0)
            {
                score += TypeNameWeight * weighting * RepeatFactor(typeHits.Count);

                foreach (var type in typeHits)
                {
                    matchedTypes.Add(type.Name);
                    reasons.Add($"Type '{type.Name}' matches '{term.Word}'{note}");
                }
            }

            var methodHits = profile.Methods
                .Where(entry => Matches(entry.Stems, term.Stem))
                .ToList();

            if (methodHits.Count > 0)
            {
                score += MethodNameWeight * weighting * RepeatFactor(methodHits.Count);

                foreach (var method in methodHits)
                {
                    matchedMethods.Add(method.Name);
                    reasons.Add($"Method '{method.Name}' matches '{term.Word}'{note}");
                }
            }

            if (Matches(profile.FileNameStems, term.Stem))
            {
                score += FileNameWeight * weighting;
                reasons.Add($"File name matches '{term.Word}'{note}");
            }

            if (Matches(profile.NamespaceStems, term.Stem))
            {
                score += NamespaceWeight * weighting;
                reasons.Add($"Namespace '{profile.Namespace}' matches '{term.Word}'{note}");
            }
        }

        if (reasons.Count == 0)
        {
            return null;
        }

        if (profile.IsTestFile)
        {
            score *= TestFileDamping;
            reasons.Add(
                "Test file: score reduced because tests follow production changes "
                + "rather than driving them");
        }

        return new ImpactItem
        {
            Path = profile.Path,
            Namespace = profile.Namespace,
            Score = Math.Round(score, 2),
            Confidence = ScoreToConfidence(score),
            IsTestFile = profile.IsTestFile,
            Reasons = reasons,
            MatchedTypes = matchedTypes.Distinct(StringComparer.Ordinal).ToList(),
            MatchedMethods = matchedMethods.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static bool Matches(IReadOnlyList<string> stems, string termStem)
    {
        return stems.Any(stem => TextNormalizer.Matches(termStem, stem));
    }

    /// <summary>
    /// Diminishing returns for repeated matches: the first hit counts fully, each
    /// further hit counts for a fraction. Without this, a file with many verbosely
    /// named members outranks the single type the requirement actually concerns.
    /// </summary>
    private static double RepeatFactor(int matchCount)
    {
        return 1.0 + (RepeatMatchRate * (matchCount - 1));
    }

    private static ConfidenceLevel ScoreToConfidence(double score)
    {
        if (score >= HighConfidenceScore)
        {
            return ConfidenceLevel.High;
        }

        return score >= MediumConfidenceScore
            ? ConfidenceLevel.Medium
            : ConfidenceLevel.Low;
    }

    /// <summary>
    /// Overall confidence is capped at Medium whenever part of the requirement's
    /// vocabulary is absent from the repository, because the analysis provably did not
    /// locate that part of the work.
    /// </summary>
    private static ConfidenceLevel DetermineOverallConfidence(
        IReadOnlyList<ImpactItem> items,
        IReadOnlyList<RequirementTerm> unmatchedTerms)
    {
        if (items.Count == 0)
        {
            return ConfidenceLevel.Low;
        }

        var best = items.Any(item => item.Confidence == ConfidenceLevel.High)
            ? ConfidenceLevel.High
            : items.Any(item => item.Confidence == ConfidenceLevel.Medium)
                ? ConfidenceLevel.Medium
                : ConfidenceLevel.Low;

        if (unmatchedTerms.Count > 0 && best == ConfidenceLevel.High)
        {
            return ConfidenceLevel.Medium;
        }

        return best;
    }

    private static IReadOnlyList<string> BuildAssumptions()
    {
        return
        [
            "The requirement is written in English and names domain concepts using words "
                + "that also appear in code identifiers.",
            "Identifier names in the repository are meaningful and current.",
            "All relevant code lives in this repository, in .cs files outside bin/ and obj/.",
            "Scoring weights (type 5, method 4, file name 3, namespace 2) and the "
                + "High/Medium thresholds (8/4) are heuristic defaults, not calibrated values."
        ];
    }

    private static IReadOnlyList<string> BuildRisks(
        IReadOnlyList<ImpactItem> items,
        IReadOnlyList<RequirementTerm> unmatchedTerms,
        IReadOnlyList<RequirementTerm> commonTerms,
        RepositoryAnalysis repository)
    {
        var risks = new List<string>();

        if (items.Count == 0)
        {
            risks.Add(
                "No file matched any requirement term. Either the requirement describes "
                + "entirely new functionality, or its wording does not overlap the code's "
                + "vocabulary. Treat this report as providing no evidence either way.");
        }

        if (unmatchedTerms.Count > 0)
        {
            var words = string.Join(", ", unmatchedTerms.Select(term => $"'{term.Word}'"));

            risks.Add(
                $"No code anywhere matches {words}. That concept is probably not modelled "
                + "yet, so the files needed to implement it cannot be identified "
                + "lexically and are likely missing from the list below.");
        }

        if (commonTerms.Count > 0)
        {
            var words = string.Join(", ", commonTerms.Select(term => $"'{term.Word}'"));

            risks.Add(
                $"{words} appear in more than half of the {repository.FileCount} scanned "
                + "files, so they cannot discriminate between candidates. Their "
                + "contribution was reduced, which may have demoted a genuinely "
                + "impacted file.");
        }

        risks.Add(
            "Indirect coupling is invisible to this analysis: dependency-injection "
            + "registrations, reflection, serialized contracts and HTTP route strings "
            + "create impact that no identifier name reveals.");

        risks.Add(
            "Ranking reflects vocabulary density, not engineering effort. A file with one "
            + "weak match may still need the largest change.");

        return risks;
    }

    private static IReadOnlyList<string> BuildLimitations()
    {
        return
        [
            "Matching is lexical. Synonyms are not related to each other, so a "
                + "requirement saying 'TTL' will not match code saying 'expiration'.",
            "There is no call-graph or data-flow analysis, so transitive impact through "
                + "callers and callees is not computed.",
            "Only .cs files are scanned. Project files, appsettings.json and "
                + "launchSettings.json are never reported, so configuration and packaging "
                + "impact is missed.",
            "Type and method extraction is regex-based: constructors, tuple-returning "
                + "methods and local functions are not detected.",
            "Files are ranked independently; the report does not propose an order of work "
                + "or a task breakdown.",
            "This capability is read-only by design. It does not modify code, and it does "
                + "not call any language model."
        ];
    }

    /// <summary>
    /// Pre-computed stems for one file, so each term is matched against prepared data
    /// rather than re-splitting identifiers for every term.
    /// </summary>
    private sealed record FileProfile(
        string Path,
        string? Namespace,
        bool IsTestFile,
        IReadOnlyList<string> FileNameStems,
        IReadOnlyList<string> NamespaceStems,
        IReadOnlyList<NamedStems> Types,
        IReadOnlyList<NamedStems> Methods)
    {
        public static FileProfile From(FileAnalysis file)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(file.Path);

            return new FileProfile(
                file.Path,
                file.Namespace,
                IsTest(file.Path, fileName),
                TextNormalizer.ExtractIdentifierStems(fileName),
                TextNormalizer.ExtractIdentifierStems(file.Namespace ?? string.Empty),
                file.Types.Select(NamedStems.From).ToList(),
                file.Methods.Select(NamedStems.From).ToList());
        }

        private static bool IsTest(string path, string fileName)
        {
            if (fileName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith("Test", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var segments = path.Split(
                [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            return segments
                .Take(segments.Length - 1)
                .Any(segment => segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("test", StringComparison.OrdinalIgnoreCase));
        }

        public bool Contains(string termStem)
        {
            return Matches(FileNameStems, termStem)
                || Matches(NamespaceStems, termStem)
                || Types.Any(type => Matches(type.Stems, termStem))
                || Methods.Any(method => Matches(method.Stems, termStem));
        }
    }

    private sealed record NamedStems(string Name, IReadOnlyList<string> Stems)
    {
        public static NamedStems From(string name)
        {
            return new NamedStems(name, TextNormalizer.ExtractIdentifierStems(name));
        }
    }
}
