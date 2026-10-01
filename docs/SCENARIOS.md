# Scenarios

Three required scenarios — greenfield, brownfield, ambiguous — plus a fourth run that
carries a requirement all the way to tested code. Each executed through the
orchestrator and recorded. The run folders under [`runs/`](runs/) are the actual output,
copied unedited from `.ziplink/runs/`: `run.json`, the append-only `audit.jsonl`, and one
artifact per stage.

Reproduce any of them with the commands in [SETUP.md](SETUP.md).

| # | Scenario | Requirement | Outcome | Run |
|---|---|---|---|---|
| 1 | Greenfield | "Build a URL shortener" | Completed, 7/7 stages, 2 approvals | `20261001-115136-82acb0` |
| 2 | Brownfield | "Add custom short names and link expiration" | Blocked at `design` approval | `20261001-115259-1cea9a` |
| 3 | Ambiguous | "Make it handle more traffic" | **Blocked at `requirements`** — clarification required | `20261001-115142-c4d822` |

---

## 1. Greenfield — "Build a URL shortener"

**Evidence:** [`runs/01-greenfield/`](runs/01-greenfield/) · 27 audit events

### Decomposition

The requirement analyzer found **no ambiguity** (risk `Low`, score 0), so the run passed
its own clarification gate and the pipeline decomposed the work along the graph:
requirements → impact → design → implement → tests ∥ docs → release.

### Orchestration

```
  [x] requirements  Succeeded         after:-                   0.02s
  [x] impact        Succeeded         after:requirements        0.17s
  [x] design        Succeeded         after:impact              0.00s   ← approved by sreed
  [x] implement     Succeeded         after:design              0.00s
  [x] tests         Succeeded         after:implement           4.23s
  [x] docs          Succeeded         after:implement           0.00s
  [x] release       Succeeded         after:tests+docs          0.00s   ← approved by sreed
```

The audit log shows `StageReady tests` and `StageReady docs` in the same tick, `docs`
completing while `tests` was still running, and `release` becoming ready only after both
— parallelism and synchronization, not a sequence.

The run stopped twice and waited for a human, exiting with code 2 each time. It advanced
only when `approve` was issued.

### Validation

`tests` ran the real suite: **`Passed! - Failed: 0, Passed: 116`**. A deterministic check
decided the outcome; no agent asserted its own correctness.

```
Stage success rate : 100% (7/7 succeeded)
Retries            : 0        Rolled back : 0
End-to-end latency : 21.22s
Time in stages     :  3.83s
Waiting on humans  : 17.14s
```

81% of the elapsed time was waiting for approvals. That separation is the point of
reporting the three numbers rather than one.

### What this does and does not show

It shows the full lifecycle under governance, with real validation at the end.

**It does not produce a URL shortener.** This run predates the implementation agent, so
`implement` is a stub that records exactly that. The working shortener in `src/` was
hand-built as the Phase 1 baseline and is the reference output this scenario would be
measured against. Reporting this run as "greenfield delivery" would be false; it is
greenfield *planning, decomposition, governance and validation*. Scenario 4 shows the
same pipeline actually producing code.

---

## 2. Brownfield — "Add custom short names and link expiration"

**Evidence:** [`runs/02-brownfield/`](runs/02-brownfield/) · 11 audit events

### Decomposition

Risk `Medium`, one ambiguity: `expiration` is a time-dependent concept with no duration
given. The run was allowed to proceed — a missing default is a question worth asking, not
a reason to stop — but the question and the assumption were recorded:

> What duration applies to 'expiration', and is it configurable?
> What should happen to records that have already passed the 'expiration' point:
> rejected, deleted, or retained for reporting?

### Codebase reasoning

This is the scenario that exercises impact analysis. 31 files implicated across 32
scanned, confidence `Medium`:

| Rank | File | Score |
|---|---|---|
| 1 | `src/ZipLink.Core/Interfaces/IShortUrlRepository.cs` | 12.8 |
| 2 | `src/ZipLink.Core/Services/UrlShorteningService.cs` | 12.8 |
| 3 | `src/ZipLink.Infrastructure/Repositories/InMemoryShortUrlRepository.cs` | 12.8 |
| 4 | `src/ZipLink.Core/Models/ShortUrl.cs` | 8.8 |
| 5 | `tests/ZipLink.Tests/UrlShorteningServiceTests.cs` *(test)* | 8.52 |
| 7 | `src/ZipLink.Api/Program.cs` | 5.0 |

That is the correct answer: the model, the repository contract, both implementations, the
API surface, and the tests that will need updating — with production code ranked above
the tests that cover it.

The most useful line in the report is about what it *could not* find:

> No code anywhere matches 'custom', 'expiration'. Those concepts are probably not
> modelled yet, so the files needed to implement them cannot be identified lexically and
> are likely missing from the ranking.

Both features are genuinely absent from the codebase, so the analysis says so and caps
overall confidence at `Medium` rather than presenting a confident but incomplete list.
It also discloses that `link` appears in more than half the files and was down-weighted.

### Orchestration and validation

The run reached `design` and **stopped** for approval, exit code 2. `implement`, `tests`,
`docs` and `release` were never started. Validation criteria were fixed in advance by the
requirements stage, including:

> Behaviour is covered by tests for both the elapsed and the not-yet-elapsed case.

### What this does and does not show

It shows real brownfield reasoning over an existing codebase and a governance stop before
any change. It does not make the change: the run stopped at the gate, and it predates the
implementation agent. Scenario 4 carries a requirement all the way to tested code.

