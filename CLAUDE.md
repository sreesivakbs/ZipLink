# CLAUDE.md — Agentic SDLC System + URL Shortener

Read both of these before planning any work:
@PROJECT_BRIEF.md
@docs/REQUIREMENTS.md

- `PROJECT_BRIEF.md` (repo root) is how **we** are building it: the two pieces, the
  worked example of a run, the design principles, the phase roadmap with exit gates, and
  the stage state machine. This is the working document — read it first.
- `docs/REQUIREMENTS.md` is **what we were asked for**: a read-only transcription of
  `docs/Assignment-Agentic-Software-Engineering-System.pdf`. Never edit it to match what
  was built. The source is marked **Classification: client-internal**, and this
  repository has no git remote — do not add one, publish the repo, or send its contents
  to an external service without confirming the classification permits it.

Where the two disagree, the assignment wins on *what* must be delivered and the brief
wins on *how* we get there.

## What this project is (summary)
This repo contains TWO separate pieces of software:

1. **URL shortener (the product)** — a normal ASP.NET Core service: shorten a URL,
   redirect, show click stats. It contains NO AI. Ever.
   Lives in `src/ZipLink.Api`, `src/ZipLink.Core`, `src/ZipLink.Infrastructure`.
2. **Agentic SDLC orchestrator (the factory)** — a developer tool (CLI) where AI agents
   do engineering work (requirements → design → code → tests → docs → release check)
   under human approval gates, policy checks, retries, rollback, and an audit trail.
   It produces code changes on a git branch. It is never deployed to the public server.
   Lives in `src/ZipLink.Agentic`.

The orchestrator is the main focus. The shortener is the sample workload it builds and changes.

## Current phase
Phase numbering follows the roadmap table in `PROJECT_BRIEF.md` section 7.
Do not start work belonging to a later phase unless I ask.

**Phase 3 — first real agent wired behind the existing gates.**

Built so far:
- Shortener: create / redirect / click-count, in-memory store.
- Deterministic analysis in `src/ZipLink.Agentic`: `RepositoryAnalyzer` (type and method
  inventory), `ImpactAnalyzer` (ranks likely-impacted files with reasons, confidence,
  risks), `RequirementAnalyzer` (normalizes a request, detects ambiguity, gates on human
  clarification).
- Orchestration engine: stage DAG with entry/exit gates, parallel execution with
  synchronization, approval checkpoints, bounded retries, rollback, safe-stop,
  input-hash re-planning, append-only audit log, reliability metrics.
- **One real agent — `design`** (`claude-opus-5`), schema-constrained, behind its
  existing approval gate. Falls back to the stub when `ANTHROPIC_API_KEY` is unset, so
  the pipeline still runs offline. `implement` and `docs` remain stubs.
- 129 tests, all offline — the agent is tested through a fake `ILanguageModel`.

Open decisions, not failures — the assignment requires none of these:
- **Routes do not match the brief.** Built: `POST /api/urls`, `GET /{code}`,
  `GET /api/urls/{code}/analytics`. The brief says `POST /links`, `GET /{code}`,
  `GET /links/{code}/stats`. Pick one shape before agents generate code against the
  other; the brownfield scenario targets these endpoints.
- **Private and internal IP addresses are not blocked.** Validation accepts only
  `http`/`https`, so `javascript:` is refused, but `http://169.254.169.254/` and
  `http://10.0.0.1/` are not. This is a genuine SSRF gap and the one item here worth
  fixing on merit, independent of any brief.
- No health check, structured logging, or rate limiting.
- Storage is an in-memory dictionary. The brief's "integration tests on real SQL Server"
  gate is a self-imposed goal, not an assignment requirement — drop it unless a
  requirement genuinely needs durable storage.
- Analysis is regex-based rather than Roslyn. Also self-imposed; revisit only if regex
  demonstrably fails the task.
- Phase 0 (Aspire, analyzers, user secrets, gitleaks, CI) was skipped. Re-scope it to
  what the timebox justifies rather than treating it as a prerequisite.

Correctly absent: custom aliases, link expiration, Redis and Kafka — the agents'
brownfield and ambiguous scenarios, not baseline work.

