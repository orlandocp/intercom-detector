using IntercomDetector.Core;
using IntercomDetector.Core.Pipeline;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// -- PARSE ARGS --
// Usage: <subcommand> --input <file-or-glob> [--output <folder>]
// Subcommands: raw | events | rest | all

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return;
}

string subcommand = args[0].ToLowerInvariant();
string inputPattern = "";
string outputFolder = "";
double eventStartV  = 0.5;
double eventEndV    = 0.3;
double gapMs        = 1000;
double maxDurMs     = 50000;

for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--input"       && i + 1 < args.Length) { inputPattern = args[++i]; continue; }
    if (args[i] == "--output"      && i + 1 < args.Length) { outputFolder = args[++i]; continue; }
    if (args[i] == "--event-start" && i + 1 < args.Length) { eventStartV  = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if (args[i] == "--event-end"   && i + 1 < args.Length) { eventEndV    = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if (args[i] == "--gap"         && i + 1 < args.Length) { gapMs        = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if (args[i] == "--max-dur"     && i + 1 < args.Length) { maxDurMs     = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
}

if (string.IsNullOrWhiteSpace(inputPattern))
{
    Console.Error.WriteLine("Error: --input is required.");
    PrintUsage();
    return;
}

if (!subcommand.IsOneOf("raw", "events", "rest", "all"))
{
    Console.Error.WriteLine($"Error: unknown subcommand '{subcommand}'. Use raw | events | rest | all.");
    PrintUsage();
    return;
}

// -- RESOLVE INPUT FILES --
var inputFiles = ResolveInputFiles(inputPattern);
if (inputFiles.Count == 0)
{
    Console.Error.WriteLine($"Error: no files matched '{inputPattern}'.");
    return;
}

// -- RESOLVE OUTPUT FOLDER --
// Resolve output: make relative paths relative to the input file's folder
string resolvedInputDir = Path.GetDirectoryName(Path.GetFullPath(inputFiles[0]))
                          ?? Directory.GetCurrentDirectory();
if (string.IsNullOrWhiteSpace(outputFolder))
{
    // Default: timestamped subfolder inside the input file's folder
    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    outputFolder     = Path.Combine(resolvedInputDir, timestamp);
}
else if (!Path.IsPathRooted(outputFolder))
{
    // Relative --output: resolve against input folder, not cwd
    outputFolder = Path.Combine(resolvedInputDir, outputFolder);
}
Directory.CreateDirectory(outputFolder);

// -- BUILD PIPELINE --
var processors = BuildProcessors(subcommand, outputFolder, eventStartV, eventEndV, gapMs, maxDurMs);
var pipeline   = new SamplePipeline(processors);

Console.WriteLine($"▶ Subcommand : {subcommand}");
Console.WriteLine($"▶ Input files: {inputFiles.Count}");
Console.WriteLine($"▶ Output     : {outputFolder}");
Console.WriteLine();

// -- PROCESS FILES --
foreach (var filePath in inputFiles)
{
    Console.WriteLine($"📂 Processing: {Path.GetFileName(filePath)}");
    await ProcessRawFileAsync(filePath, pipeline);
    Console.WriteLine();
}

Console.WriteLine("✅ Done.");

// =============================================================================

static List<ISampleProcessor> BuildProcessors(string subcommand, string capturesFolder,
    double eventStartV, double eventEndV, double gapMs, double maxDurMs)
{
    var list = new List<ISampleProcessor>();

    if (subcommand is "raw" or "all")
        list.Add(new RawWriter(capturesFolder));

    if (subcommand is "events" or "all")
        list.Add(new EventProcessor(capturesFolder, eventStartV, eventEndV, gapMs, maxDurMs));

    if (subcommand is "rest" or "all")
        list.Add(new RestWriter(capturesFolder));

    return list;
}

static async Task ProcessRawFileAsync(string filePath, SamplePipeline pipeline)
{
    int lineCount = 0;
    int skipped   = 0;

    using var fs     = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);

    string? line;
    while ((line = reader.ReadLine()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (line.StartsWith("TimeR"))
            continue;

        var parts = line.Split(',');
        if (parts.Length < 3) { skipped++; continue; }

        if (!long.TryParse(parts[1].Trim(), out long timestampMs))  { skipped++; continue; }
        if (!double.TryParse(parts[2].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double voltage))
        { skipped++; continue; }

        string timeR = parts[0].Trim();

        await pipeline.ProcessAsync(timestampMs, voltage, timeR);
        lineCount++;
    }

    Console.WriteLine($"  Samples processed: {lineCount} | Skipped: {skipped}");
}

static List<string> ResolveInputFiles(string pattern)
{
    // If pattern contains wildcards, use glob expansion
    if (pattern.Contains('*') || pattern.Contains('?'))
    {
        string dir       = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? d : Directory.GetCurrentDirectory();
        string fileGlob  = Path.GetFileName(pattern);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, fileGlob)
                        .OrderBy(f => f)
                        .ToList();
    }

    // Single file
    if (File.Exists(pattern))
        return new List<string> { pattern };

    return new List<string>();
}

static void PrintUsage()
{
    Console.WriteLine("Usage: IntercomDetector.Cli <subcommand> --input <file-or-glob> [--output <folder>]");
    Console.WriteLine("                            [--event-start <V>] [--event-end <V>] [--gap <ms>] [--max-dur <ms>]");
    Console.WriteLine();
    Console.WriteLine("Subcommands:");
    Console.WriteLine("  raw    — regenerate raw_yyyyMMdd.csv from input");
    Console.WriteLine("  events — regenerate events_log_* and event_* files from input");
    Console.WriteLine("  rest   — regenerate rest_yyyyMMdd.csv from input");
    Console.WriteLine("  all    — regenerate all output types");
    Console.WriteLine();
    Console.WriteLine("Threshold options (defaults: --event-start 0.5 --event-end 0.3 --gap 1000 --max-dur 50000):");
    Console.WriteLine("  --event-start <V>   voltage to open an event (default: 0.5)");
    Console.WriteLine("  --event-end   <V>   voltage to close an event (default: 0.3)");
    Console.WriteLine("  --gap         <ms>  gap that forces INCONSISTENT_GAP close (default: 1000)");
    Console.WriteLine("  --max-dur     <ms>  max event duration before INCONSISTENT_TIMEOUT (default: 50000)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run --project IntercomDetector.Cli -- all    --input captures/raw_20260321.csv");
    Console.WriteLine("  dotnet run --project IntercomDetector.Cli -- rest   --input captures/raw_20260321.csv");
    Console.WriteLine("  dotnet run --project IntercomDetector.Cli -- events --input \"captures/raw_*.csv\"");
    Console.WriteLine("  dotnet run --project IntercomDetector.Cli -- events --input captures/raw_20260321.csv --event-start 0.4 --gap 800");
}

// Extension helper
static class StringExtensions
{
    public static bool IsOneOf(this string value, params string[] options)
        => options.Contains(value);
}
