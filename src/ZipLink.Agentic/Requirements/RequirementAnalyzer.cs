using System.Text.RegularExpressions;
using ZipLink.Agentic.Text;

namespace ZipLink.Agentic.Requirements;

/// <summary>
/// Normalizes a raw engineering request and decides whether it is specific enough to
/// act on. Every rule is a fixed lookup or a simple textual test, so the same input
/// always produces the same output and every verdict can be traced to a trigger word.
///
/// This stage deliberately knows nothing about the repository. It judges the wording of
/// the request, not whether the code can satisfy it.
/// </summary>
public sealed class RequirementAnalyzer
{
    private const int HighRiskScore = 4;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // Openers that carry no engineering content and only obscure the actual ask.
    private static readonly string[] FillerPrefixes =
    [
        "please ", "can you ", "could you ", "i want to ", "i would like to ",
        "i need to ", "we want to ", "we need to ", "we should ", "you should ",
        "it would be good to ", "it would be nice to ", "let's ", "lets "
    ];

    private sealed record QualityAttribute(
        string Name,
        IReadOnlyList<string> Triggers,
        IReadOnlyList<string> Dimensions,
        bool SecuritySensitive);

    private static readonly QualityAttribute[] QualityAttributes =
    [
        new("security",
            ["secure", "secured", "security", "safer", "safety", "harden", "hardened",
             "hardening", "protect", "protected", "protection"],
            ["rate limiting", "authentication", "authorization",
             "malicious or phishing URL filtering", "domain allow/deny lists",
             "input validation", "transport security (HTTPS/HSTS)",
             "secret and key handling"],
            SecuritySensitive: true),

        new("performance",
            ["fast", "faster", "performant", "performance", "quick", "quicker",
             "quickly", "speed", "snappy", "latency", "throughput", "responsive"],
            ["a target latency (for example p95 under N ms)", "expected request volume",
             "payload sizes", "caching strategy"],
            SecuritySensitive: false),

        new("scalability",
            ["scalable", "scalability", "scaling"],
            ["target requests per second", "concurrent users", "expected data growth",
             "horizontal or vertical scaling"],
            SecuritySensitive: false),

        new("reliability",
            ["reliable", "reliability", "stable", "stability", "resilient", "resilience",
             "robust", "robustness", "durable", "durability"],
            ["an availability target", "which failure modes must be tolerated",
             "retry and timeout policy", "data durability guarantees"],
            SecuritySensitive: false),

        new("general improvement",
            ["better", "improve", "improved", "improvement", "enhance", "enhanced",
             "optimize", "optimized", "optimise", "optimised", "modernize", "modern",
             "clean", "cleaner", "tidy", "maintainable", "maintainability"],
            ["which dimension is being improved", "the current baseline",
             "the measurable target"],
            SecuritySensitive: false),

        new("usability",
            ["simple", "simpler", "intuitive", "friendly", "usable", "usability"],
            ["which users are affected", "which flows change", "how success is measured"],
            SecuritySensitive: false),

        new("efficiency",
            ["efficient", "efficiency", "lightweight", "cheaper"],
            ["which resource (CPU, memory, network, cost)", "current usage",
             "the target usage"],
            SecuritySensitive: false)
    ];

    private static readonly string[] ComparativeTriggers =
    [
        "more", "less", "fewer", "better", "faster", "slower", "stronger", "greater",
        "improve", "improved", "improving", "enhance", "increase", "decrease", "reduce",
        "minimize", "maximize", "optimize", "optimise"
    ];

    private static readonly string[] TimeframeTriggers =
    [
        "expire", "expires", "expired", "expiration", "expiry", "ttl", "timeout",
        "timeouts", "retention", "retain", "schedule", "scheduled", "delay", "interval",
        "duration", "lifetime", "stale"
    ];

    private static readonly string[] VagueScopeWords =
    [
        "etc", "various", "some", "several", "appropriate", "appropriately", "proper",
        "properly", "necessary", "applicable", "relevant", "things", "stuff", "anything",
        "everything", "somehow"
    ];

    private static readonly string[] VagueScopePhrases =
    [
        "and so on", "as needed", "if necessary", "where applicable", "edge cases",
        "and more", "or whatever", "that sort of thing"
    ];

    // Words that look like content but cannot themselves be the thing being changed.
    private static readonly HashSet<string> NonSubjectWords = new(
        QualityAttributes
            .SelectMany(attribute => attribute.Triggers)
            .Concat(ComparativeTriggers),
        StringComparer.Ordinal);

