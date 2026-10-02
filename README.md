# ZipLink — Agentic SDLC System + URL Shortener

Two pieces of software in one repository:

1. **URL shortener (the product)** — an ASP.NET Core service: shorten a URL, redirect,
   count clicks. Contains no AI, ever.
2. **Agentic SDLC orchestrator (the factory)** — a local tool, with a web control panel
   and a CLI, that takes a requirement and runs it through an SDLC pipeline under human
   approval gates, with policy checks, bounded retries, rollback, safe-stop and an audit
   trail. Binds to localhost. Never deployed.

The orchestrator is the focus. The shortener is the sample workload it analyses.

```bash
dotnet build ZipLink.slnx
dotnet test  ZipLink.slnx        # 205 tests, ~3s

# The shortener, with a web page at https://localhost:7179
dotnet run --project src/ZipLink.Api --launch-profile https

# The agent control panel at http://127.0.0.1:5280 (localhost only)
dotnet run --project src/ZipLink.Studio
```

> If `dotnet` is not on your PATH, use the full path — see [SETUP.md](docs/SETUP.md).

---

## Where to start

| Document | Read it for |
|---|---|
| [docs/SETUP.md](docs/SETUP.md) | Prerequisites, build, test, run both halves, CLI reference, troubleshooting |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, the stage graph, control flow, gates, state and audit, key decisions |
| [docs/SCENARIOS.md](docs/SCENARIOS.md) | The three required scenarios, each as a recorded run with real output |
| [docs/TESTING.md](docs/TESTING.md) | Testing approach, limitations, trade-offs |
| [docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md) | **Start here for the whole account** — plan, rationale, validation, risks, limitations, what the exercise taught |
| [docs/adr/0001-orchestration-built-in-house.md](docs/adr/0001-orchestration-built-in-house.md) | Why the orchestration engine was written rather than adopted — with a measured comparison |
| [spikes/ZipLink.Spike.AgentFramework/](spikes/ZipLink.Spike.AgentFramework/README.md) | The same sub-graph built on Microsoft Agent Framework, and what that showed |
| [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) | The assignment, transcribed (read-only) |
| [PROJECT_BRIEF.md](PROJECT_BRIEF.md) | Our design and phase roadmap |
| [CLAUDE.md](CLAUDE.md) | Working rules and environment facts for AI-assisted development |

Recorded evidence is in [docs/runs/](docs/runs/) - `run.json`, the append-only
`audit.jsonl`, per-stage artifacts and the diffs agents produced, copied unedited. Two of
the three required scenarios did not finish cleanly, and are published as they happened.

---

## Current state

**Phase 3–4 — three real agents behind gates, with policy guardrails and budgets.**

Built:

- URL shortener: create, redirect, click analytics (in-memory storage)
- **Repository intelligence** — namespace, type and method inventory
- **Requirement analysis** — normalization, six ambiguity rule families, a clarification
  gate that stops a pipeline rather than guessing
- **Impact analysis** — ranks likely-impacted files with evidence, confidence, and an
  explicit account of what it could not find
- **Orchestration engine** — stage DAG with entry/exit gates, parallel execution with
  synchronization, human approval checkpoints, bounded retries, rollback, safe-stop,
  input-hash re-planning, append-only audit log, reliability metrics

- **Three real agents — `design`, `implement` and `docs`** (`claude-opus-5`), all
  schema-constrained and all behind human approval gates. They propose; they never
  decide. Agents run only when `ANTHROPIC_API_KEY` is set; otherwise those stages fall
  back to stubs so the pipeline stays runnable offline.
- **A deterministic policy gate** runs alongside tests and docs: no committed secrets, no
  unapproved NuGet packages. No model is consulted, so an agent cannot argue past it, and
  a violation fails outright — there is no "approve anyway".
- **Per-run budgets** cap model calls and wall-clock time, bounded by construction.
- **Two web pages.** The shortener has one for end users. **ZipLink Studio** is a
  localhost-only control panel where you submit a requirement, watch the stages run, and
  click Approve or Reject on a gate - the governance the CLI expresses as exit code 2,
  made visible.
- **The loop closes.** `implement` writes code into an **isolated detached git worktree**
  — never your working tree, and no branches are created — restricted to the files the
  approved design named. It compiles what it wrote, feeding compiler errors back to
  itself, then the real test suite runs *in that workspace* and decides pass/fail.
  Rollback is a genuine `git reset --hard`.
  [docs/runs/05-implement-agent/](docs/runs/05-implement-agent/) is a complete run with
  the diff it produced.

Not built, and not claimed anywhere:

- **`release` is still a stub** summary sitting behind its approval gate.
- **Nothing merges.** Agent work stays in the worktree for a human to review; no code
  reaches `main` without a person applying it.
- No database, no message queue, no cloud dependency.

Every check that *decides* pass/fail is deterministic — the test suite and the policy
scan. Only interpretation comes from a model. Tests never call one: all 205 run offline
against a fake. The only packages anywhere are ASP.NET Core OpenAPI, xUnit, and the
Anthropic SDK in the orchestrator alone — the URL shortener has no AI dependency of any
kind.

---

## Repository layout

```
src/ZipLink.Api              HTTP layer            -> Core, Infrastructure
src/ZipLink.Core             Domain + service      -> (nothing)
src/ZipLink.Infrastructure   In-memory storage     -> Core
src/ZipLink.Agentic          The orchestrator      -> Core
src/ZipLink.Studio           Control panel (web)   -> Agentic   [localhost only]
tests/ZipLink.Tests          205 tests             -> Core, Agentic
docs/                        Architecture, scenarios, setup, testing, ADRs, run evidence
```

Built with .NET 10 (SDK 10.0.401). The solution file is `ZipLink.slnx`; there is no
`.sln`.
