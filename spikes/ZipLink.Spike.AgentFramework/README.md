# Spike — Microsoft Agent Framework Workflows

A working implementation of the `requirements → impact → design → (tests ∥ docs) → release`
sub-graph on **Microsoft Agent Framework** (`Microsoft.Agents.AI.Workflows` 1.23.0),
built to validate the claims in
[ADR 0001](../../docs/adr/0001-orchestration-built-in-house.md).

It calls the **same** `RequirementAnalyzer` and `ImpactAnalyzer` as the production
orchestrator, so every difference observed is a difference in orchestration, not in
analysis. No model is called.

---

## Running it

This project is deliberately **not** in `ZipLink.slnx`, so it cannot affect the main
build or the 116-test suite. Build and run it on its own:

```bash
dotnet run --project spikes/ZipLink.Spike.AgentFramework -- "Add custom short names and link expiration"
dotnet run --project spikes/ZipLink.Spike.AgentFramework -- "Make it handle more traffic"
```

The first proceeds to the design approval gate; the second is routed by a conditional
edge to the clarification gate and never reaches impact analysis. Gates are
auto-answered so the spike runs unattended — the workflow genuinely suspends either way.

## Shape of it

```csharp
var workflow = new WorkflowBuilder(requirements)
    .AddEdge<RequirementSummary>(requirements, clarificationPort, s => s is { NeedsClarification: true })
    .AddEdge<RequirementSummary>(requirements, impact,            s => s is { NeedsClarification: false })
    .AddEdge(clarificationPort, clarified)
    .AddEdge(impact, approvalPort)
    .AddEdge(approvalPort, design)
    .AddFanOutEdge(design, [tests, docs])
    .AddFanInBarrierEdge([tests, docs], release)
    .Build();
```

~310 lines total, including both gates, the host response loop and all seven executors.

---

## Findings

### Where the framework is better

**Human-in-the-loop is a first-class primitive.** `RequestPort.Create<TRequest,TResponse>`
suspends the run and surfaces an `ExternalRequest`; the host replies with
`run.SendResponseAsync(request.CreateResponse(value))`. Typed in both directions. We
built the equivalent by hand as `AwaitingApproval` plus a separate `approve` command.

**Conditional edges are declarative.** The dynamic clarification gate is a predicate on
an edge. In our engine the same decision is a `StageOutcome.Blocked` returned by the
stage — ours is more explicit, theirs is more composable.

**Checkpointing is supplied.** `CheckpointManager`, `ICheckpointStore<T>`,
`FileSystemJsonCheckpointStore`, `InProcessExecution.ResumeAsync(...)`. We hand-wrote
`RunStore` for this.

**Richer event stream.** `SuperStepStartedEvent` / `SuperStepCompletedEvent` (their
"superstep" is our tick), `ExecutorInvoked/Completed/Failed`, `WorkflowOutputEvent`,
`SubworkflowError/Warning`. Plus OpenTelemetry integration via
`OpenTelemetryWorkflowBuilderExtensions`.

**Patterns we have no answer to:** sub-workflows, and specialized builders —
`SequentialWorkflowBuilder`, `ConcurrentWorkflowBuilder`, `GroupChatWorkflowBuilder`,
`HandoffWorkflowBuilder`, `MagenticWorkflowBuilder`. Multi-agent conversation shapes are
well ahead of anything we have.

### Where we are better, for this assignment

**Fan-in does not aggregate.** `AddFanInBarrierEdge` gates *timing* — it holds messages
until every source has produced one — then **streams them individually**. `release` ran
**twice**, once per branch, and counting arrivals was our job
(`AggregatingExecutor<,>` exists for this but is extra work). In ZipLink.Agentic a stage
with several dependencies runs exactly once, after all of them succeed.

**No retry, rollback or compensation types exist** in the package — searching the public
API for `Retry`, `Rollback`, `Resilien`, `Policy` or `Compensat` returns nothing. Our
bounded retries, tick-scoped rollback and safe-stop would have to be built on top either
way, which is a large part of assignment section 4.4.

**No audit log or reliability metrics.** Telemetry hooks exist, but the append-only
decision record, success rate, retry/rollback counts and the split of end-to-end latency
into execution versus human wait are all ours.

**No governance vocabulary.** Nothing distinguishes "blocked awaiting a human" from
"failed". Our exit-code contract (`2` = blocked, not an error) is a deliberate design
decision the framework leaves open.

### Friction worth recording

**The documented replacement API does not compile.** `ReflectingExecutor<T>` and
`IMessageHandler<,>` are `[Obsolete]` at 1.23.0, directing callers to `[MessageHandler]`
methods on a partial class deriving from `Executor`. That needs a source generator to
implement the abstract `Executor.ConfigureProtocol` — and **no analyzer ships** in
`Microsoft.Agents.AI.Workflows`, `.AI`, or `.Abstractions` at this version. Deriving from
`Executor` directly fails with `CS0534`. The obsolete API is the only working option, so
it is used here behind a narrow `#pragma warning disable CS0618` with that explanation.

**Yielding a workflow output requires declaring it.** A void-returning handler calling
`context.YieldOutputAsync(...)` fails at runtime with
`Cannot output object of type String. Expecting one of []`. Output types must be declared
first, via `ExecutorOptions.AutoYieldOutputHandlerResultObject` or protocol
configuration.

---

## Verdict

Microsoft Agent Framework is a credible, well-built orchestration framework, and if the
goal were to *ship* an agent workflow on .NET it would be the obvious starting point —
especially for the checkpointing and multi-agent patterns.

For **this assignment** the conclusion in ADR 0001 stands. The framework supplies the
graph, the human interrupt and persistence; it supplies none of the retry, rollback,
safe-stop, audit or metrics machinery that section 4.4 asks for by name, and the fan-in
semantics differ from what the brief describes. Adopting it would have replaced the part
we are assessed on while leaving most of the governance work to be done anyway.
