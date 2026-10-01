# Project Brief — Agentic SDLC System + URL Shortener

## 1. The goal in one sentence
Build a system where AI agents do software engineering work under rules a human sets,
with the human acting as the manager who approves anything important — and prove it
works by having it build and improve a URL shortener.

## 2. The two pieces

### Piece 1: URL shortener (the product)
- **Users:** anyone sharing links (e.g. a marketer putting a link in an email campaign).
- **What it does:** paste a long URL → get a short URL → visitors who click the short URL are
  redirected to the long one → the owner can see click counts.
- **Expectations:** fast redirects, high availability, never redirects to the wrong place,
  refuses dangerous URLs (e.g. `javascript:` schemes, private/internal IP addresses).
- **Contains no AI.**

Baseline API (Phase 1, hand-built):
- `POST /links` — create a short link
- `GET /{code}` — redirect
- `GET /links/{code}/stats` — click count
- Health check endpoint, structured logging, input validation, rate limiting

Deliberately NOT in the baseline (the agents add these later):
- Custom short names / aliases (brownfield scenario)
- Link expiration (brownfield scenario)
- Redis caching and Kafka-based click tracking (ambiguous scenario)

### Piece 2: Agentic SDLC orchestrator (the factory — the main focus)
- **Users:** a software engineer / tech lead.
- **What it does:** takes a feature request in plain words and runs it through a fixed set of
  stages, each done by an AI agent, with deterministic checks and human approval gates.
- **Runs on:** the developer's machine as a CLI. Not deployed publicly.
- **Output:** a git branch / pull request with code, tests, docs, plus a run report and audit log.
  The normal review + CI + deploy process takes it from there.

## 3. How a developer uses the orchestrator (example)
Request: "Let users choose their own short link names."

1. `sdlc run "Let users choose their own short link names"` starts run #42.
2. **Requirements agent** asks clarifying questions and lists assumptions
   (duplicates allowed? allowed characters? max length? reserved words?).
   ⏸ Human approves or edits.
3. **Analysis agent** uses the Roslyn dependency-graph tool to find affected files and APIs,
   then the plan is broken into tasks.
4. **Design agent** proposes changes. A database schema change → ⏸ mandatory human approval.
5. **Implementation agent** writes code in a sandboxed git workspace. Real `dotnet test` decides
   pass/fail. Bounded retries (e.g. max 3); if still failing → rollback to the last good commit.
6. **Tests agent** and **Docs agent** run in parallel, then synchronize.
7. **Policy gates:** no secrets, no unapproved dependencies, tests pass, coverage threshold met.
8. **Release readiness** → ⏸ final human approval → pull request created.

Other controls:
- `sdlc stop <run>` — safe-stop: halts cleanly, preserves state, leaves nothing half-done.
- Re-planning: if an approved upstream output changes (e.g. max length 30 → 20), only the
  downstream stages that depend on it are invalidated and re-run (input-hash based).
- `sdlc report <run>` — every step, retry, approval, timing, and metrics.

## 4. Orchestrator requirements checklist (from the assignment)
- Explicit dependency graph (DAG) of stages with entry/exit gates
- Sequential and parallel paths with synchronization
- Cross-stage context and decision lineage preserved
- Human approval checkpoints for high-impact actions
- Bounded retries, fallback (e.g. secondary LLM provider), rollback, safe-stop
- Policy guardrails: security, compliance, change control
- Audit-grade observability and traceability (append-only audit log, trace IDs)
- Reliability metrics: success rate, retry/rollback frequency, MTTR, end-to-end latency
- Dynamic re-planning when upstream outputs change
- Budgets: max LLM calls, tokens, and wall-clock time per run

## 5. Core design principles
1. Agents are never in the URL shortener's runtime path.
2. Agents produce work; deterministic checks judge it; humans approve high-impact decisions.
3. Facts come from code (Roslyn, test results); interpretation comes from AI.
4. Every stage commits to git, so rollback = reset to the previous stage's commit.
5. The engine is proven with fake (stub) agents before real LLM agents are plugged in.
6. Agents never see secrets; sandbox environments have secrets stripped.
7. Unit/CI tests never call a real LLM (fakes or recorded replays only).

## 6. The three required scenarios
- **Greenfield:** "Build a URL shortener." Compare the agents' output to the hand-built baseline.
- **Brownfield:** "Add custom short names and link expiration" to the existing code.
  Must show impact analysis and a mandatory schema-change approval.
- **Ambiguous:** "Make it handle more traffic." The requirements agent must ask questions,
  record assumptions, and propose options (e.g. Redis cache, async click tracking via Kafka).
  The human chooses; the system proceeds and shows a measurable before/after.

## 7. Roadmap and exit gates
| Phase | Goal | Done when |
|---|---|---|
| 0 Foundation | Solution, Aspire, analyzers, user secrets, gitleaks, CI | CI green; a fake key is blocked from commit |
| 1 Shortener | Hand-built baseline API + tests | Shorten/redirect/stats work; integration tests pass on real SQL Server |
| 2 Engine | DAG, state machine, parallelism, retries, approvals, persistence (fake agents) | Full run succeeds; survives kill + resume |
| 3 Agents | Real LLM agents (structured output) + Roslyn tool | A requirement produces code that passes real tests |
| 4 Governance | Policy gates, rollback, safe-stop, budgets, provider fallback | Planted secret / failing tests / budget overrun are blocked or rolled back |
| 5 Metrics | Audit trail, metrics report, hash-based re-planning | Editing design re-runs only downstream stages; report prints |
| 6 Scenarios | Run all three scenarios | Each has a recorded run: plan, approvals, audit log, metrics |
| 7 Summary | Architecture, ADRs, setup, testing approach, limitations | Someone else can clone, run, and follow the reasoning |

Stage state machine:
`PENDING → READY → RUNNING → AWAITING_APPROVAL → SUCCEEDED`
with side states `RETRYING`, `FAILED`, `ROLLED_BACK`, `SKIPPED`.

## 8. Glossary
- **Agent:** an LLM-backed worker that performs one stage and returns structured (JSON) output.
- **Gate:** a check that must pass before a stage can start (entry) or finish (exit).
- **Deterministic check:** a non-AI check with a fixed answer (tests, analyzers, secret scans).
- **Run:** one execution of the pipeline for one feature request.
- **Audit log:** append-only record of every action, input, decision, and approval.
