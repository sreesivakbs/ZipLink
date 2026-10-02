using ZipLink.Agentic.Policy;

namespace ZipLink.Agentic.Orchestration;

/// <summary>
/// The stage graph plus the executor bound to each node.
///
/// Default shape:
/// <code>
/// requirements -> impact -> design* -> implement -+-> tests  -+-> release*
///                                                 +-> docs   -+
///                                                 +-> policy -+
/// </code>
/// (* needs human approval). <c>tests</c>, <c>docs</c> and <c>policy</c> are independent,
/// so they run together; <c>release</c> depends on all three, so it is the
/// synchronization point.
/// </summary>
public sealed class StagePipeline
{
    public const string Requirements = "requirements";
    public const string Impact = "impact";
    public const string Design = "design";
    public const string Implement = "implement";
    public const string Tests = "tests";
    public const string Policy = "policy";
    public const string Docs = "docs";
    public const string Release = "release";

    private readonly Dictionary<string, IStageExecutor> _executors;

    public StagePipeline(
        IReadOnlyList<StageDefinition> stages,
        IReadOnlyDictionary<string, IStageExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(executors);

        Validate(stages, executors);

        Stages = stages;
        _executors = new Dictionary<string, IStageExecutor>(executors, StringComparer.Ordinal);
    }

    public IReadOnlyList<StageDefinition> Stages { get; }

    public IStageExecutor Executor(string stageId)
    {
        return _executors.TryGetValue(stageId, out var executor)
            ? executor
            : throw new InvalidOperationException($"No executor registered for '{stageId}'.");
    }

    /// <summary>
    /// The default pipeline. <paramref name="designAgent"/> is optional: when no model is
    /// configured the design stage falls back to the stub, so the whole pipeline still
    /// runs offline and in CI. Either way the stage stays behind its approval gate.
    /// </summary>
    public static StagePipeline CreateDefault(
        ITestRunner testRunner,
        IStageExecutor? designAgent = null,
        IStageExecutor? implementAgent = null,
        IStageExecutor? docsAgent = null)
    {
        ArgumentNullException.ThrowIfNull(testRunner);

        StageDefinition[] stages =
        [
            new(Requirements, "Requirements analysis", [], RequiresApproval: false),
            new(Impact, "Impact analysis", [Requirements], RequiresApproval: false),
            new(Design, "Design proposal", [Impact], RequiresApproval: true),
            new(Implement, "Implementation", [Design], RequiresApproval: false),
            new(Tests, "Automated tests", [Implement], RequiresApproval: false),
            new(Docs, "Documentation", [Implement], RequiresApproval: false),
            new(Policy, "Policy gate", [Implement], RequiresApproval: false),
            new(Release, "Release readiness", [Tests, Docs, Policy], RequiresApproval: true)
        ];

        var executors = new Dictionary<string, IStageExecutor>(StringComparer.Ordinal)
        {
            [Requirements] = new RequirementsStageExecutor(),
            [Impact] = new ImpactStageExecutor(),
            [Design] = designAgent ?? new StubStageExecutor(
                Design,
                "No model configured; set ANTHROPIC_API_KEY to use the design agent. "
                + "A human reviews the impact report instead."),
            [Implement] = implementAgent ?? new StubStageExecutor(
                Implement,
                "No model configured; set ANTHROPIC_API_KEY to use the implementation "
                + "agent. No code was written."),
            [Tests] = new TestsStageExecutor(testRunner),
            [Policy] = new PolicyStageExecutor(),
            [Docs] = docsAgent ?? new StubStageExecutor(
                Docs,
                "No model configured; set ANTHROPIC_API_KEY to use the documentation "
                + "agent. Nothing was generated."),
            [Release] = new StubStageExecutor(
                Release, "Release readiness summary pending final human approval.")
        };

        return new StagePipeline(stages, executors);
    }

    /// <summary>
    /// Rejects a malformed graph up front: unknown dependencies, duplicate ids, missing
    /// executors, or a cycle. A scheduler that silently stalls on a bad graph is far
    /// harder to diagnose than one that refuses to start.
    /// </summary>
    private static void Validate(
        IReadOnlyList<StageDefinition> stages,
        IReadOnlyDictionary<string, IStageExecutor> executors)
    {
        if (stages.Count == 0)
        {
            throw new ArgumentException("A pipeline needs at least one stage.", nameof(stages));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stage in stages)
        {
            if (!ids.Add(stage.Id))
            {
                throw new ArgumentException($"Duplicate stage id '{stage.Id}'.", nameof(stages));
            }

            if (!executors.ContainsKey(stage.Id))
            {
                throw new ArgumentException(
                    $"Stage '{stage.Id}' has no executor.", nameof(executors));
            }
        }

        foreach (var stage in stages)
        {
            foreach (var dependency in stage.DependsOn)
            {
                if (!ids.Contains(dependency))
                {
                    throw new ArgumentException(
                        $"Stage '{stage.Id}' depends on unknown stage '{dependency}'.",
                        nameof(stages));
                }
            }
        }

        EnsureAcyclic(stages);
    }

    private static void EnsureAcyclic(IReadOnlyList<StageDefinition> stages)
    {
        var remaining = stages.ToDictionary(
            stage => stage.Id,
            stage => new HashSet<string>(stage.DependsOn, StringComparer.Ordinal),
            StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var settled = remaining
                .Where(entry => entry.Value.Count == 0)
                .Select(entry => entry.Key)
                .ToList();

            if (settled.Count == 0)
            {
                throw new ArgumentException(
                    "The stage graph contains a cycle: "
                    + string.Join(", ", remaining.Keys.Order(StringComparer.Ordinal)),
                    nameof(stages));
            }

            foreach (var id in settled)
            {
                remaining.Remove(id);
            }

            foreach (var dependencies in remaining.Values)
            {
                dependencies.ExceptWith(settled);
            }
        }
    }
}
