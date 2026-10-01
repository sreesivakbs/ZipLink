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

    public ClaudeLanguageModel(string? model = null, int maxTokens = 16000)
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
        var response = await _client.Messages.Create(new MessageCreateParams
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
        });

        // A refusal arrives as a successful HTTP response, so stop_reason must be checked
        // before the content is read.
        if (string.Equals(response.StopReason?.ToString(), "refusal", StringComparison.Ordinal))
        {
            return LanguageModelResult.Refusal(
                "The model declined this request. A human should review the requirement "
                + "before it is retried.");
        }

        var text = new StringBuilder();

        foreach (var block in response.Content.Select(block => block.Value).OfType<TextBlock>())
        {
            text.Append(block.Text);
        }

        return LanguageModelResult.Answer(text.ToString());
    }
}
