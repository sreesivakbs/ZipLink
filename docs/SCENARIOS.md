# Scenarios

The three required scenarios — greenfield, brownfield, ambiguous — each executed through
the orchestrator **with real agents enabled**, and recorded. The folders under
[`runs/`](runs/) are the actual output, copied unedited: `run.json`, the append-only
`audit.jsonl`, one artifact per stage, and the diff where code was written.

Reproduce any of them with the commands in [SETUP.md](SETUP.md).

| # | Scenario | Requirement | Outcome |
|---|---|---|---|
| 1 | Greenfield | "Build a URL shortener" | **Failed at the policy gate and the test stage** — guardrails bit |
| 2 | Brownfield | "Add custom short names and link expiration" | Completed: 9 files written, compiling, tests green |
| 3 | Ambiguous | "Make it handle more traffic" | Blocked for clarification, then proceeded on approval |

Two of three did not finish cleanly. That is the point of the exercise and the runs are
published as they happened: a system whose guardrails never fire has not been shown to
have any.

---

## 1. Greenfield — "Build a URL shortener"

**Evidence:** [`runs/01-greenfield/`](runs/01-greenfield/) · 30 audit events ·
[`workspace.diff`](runs/01-greenfield/workspace.diff)

### Orchestration

```
  [x] requirements  Succeeded     0.02s   Risk Low, 0 ambiguities
  [x] impact        Succeeded     0.08s   14 files implicated, confidence High
  [x] design        Succeeded    35.38s   9 steps across 8 files      <- approved by a human
  [x] implement     Succeeded   133.87s   8 files written, compiling on attempt 1
  [!] tests         Failed       10.57s   Failed! 1 failed, 214 passed of 215
  [-] docs          RolledBack   11.85s   6 release notes, 4 gaps
  [!] policy        Failed        0.37s   1 violation
  [ ] release       Pending                                            never started
```

### Three guardrails fired at once

**The policy gate caught an unapproved dependency.** The agent added
`Microsoft.AspNetCore.Mvc.Testing` to the test project so it could write HTTP-level
integration tests — a *reasonable* engineering instinct, and precisely the gap
[TESTING.md](TESTING.md) records as our largest. It is still a change-control decision a
human makes, and the gate stopped it:

> `[no-unapproved-dependencies] tests/ZipLink.Tests/ZipLink.Tests.csproj:12 -
> 'Microsoft.AspNetCore.Mvc.Testing' is not on the approved dependency list.`

**The test stage failed** on a deterministic check: 1 of 215 tests red. No agent opinion
was consulted and none could have overridden it.

**Rollback undid the work of the failed step.** `docs` succeeded in the same tick and was
marked `RolledBack`; `release` never started.

### What this does and does not show

It shows the governance machinery working on real agent output rather than on fixtures:
change control, deterministic validation, tick-scoped rollback, and a run that stops
rather than asking a human to approve something broken.

It does **not** show a working URL shortener built by agents. The run failed. The
shortener in `src/` remains the hand-built Phase 1 baseline.

---

## 2. Brownfield — "Add custom short names and link expiration"

**Evidence:** [`runs/02-brownfield/`](runs/02-brownfield/) · 30 audit events ·
[`workspace.diff`](runs/02-brownfield/workspace.diff)

The assignment's named brownfield scenario, carried end to end.

```
  [x] requirements  Succeeded     0.02s   Risk Medium, 1 ambiguity (no duration given)
  [x] impact        Succeeded     0.08s   46 files implicated, confidence Medium
  [x] design        Succeeded    31.74s   10 steps across 9 files     <- approved by a human
  [x] implement     Succeeded   182.90s   9 files written, compiling on attempt 2
  [x] tests         Succeeded     4.80s   Passed! 194 tests, in the agent's workspace
  [x] docs          Succeeded    16.68s   6 release notes, 7 documentation gaps
  [x] policy        Succeeded     0.14s   60 files scanned, clean
  [x] release       Succeeded     0.00s                                <- approved by a human
```

### What the agent produced

570 insertions across 9 files: `ExpiresAt` on `ShortUrl`, an `IClock` abstraction with a
`SystemClock` and a `FakeClock` for deterministic tests, alias validation and reservation
in the service, repository support for both, API wiring, and tests.