    public RequirementAnalysis Analyze(string requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
        {
            throw new ArgumentException(
                "A requirement description is required", nameof(requirement));
        }

        var normalized = Normalize(requirement);
        var lowered = normalized.ToLowerInvariant();

        var wordSequence = TextNormalizer
            .SplitWords(normalized)
            .Select(word => word.ToLowerInvariant())
            .ToList();

        var words = new HashSet<string>(wordSequence, StringComparer.Ordinal);

        // A requirement carrying a concrete number is treated as quantified, which
        // suppresses the "no stated target" family of rules.
        var isQuantified = normalized.Any(char.IsDigit);

        var ambiguities = new List<Ambiguity>();
        var questions = new List<string>();
        var assumptions = new List<string>();
        var criteria = new List<string>();

        DetectMissingSubject(normalized, ambiguities, questions);
        DetectVagueQualities(words, ambiguities, questions, assumptions, criteria);
        DetectComparatives(words, isQuantified, ambiguities, questions, assumptions);
        DetectUndefinedTimeframe(words, isQuantified, ambiguities, questions, assumptions, criteria);
        DetectVagueScope(words, lowered, ambiguities, questions);
        DetectBundledRequests(wordSequence, ambiguities, questions);

        var score = ambiguities.Sum(ambiguity => ambiguity.Weight);
        var risk = ToRiskLevel(score);
        var requiresClarification = risk == RiskLevel.High;

        AddBaselineAssumptions(assumptions);
        AddBaselineCriteria(criteria, ambiguities.Count == 0);

        return new RequirementAnalysis
        {
            OriginalText = requirement.Trim(),
            NormalizedRequirement = normalized,
            Ambiguities = ambiguities,
            Assumptions = Distinct(assumptions),
            ClarificationQuestions = Distinct(questions),
            AcceptanceCriteria = Distinct(criteria),
            AmbiguityScore = score,
            RiskLevel = risk,
            RequiresHumanClarification = requiresClarification,
            ClarificationRationale = BuildRationale(requiresClarification, risk, score, ambiguities)
        };
    }

