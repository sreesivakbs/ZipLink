# ZipLink — Agentic SDLC System + URL Shortener

Two pieces of software in one repository:

1. **URL shortener (the product)** — an ASP.NET Core service: shorten a URL, redirect,
   count clicks. Contains no AI, ever.
2. **Agentic SDLC orchestrator (the factory)** — a local CLI that takes a requirement and
   runs it through an SDLC pipeline under human approval gates, with policy checks,
   bounded retries, rollback, safe-stop and an audit trail. Never deployed.

The orchestrator is the focus. The shortener is the sample workload it analyses.

```bash
dotnet build ZipLink.slnx
dotnet test  ZipLink.slnx        # 116 tests, ~2s

# A requirement too vague to act on — stops and asks, exit code 2
dotnet run --project src/ZipLink.Agentic -- run "Make it handle more traffic"
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
| [docs/adr/0001-orchestration-built-in-house.md](docs/adr/0001-orchestration-built-in-house.md) | Why the orchestration engine was written rather than adopted — with a measured comparison |
| [spikes/ZipLink.Spike.AgentFramework/](spikes/ZipLink.Spike.AgentFramework/README.md) | The same sub-graph built on Microsoft Agent Framework, and what that showed |
| [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) | The assignment, transcribed (read-only) |
| [PROJECT_BRIEF.md](PROJECT_BRIEF.md) | Our design and phase roadmap |
| [CLAUDE.md](CLAUDE.md) | Working rules and environment facts for AI-assisted development |

Recorded evidence for the three scenarios is in [docs/runs/](docs/runs/) — `run.json`,
`audit.jsonl` and per-stage artifacts, copied unedited from the orchestrator's output.

---

## Current state

**Phase 2 — read-only repository and requirement intelligence.**

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

Not built, and not claimed anywhere:

- **No AI agents.** `design`, `implement` and `docs` are stubs that say so in their own
  artifacts. No model is called; the engine is being proven with fakes first, per the
  project brief.
- No code generation, no autonomous edits.
- No database, no message queue, no cloud dependency.

Everything is deterministic and dependency-free: the orchestrator uses no third-party
packages, and the only packages anywhere are ASP.NET Core OpenAPI and xUnit.

---

## Repository layout

```
src/ZipLink.Api              HTTP layer            -> Core, Infrastructure
src/ZipLink.Core             Domain + service      -> (nothing)
src/ZipLink.Infrastructure   In-memory storage     -> Core
src/ZipLink.Agentic          The orchestrator CLI  -> Core
tests/ZipLink.Tests          116 tests             -> Core, Agentic
docs/                        Architecture, scenarios, setup, testing, ADRs, run evidence
```

Built with .NET 10 (SDK 10.0.401). The solution file is `ZipLink.slnx`; there is no
`.sln`.