---

## 3. Ambiguous — "Make it handle more traffic"

**Evidence:** [`runs/03-ambiguous/`](runs/03-ambiguous/) · 5 audit events

### The gate fires

Risk `High` (score 4), two ambiguities, and the run **never reached impact analysis**:

```
  [?] requirements  AwaitingApproval  after:-                   0.02s
       GATE: Risk is High (score 4). Implementing this without clarification would
             mean guessing at the requester's intent.
  [ ] impact        Pending           after:requirements            -
  [ ] design        Pending           after:impact                  -
  ...
```

Two rules fired:

- `traffic` names a **scalability** goal without saying which property is in scope or how
  it will be verified.
- `more` asks for a relative change **without stating a baseline or a target**, so
  completion cannot be judged.

### Questions asked

> Which aspect of scalability is in scope: target requests per second, concurrent users,
> expected data growth, horizontal or vertical scaling, acceptable cost ceiling?
>
> How will 'traffic' be measured or demonstrated once implemented?
>
> 'more' compared to what? Please state the current value and the target value.

### Assumptions recorded

> Current behaviour is the baseline for 'traffic'; no target value was supplied.
> The comparison is against the behaviour currently in the repository.

### Acceptance criteria — marked provisional

> **(provisional — cannot be finalised until the questions above are answered)**
> The agreed scalability target is stated as a number and verified by a repeatable check.

The system refuses to pretend it can finalise acceptance criteria for a requirement it
cannot yet interpret.

### Validation

The *correct* behaviour here is to produce nothing but questions. Exit code 2 and five
audit events — `RunStarted`, `StageReady`, `StageStarted`, `StageBlocked`,
`RunAwaitingApproval` — are the whole run. A human now chooses a direction (caching,
async click tracking, read replicas); `approve` resumes from exactly this point.

### What this does and does not show

It shows the ambiguity gate stopping a pipeline before any work is done on a guess.

It does **not** propose the options itself. The brief envisages the requirements agent
suggesting e.g. Redis or Kafka; our deterministic analyzer names the *dimensions* that
need deciding, not candidate technologies. Proposing solutions requires judgment about
this specific system, which is Phase 3 agent work.

---

---

## 4. End to end with agents — "Block private and internal IP addresses when shortening a URL"

**Evidence:** [`runs/05-implement-agent/`](runs/05-implement-agent/) including
[`workspace.diff`](runs/05-implement-agent/workspace.diff) · 27 audit events

Not one of the three required scenarios, but the one that closes the loop: a requirement
becoming code that passes real tests, which is the project brief's Phase 3 exit gate.

```
  [x] requirements  Succeeded    0.02s   Risk Low, 0 ambiguity(ies)
  [x] impact        Succeeded    0.10s   12 file(s) implicated
  [x] design        Succeeded   44.28s   9 steps across 3 files   <- approved by a human
  [x] implement     Succeeded   66.97s   3 file(s) written and compiling after 1 attempt
  [x] tests         Succeeded    4.59s   Passed! 152 tests (in the agent's workspace)
  [x] docs          Succeeded    0.01s   STUB
  [x] release       Succeeded    0.00s                            <- approved by a human
```

The suite reports **152** rather than this repository's 146 because the agent added six
of its own tests. It ran in the detached worktree, so it judged the agent's code.

### What the agent produced

A new `PrivateAddressGuard` in `ZipLink.Core`, three lines of wiring in
`UrlShorteningService`, and parameterised tests — 269 insertions across the three files
the approved design named, and no others.

The guard refuses loopback, RFC1918, link-local, unique-local and CGNAT ranges, IPv6
literals including bracketed and zone-indexed forms, and internal-looking hostnames. It
performs **no DNS resolution**, so it stays deterministic.

Two details worth noting, because they are the difference between generated code and
*considered* code:

- It blocks `http://169.254.169.254/latest/meta-data` — the cloud metadata endpoint, and
  the exact gap recorded against this repository since the first architecture review.
- Its "should be allowed" cases test **range boundaries**: `172.32.0.1` and
  `100.128.0.1` sit just outside the private blocks, so the test would catch an
  off-by-one in the mask arithmetic.

The design stage had already flagged, unprompted, that alternative IP encodings
(`http://2130706433/`, `http://0x7f.0.0.1/`) can bypass this kind of guard — a real
limitation of the change, surfaced before it was written rather than discovered later.

### What this does not show

The change was **not merged**. It sits in the worktree for a human to review, which is
the designed behaviour — no agent output reaches `main` without a person applying it.

---

## Honest summary

| Capability | Demonstrated? |
|---|---|
| Requirement understanding and ambiguity detection | Yes — scenario 3 |
| Task decomposition with dependencies | Yes — all three |
| Codebase reasoning over existing code | Yes — scenario 2 |
| Non-linear, stateful orchestration with gates | Yes — all three |
| Parallel execution with synchronization | Yes — scenario 1 |
| Deterministic validation | Yes — scenario 1, real `dotnet test` |
| Audit trail and metrics | Yes — all three |
| Bounded retries, rollback, safe-stop, re-planning | Implemented and tested; not exercised by these runs |
| **Code generation** | **Yes** — scenario 4, verified by the real test suite |
| **Options proposed for an ambiguous requirement** | **No** — dimensions named, not solutions |

The remaining "No" row is Phase 3 agent work and is not claimed anywhere in this
repository. Nothing an agent writes is merged: it stays in an isolated worktree for a
human to review.
