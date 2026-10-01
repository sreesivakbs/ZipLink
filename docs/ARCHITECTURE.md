# Architecture

How the system is put together, how work flows through it, and which decisions were
deliberate. For what was asked for, see [REQUIREMENTS.md](REQUIREMENTS.md); for the
phase plan, see [../PROJECT_BRIEF.md](../PROJECT_BRIEF.md).

---

## 1. Two separate systems

The repository holds two pieces of software that must not be confused.

| | **URL shortener** (the product) | **Agentic SDLC orchestrator** (the factory) |
|---|---|---|
| What | Shorten a URL, redirect, count clicks | Takes a requirement and runs it through an SDLC pipeline under human control |
| Users | Anyone sharing links | A software engineer |
| Runs as | ASP.NET Core web service | Local CLI |
| Contains AI | **Never** | Yes - one agent today (see §7) |
| Deployed | Yes | **Never** |

The orchestrator is the focus of the assignment. The shortener is the sample workload it
analyses and, in a later phase, changes.

**Why the separation is absolute:** a redirect must be fast and deterministic. Putting a
model call anywhere in that path would make the product slower, non-deterministic, and
dependent on a third party for its core function. The two halves share no runtime code —
only `ZipLink.Agentic` references `ZipLink.Core`, and only to read it.

---

## 2. Components

```
src/
  ZipLink.Api              HTTP layer: create, redirect, analytics       -> Core, Infrastructure
  ZipLink.Core             ShortUrl, IShortUrlRepository, service        -> (nothing)
  ZipLink.Infrastructure   InMemoryShortUrlRepository                    -> Core
  ZipLink.Agentic          The orchestrator (CLI)                        -> Core
tests/
  ZipLink.Tests            129 tests                                     -> Core, Agentic
```

Inside `ZipLink.Agentic`, five layers, each usable on its own:

| Namespace | Responsibility |
|---|---|
| `Text` | Splits prose and identifiers into comparable word stems |
| `Repository` | Scans `.cs` files for namespace, types, methods |
| `Requirements` | Normalizes a request, detects ambiguity, owns the clarification gate |
| `Impact` | Ranks the files a requirement is likely to touch, with evidence |
| `Orchestration` | The engine: stage graph, state, gates, persistence, audit, metrics |

`Requirements` and `Impact` depend on `Text`; `Impact` also depends on `Repository`.
`Orchestration` depends on all of them but only through `IStageExecutor`, so a stage can
be swapped without touching the engine.

---

## 3. Orchestration model

Execution walks a **directed acyclic graph**, not a list.

```
                              ┌─────────┐
                              │ release │◄──────────┐  * human approval
                              └────▲────┘           │
                                   │                │
                  ┌────────────────┴───┐        ┌───┴────┐
                  │       tests        │        │  docs  │
                  └────────▲───────────┘        └───▲────┘
                           └──────────┬────────────-┘
                                 ┌────┴──────┐
                                 │ implement │
                                 └────▲──────┘
                                 ┌────┴───┐
                                 │ design │ *
                                 └────▲───┘
                                 ┌────┴───┐
                                 │ impact │
                                 └────▲───┘
                              ┌───────┴───────┐
                              │ requirements  │ (clarification gate)
                              └───────────────┘
```

On each tick the engine collects **every** stage whose dependencies are satisfied and
runs them together. Two consequences fall out of the graph rather than being coded
specially:

- **Parallelism** — `tests` and `docs` both depend only on `implement`, so they run
  concurrently.
- **Synchronization** — `release` depends on both, so it cannot start until both finish.
  The join is a property of the edges, not a barrier primitive.

A malformed graph — a cycle, an unknown dependency, a stage with no executor — is
rejected when the pipeline is constructed, not discovered as a stall at runtime.

### Stage state machine

```
Pending ──► Ready ──► Running ──┬──► Succeeded
                                ├──► AwaitingApproval ──(approve)──► Succeeded
                                │                     ──(reject)───► Failed
                                ├──► Retrying ──► Running   (bounded)
                                └──► Failed ──► RolledBack
```

`Skipped` exists for conditional stages and is currently unreachable. Re-planning returns
a stage to `Pending`.

---

## 4. Control flow

A run is not a single process invocation. Start, approve, resume and stop are separate
commands against state on disk.

```
run "requirement"
      │
      ├─ requirements ──► ambiguous? ──yes──► AwaitingApproval  ─┐  exit 2
      │                      │ no                                │
      ├─ impact ─────────────┘                                   │
      ├─ design ────────────────────────────► AwaitingApproval  ─┤  exit 2
      │                                                          │
      │   (human runs: approve <run> <stage>)  ◄─────────────────┘
      │
      ├─ implement
      ├─ tests ∥ docs          ← real `dotnet test` decides pass/fail
      └─ release ──────────────────────────► AwaitingApproval ──► Succeeded
```

**Exit codes are part of the contract.** `0` completed, `1` invalid input or failed run,
`2` blocked awaiting a human. A governance stop is *not* a failure, and automation must
not be able to read it as success.

### Where the gates are

