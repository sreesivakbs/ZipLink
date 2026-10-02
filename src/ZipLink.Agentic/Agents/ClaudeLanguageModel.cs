using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace ZipLink.Agentic.Agents;

/// <summary>
/// <see cref="ILanguageModel"/> backed by the Anthropic API.
///
/// The credential is never passed through this code: the SDK reads ANTHROPIC_API_KEY
/// from the environment itself, so no key is held in a field, written to a run artifact,
/// or logged. Nothing in a stage can reach it.
/// </summary>
public sealed class ClaudeLanguageModel : ILanguageModel
{
    public const string DefaultModel = "claude-opus-5";

    private const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly int _maxTokens;

    /// <summary>
    /// Writing several whole files in one answer needs far more room than a 16k default.
    /// A truncated answer costs a full generation and yields nothing parseable, so the
    /// limit is generous and the request is streamed to stay clear of HTTP timeouts.
    /// </summary>
    public const int DefaultMaxTokens = 64000;

    public ClaudeLanguageModel(string? model = null, int maxTokens = DefaultMaxTokens)
    {
        _client = new AnthropicClient();
        _model = model ?? DefaultModel;
        _maxTokens = maxTokens;
    }

    /// <summary>
    /// True when a credential is available. The orchestrator uses this to decide whether
    /// to wire a real agent or fall back to the stub, so the whole pipeline still runs
    /// unattended and offline.
    /// </summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable));

    public async Task<LanguageModelResult> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyDictionary<string, JsonElement> jsonSchema,
        CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = _maxTokens,
            System = systemPrompt,
            Messages = [new() { Role = Role.User, Content = userPrompt }],
            OutputConfig = new OutputConfig
            {
                Effort = Effort.High,
                Format = new JsonOutputFormat
                {
                    Schema = jsonSchema.ToDictionary(
                        entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
                }
            }
        };

        var text = new StringBuilder();
        string? stopReason = null;

        // Streamed because a large MaxTokens on a single request risks an HTTP timeout,
        // and a timeout after several minutes of generation wastes the whole attempt.
        await foreach (var streamEvent in _client.Messages.CreateStreaming(parameters)
            .WithCancellation(cancellationToken))
        {
            if (streamEvent.TryPickContentBlockDelta(out var delta)
                && delta.Delta.TryPickText(out var chunk))
            {
                text.Append(chunk.Text);
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                stopReason = messageDelta.Delta.StopReason?.ToString();
            }
        }

        // A refusal arrives as a successful response, so stop_reason must be checked
        // before the content is trusted.
        if (string.Equals(stopReason, "refusal", StringComparison.Ordinal))
        {
            return LanguageModelResult.Refusal(
                "The model declined this request. A human should review the requirement "
                + "before it is retried.");
        }

        // Truncation produces half a JSON document, which otherwise surfaces downstream as
        // a baffling parse error. Say what actually happened.
        if (string.Equals(stopReason, "max_tokens", StringComparison.Ordinal))
        {
            throw new ModelOutputTruncatedException(
                $"The answer hit the {_maxTokens}-token output limit and was cut off. "
                + "Narrow the scope of the change, or raise the limit.");
        }

        return LanguageModelResult.Answer(text.ToString());
    }
}