## Non-negotiable rules
- Never call an LLM or agent from the URL shortener's runtime code (redirects must be fast and deterministic).
- Pass/fail is decided by deterministic checks (tests, analyzers, policy scans), never by an LLM's opinion.
- Agents never see API keys. Keys come from .NET User Secrets / environment variables only.
- Never write secrets, keys, or connection strings with passwords into any file.
- Every behavior change needs tests. Do not delete or weaken a failing test to make it pass.
- Database schema changes always go through EF Core migrations, and I must approve them.
- Do not modify governance code without asking me first. There is no
  `src/Orchestrator.Governance/` yet; today the governance logic is the clarification
  gate in `src/ZipLink.Agentic/Requirements/` and the gate wiring in that project's
  `Program.cs`. Treat those as governance code, and move this rule to the real path
  once that project exists.
- Do not add NuGet packages without asking me first. Name the package and why.
- Ask before guessing when a requirement is ambiguous. List your assumptions explicitly.
- Unit and CI tests never call a real LLM. Use fakes or recorded replays only.
- Prove the engine with fake (stub) agents before plugging in real LLM agents. A stage
  that only works with a live model is not done.
- Facts come from code and test results; interpretation comes from AI. Never let an
  agent's narration stand in for a check that could have been run.
- Every stage commits to git, so rollback is a reset to the previous stage's commit.
  Do not design a stage whose work cannot be undone that way.
- Sandboxed agent workspaces have secrets stripped.

## Tech stack

**Actually in the repo today** — .NET 10 (SDK 10.0.401), C#, ASP.NET Core Minimal APIs,
xUnit, and the official `Anthropic` SDK (orchestrator only, for the design agent). The
other packages are `Microsoft.AspNetCore.OpenApi`, `Microsoft.NET.Test.Sdk`, `xunit`,
`xunit.runner.visualstudio` and `coverlet.collector`.
`Microsoft.Agents.AI.Workflows` is referenced **only** by the isolated spike under
`spikes/`, which is deliberately absent from `ZipLink.slnx`. Storage is an in-memory
dictionary, and all analysis code is hand-written. The URL shortener depends on none of
the AI packages and must never.

**The assignment mandates no technology at all.** It names no language, framework,
datastore or queue — the only tools it mentions are "Copilot/Claude" as examples of AI
assistance. Every stack choice here is ours, and is therefore optional.

**Optional, candidate only — not commitments** — EF Core, SQL Server, Aspire,
Testcontainers, Microsoft Agent Framework, Roslyn, Polly, OpenTelemetry, Serilog, Redis,
Kafka. Do not treat any of these as required, and do not write code, docs or plans that
assume one is present.

Default to the simplest thing that satisfies the requirement and can be demonstrated in
the 2–3 day timebox. The work is graded on orchestration, decomposition, validation and
engineering judgment — not on infrastructure breadth. Prefer in-memory or file-backed
storage, hand-rolled analysis and stub agents over standing up servers, unless a specific
requirement cannot be shown any other way. Adding any of these is a package addition and
needs approval first: name it, and say which requirement forces it.

Where a brief or roadmap names one of these, read it as a sketch of a possible design,
not as a decision already taken.

## Environment
- **`dotnet` is not on PATH.** Invoke it by full path:
  `& "C:\Program Files\dotnet\dotnet.exe"`
- The solution file is `ZipLink.slnx` (new-format). There is no `.sln`.
- Shell is PowerShell. Quote paths containing spaces and use the call operator `&`.

## Commands
Run from the repository root; prefix each with `& "C:\Program Files\dotnet\dotnet.exe"`.

- Build: `build ZipLink.slnx`
- Test: `test ZipLink.slnx`
- Format check: `format --verify-no-changes ZipLink.slnx` (currently passes clean)
- Run the shortener: `run --project src/ZipLink.Api --launch-profile https`
- Run the orchestrator CLI: `run --project src/ZipLink.Agentic -- "<requirement>"`

There is no `src/AppHost` yet; that arrives with Aspire. Until then the two executables
are started separately.

The API's default (`http`) profile binds `http://localhost:5272` only, which makes
`UseHttpsRedirection()` log `Failed to determine the https port for redirect` and skip
redirection. The `https` profile binds `https://localhost:7179` as well, which fixes
that but warns that the dev certificate is untrusted; run
`dev-certs https --trust` once to clear it.

