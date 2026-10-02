# Final Engineering Summary

Assignment [core requirement 8](REQUIREMENTS.md#4-core-requirements): plan and rationale,
artifacts, risks, trade-offs, validation, assumptions, limitations.

---

## 1. What was asked, and what was built

Build a system where AI agents do software engineering work under human control, and prove
it by having that system build and change a URL shortener. The assignment names workflow
orchestration as the **critical differentiator** and leads its evaluation criteria with it.

Two pieces exist, sharing no runtime code:

- **The URL shortener** — ASP.NET Core: create, redirect, click analytics, with a guard
  against private and internal addresses. Contains no AI and never will; a redirect must
  be fast and deterministic.
- **The orchestrator** - a local tool, with a web control panel and a CLI, that takes a
  plain-English requirement and runs it through a stage graph under approval gates, policy
  checks, bounded retries, rollback and an audit trail. Localhost only. Never deployed.

**Status:** 16 commits, 240 tests, zero build warnings, format gate clean. Three agents
(`design`, `implement`, `docs`) plus two deterministic analysis stages. The loop closes:
a requirement becomes code that a real test suite judges.

## 2. Plan and rationale

The order of work was deliberate and is the main engineering decision in the project.

| Phase | Why in this order |
|---|---|
| Hand-built shortener | A sample workload to analyse and change. Without it the orchestrator has nothing real to reason about. |
| Deterministic analysis | Repository, requirement and impact analysis with no model involved. Cheap, reproducible, and the baseline an agent must beat. |
| Orchestration engine with **stub** agents | The graded differentiator. Proving ordering, joins, gates, retries, rollback and re-planning against fakes means those guarantees hold independently of any model — and stay held when agents arrive. |
| Real agents, one stage at a time | `design` first: it only produces a proposal behind an approval gate, so a bad answer costs nothing. Then `implement`, which writes code. |
| Governance | Policy gate, budgets, and the test-coverage guard — added last because the failures they prevent only became visible once agents were actually writing code. |

**Why the engine was written rather than adopted.** LangGraph and Microsoft Agent
Framework exist to supply exactly this. Adopting one would have replaced the capability
being assessed with a dependency. That reasoning was then *tested* rather than asserted:
[ADR 0001](adr/0001-orchestration-built-in-house.md) records a working Agent Framework
spike implementing the same sub-graph, and corrects the original argument — the framework
is better than ours at the graph, human interrupts and checkpointing, and supplies **none**
of the retries, rollback, safe-stop, audit or metrics that the assignment names.

## 3. Artifacts

| Artifact | Where |
|---|---|
| Working prototype | `src/`, run via [SETUP.md](SETUP.md) |
| Architecture overview | [ARCHITECTURE.md](ARCHITECTURE.md) |
| Three scenarios, executed and recorded | [SCENARIOS.md](SCENARIOS.md), [`runs/`](runs/) |
| Setup instructions | [SETUP.md](SETUP.md) |
| Testing approach, limitations, trade-offs | [TESTING.md](TESTING.md) |
| Decision record with a measured comparison | [adr/0001](adr/0001-orchestration-built-in-house.md) |
| Framework spike | [`spikes/ZipLink.Spike.AgentFramework/`](../spikes/ZipLink.Spike.AgentFramework/README.md) |
| Five recorded runs incl. diffs | [`runs/`](runs/) |

## 4. Validation

**Pass/fail is never an agent's opinion.** The `tests` stage shells out to `dotnet test`
in the agent's own workspace; the `policy` stage is pure pattern matching. Both can fail a
run and neither can be argued with — the policy stage deliberately offers no "approve
anyway", unlike the design and release gates.

Three layers:

1. **240 unit tests**, all offline. Agents are tested through a fake `ILanguageModel`, so
   the suite neither slowed nor became flaky when agents arrived.
2. **The engine's own guarantees** — parallelism asserted as a fact via a shared execution
   log, not hoped for; rollback asserted by checking artifacts are actually deleted.
3. **Five recorded end-to-end runs**, including two that failed.

The honest headline: **two of the three required scenarios did not finish cleanly**, and
both are published as they happened. A system whose guardrails never fire has not been
shown to have any.

## 5. Risks and trade-offs

| Decision | Bought | Cost |
|---|---|---|
| Build the engine, don't adopt one | The graded capability is ours and traceable to §4.4 | ~2,100 lines to maintain; no ecosystem, no distributed execution |
| Zero third-party deps until agents | No supply chain, readable in one sitting | Hand-written analysis where Roslyn would be exact |
| Lexical analysis, not semantic | Deterministic, instant, free | "TTL" never matches "expiration"; reported as a limitation in every run |
| Agents write to a detached worktree | Working tree and `main` untouchable; no branches created | Nothing merges automatically — a human must apply it |
| Full-file rewrites, not patches | Far more reliable from a model | Large outputs; hit a 16k ceiling before it was raised to 64k and streamed |
| Unit-level over end-to-end testing | ~2s suite that runs on every change | **No HTTP-level API tests** — the largest gap, documented |
| Stub agents before real ones | Every orchestration guarantee verified without a model | No test proves the engine against a *slow, non-deterministic* executor, which is what an LLM is |

## 6. Assumptions

- The assignment mandates **no technology** — verified, it names none. Every stack choice
  is ours and optional, so the simplest thing that satisfies a requirement won.
- A 2-3 day timebox makes orchestration depth worth more than infrastructure breadth. No
  database, queue or cloud service was added - links persist to a JSON file instead.
- Identifier names are meaningful enough for lexical impact analysis to be a useful hint.
- Scoring weights and thresholds throughout (impact: type 5 / method 4 / file 3; ambiguity
  High ≥ 4) are judgment, **not calibration**. No corpus was used.
- `main` has no remote, and the assignment PDF is marked client-internal; nothing is
  published anywhere.

## 7. Limitations

Stated plainly, because a summary that only lists successes is marketing.

**Not built.** The `release` stage is a stub summary. No distributed execution, durable
queues, provider fallback, or coverage threshold. `StageState.Skipped` is unreachable.

**Not merged.** Exactly one piece of agent-written code reached `main` — the
private-address guard — and only because a human read the diff and chose to take it.

**Known gaps.**

- **No HTTP-level API tests.** Route wiring, status codes and redirect behaviour were
  verified by hand and are unprotected against regression. Ironically, the greenfield
  agent tried to fix this and was stopped by the dependency gate.
- **Analysis is lexical.** Ambiguity rules are keyword lookups; a requirement vague in
  words outside the tables scores 0 and sails through.
- **The guard the agent wrote is bypassable** by alternative IP encodings
  (`http://2130706433/`). The design agent flagged this itself before the code was
  written; it is recorded, not silently fixed.
- **Routes do not match the project brief** (`/api/urls` vs `/links`). An open decision,
  not an oversight.
- **Budgets are not enforced across process restarts** — each invocation starts a fresh
  budget.

## 8. What the exercise actually taught

Three findings, each of which changed the system:

**A passing test suite proves nothing about coverage.** An agent asked to add aliases and
expiration rewrote a test file, added twelve methods for the new feature and silently
deleted fifteen security assertions. Every gate passed. It was found by reading the diff,
and the fix — fail the policy gate on a net loss of test cases — now exists because the
failure did.

**Guardrails are only demonstrated when they fire.** The greenfield run failed at the
policy gate because the agent added an unapproved package, and at the test stage because
one test was red. That run is better evidence than a clean success would have been.

**Limits are found by running the thing.** A 16k output ceiling silently truncated
multi-file changes and surfaced three stage failures as "invalid JSON". No amount of
reasoning about the design would have produced that; one real run did.

---

*Everything above is checkable: `git log` for the decisions, [`runs/`](runs/) for the
evidence, and `dotnet test ZipLink.slnx` for the claim that it works.*
