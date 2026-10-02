using ZipLink.Agentic.Impact;
using ZipLink.Agentic.Orchestration;
using ZipLink.Agentic.Repository;
using ZipLink.Agentic.Requirements;

var repoRoot = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

// Orchestration verbs take precedence; anything else keeps the previous behaviour of
// treating the arguments as a requirement to analyse.
if (args.Length > 0)
{
    var verb = args[0].ToLowerInvariant();
    var rest = args.Skip(1).ToArray();

    switch (verb)
    {
        case "run":
            return await new OrchestratorCommands(repoRoot)
                .RunAsync(string.Join(' ', rest).Trim());

        case "status":
            return new OrchestratorCommands(repoRoot).Status(
                rest.FirstOrDefault(),
                rest.Any(arg => string.Equals(arg, "--audit", StringComparison.OrdinalIgnoreCase)));

        case "runs":
            return new OrchestratorCommands(repoRoot).List();

        case "resume":
            return await new OrchestratorCommands(repoRoot).ResumeAsync(rest.FirstOrDefault());

        case "stop":
            return new OrchestratorCommands(repoRoot)
                .Stop(rest.FirstOrDefault(), Environment.UserName);

        case "report":
            return new OrchestratorCommands(repoRoot).Report(rest.FirstOrDefault());

        case "replan":
            return new OrchestratorCommands(repoRoot).Replan(rest.FirstOrDefault());

        // Previews by default: --apply is the explicit act of changing this working tree.
        case "adopt":
            return await new OrchestratorCommands(repoRoot).AdoptAsync(
                rest.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)),
                rest.Any(arg => string.Equals(arg, "--apply", StringComparison.OrdinalIgnoreCase)),
                Environment.UserName);

        case "approve":
            if (rest.Length < 2)
            {
                Console.Error.WriteLine("usage: approve <runId|latest> <stageId>");

                return 1;
            }

            return await new OrchestratorCommands(repoRoot)
                .ApproveAsync(rest[0], rest[1], Environment.UserName);

        case "reject":
            if (rest.Length < 2)
            {
                Console.Error.WriteLine("usage: reject <runId|latest> <stageId> [reason]");

                return 1;
            }

            return new OrchestratorCommands(repoRoot).Reject(
                rest[0],
                rest[1],
                Environment.UserName,
                rest.Length > 2 ? string.Join(' ', rest.Skip(2)) : null);
    }
}

// --force is an explicit human override of the clarification gate, not a default.
var forceImpact = args.Any(
    arg => string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase));

var requirement = string.Join(
    ' ',
    args.Where(arg => !string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase)))
    .Trim();

if (requirement is "--help" or "-h" or "/?")
{
    PrintUsage();
    return 0;
}

// No requirement given: keep the original repository inventory behaviour.
if (requirement.Length == 0)
{
    PrintInventory();
    return 0;
}

try
{
    var requirementAnalysis = new RequirementAnalyzer().Analyze(requirement);

    Console.WriteLine(RequirementReportRenderer.Render(requirementAnalysis));

    if (requirementAnalysis.RequiresHumanClarification && !forceImpact)
    {
        Console.WriteLine(
            "Impact analysis was NOT run. The requirement is too ambiguous to analyse "
            + "safely:");
        Console.WriteLine($"  {requirementAnalysis.ClarificationRationale}");
        Console.WriteLine();
        Console.WriteLine(
            "Answer the clarification questions above and re-run, or pass --force to "
            + "analyse anyway on the understanding that the result rests on unstated "
            + "assumptions.");

        return 2;
    }

    if (requirementAnalysis.RequiresHumanClarification)
    {
        Console.WriteLine(
            "--force supplied: running impact analysis despite unresolved ambiguity. "
            + "The ranking below rests on the unanswered questions above.");
        Console.WriteLine();
    }

    var report = new ImpactAnalyzer()
        .Analyze(repoRoot, requirementAnalysis.NormalizedRequirement);

    Console.WriteLine(ImpactReportRenderer.Render(report));

    return 0;
}
catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
{
    Console.Error.WriteLine($"error: {ex.Message}");

    return 1;
}