The orchestrator CLI exits **0** when analysis completes, **1** on invalid input, and
**2** when it is blocked awaiting human clarification. Exit 2 is a governance outcome,
not a failure — do not treat it as an error or retry around it.

When smoke-testing endpoints from PowerShell, pass JSON bodies via a file
(`curl.exe --data-binary "@body.json"`). Inline `-d '{"url":"..."}'` does not survive
PowerShell quoting and reaches the server as malformed JSON.

## Version Control
This repository is under git (branch `main`), with no remote.
- Report changes with `git diff --stat` and `git status --short`, not hand-built diffs.
- Do not commit or push unless I ask.
- Never force-push, `reset --hard`, rewrite history, or discard uncommitted work without
  explicit approval.
- Build output is gitignored; do not commit `bin/` or `obj/`.

## How I want you to work
- For any task bigger than a small fix: propose a plan first and wait for my OK.
- Keep changes small and focused; one concern per change.
- Inspect the repository before editing.
- Do not modify files unrelated to the requirement.
- Preserve existing behavior unless the requirement explicitly changes it.
- Keep the build at **zero warnings**. Nothing enforces this yet — there is no
  `Directory.Build.props` or `.editorconfig` — so hold the line manually.
- Every behavior change needs tests. `tests/ZipLink.Tests` already references
  `ZipLink.Core` and `ZipLink.Agentic`; adding a `ProjectReference` for another project
  under test is **pre-approved** and does not count as an unrelated file. Say that you did it.
- After changes: run build and tests, and report the results honestly (including failures).
- Show impacted files and explain why they are affected.
- Surface assumptions, risks, and limitations.
- A source file that is present but zero bytes is deliberate scaffolding: implement it
  against the contract its callers already expect, rather than asking whether to fill it in.
  If no caller defines the contract, state the contract you chose and why.
- When I correct you on something that should always apply, suggest adding it to this file.

## Handling ambiguity
Ask before guessing, and list assumptions explicitly. In practice:
1. If the codebase answers the question, use that answer — existing call sites,
   signatures and tests define the contract.
2. If competing readings lead to substantially the same work, pick the sensible default,
   **state the assumption**, and proceed.
3. Ask before building when competing readings lead to materially different work, when
   the requirement is security-related, or when proceeding would be destructive or hard
   to reverse.

Do everything that does not depend on the open question first, then raise the question.

## Change-control rules
Needs explicit approval before the change is made:
- adding, removing, or upgrading a NuGet package (name it and say why)
- database schema changes (always via EF Core migrations)
- modifying governance code (see Non-negotiable rules)
- deleting or renaming files
- adding a new project, or changing the solution layout
- changing a public API signature that existing callers depend on
- destructive git operations (see Version Control)
- anything touching CI/CD
- refactors beyond the stated scope, however obviously beneficial

Everything else — implementing the requested change, adding tests for it, adding the
test-project reference it needs — proceeds without a gate. Do not deploy anywhere.

## Expected workflow
1. Understand the requirement.
2. Inspect relevant repository files.
3. Produce an impact analysis.
4. Propose a short implementation plan and wait for approval on anything non-trivial.
5. Identify risks and assumptions.
6. Implement only the approved scope.
7. Build.
8. Test.
9. Show the diff (`git diff --stat`, plus `git diff` for the files that matter).
10. Report: files changed; commands actually run, verbatim; build status with warning
    count; tests run with pass/fail counts; risks; limitations; follow-up
    recommendations, proposed but not applied.

If a step was skipped, say so. If tests fail, show the output rather than summarizing it away.

## Agentic principles
- Agents may analyze, plan, code, test, and review.
- Humans own approval for the changes listed in Change-control rules.
- Retry a failing operation at most **twice**, then stop and surface the failure with the
  actual error output. Never loop indefinitely, and never retry an identical call that
  was denied. (The brief allows up to 3 retries inside an orchestrator run; this stricter
  limit applies to me working interactively.)
- Preserve decision history and reasoning in commits and written summaries.

Stage state machine (brief section 7), for any orchestrator work:
`PENDING → READY → RUNNING → AWAITING_APPROVAL → SUCCEEDED`,
with side states `RETRYING`, `FAILED`, `ROLLED_BACK`, `SKIPPED`.

Vocabulary is defined in the brief's glossary — **agent**, **gate**, **deterministic
check**, **run**, **audit log**. Use those words with those meanings.
