using IntercomDetector.Core;
using IntercomDetector.Core.Pipeline;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// -- PARSE ARGS --
// Usage: <subcommand> [input] [options]
// Subcommands: event | rest | process

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
    if ((args[i] == "--input"  || args[i] == "-i") && i + 1 < args.Length) { inputPattern = args[++i]; continue; }
    if ((args[i] == "--output" || args[i] == "-o") && i + 1 < args.Length) { outputFolder = args[++i]; continue; }
    if ((args[i] == "--event-start" || args[i] == "-s") && i + 1 < args.Length) { eventStartV = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if ((args[i] == "--event-end"   || args[i] == "-e") && i + 1 < args.Length) { eventEndV   = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if ((args[i] == "--gap"         || args[i] == "-g") && i + 1 < args.Length) { gapMs       = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if ((args[i] == "--max-dur"     || args[i] == "-m") && i + 1 < args.Length) { maxDurMs    = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }

    // Positional argument — first non-flag token is the input path
    if (!args[i].StartsWith('-') && string.IsNullOrWhiteSpace(inputPattern))
    {
        inputPattern = args[i];
        continue;
    }
}

// Default input to current working directory
if (string.IsNullOrWhiteSpace(inputPattern))
    inputPattern = Directory.GetCurrentDirectory();

if (!subcommand.IsOneOf("event", "rest", "process"))
{
    Console.Error.WriteLine($"Error: unknown subcommand '{subcommand}'. Use event | rest | process.");
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

foreach (var p in processors.OfType<ISummaryProvider>())
    p.PrintSummary();

Console.WriteLine("✅ Done.");

// =============================================================================

static List<ISampleProcessor> BuildProcessors(string subcommand, string capturesFolder,
    double eventStartV, double eventEndV, double gapMs, double maxDurMs)
{
    var list = new List<ISampleProcessor>();

    if (subcommand is "event" or "process")
        list.Add(new EventProcessor(capturesFolder, eventStartV, eventEndV, gapMs, maxDurMs));

    if (subcommand is "rest" or "process")
        list.Add(new RestWriter(capturesFolder, eventEndV));

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
    // Folder path — find all raw_*.csv inside
    if (Directory.Exists(pattern))
        return Directory.GetFiles(pattern, "raw_*.csv")
                        .OrderBy(f => f)
                        .ToList();

    // Wildcard glob
    if (pattern.Contains('*') || pattern.Contains('?'))
    {
        string dir      = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? d : Directory.GetCurrentDirectory();
        string fileGlob = Path.GetFileName(pattern);
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
    Console.WriteLine("Usage: idc <subcommand> [input] [options]");
    Console.WriteLine();
    Console.WriteLine("Subcommands:");
    Console.WriteLine("  event   — generate events_* and event_* files from raw input");
    Console.WriteLine("  rest    — generate rest_yyyyMMdd.csv from raw input");
    Console.WriteLine("  process — event + rest in one pass");
    Console.WriteLine();
    Console.WriteLine("Input (optional, default: current directory):");
    Console.WriteLine("  <folder>           finds all raw_*.csv inside");
    Console.WriteLine("  <file>             single raw file (any name)");
    Console.WriteLine("  <glob>             e.g. captures/raw_2026*.csv");
    Console.WriteLine("  -i, --input <path>");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -o, --output      <folder>  output folder (default: timestamped subfolder next to input)");
    Console.WriteLine("  -e, --event-end   <V>       [all]            voltage < V closes event / sets rest threshold  (default: 0.3)");
    Console.WriteLine("  -s, --event-start <V>       [event, process] voltage >= V opens event  (default: 0.5)");
    Console.WriteLine("  -g, --gap         <ms>      [event, process] INCONSISTENT_GAP when gap > ms  (default: 1000)");
    Console.WriteLine("  -m, --max-dur     <ms>      [event, process] INCONSISTENT_TIMEOUT when duration > ms  (default: 50000)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  idc process                                        # cwd, all raw_*.csv, default thresholds");
    Console.WriteLine("  idc process captures/                              # folder input");
    Console.WriteLine("  idc process captures/ -o results/                  # custom output folder");
    Console.WriteLine("  idc process captures/ -s 0.4 -e 0.25 -g 800 -m 60000 -o out/  # all flags");
    Console.WriteLine("  idc event   captures/raw_20260321.csv              # single file, events only");
    Console.WriteLine("  idc event   captures/raw_2026*.csv                 # glob input");
    Console.WriteLine("  idc event   captures/ -s 0.4 -e 0.25              # custom open/close thresholds");
    Console.WriteLine("  idc event   captures/ -g 800 -m 60000             # custom gap and max duration");
    Console.WriteLine("  idc rest    captures/raw_20260321.csv              # single file, rest only");
    Console.WriteLine("  idc rest    captures/ -e 0.25                     # custom rest threshold");
    Console.WriteLine("  idc event   -i captures/raw_20260321.csv          # named input flag");
}

// Extension helper
static class StringExtensions
{
    public static bool IsOneOf(this string value, params string[] options)
        => options.Contains(value);
}
