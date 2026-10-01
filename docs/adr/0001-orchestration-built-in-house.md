# ADR 0001 — Build the orchestration engine in-house

- **Status:** Accepted
- **Date:** 2026-10-01
- **Supersedes:** nothing
- **Validated:** by a working Microsoft Agent Framework spike — see [Validation](#validation--completed-2026-10-01).

---

## Context

The assignment requires an agentic orchestration layer coordinating the SDLC lifecycle,
and names it the **critical differentiator**. Its evaluation criteria begin with
*"effectiveness of agentic orchestration"* and include *"architecture/system design
quality"* and *"clarity and defensibility of decisions"*.

Mature frameworks exist for exactly this shape of problem:

- **LangGraph** — stateful graph execution with checkpointing and human-in-the-loop
  interrupts. Python/JS only; no official .NET support.
- **LangChain** — chains and agents; the .NET port is community-maintained and
  substantially less mature than the Python original.
- **Microsoft Agent Framework** — first-party .NET, successor to Semantic Kernel and
  AutoGen, with workflow and multi-agent primitives.
- **Semantic Kernel** — first-party .NET, widely deployed, being superseded.

The assignment mandates **no technology whatsoever** — it names no language, framework,
datastore or queue. Every stack choice is therefore ours to justify. The timebox is 2–3
days.

## Decision

**Build the orchestration engine in this repository**, with no third-party dependency.
Ten files in `src/ZipLink.Agentic/Orchestration/`: stage graph, state machine, gates,
persistence, append-only audit log, bounded retries, rollback, safe-stop, input-hash
re-planning and metrics.

Adopt a framework later only for a capability we do not have and would not want to write
— at present that means a **model client**, not an orchestrator.

## Rationale

**The frameworks' core value proposition is the deliverable.** LangGraph's purpose is
stateful graph orchestration with human interrupts: nodes, edges, checkpointing,
resumable state. That is a precise description of what is being assessed. Adopting it
would have replaced the graded capability with a dependency and left us demonstrating
configuration rather than design.

**Language fit.** The solution is .NET 10 / C#. LangGraph would mean a Python sidecar,
a second toolchain, a second test story and a cross-process boundary — real cost for a
repository whose exit gate is *"someone else can clone, run, and follow the reasoning."*

**Microsoft Agent Framework overlaps rather than complements.** Being .NET, it avoids the
polyglot problem, but it supplies workflow orchestration — the same ground as
`Orchestrator`, `StagePipeline` and `RunStore`. Using it would mean deleting working,
tested code to adopt an equivalent we did not write.

**The requirements are unusually specific.** Section 4.4 demands entry/exit gates,
decision lineage, bounded retries, fallback, rollback, safe-stop, policy guardrails,
audit-grade traceability, named reliability metrics, and dynamic re-planning when upstream
outputs change. A general framework supplies some of these and leaves the rest as
integration work. Writing them directly made each requirement traceable to code — and the
exercise surfaced decisions a framework would have hidden, such as whether a safety
refusal is a failure or a gate (we made it a gate).

**Dependency-free is defensible here.** No packages means no supply chain, no version
drift, no approval cycle, and a reviewer can read the entire engine in one sitting.

## What this costs

Stated honestly, because an ADR that only lists upsides is advocacy:

- **We wrote and must maintain ~2,100 lines** a framework would have supplied.
- **No ecosystem.** No community checkpointers, no tracing integrations, no persistence
  backends — our store is JSON on disk.
- **Features we skipped are genuinely missing**, not deliberately excluded: distributed
  execution, durable queues, multi-agent conversation patterns, streaming.
- **Hiring and onboarding.** An engineer who knows LangGraph knows LangGraph. Ours needs
  reading.
- **Framework fluency is not demonstrated by the engine itself.** For an assessment that
  was a real gap; the spike below closes it.

## Alternatives considered

| Option | Why not |
|---|---|
| **LangGraph** | Python-only; would replace the graded capability; cross-language boundary |
| **LangChain (.NET port)** | Community-maintained, less mature, same overlap problem |
| **Microsoft Agent Framework** | .NET and credible, but duplicates the engine we were asked to design |
| **Semantic Kernel** | Same overlap, and being superseded |
| **Microsoft Foundry** | Not an orchestration framework — a model hosting platform. Solves a different problem (model access and governance); remains an option when a model client is added |

## Consequences

- The engine is ours: the DAG, gates, lineage, retries, rollback, safe-stop, re-planning
  and metrics are all traceable to the assignment's section 4.4.
- `IStageExecutor` is the extension seam. A framework-backed or model-backed stage drops
  in without the engine changing — which is what made the spike below cheap to build.
- When agents arrive, the choice is a **model client**, not an orchestrator. The official
  Anthropic C# SDK is the current candidate and needs approval as a package addition.
- This decision is reversible per stage, not all-or-nothing.

## Validation — completed 2026-10-01

The claims above were tested, not asserted. A working spike implements the
`requirements → impact → design → (tests ∥ docs) → release` sub-graph on **Microsoft
Agent Framework** (`Microsoft.Agents.AI.Workflows` 1.23.0), calling the *same*
`RequirementAnalyzer` and `ImpactAnalyzer` so the comparison isolates orchestration. It
runs both paths end to end — the clarification gate and the approval gate — in ~310
lines. Code and full findings:
[`spikes/ZipLink.Spike.AgentFramework/`](../../spikes/ZipLink.Spike.AgentFramework/README.md).

| Capability | Agent Framework | ZipLink.Agentic |
|---|---|---|
| DAG definition | `WorkflowBuilder` + typed edges | `StagePipeline` + `DependsOn` |
| Conditional / dynamic gate | **Predicate on an edge** — declarative | `StageOutcome.Blocked` from the stage |
| Human interrupt | **`RequestPort` + `ExternalRequest`/`Response`, typed** | `AwaitingApproval` + a separate `approve` command |
| Parallel fan-out | `AddFanOutEdge` | Any stage whose dependencies are met |
| Synchronization | `AddFanInBarrierEdge` — gates timing, **streams individually** | Join runs **once**, after all dependencies succeed |
| State persistence | **`CheckpointManager`, `FileSystemJsonCheckpointStore`, `ResumeAsync`** | Hand-written `RunStore` |
| Event stream | **Rich** — supersteps, executor lifecycle, OpenTelemetry | Audit events |
| Bounded retries | **None in the API** | `RetryPolicy`, bounded by construction |
| Rollback / compensation | **None in the API** | Tick-scoped rollback, artifacts deleted |
| Safe-stop | **None** | Stop file, honoured between stages |
| Audit log | **None** (telemetry only) | Append-only JSONL, 17 event types |
| Reliability metrics | **None** | Success rate, retries, rollbacks, latency split |
| Governance vocabulary | **None** — no "blocked" vs "failed" | Exit code 2 is distinct from failure |
| Multi-agent patterns | **Sequential / Concurrent / GroupChat / Handoff / Magentic, sub-workflows** | None |

### What the spike changed in this decision

**Nothing in the outcome — but two claims above were too strong.** The original rationale
implied a framework would supply "some" of section 4.4. Measured: it supplies the graph,
the human interrupt and persistence *well* — better than ours on all three — and supplies
**none** of retries, rollback, safe-stop, audit or metrics. Searching the public API for
`Retry`, `Rollback`, `Resilien`, `Policy` or `Compensat` returns no types. So the
governance half of 4.4 was always going to be ours to write.

Conversely, the spike showed the framework is **ahead of us** on human-in-the-loop
ergonomics, checkpointing and multi-agent patterns. `RequestPort` is a cleaner primitive
than our approve-command pairing, and if this system grows beyond one process, their
checkpointing is the thing to reach for.

### Friction found

- **The documented replacement API does not compile at 1.23.0.** `ReflectingExecutor<T>`
  and `IMessageHandler<,>` are `[Obsolete]`, directing callers to `[MessageHandler]` on a
  partial class deriving from `Executor` — which needs a source generator that **ships in
  none** of the three packages. Deriving from `Executor` fails with `CS0534`. The spike
  uses the obsolete API behind a narrow, documented suppression.
- **Fan-in semantics differ from the brief's model.** The barrier waits for all sources,
  then delivers messages one at a time; `release` executed twice. Aggregation is the
  caller's job.
- **Yielding a workflow output requires declaring the output type first**, or
  `YieldOutputAsync` throws at runtime.

### Conclusion

**The decision stands.** For a production .NET agent workflow, Agent Framework would be
the sensible starting point. For this assignment it would have replaced the capability
being assessed while leaving the governance machinery — the majority of section 4.4 — to
be written anyway.

The honest revision is that this is a closer call than the original rationale implied,
and the reason is narrower: not that a framework adds little, but that what it adds is
the half we were least being graded on.
