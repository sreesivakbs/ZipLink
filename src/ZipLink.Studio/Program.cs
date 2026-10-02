using ZipLink.Agentic.Agents;
using ZipLink.Agentic.Orchestration;
using ZipLink.Studio;

var repositoryRoot = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

var builder = WebApplication.CreateBuilder(args);

// Localhost only, always. This process calls a model, writes files and runs git; it is a
// developer tool and must never be reachable from anywhere else.
builder.WebHost.UseUrls("http://127.0.0.1:5280");

builder.Services.AddSingleton(new RunStore(repositoryRoot));
builder.Services.AddSingleton<RunHost>();
builder.Services.AddSingleton(_ =>
{
    var model = ClaudeLanguageModel.IsConfigured
        ? new BudgetedLanguageModel(new ClaudeLanguageModel(), RunBudget.Default)
        : null;

    return StagePipeline.CreateDefault(
        new DotnetTestRunner(),
        model is null ? null : new DesignAgentExecutor(model),
        model is null ? null : new ImplementAgentExecutor(model),
        model is null ? null : new DocsAgentExecutor(model));
});
builder.Services.AddSingleton(provider => new Orchestrator(
    provider.GetRequiredService<StagePipeline>(),
    provider.GetRequiredService<RunStore>(),
    repositoryRoot));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/environment", () => Results.Ok(new
{
    repositoryRoot,
    agentsEnabled = ClaudeLanguageModel.IsConfigured,
    model = ClaudeLanguageModel.DefaultModel,
    budgetCalls = RunBudget.Default.MaxModelCalls,
    budgetMinutes = (int)RunBudget.Default.EffectiveWallClock.TotalMinutes
}));

app.MapGet("/api/runs", (RunStore store) =>
{
    var runs = store.ListRunIds()
        .Select(store.Load)
        .Where(state => state is not null)
        .Select(state => new
        {
            state!.RunId,
            state.Requirement,
            Status = state.Status.ToString(),
            state.CreatedAtUtc
        })
        .OrderByDescending(run => run.RunId)
        .ToList();

    return Results.Ok(runs);
});

app.MapPost("/api/runs", (StartRequest request, Orchestrator orchestrator, RunHost host) =>
{
    if (string.IsNullOrWhiteSpace(request.Requirement))
    {
        return Results.BadRequest(new { error = "A requirement is required." });
    }

    // Created first so the browser gets an id straight away, then executed in the
    // background: a run takes minutes and must not hold the request open.
    var state = orchestrator.CreateRun(request.Requirement);

    host.TryLaunch(state.RunId, token => orchestrator.ExecuteAsync(state.RunId, token));

    return Results.Ok(new { state.RunId });
});

app.MapGet("/api/runs/{runId}", (string runId, RunStore store, StagePipeline pipeline, RunHost host) =>
{
    var state = store.Load(runId);

    if (state is null)
    {
        return Results.NotFound();
    }

    var artifacts = store.ReadArtifacts(runId);

    var stages = pipeline.Stages.Select(definition =>
    {
        var record = state.Stage(definition.Id);

        return new
        {
            definition.Id,
            definition.Name,
            definition.DependsOn,
            definition.RequiresApproval,
            State = record?.State.ToString() ?? "Pending",
            record?.Summary,
            record?.GateReason,
            Attempts = record?.Attempts ?? 0,
            Seconds = record?.Duration?.TotalSeconds,
            record?.DecidedBy,
            HasArtifact = artifacts.ContainsKey(definition.Id)
        };
    });

    return Results.Ok(new
    {
        state.RunId,
        state.Requirement,
        Status = state.Status.ToString(),
        state.CreatedAtUtc,
        state.FailureReason,
        Busy = host.IsBusy(runId),
        Error = host.LastError(runId),
        Stages = stages
    });
});

app.MapGet("/api/runs/{runId}/artifacts/{stageId}", (string runId, string stageId, RunStore store) =>
{
    var artifacts = store.ReadArtifacts(runId);

    return artifacts.TryGetValue(stageId, out var json)
        ? Results.Text(json, "application/json")
        : Results.NotFound();
});

app.MapGet("/api/runs/{runId}/audit", (string runId, RunStore store) =>
    Results.Ok(store.ReadAudit(runId)));

app.MapGet("/api/runs/{runId}/metrics", (string runId, RunStore store) =>
{
    var state = store.Load(runId);

    return state is null
        ? Results.NotFound()
        : Results.Ok(RunMetrics.Calculate(state, store.ReadAudit(runId)));
});

app.MapPost("/api/runs/{runId}/approve", (
    string runId, DecisionRequest request, Orchestrator orchestrator, RunHost host) =>
{
    if (!host.TryLaunch(
        runId,
        token => orchestrator.ApproveAsync(runId, request.Stage, Environment.UserName, token)))
    {
        return Results.Conflict(new { error = "This run is already doing something." });
    }

    return Results.Accepted();
});

app.MapPost("/api/runs/{runId}/reject", (
    string runId, DecisionRequest request, Orchestrator orchestrator) =>
{
    try
    {
        orchestrator.Reject(runId, request.Stage, Environment.UserName, request.Reason);

        return Results.Accepted();
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/runs/{runId}/stop", (string runId, Orchestrator orchestrator) =>
{
    orchestrator.Stop(runId, Environment.UserName);

    return Results.Accepted();
});

Console.WriteLine("ZipLink Studio — http://127.0.0.1:5280  (localhost only)");
Console.WriteLine($"Repository: {repositoryRoot}");
Console.WriteLine(
    ClaudeLanguageModel.IsConfigured
        ? $"Agents enabled ({ClaudeLanguageModel.DefaultModel})."
        : "No ANTHROPIC_API_KEY set — agent stages will run as stubs.");

app.Run();

internal sealed record StartRequest(string Requirement);

internal sealed record DecisionRequest(string Stage, string? Reason);