**"compiling on attempt 2" is the interesting number.** The first attempt did not compile;
the agent was given its own compiler errors and fixed them. That loop is why the stage
hands on code that builds rather than code that looks plausible.

### The failure this scenario exposed

The run completed, every gate passed, and a human approved a release. The change was
still not safe to take.

Comparing the agent's test file against the baseline:

| | baseline | agent's version |
|---|---|---|
| Test methods | 7 | 19 |
| `InlineData` cases | **23** | **8** |
| `169.254.169.254` covered | yes | **no** |

It added twelve methods for aliases and expiration and **silently deleted the
parameterised cases covering private and internal addresses** — fifteen assertions of
security behaviour, including the cloud-metadata endpoint. The suite still reported green,
because deleted tests do not fail.

Nothing caught it. The test stage saw 194 passing. The policy gate was only looking for
secrets and dependencies. It was found by reading the diff.

**The fix is committed**: `TestCoverageGuard` now fails the policy gate when a change
removes more test cases than it adds. Checked against this very diff — 24 removed, 21
added, net −3 — it would have failed this run. Tests may be rewritten; coverage may not
shrink.

The run is published unchanged, with the regression in it, because the finding is worth
more than a tidy result.

---

## 3. Ambiguous — "Make it handle more traffic"

**Evidence:** [`runs/03-ambiguous/`](runs/03-ambiguous/) · 20 audit events

### The gate fires first

Risk `High` (score 4). The run stopped at `requirements` and **never reached impact
analysis**: `traffic` names a scalability goal with no property in scope, and `more` asks
for a relative change with no baseline or target.

### Then the human chooses, and the system proceeds

On approval the run continued, and the design agent proposed *options* rather than
assuming one — which is what the brief asks of this scenario:

> …make throughput measurable first and then apply one targeted, low-risk improvement on
> the hot path, rather than a speculative re-architecture…

with the decisions a person has to make stated as questions:

> Is the target to be met by a single instance (vertical) or by running multiple instances
> (horizontal)? **Horizontal scaling makes in-memory caching the wrong choice** and shifts
> the work to statelessness, distributed cache and ID-generation collision safety.
>
> What is the read/write mix? If the workload is write-heavy rather than read-heavy,
> caching the lookup path yields nothing and the bottleneck is the store's write path.

That is the scenario's substance: ambiguity detected deterministically, escalated to a
human, and then answered with options and trade-offs rather than a guess.

### Where it stopped, and why

`implement` failed after three bounded attempts — the retry policy working — with
`the input does not contain any JSON tokens`.

The cause was ours, not the agent's: an eight-file change exceeded the 16k output limit,
so each answer was truncated mid-JSON and surfaced downstream as a parse error. **Fixed**:
the limit is now 64k, requests are streamed to stay clear of HTTP timeouts, and truncation
is reported as truncation. The brownfield scenario above ran after that fix and completed.

This run is published as it happened rather than re-run, because it is the clearest record
of bounded retries and a clean failure — and of a limit discovered by running the thing
rather than by reasoning about it.

---

## Honest summary

| Capability | Demonstrated? |
|---|---|
| Requirement understanding and ambiguity detection | Yes — scenario 3 |
| Task decomposition with dependencies | Yes — all three |
| Codebase reasoning over existing code | Yes — scenario 2, 46 files implicated |
| Non-linear, stateful orchestration with gates | Yes — all three |
| Parallel execution with synchronization | Yes — tests ∥ docs ∥ policy, joining at release |
| Human approval checkpoints | Yes — all three stopped and waited |
| Code generation | Yes — scenarios 1 and 2 |
| Deterministic validation | Yes — real `dotnet test` decided scenarios 1 and 2 |
| Bounded retries | Yes — scenario 3 (3 attempts), scenario 2 (recovered on 2) |
| Rollback | Yes — scenario 1, `docs` rolled back |
| Policy guardrails | Yes — scenario 1, unapproved dependency blocked |
| Audit trail and metrics | Yes — all three |
| Safe-stop, re-planning | Implemented and tested; not exercised by these runs |
| **Agents merging their own work** | **No, by design** — a human applies it or does not |
| **Measurable before/after for the ambiguous scenario** | **No** — it stopped at implementation |

Nothing an agent wrote has reached `main` except one change a human read and chose to
take: the private-address guard in
[`runs/05-implement-agent/`](runs/05-implement-agent/).
