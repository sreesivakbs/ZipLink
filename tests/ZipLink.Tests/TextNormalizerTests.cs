using ZipLink.Agentic.Text;

namespace ZipLink.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("UrlShorteningService", new[] { "Url", "Shortening", "Service" })]
    [InlineData("shortUrl", new[] { "short", "Url" })]
    [InlineData("URLReader", new[] { "URL", "Reader" })]
    [InlineData("URLs", new[] { "URLs" })]
    [InlineData("HttpURLReader", new[] { "Http", "URL", "Reader" })]
    [InlineData("Add expiration support", new[] { "Add", "expiration", "support" })]
    [InlineData("GetByShortCodeAsync", new[] { "Get", "By", "Short", "Code", "Async" })]
    public void SplitWords_SplitsOnCaseAndPunctuation(string input, string[] expected)
    {
        Assert.Equal(expected, TextNormalizer.SplitWords(input).ToArray());
    }

    [Theory]
    [InlineData("shortened", "shorten")]
    [InlineData("shortening", "shorten")]
    [InlineData("expiration", "expir")]
    [InlineData("expires", "expir")]
    [InlineData("urls", "url")]
    [InlineData("repositories", "repository")]
    [InlineData("redirect", "redirect")]
    public void Stem_ReducesInflectionsToASharedRoot(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Stem(input));
    }

    [Fact]
    public void Stem_DoesNotStripWhenTooLittleWouldSurvive()
    {
        // "user" must not become "us".
        Assert.Equal("user", TextNormalizer.Stem("user"));
    }

    [Fact]
    public void ExtractTerms_DropsStopWordsAndKeepsDomainVocabulary()
    {
        var terms = TextNormalizer.ExtractTerms("Add expiration support to shortened URLs");

        var words = terms.Select(term => term.Word).ToArray();

        Assert.Contains("expiration", words);
        Assert.Contains("shortened", words);
        Assert.Contains("urls", words);
        Assert.DoesNotContain("add", words);
        Assert.DoesNotContain("support", words);
        Assert.DoesNotContain("to", words);
    }

    [Fact]
    public void ExtractTerms_DeduplicatesByStem()
    {
        var terms = TextNormalizer.ExtractTerms("expiration expires expiration");

        Assert.Single(terms);
    }

    [Fact]
    public void ExtractTerms_ReturnsEmptyForBlankInput()
    {
        Assert.Empty(TextNormalizer.ExtractTerms("   "));
    }

    [Theory]
    [InlineData("shorten", "short", true)]
    [InlineData("url", "url", true)]
    [InlineData("expir", "short", false)]
    [InlineData("url", "use", false)]
    public void Matches_RelatesSharedRootsOnly(string left, string right, bool expected)
    {
        Assert.Equal(expected, TextNormalizer.Matches(left, right));
    }

    [Fact]
    public void Matches_RejectsShortPrefixCollisions()
    {
        // "url" is only 3 characters, so it must not prefix-match "urn".
        Assert.False(TextNormalizer.Matches("url", "urn"));
    }
}
