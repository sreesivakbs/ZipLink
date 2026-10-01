# Setup

Everything needed to clone, build, test and run both halves of the system.

---

## Prerequisites

- **.NET SDK 10.0** (developed on 10.0.401) — `https://dotnet.microsoft.com/download`
- **git**
- No database, container runtime or cloud account is required.
- **An Anthropic API key is optional.** Without one everything still builds, tests and
  runs — the `design` stage falls back to a stub. With one, that stage becomes a real
  agent. See [Enabling the design agent](#enabling-the-design-agent).

### Enabling the design agent

Set `ANTHROPIC_API_KEY` in the environment, or use .NET User Secrets. The SDK reads it
directly; nothing in this repository stores, logs or forwards it, and no key is ever
written into a run artifact.

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
```

The orchestrator announces which mode it is in on startup:

```
Design agent enabled (claude-opus-5). Its proposal still requires human approval.
```

With no key set, the stage runs the stub and says so in its artifact. Either way the
`design` approval gate is unchanged — an agent proposal is never acted on without a
human. Expect that stage to take roughly 30–60 seconds when the agent is enabled, and to
cost a small amount per run.

**Never commit a key.** Tests never call a model: all 129 run offline against a fake.

### `dotnet` may not be on your PATH

On the development machine it was not, which makes every `dotnet …` command fail with
`command not found`. Check first:

```bash
dotnet --version
```

If that fails, use the full path. On Windows it is normally:

```
C:\Program Files\dotnet\dotnet.exe
```

In PowerShell, invoke it with the call operator and keep the quotes:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" --version
```

Every command below is written as plain `dotnet …`. If you need the full path, prefix
accordingly.

---

## Build and test

From the repository root:

```bash
dotnet build ZipLink.slnx
dotnet test  ZipLink.slnx
```

Expected:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Passed!  - Failed: 0, Passed: 129, Skipped: 0, Total: 129
```

The solution file is `ZipLink.slnx` (the newer XML format). There is no `.sln`.

Optional formatting gate:

```bash
dotnet format --verify-no-changes ZipLink.slnx
```

---

## Running the URL shortener

```bash
dotnet run --project src/ZipLink.Api --launch-profile https
```

Listens on `https://localhost:7179` and `http://localhost:5272`. OpenAPI is served at
`/openapi/v1.json` in Development.

Two first-run notes:

- The default (`http`) profile binds HTTP only, which makes `UseHttpsRedirection()` log
  `Failed to determine the https port for redirect` and quietly skip redirection. Use
  `--launch-profile https` as above.
- The `https` profile warns that the ASP.NET Core developer certificate is untrusted.
  Harmless for local testing with `curl -k`; clear it once with
  `dotnet dev-certs https --trust`.

### Endpoints

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/urls` | Create a short link — body `{"url":"https://…"}` |
| `GET` | `/{code}` | Redirect (302) and increment the click count |
| `GET` | `/api/urls/{code}/analytics` | Original URL, click count, created timestamp |

Smoke test:

```bash
echo '{"url":"https://www.example.com/some/long/path"}' > body.json
curl -k -s -X POST https://localhost:7179/api/urls \
     -H "Content-Type: application/json" --data-binary "@body.json"

curl -k -s -o /dev/null -D - https://localhost:7179/<code>        # 302 + Location
curl -k -s https://localhost:7179/api/urls/<code>/analytics       # clickCount: 1
```

> **PowerShell users:** pass JSON from a file as above. An inline
> `-d '{"url":"..."}'` does not survive PowerShell quoting and reaches the server as
> malformed JSON.

Storage is an in-memory dictionary — links do not survive a restart.

---

## Running the orchestrator

```bash
dotnet run --project src/ZipLink.Agentic -- <command>
```

Note the `--`: it separates `dotnet run`'s own arguments from the application's.

### Commands

| Command | Does |
|---|---|
| *(no arguments)* | Print the repository inventory — namespaces, types, methods |
| `"<requirement>"` | Analyse a requirement, then its impact (no run is created) |
| `"<requirement>" --force` | Override the clarification gate for analysis |
| `run "<requirement>"` | Start an orchestrated run |
| `runs` | List all runs |
| `status [runId\|latest] [--audit]` | Show a run, optionally with its audit log |
| `report [runId\|latest]` | Run report plus reliability metrics and audit log |
| `approve <runId\|latest> <stage>` | Approve a gated stage and continue |
| `reject <runId\|latest> <stage> [reason]` | Reject a gated stage; the run fails |
| `resume [runId\|latest]` | Continue a run that stopped |
| `stop [runId\|latest]` | Safe-stop — halts between stages, never mid-stage |
| `replan [runId\|latest]` | Invalidate stages whose inputs changed |
| `--help` | Usage |

`latest` resolves to the most recent run, and is the default where the argument is
optional.

> A requirement whose first word collides with a command name must be submitted with
> `run` — e.g. `run "Run reports nightly"`.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Analysis or run completed |
| `1` | Invalid input, or the run failed |
| `2` | **Blocked — human clarification or approval required** |

`2` is a governance outcome, not an error. Scripts must not treat it as success or as
failure.

---

## Walkthrough

```bash
# 1. A requirement too vague to act on: stops immediately, exit 2
dotnet run --project src/ZipLink.Agentic -- run "Make it handle more traffic"

# 2. A clear one: runs to the design approval gate, exit 2
dotnet run --project src/ZipLink.Agentic -- run "Add a ClickCount property to the ShortUrl model"

# 3. Approve it — implement, then tests ∥ docs, then the release gate
dotnet run --project src/ZipLink.Agentic -- approve latest design

# 4. Final sign-off
dotnet run --project src/ZipLink.Agentic -- approve latest release

# 5. What happened, with metrics and the full audit trail
dotnet run --project src/ZipLink.Agentic -- report latest
```

The `tests` stage runs `dotnet test --no-build`, so **build the solution before starting a
run** or that stage fails. `--no-build` is deliberate: the orchestrator executes from the
solution's own output directory, and a rebuild would try to overwrite the running
executable.

---

## Where run state lives

```
.ziplink/runs/<runId>/
    run.json        state of every stage
    audit.jsonl     append-only log, one JSON object per line
    artifacts/      one JSON file per stage output
    STOP            present only while a safe-stop is pending
```

`.ziplink/` is gitignored. The three recorded scenario runs are copied into
[`runs/`](runs/) as committed evidence — see [SCENARIOS.md](SCENARIOS.md).

Delete `.ziplink/` at any time to start clean; it holds no configuration.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `dotnet: command not found` | Not on PATH — use the full path (top of this page) |
| `tests` stage fails with "Could not locate the dotnet host" | Same cause; set `DOTNET_HOST_PATH` or `DOTNET_ROOT` |
| `tests` stage fails complaining about missing binaries | Run `dotnet build ZipLink.slnx` first — the stage uses `--no-build` |
| Exit code 2 from a run | Working as intended: a gate is waiting. `status latest` shows which |
| `Failed to determine the https port for redirect` | Started the API with the `http` profile — use `--launch-profile https` |
| Dev certificate warning | `dotnet dev-certs https --trust` |
| `400 Bad Request` posting JSON from PowerShell | Inline `-d` quoting — use `--data-binary "@file.json"` |
| Short links vanish after restart | Expected — storage is in-memory |
