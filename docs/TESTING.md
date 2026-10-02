# Testing approach, limitations and trade-offs

---

## Approach

One xUnit project, `tests/ZipLink.Tests`, covering both halves of the system. **205 tests,
~1.9k lines of test code against ~4.5k lines of source.** No mocking framework, no
assertion library beyond xUnit — collaborators are small enough to hand-write fakes.

```bash
dotnet test ZipLink.slnx     # ~2 seconds
```

### Coverage by area

| Area | Tests | What is asserted |
|---|---|---|
| `UrlShorteningServiceTests` | 5 | Create / resolve / click counting, URL validation rejects |
| `TextNormalizerTests` | 23 | Camel-case splitting, acronyms, stemming, stop words, stem matching |
| `RepositoryAnalyzerTests` | 6 | Namespace/type/method extraction, `bin`/`obj` exclusion, comments and literals ignored |
| `ImpactAnalyzerTests` | 14 | Ranking, production-over-tests ordering, unmatched terms, confidence, raw-string immunity |
| `RequirementAnalyzerTests` | 35 | All six ambiguity rule families, risk thresholds, the clarification gate, normalization, determinism |
| `OrchestratorTests` | 19 | Graph ordering, parallelism and join, gates, approve/reject, failure isolation, persistence, audit, graph validation |
| `OrchestratorResilienceTests` | 14 | Bounded retries, rollback, safe-stop, re-planning, metrics |
| `DesignAgentExecutorTests` | 13 | Schema-constrained prompt, typed parse, malformed JSON retried, refusal gated not retried, upstream context passed, stub-vs-agent wiring |
| `PolicyScannerTests` | 17 | Secret and dependency rules, inline allow marker, skipped directories, stage fails rather than blocks, and this repository passing its own gate |
| `BudgetAndDocsAgentTests` | 10 | Call and wall-clock budgets enforced before the model is reached, breach recorded as a stage failure, docs agent parse and prompt context |
| `CodeWorkspaceTests` | 17 | The agent sandbox: allow-list enforcement, path traversal, absolute paths, protected directories and governance files refused even when the design lists them, all-or-nothing batches |

### Three deliberate techniques

**Fixture repositories, not this repository.** `TempRepository` builds a throwaway
directory tree per test. Scanning the real `src/` would make assertions depend on code
that changes every commit — a test that breaks when you add an unrelated class is a test
that will be deleted.

**The engine is tested through fake executors.** `RecordingExecutor` appends to a shared
log under a lock, which is how parallelism is asserted as a *fact* rather than hoped for:

```csharp
// Both branches start before either finishes: they really overlap.
Assert.True(log.IndexOf("start:left")  < firstEnd);
Assert.True(log.IndexOf("start:right") < firstEnd);
// The join waits for both.
Assert.True(log.IndexOf("start:join") > log.IndexOf("end:left"));
Assert.True(log.IndexOf("start:join") > log.IndexOf("end:right"));
```

**The real test runner is injected.** `ITestRunner` has a production implementation that
shells out to `dotnet test` and a `FakeTestRunner` used everywhere in the suite. Without
that seam the engine's tests would recursively invoke the test suite.

### Determinism

No test touches the network, no test sleeps for more than 60 ms, and nothing calls a
language model - the design agent is tested entirely through a fake ILanguageModel, which
is why adding an agent did not slow the suite or make it flaky. `RetryPolicy.None` and explicit `MaxAttempts` keep retry tests from
depending on timing. One test asserts determinism directly — the same requirement
analysed twice must produce identical scores and questions.

---

## Limitations

### Not covered by tests

- **No HTTP-level tests for the API.** No `WebApplicationFactory`, no integration test
  hitting a real endpoint. The service logic is tested; the route wiring, DI
  registrations, status codes and redirect behaviour were verified by hand with `curl`
  and are not protected against regression. This is the largest gap.
- **No persistence tests beyond the happy path** — no corrupt `run.json`, no partially
  written `audit.jsonl`, no concurrent runs over the same run folder.
- **No CLI argument-parsing tests.** `Program.cs` verb dispatch is exercised manually.
- **No cancellation test.** `CancellationToken` is threaded through but no test cancels
  mid-run.
- **No coverage measurement.** `coverlet.collector` is referenced but no threshold is
  enforced and no report is produced.

### Known weaknesses in what is tested

- **Analysis is lexical, not semantic.** "TTL" will not match "expiration". Synonyms,
  abbreviations and domain knowledge are all absent, and the tests encode that behaviour
  rather than challenge it.
- **Ambiguity rules are keyword lookups.** A vague requirement phrased in words outside
  the tables scores 0 and sails through. The rules catch the common vocabulary of
  vagueness, not vagueness itself.
- **Scoring weights and thresholds are asserted, not calibrated.** Type 5 / method 4 /
  file 3 / namespace 2, High ≥ 8, and the ambiguity threshold of 4 were chosen by
  judgment. No corpus was used to tune them, and the tests lock in those judgments.
- **Regex extraction misses constructors, tuple-returning methods and local functions**,
  and `Skipped` / `RolledBack` states have partial coverage — `Skipped` is unreachable.
- **Metrics are thinly tested** — counts and null-vs-value for mean recovery time, not
  the arithmetic of every figure.
- **Timing-sensitive parallelism test.** The overlap assertion uses a 60 ms versus 10 ms
  delay. Correct on normal hardware; a sufficiently starved CI agent could serialise
  them and produce a false failure.

---

## Trade-offs

**Fixtures over the real repository.** Bought stability; cost is that the analyzers are
never tested against the full messiness of real source. Mitigated by running all three
scenarios against this repository and reading the output.

**Fakes over a mocking framework.** Bought zero dependencies and tests that read as
plain C#; cost is a little hand-written plumbing per test class.

**Unit-level over end-to-end.** Bought a ~2 second suite that runs on every change; cost
is the API HTTP gap above. For a 2–3 day timebox where the graded differentiator is
orchestration, depth on the engine was worth more than breadth across the sample product.

**Testing the engine with stubs.** Follows the brief's instruction to prove the engine
with fake agents first. It means every orchestration guarantee — ordering, joins, gates,
retries, rollback, safe-stop, re-planning — is verified independently of any model, and
will stay verified when real agents arrive. The cost is that no test proves the engine
behaves well against a *slow or non-deterministic* executor, which is exactly what an LLM
is.

**Deterministic validation inside the pipeline.** The `tests` stage runs the real suite,
so the system's own quality gate is the same one a developer uses. The cost is a ~4
second stage and a hard requirement that the solution is built first.

**No coverage gate.** Line coverage would mostly measure the renderers. Given the
timebox, the effort went into asserting behaviour that matters — gates actually stopping
pipelines, rollback actually deleting artifacts — rather than into a number.

---

## What would be next

In priority order, if the timebox allowed:

1. HTTP-level API tests with `WebApplicationFactory` — closes the biggest gap.
2. Persistence failure tests — corrupt and truncated state files.
3. A slow/flaky executor test to probe the engine against LLM-like behaviour.
4. Replace the timing-based parallelism assertion with a deterministic barrier.
5. Coverage reporting, with a threshold on the orchestration namespace only.