void PrintUsage()
{
    Console.WriteLine("ZipLink Agentic - repository intelligence and impact analysis");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/ZipLink.Agentic");
    Console.WriteLine("      Print the repository inventory (namespaces, types, methods).");
    Console.WriteLine();
    Console.WriteLine("  dotnet run --project src/ZipLink.Agentic -- \"<requirement>\"");
    Console.WriteLine("      Analyse the requirement, then analyse its likely impact.");
    Console.WriteLine("      Impact analysis is skipped when the requirement is judged");
    Console.WriteLine("      too ambiguous to act on safely.");
    Console.WriteLine();
    Console.WriteLine("  dotnet run --project src/ZipLink.Agentic -- \"<requirement>\" --force");
    Console.WriteLine("      Override the clarification gate and analyse impact anyway.");
    Console.WriteLine();
    Console.WriteLine("Orchestration:");
    Console.WriteLine("  run \"<requirement>\"           Start an orchestrated run.");
    Console.WriteLine("  runs                          List runs.");
    Console.WriteLine("  status [runId|latest] [--audit]  Show a run, optionally with its audit log.");
    Console.WriteLine("  approve <runId|latest> <stage>   Approve a gated stage and continue.");
    Console.WriteLine("  reject  <runId|latest> <stage> [reason]  Reject a gated stage.");
    Console.WriteLine("  resume  [runId|latest]        Continue a run that stopped.");
    Console.WriteLine("  stop    [runId|latest]        Safe-stop: halts between stages.");
    Console.WriteLine("  replan  [runId|latest]        Invalidate stages whose inputs changed.");
    Console.WriteLine("  report  [runId|latest]        Run report with reliability metrics.");
    Console.WriteLine();
    Console.WriteLine("Taking the work:");
    Console.WriteLine("  adopt   [runId|latest]        Preview what the run would copy here.");
    Console.WriteLine("  adopt   [runId|latest] --apply   Copy it into this working tree.");
    Console.WriteLine("      Refused unless implement, tests and policy all succeeded and");
    Console.WriteLine("      this working tree is clean. Nothing is committed, so the undo");
    Console.WriteLine("      is 'git restore .'. Rebuild afterwards to run the new code.");
    Console.WriteLine();
    Console.WriteLine("  Note: a requirement whose first word is a verb above must be");
    Console.WriteLine("  submitted with 'run', e.g. run \"Run reports nightly\".");
    Console.WriteLine();
    Console.WriteLine("Exit codes:");
    Console.WriteLine("  0  analysis or run completed");
    Console.WriteLine("  1  invalid input, or the run failed");
    Console.WriteLine("  2  blocked: human clarification or approval required");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine(
        "  dotnet run --project src/ZipLink.Agentic -- "
        + "\"Add expiration support to shortened URLs\"");
    Console.WriteLine();
    Console.WriteLine("This tool is read-only. It never modifies application code.");
}

void PrintInventory()
{
    var analysis = new RepositoryAnalyzer().Analyze(repoRoot);

    Console.WriteLine("ZipLink Repository Intelligence");
    Console.WriteLine("==============================");
    Console.WriteLine();

    foreach (var file in analysis.Files)
    {
        Console.WriteLine($"FILE: {file.Path}");

        if (!string.IsNullOrWhiteSpace(file.Namespace))
        {
            Console.WriteLine($"  Namespace: {file.Namespace}");
        }

        if (file.Types.Count > 0)
        {
            Console.WriteLine(
                $"  Types: {string.Join(", ", file.Types)}");
        }

        if (file.Methods.Count > 0)
        {
            Console.WriteLine(
                $"  Methods: {string.Join(", ", file.Methods)}");
        }

        Console.WriteLine();
    }

    Console.WriteLine(
        $"{analysis.FileCount} files, {analysis.TypeCount} types, "
        + $"{analysis.MethodCount} methods.");
    Console.WriteLine();
    Console.WriteLine(
        "Pass a requirement to analyse its impact, e.g. "
        + "-- \"Add expiration support to shortened URLs\"");
}