| Gate | Kind | Fires when |
|---|---|---|
| `requirements` exit | Dynamic | The requirement scores High for ambiguity |
| `design` exit | Policy | Always — design is high-impact by definition |
| `release` exit | Policy | Always — final sign-off before anything ships |

Both kinds land in the same `AwaitingApproval` state, so the engine has one pause
mechanism rather than two.

---

## 5. State, lineage and audit

Everything a run produces is written to `.ziplink/runs/<runId>/`:

```
run.json          current state of every stage (status, attempts, timings, input hash)
audit.jsonl       append-only, one JSON object per line
artifacts/        one JSON file per stage output
STOP              present only while a safe-stop is pending
```

State is saved after **every** transition, which is what makes approve-later,
resume-after-kill and cross-process inspection work.

**Cross-stage context** flows through `StageContext.Artifacts`: each stage receives the
outputs of its completed upstream stages and reads them typed. `impact` reads the
*normalized* requirement from the `requirements` artifact rather than re-deriving it from
the raw text — that is the decision lineage, made concrete.

**Audit** is 17 event types (`RunStarted`, `StageReady`, `StageStarted`, `StageSucceeded`,
`StageBlocked`, `StageAwaitingApproval`, `StageApproved`, `StageRejected`, `StageRetrying`,
`StageRolledBack`, `StageInvalidated`, `RunStopped`, …), each with a UTC timestamp, the
stage, the actor (`system` or the username) and a detail string. Appended under a lock so
parallel stages cannot interleave a partial line.

**Metrics** are derived from that record, never measured in memory — so a report can be
produced for a run that finished in an earlier process. End-to-end latency is reported
separately from time in stages and time waiting on humans, because otherwise the headline
number mostly measures how fast the reviewer was.

---

## 6. Reliability controls

| Control | Behaviour |
|---|---|
| **Bounded retries** | `RetryPolicy.MaxAttempts` is a constructor parameter. There is no retry-forever setting. |
| **Rollback** | When a stage exhausts its retries, work that succeeded *in the same tick* is marked `RolledBack` and its artifacts deleted. Earlier ticks are untouched. |
| **Safe-stop** | A stop request is a file; the engine checks it **between** stages only, so a stop never interrupts work in flight. |
| **Re-planning** | Each stage records a SHA-256 fingerprint of its inputs. If an upstream artifact is edited, that stage's dependents are invalidated and re-run — the edited stage itself is untouched, because *its* inputs did not change. |

Rollback is currently artifact-scoped. Nothing writes application code yet, so there is
nothing else to undo; once an implementation agent exists this becomes a git reset to the
previous stage's commit.

---

## 7. Key decisions

**Orchestration was built, not adopted.** LangGraph and Microsoft Agent Framework exist
precisely to provide stateful graph execution with human interrupts. Adopting one would
have replaced the capability the assignment calls the critical differentiator with a
dependency. See [adr/0001-orchestration-built-in-house.md](adr/0001-orchestration-built-in-house.md).

**Agents arrive one stage at a time.** The brief requires the engine to be proven with
stub agents before real ones are plugged in, so it was. `requirements` and `impact` are
real but deterministic; `design` is a real Claude-backed agent; `implement` and `docs`
remain stubs that label themselves as such in their artifacts. `IStageExecutor` is the
seam each agent drops into, which is why adding one changed no engine code.

**The agent proposes; it never decides.** Its output is schema-constrained and parsed,
so a malformed answer is an ordinary stage failure that feeds the existing bounded-retry
policy. A refusal becomes a gate rather than a failure. And `design` keeps its approval
gate, so nothing an agent writes is acted on without a human.

**No credential reaches a stage.** The SDK reads `ANTHROPIC_API_KEY` from the
environment itself; no key is held in a field, written to an artifact, or logged. With no
key set, the stage falls back to the stub and the pipeline still runs.

**Deterministic checks decide pass/fail.** The `tests` stage shells out to `dotnet test`.
An agent will never be asked whether its own work was correct.

**One third-party dependency in the orchestrator**, and only since the design agent: the
Anthropic SDK. Everything else is hand-written - analysis, persistence as JSON on disk,
`System.Text.Json` and SHA-256 from the BCL. The assignment mandates no technology, and
the 2-3 day timebox is better spent on orchestration than on infrastructure. The URL
shortener depends on none of it.

**Artifacts are small flat records**, not serialized analyzer models — they are written
to disk, read back by downstream stages, and meant to be legible to a human reading the
run folder.

**Rendering is separate from logic** (`ImpactReportRenderer`, `RunReportRenderer`,
`RunMetricsRenderer`), so output format is asserted in tests rather than eyeballed.

---

## 8. What is not built

Stated plainly, because an architecture document that only describes what exists is
marketing:

- **Only one agent.** `implement` and `docs` are still stubs; only `design` calls a model.
- **No code generation** and no autonomous edits.
- **No persistence beyond JSON files** — no database, no migrations.
- **No budgets** (max LLM calls, tokens, wall-clock) — nothing consumes them yet.
- **No provider fallback** — there is no provider.
- **Analysis is lexical, not semantic.** A requirement saying "TTL" will not find code
  saying "expiration". Limitations are listed in [TESTING.md](TESTING.md#limitations).