    /// <summary>
    /// Collapses whitespace, removes conversational openers and trailing punctuation so
    /// that equivalent phrasings reduce to the same reviewable sentence.
    /// </summary>
    public static string Normalize(string requirement)
    {
        var text = WhitespaceRun.Replace(requirement.Trim(), " ");

        bool trimmed;

        do
        {
            trimmed = false;

            foreach (var prefix in FillerPrefixes)
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[prefix.Length..].TrimStart();
                    trimmed = true;
                }
            }
        }
        while (trimmed && text.Length > 0);

        text = text.TrimEnd('.', '!', ' ', '\t');

        if (text.Length > 0 && char.IsLower(text[0]))
        {
            text = char.ToUpperInvariant(text[0]) + text[1..];
        }

        return text;
    }

    private static void DetectMissingSubject(
        string normalized,
        List<Ambiguity> ambiguities,
        List<string> questions)
    {
        // A vague quality word is not a subject: "make it better" names no thing to
        // change, even though "better" survives stop-word filtering.
        var concreteTerms = TextNormalizer
            .ExtractTerms(normalized)
            .Where(term => !NonSubjectWords.Contains(term.Word))
            .ToList();

        if (concreteTerms.Count > 0)
        {
            return;
        }

        ambiguities.Add(new Ambiguity
        {
            Category = AmbiguityCategory.MissingSubject,
            Trigger = normalized,
            Explanation =
                "The requirement contains no concrete noun identifying what should change.",
            Weight = 4
        });

        questions.Add("Which component, endpoint or behaviour should change?");
    }

    private static void DetectVagueQualities(
        HashSet<string> words,
        List<Ambiguity> ambiguities,
        List<string> questions,
        List<string> assumptions,
        List<string> criteria)
    {
        foreach (var attribute in QualityAttributes)
        {
            var trigger = attribute.Triggers.FirstOrDefault(words.Contains);

            if (trigger is null)
            {
                continue;
            }

            ambiguities.Add(new Ambiguity
            {
                Category = AmbiguityCategory.VagueQualityAttribute,
                Trigger = trigger,
                Explanation =
                    $"'{trigger}' names a {attribute.Name} goal without saying which "
                    + "property is in scope or how it will be verified.",
                Weight = attribute.SecuritySensitive ? 4 : 2
            });

            questions.Add(
                $"Which aspect of {attribute.Name} is in scope: "
                + $"{string.Join(", ", attribute.Dimensions)}?");

            questions.Add(
                $"How will '{trigger}' be measured or demonstrated once implemented?");

            if (attribute.SecuritySensitive)
            {
                questions.Add(
                    "What is the threat model: who is the attacker, and what are they "
                    + "trying to achieve?");

                assumptions.Add(
                    "No threat model, compliance standard or specific attack was named, "
                    + "so no security control can be selected on the requester's behalf.");

                criteria.Add(
                    "Each agreed security control has a dedicated automated test, "
                    + "including a negative case that proves the control blocks abuse.");
            }
            else
            {
                assumptions.Add(
                    $"Current behaviour is the baseline for '{trigger}'; no target value "
                    + "was supplied.");

                criteria.Add(
                    $"The agreed {attribute.Name} target is stated as a number and "
                    + "verified by a repeatable check.");
            }
        }
    }

    private static void DetectComparatives(
        HashSet<string> words,
        bool isQuantified,
        List<Ambiguity> ambiguities,
        List<string> questions,
        List<string> assumptions)
    {
        if (isQuantified)
        {
            return;
        }

        var trigger = ComparativeTriggers.FirstOrDefault(words.Contains);

        if (trigger is null)
        {
            return;
        }

        ambiguities.Add(new Ambiguity
        {
            Category = AmbiguityCategory.UnquantifiedComparative,
            Trigger = trigger,
            Explanation =
                $"'{trigger}' asks for a relative change without stating the current "
                + "baseline or the target, so completion cannot be judged.",
            Weight = 1
        });

        questions.Add(
            $"'{trigger}' compared to what? Please state the current value and the "
            + "target value.");

        assumptions.Add(
            "The comparison is against the behaviour currently in the repository.");
    }

    private static void DetectUndefinedTimeframe(
        HashSet<string> words,
        bool isQuantified,
        List<Ambiguity> ambiguities,
        List<string> questions,
        List<string> assumptions,
        List<string> criteria)
    {
        if (isQuantified)
        {
            return;
        }

        var trigger = TimeframeTriggers.FirstOrDefault(words.Contains);

        if (trigger is null)
        {
            return;
        }

        ambiguities.Add(new Ambiguity
        {
            Category = AmbiguityCategory.UndefinedTimeframe,
            Trigger = trigger,
            Explanation =
                $"'{trigger}' depends on a duration, but no period or default was given.",
            Weight = 2
        });

        questions.Add($"What duration applies to '{trigger}', and is it configurable?");
        questions.Add(
            $"What should happen to records that have already passed the '{trigger}' "
            + "point: rejected, deleted, or retained for reporting?");

        assumptions.Add(
            $"'{trigger}' needs a default value; none was supplied, so one must be "
            + "agreed before implementation.");

        criteria.Add(
            "Behaviour is covered by tests for both the elapsed and the not-yet-elapsed "
            + "case.");
    }

    private static void DetectVagueScope(
        HashSet<string> words,
        string loweredText,
        List<Ambiguity> ambiguities,
        List<string> questions)
    {
        var trigger = VagueScopeWords.FirstOrDefault(words.Contains)
            ?? VagueScopePhrases.FirstOrDefault(
                phrase => loweredText.Contains(phrase, StringComparison.Ordinal));

        if (trigger is null)
        {
            return;
        }

        ambiguities.Add(new Ambiguity
        {
            Category = AmbiguityCategory.VagueScope,
            Trigger = trigger,
            Explanation =
                $"'{trigger}' leaves the boundary of the work open, so the finished "
                + "state cannot be agreed in advance.",
            Weight = 2
        });

        questions.Add($"Please replace '{trigger}' with the explicit list of cases in scope.");
    }

    private static void DetectBundledRequests(
        IReadOnlyList<string> wordSequence,
        List<Ambiguity> ambiguities,
        List<string> questions)
    {
        string[] connectorWords = ["and", "or", "also", "plus"];

        // Occurrences, not distinct words: "add X and Y and Z" is three asks.
        var connectors = wordSequence.Count(
            word => connectorWords.Contains(word, StringComparer.Ordinal));

        if (connectors < 2)
        {
            return;
        }

        ambiguities.Add(new Ambiguity
        {
            Category = AmbiguityCategory.BundledRequests,
            Trigger = "and/or",
            Explanation =
                "Several separate asks appear to be combined, which prevents independent "
                + "sequencing, review and rollback.",
            Weight = 1
        });

        questions.Add(
            "Should this be split into separate requirements that can ship independently?");
    }

    private static void AddBaselineAssumptions(List<string> assumptions)
    {
        assumptions.Add("The change is limited to this repository.");
        assumptions.Add(
            "Existing public behaviour stays backward compatible unless the requirement "
            + "says otherwise.");
    }

    private static void AddBaselineCriteria(List<string> criteria, bool isUnambiguous)
    {
        criteria.Add("`dotnet build` succeeds with zero warnings.");
        criteria.Add("`dotnet test` passes, including at least one new test for the change.");
        criteria.Add("No files outside the agreed scope are modified.");

        if (isUnambiguous)
        {
            criteria.Add(
                "The requested behaviour is demonstrated end to end by an automated test.");
        }
    }

    private static RiskLevel ToRiskLevel(int score)
    {
        if (score >= HighRiskScore)
        {
            return RiskLevel.High;
        }

        return score > 0 ? RiskLevel.Medium : RiskLevel.Low;
    }

    private static string BuildRationale(
        bool requiresClarification,
        RiskLevel risk,
        int score,
        IReadOnlyList<Ambiguity> ambiguities)
    {
        if (!requiresClarification)
        {
            return ambiguities.Count == 0
                ? "No ambiguity detected; the requirement can proceed to impact analysis."
                : $"Risk is {risk} (score {score}). The open questions are worth answering "
                    + "but do not block impact analysis.";
        }

        var categories = ambiguities
            .Where(ambiguity => ambiguity.Weight >= HighRiskScore)
            .Select(ambiguity => $"'{ambiguity.Trigger}'")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var driver = categories.Count > 0
            ? $" driven by {string.Join(", ", categories)}"
            : string.Empty;

        return $"Risk is High (score {score}){driver}. Implementing this without "
            + "clarification would mean guessing at the requester's intent.";
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values)
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
}
