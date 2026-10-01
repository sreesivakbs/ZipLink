# ADR 0001 — Build the orchestration engine in-house

- **Status:** Accepted
- **Date:** 2026-10-01
- **Supersedes:** nothing
- **Open follow-up:** a Microsoft Agent Framework spike is planned to validate the
  comparison below with working code. Until it lands, the "what the framework gives you"
  column is based on documentation and prior experience, not measurement — see
  [Validation](#validation).

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
- **Framework fluency is not demonstrated by this code.** For an assessment, that is a
  real gap — hence the planned spike.

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
  in without the engine changing — which is what makes the planned spike cheap.
- When agents arrive, the choice is a **model client**, not an orchestrator. The official
  Anthropic C# SDK is the current candidate and needs approval as a package addition.
- This decision is reversible per stage, not all-or-nothing.

## Validation

A spike implementing the `requirements → impact → design` sub-graph on **Microsoft Agent
Framework**, isolated under `spikes/` and excluded from the main build, will test the
claims above. Its findings — including anything this ADR gets wrong — will be recorded
here as an update.

Planned comparison: DAG definition · entry/exit gates · parallelism and synchronization ·
state persistence · human interrupt · bounded retries · rollback · audit trail ·
reliability metrics.
