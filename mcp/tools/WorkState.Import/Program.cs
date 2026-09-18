using System.Text;
using WorkState.Import;

var roots = new List<string>();
var connection = Environment.GetEnvironmentVariable("WORKSTATE_CONNECTION")
                 ?? "Host=localhost;Port=55433;Database=workstate;Username=workstate;Password=workstate";
var workspace = "import";
string? reportPath = null;
var ephemeral = false;
var migrate = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root" or "-r" when i + 1 < args.Length:
            roots.Add(args[++i]);
            break;
        case "--connection" or "-c" when i + 1 < args.Length:
            connection = args[++i];
            break;
        case "--workspace" or "-w" when i + 1 < args.Length:
            workspace = args[++i];
            break;
        case "--report" when i + 1 < args.Length:
            reportPath = args[++i];
            break;
        case "--ephemeral":
            ephemeral = true;
            break;
        case "--migrate":
            migrate = true;
            break;
        case "--help" or "-h":
            Console.WriteLine(Judgements.Usage);
            return 0;
        default:
            roots.Add(args[i]);
            break;
    }
}

if (roots.Count == 0)
{
    Console.Error.WriteLine("No corpus path given.");
    Console.Error.WriteLine(Judgements.Usage);
    return 2;
}

Console.OutputEncoding = Encoding.UTF8;

await using var database = await Database.OpenAsync(connection, ephemeral, migrate);
if (ephemeral)
{
    Console.WriteLine("ephemeral postgres:17 up — nothing outside this container is written");
}

var outcome = await new Importer(database.ConnectionString, workspace).ImportAsync(roots);

foreach (var run in outcome.Runs)
{
    Console.WriteLine(
        $"{run.Project}/{run.Run}: {run.Files} files, {run.Blocks} blocks, {run.Items} items, " +
        $"{run.Assumptions} assumptions, {run.Notes} notes, {run.Verifications} verifications");
}

Console.WriteLine();
Console.WriteLine(
    $"TOTAL {outcome.Runs.Count} runs · {outcome.Files} files · {outcome.Blocks} blocks · {outcome.Items} items · " +
    $"{outcome.Assumptions} assumptions · {outcome.Notes} notes · {outcome.Constraints} rules · " +
    $"{outcome.Verifications} verifications · {outcome.Attempts} attempts · {outcome.ReplacementChars} U+FFFD");

foreach (var shape in outcome.Unmapped.Shapes.OrderByDescending(s => s.Count).Take(15))
{
    Console.WriteLine($"UNMAPPED {shape.Count,5}  {shape.Category}  {shape.Shape}");
}

if (reportPath is not null)
{
    File.WriteAllText(
        reportPath,
        ImportReport.Render(outcome, Judgements.ByCategory, Judgements.SchemaDoubts),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    Console.WriteLine($"report → {reportPath}");
}

return 0;
