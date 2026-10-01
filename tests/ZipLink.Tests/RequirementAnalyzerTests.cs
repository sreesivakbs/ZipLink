using ZipLink.Agentic.Requirements;

namespace ZipLink.Tests;

public class RequirementAnalyzerTests
{
    private const string Clear = "Add a ClickCount property to the ShortUrl model";
    private const string PartiallyAmbiguous = "Add expiration support to shortened URLs";
    private const string HighlyAmbiguous = "Make the URL shortener more secure";

    private static RequirementAnalysis Analyze(string requirement)
    {
        return new RequirementAnalyzer().Analyze(requirement);
    }

    // ---------- clear requirement ----------

    [Fact]
    public void Clear_RequirementIsLowRiskAndNeedsNoClarification()
    {
        var analysis = Analyze(Clear);

        Assert.Empty(analysis.Ambiguities);
        Assert.Equal(RiskLevel.Low, analysis.RiskLevel);
        Assert.False(analysis.RequiresHumanClarification);
        Assert.Equal(0, analysis.AmbiguityScore);
    }

    [Fact]
    public void Clear_RequirementGetsFinalAcceptanceCriteria()
    {
        var analysis = Analyze(Clear);

        Assert.False(analysis.AcceptanceCriteriaProvisional);
        Assert.NotEmpty(analysis.AcceptanceCriteria);
        Assert.Contains(
            analysis.AcceptanceCriteria,
            criterion => criterion.Contains("demonstrated end to end"));
    }

    [Fact]
    public void Clear_RequirementAsksNoQuestions()
    {
        Assert.Empty(Analyze(Clear).ClarificationQuestions);
    }

    // ---------- partially ambiguous requirement ----------

    [Fact]
    public void PartiallyAmbiguous_IsMediumRiskAndStillProceeds()
    {
        var analysis = Analyze(PartiallyAmbiguous);

        Assert.Equal(RiskLevel.Medium, analysis.RiskLevel);
        Assert.False(analysis.RequiresHumanClarification);
    }

    [Fact]
    public void PartiallyAmbiguous_FlagsTheMissingDuration()
    {
        var analysis = Analyze(PartiallyAmbiguous);

        var ambiguity = Assert.Single(analysis.Ambiguities);

        Assert.Equal(AmbiguityCategory.UndefinedTimeframe, ambiguity.Category);
        Assert.Equal("expiration", ambiguity.Trigger);
    }

    [Fact]
    public void PartiallyAmbiguous_AsksAboutDurationAndRecordsAnAssumption()
    {
        var analysis = Analyze(PartiallyAmbiguous);

        Assert.Contains(
            analysis.ClarificationQuestions,
            question => question.Contains("duration"));

        Assert.Contains(
            analysis.Assumptions,
            assumption => assumption.Contains("default value"));
    }

    [Fact]
    public void QuantifiedTimeframeIsNotAmbiguous()
    {
        var analysis = Analyze("Expire shortened URLs after 30 days");

        Assert.DoesNotContain(
            analysis.Ambiguities,
            ambiguity => ambiguity.Category == AmbiguityCategory.UndefinedTimeframe);
    }

    // ---------- highly ambiguous requirement ----------

    [Fact]
    public void HighlyAmbiguous_RequiresHumanClarification()
    {
        var analysis = Analyze(HighlyAmbiguous);

        Assert.Equal(RiskLevel.High, analysis.RiskLevel);
        Assert.True(analysis.RequiresHumanClarification);
        Assert.True(analysis.AcceptanceCriteriaProvisional);
    }

    [Fact]
    public void HighlyAmbiguous_FlagsSecurityAndTheMissingBaseline()
    {
        var analysis = Analyze(HighlyAmbiguous);

        Assert.Contains(
            analysis.Ambiguities,
            ambiguity => ambiguity.Category == AmbiguityCategory.VagueQualityAttribute
                && ambiguity.Trigger == "secure");

        Assert.Contains(
            analysis.Ambiguities,
            ambiguity => ambiguity.Category == AmbiguityCategory.UnquantifiedComparative
                && ambiguity.Trigger == "more");
    }

    [Theory]
    [InlineData("rate limiting")]
    [InlineData("authentication")]
    [InlineData("authorization")]
    [InlineData("malicious or phishing URL filtering")]
    [InlineData("domain allow/deny lists")]
    public void HighlyAmbiguous_NamesTheSecurityDimensionsToChooseFrom(string dimension)
    {
        var analysis = Analyze(HighlyAmbiguous);

        Assert.Contains(
            analysis.ClarificationQuestions,
            question => question.Contains(dimension, StringComparison.Ordinal));
    }

    [Fact]
    public void HighlyAmbiguous_AsksForAThreatModel()
    {
        Assert.Contains(
            Analyze(HighlyAmbiguous).ClarificationQuestions,
            question => question.Contains("threat model"));
    }

