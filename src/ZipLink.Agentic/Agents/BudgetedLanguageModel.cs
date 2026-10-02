using System.Diagnostics;
using System.Text.Json;

namespace ZipLink.Agentic.Agents;

/// <summary>
/// Thrown when a run exhausts its budget. Distinct from an ordinary failure so the
/// orchestrator can report it as a governance stop rather than a broken stage.
/// </summary>
public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Caps on what one run may consume. Bounded by construction - there is no "unlimited"
/// setting, because the failure mode being guarded against is an agent loop that is
/// working correctly and simply will not stop.
/// </summary>
public sealed record RunBudget(int MaxModelCalls = 20, TimeSpan MaxWallClock = default)
{
    public static readonly RunBudget Default = new();

    public TimeSpan EffectiveWallClock =>
        MaxWallClock == default ? TimeSpan.FromMinutes(30) : MaxWallClock;
}

/// <summary>
/// Wraps a model and enforces the run budget around it.
///
/// A decorator rather than a check inside each agent: an agent cannot forget to call it,
/// and adding a new agent cannot quietly escape the cap.
/// </summary>
public sealed class BudgetedLanguageModel : ILanguageModel
{
    private readonly ILanguageModel _inner;
    private readonly RunBudget _budget;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Lock _gate = new();

    private int _calls;

    public BudgetedLanguageModel(ILanguageModel inner, RunBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _budget = budget ?? RunBudget.Default;
    }

    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls;
            }
        }
    }

    public TimeSpan Elapsed => _elapsed.Elapsed;

    public Task<LanguageModelResult> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyDictionary<string, JsonElement> jsonSchema,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_calls >= _budget.MaxModelCalls)
            {
                throw new BudgetExceededException(
                    $"This run has used its budget of {_budget.MaxModelCalls} model call(s). "
                    + "Raise the budget deliberately or split the work.");
            }

            if (_elapsed.Elapsed > _budget.EffectiveWallClock)
            {
                throw new BudgetExceededException(
                    $"This run exceeded its wall-clock budget of "
                    + $"{_budget.EffectiveWallClock.TotalMinutes:0} minute(s) after "
                    + $"{_calls} model call(s).");
            }

            _calls++;
        }

        return _inner.CompleteJsonAsync(systemPrompt, userPrompt, jsonSchema, cancellationToken);
    }
}
