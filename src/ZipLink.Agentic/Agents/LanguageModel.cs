using System.Text.Json;

namespace ZipLink.Agentic.Agents;

/// <summary>
/// What a model returned. A refusal is reported, not thrown: the safety classifiers
/// declining a request is a governance outcome for a human to look at, not a transport
/// failure to retry, and the orchestrator maps it to a gate rather than a failure.
/// </summary>
public sealed record LanguageModelResult(string Json, bool Refused, string? RefusalReason)
{
    public static LanguageModelResult Answer(string json)
    {
        return new LanguageModelResult(json, Refused: false, RefusalReason: null);
    }

    public static LanguageModelResult Refusal(string reason)
    {
        return new LanguageModelResult(string.Empty, Refused: true, RefusalReason: reason);
    }
}

/// <summary>
/// The orchestrator's only view of a language model.
///
/// Deliberately narrow and expressed in BCL types so that stages never see a vendor SDK,
/// tests can substitute a fake - unit and CI tests must never call a real model - and a
/// provider can be swapped without touching an agent.
/// </summary>
public interface ILanguageModel
{
    /// <summary>
    /// Asks for a single JSON answer constrained to <paramref name="jsonSchema"/>.
    /// Returns the raw JSON text; deserializing and validating it is the caller's job,
    /// so a malformed answer becomes an ordinary stage failure and feeds the retry
    /// policy already in the engine.
    /// </summary>
    Task<LanguageModelResult> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyDictionary<string, JsonElement> jsonSchema,
        CancellationToken cancellationToken);
}