    [Fact]
    public void HighlyAmbiguous_ExplainsWhyTheGateIsClosed()
    {
        var analysis = Analyze(HighlyAmbiguous);

        Assert.Contains("High", analysis.ClarificationRationale);
        Assert.Contains("secure", analysis.ClarificationRationale);
    }

    // The brief's canonical ambiguous scenario. It must reach the gate, because the
    // human is the one who chooses between caching, async processing and so on.
    [Fact]
    public void ScalabilityWordingWithNoTargetRequiresClarification()
    {
        var analysis = Analyze("Make it handle more traffic");

        Assert.Equal(RiskLevel.High, analysis.RiskLevel);
        Assert.True(analysis.RequiresHumanClarification);

        Assert.Contains(
            analysis.Ambiguities,
            ambiguity => ambiguity.Category == AmbiguityCategory.VagueQualityAttribute
                && ambiguity.Trigger == "traffic");
    }

    [Theory]
    [InlineData("target requests per second")]
    [InlineData("concurrent users")]
    [InlineData("expected data growth")]
    public void ScalabilityQuestionsNameTheMissingNumbers(string dimension)
    {
        Assert.Contains(
            Analyze("Make it handle more traffic").ClarificationQuestions,
            question => question.Contains(dimension, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Reduce the load on the database")]
    [InlineData("Increase capacity for peak periods")]
    [InlineData("Support more concurrent users")]
    public void OtherScalabilityVocabularyIsAlsoRecognised(string requirement)
    {
        Assert.Contains(
            Analyze(requirement).Ambiguities,
            ambiguity => ambiguity.Category == AmbiguityCategory.VagueQualityAttribute);
    }

    [Fact]
    public void AQuantifiedScalabilityTargetStillAsksWhatToMeasureButDoesNotBlock()
    {
        // A number removes the "no stated target" problem, so the comparative rule is
        // suppressed and the requirement is actionable.
        var analysis = Analyze("Handle 5000 requests per second");

        Assert.DoesNotContain(
            analysis.Ambiguities,
            ambiguity => ambiguity.Category == AmbiguityCategory.UnquantifiedComparative);
    }

    [Fact]
    public void RequirementWithNoConcreteSubjectIsBlocked()
    {
        var analysis = Analyze("Make it better");

        Assert.True(analysis.RequiresHumanClarification);
    }

    // ---------- normalization ----------

    [Theory]
    [InlineData("  make   the url shortener  more secure. ", "Make the url shortener more secure")]
    [InlineData("Please add expiration support", "Add expiration support")]
    [InlineData("We should add rate limiting!", "Add rate limiting")]
    [InlineData("i want to add caching", "Add caching")]
    public void Normalize_StripsFillerAndCollapsesWhitespace(string input, string expected)
    {
        Assert.Equal(expected, RequirementAnalyzer.Normalize(input));
    }

    [Fact]
    public void Analyze_KeepsTheOriginalTextAlongsideTheNormalizedForm()
    {
        var analysis = Analyze("  please make the URL shortener more secure.  ");

        Assert.Equal("please make the URL shortener more secure.", analysis.OriginalText);
        Assert.Equal("Make the URL shortener more secure", analysis.NormalizedRequirement);
    }

    [Fact]
    public void Analyze_ThrowsWhenRequirementIsBlank()
    {
        Assert.Throws<ArgumentException>(() => Analyze("   "));
    }

    [Fact]
    public void Analyze_IsDeterministic()
    {
        var first = Analyze(HighlyAmbiguous);
        var second = Analyze(HighlyAmbiguous);

        Assert.Equal(first.AmbiguityScore, second.AmbiguityScore);
        Assert.Equal(first.RiskLevel, second.RiskLevel);
        Assert.Equal(first.ClarificationQuestions, second.ClarificationQuestions);
    }

    // ---------- other rule families ----------

    [Fact]
    public void VagueScopeWordingIsFlagged()
    {
        var analysis = Analyze("Handle edge cases in the shortening service");

        Assert.Contains(
            analysis.Ambiguities,
            ambiguity => ambiguity.Category == AmbiguityCategory.VagueScope);
    }

    [Fact]
    public void BundledRequestsAreFlagged()
    {
        var analysis = Analyze(
            "Add analytics and rate limiting and a dashboard to the shortener");

        Assert.Contains(
            analysis.Ambiguities,
            ambiguity => ambiguity.Category == AmbiguityCategory.BundledRequests);
    }

    [Fact]
    public void Render_ProducesTheReportSections()
    {
        var text = RequirementReportRenderer.Render(Analyze(HighlyAmbiguous));

        Assert.Contains("ZipLink Requirement Analysis", text);
        Assert.Contains("CLARIFICATION REQUIRED", text);
        Assert.Contains("DETECTED AMBIGUITIES", text);
        Assert.Contains("ASSUMPTIONS", text);
        Assert.Contains("CLARIFICATION QUESTIONS", text);
        Assert.Contains("ACCEPTANCE CRITERIA", text);
        Assert.Contains("provisional", text);
    }
}
