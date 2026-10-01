using System.Text;

namespace ZipLink.Agentic.Text;

/// <summary>
/// One meaningful word from the requirement. <see cref="Word"/> is kept for display so
/// reports read naturally; <see cref="Stem"/> is what matching actually compares.
/// </summary>
public sealed record RequirementTerm(string Word, string Stem);

/// <summary>
/// Splits prose and identifiers into comparable word stems. This is deliberately a
/// lexical layer only: it has no notion of meaning, so "TTL" and "expiration" are
/// unrelated here. Callers must surface that as a limitation.
/// </summary>
public static class TextNormalizer
{
    private const int MinimumTermLength = 3;

    // Matching a 3-character stem by prefix is too noisy, so prefix matches require a
    // slightly longer shared root. Exact stem equality is always allowed.
    private const int MinimumPrefixMatchLength = 4;

    // Filler words plus the verbs and nouns that appear in almost every requirement
    // sentence. Removing these keeps the signal on domain vocabulary.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "without", "from", "into", "that", "this", "these",
        "those", "its", "are", "was", "were", "been", "being", "have", "has", "had",
        "not", "but", "all", "any", "can", "could", "would", "will", "shall", "should",
        "must", "may", "might", "does", "did", "done", "add", "adds", "added", "adding",
        "support", "supports", "supported", "new", "also", "when", "while", "such",
        "via", "per", "use", "uses", "using", "used", "make", "makes", "made", "need",
        "needs", "needed", "want", "wants", "allow", "allows", "ensure", "ensures",
        "implement", "implements", "feature", "features", "requirement", "requirements",
        "system", "app", "application", "please", "each", "every", "some", "more",
        "less", "than", "then", "else", "there", "their", "them", "they", "you", "your",
        "our", "out", "about", "over", "under", "between", "where", "which", "what",
        "how", "why", "who", "whom", "both", "either", "neither", "only", "just"
    };

    /// <summary>
    /// Extracts the distinct, stemmed, non-trivial terms from free-text requirement prose.
    /// </summary>
    public static IReadOnlyList<RequirementTerm> ExtractTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<RequirementTerm>();
        }

        var terms = new List<RequirementTerm>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var word in SplitWords(text))
        {
            var lowered = word.ToLowerInvariant();

            if (lowered.Length < MinimumTermLength || StopWords.Contains(lowered))
            {
                continue;
            }

            var stem = Stem(lowered);

            if (stem.Length < MinimumTermLength || !seen.Add(stem))
            {
                continue;
            }

            terms.Add(new RequirementTerm(lowered, stem));
        }

        return terms;
    }

    /// <summary>
    /// Extracts the distinct stems from a code identifier, splitting on camel case so
    /// "UrlShorteningService" contributes "url", "shorten" and "service".
    /// </summary>
    public static IReadOnlyList<string> ExtractIdentifierStems(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return Array.Empty<string>();
        }

        var stems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var word in SplitWords(identifier))
        {
            var stem = Stem(word.ToLowerInvariant());

            if (stem.Length > 0 && seen.Add(stem))
            {
                stems.Add(stem);
            }
        }

        return stems;
    }

    /// <summary>
    /// True when a requirement stem and an identifier stem refer to the same root word.
    /// </summary>
    public static bool Matches(string requirementStem, string identifierStem)
    {
        if (requirementStem.Length == 0 || identifierStem.Length == 0)
        {
            return false;
        }

        if (string.Equals(requirementStem, identifierStem, StringComparison.Ordinal))
        {
            return true;
        }

        var shorter = requirementStem.Length <= identifierStem.Length
            ? requirementStem
            : identifierStem;

        var longer = ReferenceEquals(shorter, requirementStem)
            ? identifierStem
            : requirementStem;

        return shorter.Length >= MinimumPrefixMatchLength
            && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Splits text on non-alphanumeric characters and on camel-case boundaries,
    /// keeping acronyms intact ("HttpURLReader" yields "Http", "URL", "Reader").
    /// </summary>
    public static IEnumerable<string> SplitWords(string text)
    {
        var buffer = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];

            if (!char.IsLetterOrDigit(current))
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                continue;
            }

            if (buffer.Length > 0 && IsWordBoundary(text, i, buffer))
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            buffer.Append(current);
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    private static bool IsWordBoundary(string text, int index, StringBuilder buffer)
    {
        var current = text[index];
        var previous = buffer[^1];

        // lower -> upper, as in "shortUrl".
        if (char.IsUpper(current) && !char.IsUpper(previous))
        {
            return true;
        }

        // Tail of an acronym followed by a new word, as in "URLReader". A single
        // trailing lower-case letter is a plural suffix rather than a new word, so
        // "URLs" must stay whole instead of splitting into "UR" and "Ls".
        if (char.IsUpper(current) && char.IsUpper(previous))
        {
            return LowerCaseRunLength(text, index + 1) >= 2;
        }

        return false;
    }

    private static int LowerCaseRunLength(string text, int start)
    {
        var length = 0;

        while (start + length < text.Length && char.IsLower(text[start + length]))
        {
            length++;
        }

        return length;
    }

    /// <summary>
    /// Strips the common English suffixes that separate a requirement's wording from an
    /// identifier's wording ("shortened"/"shortening" both reduce to "shorten"). A
    /// suffix is only removed when enough of the word survives to stay meaningful.
    /// </summary>
    public static string Stem(string word)
    {
        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal))
        {
            return string.Concat(word.AsSpan(0, word.Length - 3), "y");
        }

        string[] suffixes =
            ["ations", "ation", "ings", "ing", "ers", "er", "edly", "ed", "es", "s"];

        foreach (var suffix in suffixes)
        {
            if (!word.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var trimmed = word[..^suffix.Length];

            if (trimmed.Length >= MinimumTermLength)
            {
                return trimmed;
            }
        }

        return word;
    }
}
