using IntercomDetector.Core;
using IntercomDetector.Core.Analysis;
using IntercomDetector.Core.Pipeline;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// -- PARSE ARGS --
// Usage: <subcommand> [input] [options]
// Subcommands: event | rest | process | analyze-rest

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return;
}

string subcommand   = args[0].ToLowerInvariant();
string inputPattern = "";
string outputFolder = "";
double eventStartV  = 0.5;
double eventEndV    = 0.3;
double gapMs        = 1000;
double maxDurMs     = 50000;
long?  zoomFromMs   = null;
long?  zoomToMs     = null;
long?  zoomBucketMs = null;

for (int i = 1; i < args.Length; i++)
{
    if ((args[i] == "--input"  || args[i] == "-i") && i + 1 < args.Length) { inputPattern = args[++i]; continue; }
    if ((args[i] == "--output" || args[i] == "-o") && i + 1 < args.Length) { outputFolder = args[++i]; continue; }
    if ((args[i] == "--event-start" || args[i] == "-s") && i + 1 < args.Length) { eventStartV = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if ((args[i] == "--event-end"   || args[i] == "-e") && i + 1 < args.Length) { eventEndV   = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if ((args[i] == "--gap"         || args[i] == "-g") && i + 1 < args.Length) { gapMs       = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if ((args[i] == "--max-dur"     || args[i] == "-m") && i + 1 < args.Length) { maxDurMs    = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); continue; }
    if (args[i] == "--from"   && i + 1 < args.Length) { zoomFromMs   = long.Parse(args[++i]); continue; }
    if (args[i] == "--to"     && i + 1 < args.Length) { zoomToMs     = long.Parse(args[++i]); continue; }
    if (args[i] == "--bucket" && i + 1 < args.Length) { zoomBucketMs = long.Parse(args[++i]); continue; }

    if (!args[i].StartsWith('-') && string.IsNullOrWhiteSpace(inputPattern))
    {
        inputPattern = args[i];
        continue;
    }
}

// Default input to current working directory
if (string.IsNullOrWhiteSpace(inputPattern))
    inputPattern = Directory.GetCurrentDirectory();

if (!subcommand.IsOneOf("event", "rest", "process", "analyze-rest", "analyze-raw", "analyze-event"))
{
    Console.Error.WriteLine($"Error: unknown subcommand '{subcommand}'. Use event | rest | process | analyze-rest | analyze-raw | analyze-event.");
    PrintUsage();
    return;
}

// ── ANALYZE-REST ─────────────────────────────────────────────────────────────
if (subcommand == "analyze-rest")
{
    var restFiles = ResolveInputFiles(inputPattern, "rest_*.csv");
    if (restFiles.Count == 0)
    {
        Console.Error.WriteLine($"Error: no rest_*.csv files found at '{inputPattern}'.");
        return;
    }

    Console.WriteLine($"▶ Subcommand : analyze-rest");
    Console.WriteLine($"▶ Input files: {restFiles.Count} rest file(s)");
    Console.WriteLine();

    var result = RestAnalyzer.Analyze(restFiles);
    PrintRestAnalysis(result);
    return;
}

// ── ANALYZE-RAW ──────────────────────────────────────────────────────────────
if (subcommand == "analyze-raw")
{
    var rawFiles = ResolveInputFiles(inputPattern, "raw_*.csv");
    if (rawFiles.Count == 0)
    {
        Console.Error.WriteLine($"Error: no raw_*.csv files found at '{inputPattern}'.");
        return;
    }

    Console.WriteLine($"▶ Subcommand : analyze-raw");
    Console.WriteLine($"▶ Input files: {rawFiles.Count} raw file(s)");
    Console.WriteLine();

    var result = RawAnalyzer.Analyze(rawFiles, zoomFromMs, zoomToMs, zoomBucketMs);
    PrintRawAnalysis(result);
    return;
}

// ── ANALYZE-EVENT ─────────────────────────────────────────────────────────────
if (subcommand == "analyze-event")
{
    var eventFiles = ResolveInputFiles(inputPattern, "event_*.csv");
    if (eventFiles.Count == 0 && Directory.Exists(inputPattern))
        eventFiles = ResolveInputFiles(Path.Combine(inputPattern, "events"), "event_*.csv");
    if (eventFiles.Count == 0)
    {
        Console.Error.WriteLine($"Error: no event_*.csv files found at '{inputPattern}' or '{Path.Combine(inputPattern, "events")}'.");
        return;
    }

    Console.WriteLine($"▶ Subcommand : analyze-event");
    Console.WriteLine($"▶ Input files: {eventFiles.Count} event file(s)");
    Console.WriteLine();

    var result = EventAnalyzer.Analyze(eventFiles, zoomFromMs, zoomToMs, zoomBucketMs);
    PrintEventAnalysis(result);
    return;
}

// ── PIPELINE SUBCOMMANDS (event / rest / process) ────────────────────────────
var inputFiles = ResolveInputFiles(inputPattern, "raw_*.csv");
if (inputFiles.Count == 0)
{
    Console.Error.WriteLine($"Error: no raw_*.csv files found at '{inputPattern}'.");
    return;
}

// Resolve output folder
string resolvedInputDir = Path.GetDirectoryName(Path.GetFullPath(inputFiles[0]))
                          ?? Directory.GetCurrentDirectory();
if (string.IsNullOrWhiteSpace(outputFolder))
{
    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    outputFolder     = Path.Combine(resolvedInputDir, timestamp);
}
else if (!Path.IsPathRooted(outputFolder))
{
    outputFolder = Path.Combine(resolvedInputDir, outputFolder);
}
Directory.CreateDirectory(outputFolder);

var processors = BuildProcessors(subcommand, outputFolder, eventStartV, eventEndV, gapMs, maxDurMs);
var pipeline   = new SamplePipeline(processors);

Console.WriteLine($"▶ Subcommand : {subcommand}");
Console.WriteLine($"▶ Input files: {inputFiles.Count}");
Console.WriteLine($"▶ Output     : {outputFolder}");
Console.WriteLine();

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
        if (line.StartsWith("TimeR"))       continue;

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

static List<string> ResolveInputFiles(string pattern, string glob = "raw_*.csv")
{
    if (Directory.Exists(pattern))
        return Directory.GetFiles(pattern, glob)
                        .OrderBy(f => f)
                        .ToList();

    if (pattern.Contains('*') || pattern.Contains('?'))
    {
        string dir      = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? d : Directory.GetCurrentDirectory();
        string fileGlob = Path.GetFileName(pattern);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, fileGlob)
                        .OrderBy(f => f)
                        .ToList();
    }

    if (File.Exists(pattern))
        return new List<string> { pattern };

    return new List<string>();
}

static void PrintEventAnalysis(EventAnalysisResult r)
{
    const int W  = 82;
    string dline = new string('═', W);
    string line  = new string('─', W);

    Console.WriteLine(dline);
    Console.WriteLine("  EVENT GAP ANALYSIS");
    Console.WriteLine(dline);
    Console.WriteLine($"  Files   : {r.FileCount}  ({r.DateFrom} → {r.DateTo})");
    Console.WriteLine($"  Samples : {r.TotalSamples:N0}");
    if (r.EventsLoaded > 0)
        Console.WriteLine($"  Events  : {r.EventsLoaded} COMPLETE events loaded for correlation");
    Console.WriteLine();
    Console.WriteLine(line);
    Console.WriteLine("  CONFIGURATION");
    Console.WriteLine(line);
    PrintZoomConfig(r.Gaps);
    Console.WriteLine();
    Console.WriteLine(line);
    Console.WriteLine("  GAP SUMMARY");
    Console.WriteLine(line);
    PrintGapSummary(r.Gaps);
    Console.WriteLine();
    Console.WriteLine(line);
    Console.WriteLine("  GAP DISTRIBUTION");
    Console.WriteLine(line);
    PrintGapTable(r.Gaps.Buckets, r.Gaps.TotalGaps);

    if (r.Gaps.ZoomBuckets != null)
    {
        Console.WriteLine();
        Console.WriteLine(line);
        long bms = r.Gaps.ZoomBucketMs ?? Math.Max(1, (r.Gaps.ZoomToMs!.Value - r.Gaps.ZoomFromMs!.Value) / 20);
        string evtNote = r.EventsLoaded > 0
            ? $"  ·  {r.EventsLoaded} events"
            : "  ·  no events loaded";
        Console.WriteLine($"  GAP ZOOM  {FormatMsValue(r.Gaps.ZoomFromMs!.Value)} – {FormatMsValue(r.Gaps.ZoomToMs!.Value)}  (bucket: {bms}ms){evtNote}");
        Console.WriteLine(line);
        int zoomTotal = r.Gaps.ZoomBuckets.Sum(b => b.Count);
        PrintGapTable(r.Gaps.ZoomBuckets, zoomTotal, hideZeros: true, correlations: r.BucketCorrelations);
    }

    if (r.BucketCorrelations != null && r.BucketCorrelations.Any(c => c.HasAny))
    {
        int totR   = r.BucketCorrelations.Sum(c => c.R);
        int totV   = r.BucketCorrelations.Sum(c => c.V);
        int totC   = r.BucketCorrelations.Sum(c => c.C);
        int totUnk = r.BucketCorrelations.Sum(c => c.Unknown);
        int grand  = totR + totV + totC + totUnk;
        int outsideZoom = r.Gaps.TotalGaps - (r.Gaps.ZoomBuckets?.Sum(b => b.Count) ?? 0);

        Console.WriteLine();
        Console.WriteLine(line);
        Console.WriteLine("  CORRELATION TOTALS");
        Console.WriteLine(line);
        bool   ansiCt  = !Console.IsOutputRedirected;
        string boldCt  = ansiCt ? "\x1b[1m" : "";
        string dimCt   = ansiCt ? "\x1b[2m" : "";
        string resetCt = ansiCt ? "\x1b[0m" : "";
        Console.WriteLine($"{boldCt}  {"total",-21}   {grand,12:N0}   {"100.00%",7}   {new string('█', 30)}|{resetCt}");

        foreach (var (label, count) in new[] { ("r", totR), ("v", totV), ("c", totC), ("unk", totUnk) })
        {
            if (count == 0) continue;
            double pct    = grand > 0 ? 100.0 * count / grand : 0;
            string pctStr = count > 0 && pct < 0.005 ? "<0.01%" : $"{pct:F2}%";
            int    bars   = grand > 0 ? (int)Math.Round((double)count / grand * 15) : 0;
            string bar    = new string('░', bars).PadRight(30);
            Console.WriteLine($"{dimCt}{"",18}{"↳ " + label,-5}   {count,12:N0}   {pctStr,7}   {bar}|{resetCt}");
        }
        if (outsideZoom > 0)
        {
            double ozPct    = r.Gaps.TotalGaps > 0 ? 100.0 * outsideZoom / r.Gaps.TotalGaps : 0;
            string ozPctStr = outsideZoom > 0 && ozPct < 0.005 ? "<0.01%" : $"{ozPct:F2}%";
            string ozCond = r.Gaps.ZoomFromMs!.Value > 0
                ? $"< {FormatMsValue(r.Gaps.ZoomFromMs.Value)} or > {FormatMsValue(r.Gaps.ZoomToMs!.Value)}"
                : $"> {FormatMsValue(r.Gaps.ZoomToMs!.Value)}";
            Console.WriteLine();
            Console.WriteLine($"  · {outsideZoom:N0} ({ozPctStr}) of {r.Gaps.TotalGaps:N0} gaps {ozCond} (outside zoom)");
        }
    }

    if (r.UnknownEvents.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine(line);
        Console.WriteLine($"  UNKNOWN EVENTS  ({r.UnknownEvents.Count})");
        Console.WriteLine(line);
        foreach (var u in r.UnknownEvents)
        {
            Console.WriteLine($"  [{Path.GetFileName(u.FilePath)}]  {u.TimeR} → {u.EndTimeR}  label: {u.Label}");
            if (u.RawGapCount.HasValue)
                Console.WriteLine(u.RawGapCount.Value > 0
                    ? $"  └ {u.RawGapCount.Value} gaps in event file"
                    : "  └ no event file found ⚠");
        }
    }

    Console.WriteLine(dline);
}

static void PrintRawAnalysis(RawAnalysisResult r)
{
    const int W  = 82;
    string dline = new string('═', W);
    string line  = new string('─', W);

    Console.WriteLine(dline);
    Console.WriteLine("  RAW GAP ANALYSIS");
    Console.WriteLine(dline);
    Console.WriteLine($"  Files   : {r.SourceFiles.Count}  ({r.DateFrom} → {r.DateTo})");
    if (r.EventsLoaded > 0)
        Console.WriteLine($"  Events  : {r.EventsLoaded} COMPLETE events loaded for correlation");
    Console.WriteLine();
    Console.WriteLine(line);
    Console.WriteLine("  CONFIGURATION");
    Console.WriteLine(line);
    string stitchSrc = r.StitchThresholdFromEvents ? "computed from event files" : "fallback (no event files)";
    Console.WriteLine($"  Stitch threshold : {FormatMsValue(r.StitchThresholdMs),-10}  ← {stitchSrc}");
    PrintZoomConfig(r.Gaps);
    Console.WriteLine();
    Console.WriteLine(line);
    Console.WriteLine("  GAP SUMMARY");
    Console.WriteLine(line);
    PrintGapSummary(r.Gaps);
    Console.WriteLine();
    Console.WriteLine(line);
    Console.WriteLine("  GAP DISTRIBUTION");
    Console.WriteLine(line);
    PrintGapTable(r.Gaps.Buckets, r.Gaps.TotalGaps);

    if (r.Gaps.ZoomBuckets != null)
    {
        Console.WriteLine();
        Console.WriteLine(line);
        long bms = r.Gaps.ZoomBucketMs ?? Math.Max(1, (r.Gaps.ZoomToMs!.Value - r.Gaps.ZoomFromMs!.Value) / 20);
        string evtNote = r.EventsLoaded > 0
            ? $"  ·  {r.EventsLoaded} events"
            : "  ·  no events loaded";
        Console.WriteLine($"  GAP ZOOM  {FormatMsValue(r.Gaps.ZoomFromMs!.Value)} – {FormatMsValue(r.Gaps.ZoomToMs!.Value)}  (bucket: {bms}ms){evtNote}");
        Console.WriteLine(line);
        int zoomTotal = r.Gaps.ZoomBuckets.Sum(b => b.Count);
        PrintGapTable(r.Gaps.ZoomBuckets, zoomTotal, hideZeros: true, correlations: r.BucketCorrelations);
    }

    if (r.BucketCorrelations != null && r.BucketCorrelations.Any(c => c.HasAny))
    {
        int totR   = r.BucketCorrelations.Sum(c => c.R);
        int totV   = r.BucketCorrelations.Sum(c => c.V);
        int totC   = r.BucketCorrelations.Sum(c => c.C);
        int totUnk = r.BucketCorrelations.Sum(c => c.Unknown);
        int totOut = r.BucketCorrelations.Sum(c => c.Outside);
        int grand  = totR + totV + totC + totUnk + totOut;
        int outsideZoom = r.Gaps.TotalGaps - (r.Gaps.ZoomBuckets?.Sum(b => b.Count) ?? 0);

        Console.WriteLine();
        Console.WriteLine(line);
        Console.WriteLine("  CORRELATION TOTALS");
        Console.WriteLine(line);
        bool   ansiCt2  = !Console.IsOutputRedirected;
        string boldCt2  = ansiCt2 ? "\x1b[1m" : "";
        string dimCt2   = ansiCt2 ? "\x1b[2m" : "";
        string resetCt2 = ansiCt2 ? "\x1b[0m" : "";
        Console.WriteLine($"{boldCt2}  {"total",-21}   {grand,12:N0}   {"100.00%",7}   {new string('█', 30)}|{resetCt2}");

        foreach (var (label, count) in new[] { ("r", totR), ("v", totV), ("c", totC), ("unk", totUnk), ("out", totOut) })
        {
            if (count == 0) continue;
            double pct    = grand > 0 ? 100.0 * count / grand : 0;
            string pctStr = count > 0 && pct < 0.005 ? "<0.01%" : $"{pct:F2}%";
            int    bars   = grand > 0 ? (int)Math.Round((double)count / grand * 15) : 0;
            string bar    = new string('░', bars).PadRight(30);
            Console.WriteLine($"{dimCt2}{"",18}{"↳ " + label,-5}   {count,12:N0}   {pctStr,7}   {bar}|{resetCt2}");
        }
        if (outsideZoom > 0)
        {
            double ozPct    = r.Gaps.TotalGaps > 0 ? 100.0 * outsideZoom / r.Gaps.TotalGaps : 0;
            string ozPctStr = outsideZoom > 0 && ozPct < 0.005 ? "<0.01%" : $"{ozPct:F2}%";
            string ozCond = r.Gaps.ZoomFromMs!.Value > 0
                ? $"< {FormatMsValue(r.Gaps.ZoomFromMs.Value)} or > {FormatMsValue(r.Gaps.ZoomToMs!.Value)}"
                : $"> {FormatMsValue(r.Gaps.ZoomToMs!.Value)}";
            Console.WriteLine();
            Console.WriteLine($"  · {outsideZoom:N0} ({ozPctStr}) of {r.Gaps.TotalGaps:N0} gaps {ozCond} (outside zoom)");
        }
    }

    if (r.UnknownEvents.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine(line);
        Console.WriteLine($"  UNKNOWN EVENTS  ({r.UnknownEvents.Count})");
        Console.WriteLine(line);
        foreach (var u in r.UnknownEvents)
        {
            string rawInfo = u.RawGapCount.HasValue
                ? u.RawGapCount.Value > 0 ? $"  ·  {u.RawGapCount.Value} raw gaps" : "  ·  no raw data ⚠"
                : "";
            Console.WriteLine($"  [{Path.GetFileName(u.FilePath)}]  {u.TimeR} → {u.EndTimeR}  label: {u.Label}{rawInfo}");
        }
    }

    Console.WriteLine(dline);
}

// ── SHARED GAP PRINTING HELPERS ───────────────────────────────────────────────

static void PrintZoomConfig(GapAnalysisResult g)
{
    if (g.ZoomFromMs.HasValue)
    {
        string tag = g.ZoomFromIsAuto ? "(auto)" : "(manual)";
        Console.WriteLine($"  Zoom from        : {FormatMsValue(g.ZoomFromMs.Value),-10}  {tag}");
    }
    else
    {
        Console.WriteLine($"  Zoom from        : —");
    }

    if (g.ZoomToMs.HasValue)
    {
        string tag = g.ZoomToIsAuto ? "(auto)" : "(manual)";
        Console.WriteLine($"  Zoom to          : {FormatMsValue(g.ZoomToMs.Value),-10}  {tag}");
    }
    else
    {
        Console.WriteLine($"  Zoom to          : —");
    }

    if (g.ZoomBucketMs.HasValue)
    {
        string tag = g.ZoomBucketIsAuto ? "(auto)" : "(manual)";
        Console.WriteLine($"  Zoom bucket      : {FormatMsValue(g.ZoomBucketMs.Value),-10}  {tag}");
    }
    else
    {
        Console.WriteLine($"  Zoom bucket      : —");
    }
}

static void PrintGapSummary(GapAnalysisResult g)
{
    Console.WriteLine($"  Total gaps : {g.TotalGaps,12:N0}");
    Console.WriteLine($"  Min        : {FormatMsValue(g.MinGapMs),10}");
    Console.WriteLine($"  P50        : {FormatMsValue(g.P50GapMs),10}");
    Console.WriteLine($"  P95        : {FormatMsValue(g.P95GapMs),10}");
    Console.WriteLine($"  P99        : {FormatMsValue(g.P99GapMs),10}");
    Console.WriteLine($"  Max        : {FormatMsValue(g.MaxGapMs),10}");

    if (g.Outages is { Count: > 0 } o)
    {
        double outagePct    = g.TotalGaps > 0 ? 100.0 * o.Count / g.TotalGaps : 0;
        string outagePctStr = o.Count > 0 && outagePct < 0.005 ? "<0.01%" : $"{outagePct:F2}%";
        Console.WriteLine($"  Outages    : {o.Count:N0} ({outagePctStr}) of {g.TotalGaps:N0} gaps > {FormatMsValue(o.ThresholdMs)}");
        Console.WriteLine($"  └ offline  : {FormatMsValue(o.TotalMs),10} total  |  {FormatMsValue(o.LongestMs)} longest");
        Console.WriteLine($"  └ bound    : {FormatMsValue(o.ThresholdMs),10}   ← ceil((event max+50)/50)×50");
    }
    else
    {
        long bound = GapAnalyzer.ComputeBound(g.MaxGapMs);
        Console.WriteLine($"  └ bound    : {FormatMsValue(bound),10}   ← ceil((max+50)/50)×50");
    }
}

static void PrintGapTable(List<GapBucket> buckets, int total,
    bool hideZeros = false, List<BucketCorrelation>? correlations = null)
{
    const int barMax    = 30;
    const int subBarMax = 15;
    bool      ansi      = !Console.IsOutputRedirected;
    const string bold   = "\x1b[1m";
    const string dim    = "\x1b[2m";
    const string reset  = "\x1b[0m";

    // Trim trailing zero-count buckets (keep at least one row), preserving original indices
    int lastNonZero = buckets.Count - 1;
    while (lastNonZero > 0 && buckets[lastNonZero].Count == 0)
        lastNonZero--;

    // Build list of (bucket, originalIndex) so correlations stay aligned after filtering
    var visible = buckets
        .Take(lastNonZero + 1)
        .Select((b, i) => (b, i))
        .Where(x => !hideZeros || x.b.Count > 0)
        .ToList();

    int maxCount = visible.Count > 0 ? visible.Max(x => x.b.Count) : 0;

    Console.WriteLine($"  {"FROM",9}   {"TO",9}   {"COUNT",12}   {"%",7}   DISTRIBUTION");
    Console.WriteLine($"  {"─────────",9}   {"─────────",9}   {"────────────",12}   {"───────",7}   ───────────────¦──────────────¦");

    foreach (var (b, origIdx) in visible)
    {
        long   step   = b.ToMs == long.MaxValue ? 0 : b.ToMs - b.FromMs;
        string from   = FormatMsValue(b.FromMs, step);
        string to     = b.ToMs == long.MaxValue ? "∞" : FormatMsValue(b.ToMs, step);
        double pct    = total > 0 ? 100.0 * b.Count / total : 0;
        string pctStr = b.Count > 0 && pct < 0.005 ? "<0.01%" : $"{pct:F2}%";
        int    bars   = maxCount > 0 ? (int)Math.Round((double)b.Count / maxCount * barMax) : 0;
        string bar    = new string('█', bars).PadRight(barMax);

        BucketCorrelation? corr = correlations != null && origIdx < correlations.Count ? correlations[origIdx] : null;

        string pre = ansi ? bold : "";
        string suf = ansi ? reset : "";
        Console.WriteLine($"{pre}  {from,9}   {to,9}   {b.Count,12:N0}   {pctStr,7}   {bar}|{suf}");

        // Sub-rows for each non-zero label
        if (corr != null && corr.HasAny)
        {
            string spre = ansi ? dim : "";
            string ssuf = ansi ? reset : "";
            foreach (var (label, count) in new[]
            {
                ("r",   corr.R),
                ("v",   corr.V),
                ("c",   corr.C),
                ("unk", corr.Unknown),
                ("out", corr.Outside),
            })
            {
                if (count == 0) continue;
                double subPct    = b.Count > 0 ? 100.0 * count / b.Count : 0;
                string subPctStr = count > 0 && subPct < 0.005 ? "<0.01%" : $"{subPct:F2}%";
                int    subBars   = b.Count > 0 ? (int)Math.Round((double)count / b.Count * subBarMax) : 0;
                string subBar    = new string('░', subBars).PadRight(barMax);
                Console.WriteLine($"{spre}{"",18}{"↳ " + label,-5}   {count,12:N0}   {subPctStr,7}   {subBar}|{ssuf}");
            }
        }
    }
}

// Returns decimal places needed so a step of stepMs is distinguishable in a unit of unitMs.
static int NeededDp(long stepMs, long unitMs)
{
    double s = (double)stepMs / unitMs;
    if (s >= 1)    return 0;
    if (s >= 0.1)  return 1;
    if (s >= 0.01) return 2;
    return 3; // signals: too fine for this unit, fall back to smaller unit
}

// Formats ms into a human-readable string.
// stepMs: bucket width — when provided, guarantees adjacent bucket labels are always distinct.
// If the chosen unit needs > 2 decimal places for that step, falls back to a smaller unit.
static string FormatMsValue(long ms, long stepMs = 0)
{
    long step = stepMs > 0 ? stepMs : 1;

    if (ms >= 3_600_000 && (stepMs == 0 || NeededDp(step, 3_600_000) <= 2))
    {
        int dp = stepMs == 0 ? 1 : NeededDp(step, 3_600_000);
        return (ms / 3_600_000.0).ToString($"F{dp}") + "hr";
    }
    if (ms >= 60_000 && (stepMs == 0 || NeededDp(step, 60_000) <= 2))
    {
        int dp = stepMs == 0 ? 1 : NeededDp(step, 60_000);
        return (ms / 60_000.0).ToString($"F{dp}") + "min";
    }
    if (ms >= 1_000 && (stepMs == 0 || NeededDp(step, 1_000) <= 2))
    {
        int dp = stepMs == 0 ? 1 : NeededDp(step, 1_000);
        return (ms / 1_000.0).ToString($"F{dp}") + "s";
    }
    return $"{ms}ms";
}

static void PrintRestAnalysis(RestAnalysisResult r)
{
    const int W = 66;
    string line = new string('─', W);
    string dline = new string('═', W);

    Console.WriteLine(dline);
    Console.WriteLine("  REST SIGNAL ANALYSIS");
    Console.WriteLine(dline);
    Console.WriteLine($"  Files  : {r.SourceFiles.Count}");
    foreach (var f in r.SourceFiles)
        Console.WriteLine($"           {Path.GetFileName(f)}");
    if (r.ConfigFilter.HasValue)
        Console.WriteLine($"  Filter : voltage < {r.ConfigFilter:F2}V  (from #config in file)");
    Console.WriteLine($"  Samples: {r.TotalSamples:N0}");
    Console.WriteLine();

    Console.WriteLine(line);
    Console.WriteLine("  ALL SAMPLES");
    Console.WriteLine(line);
    PrintStats(r.All);
    Console.WriteLine();

    Console.WriteLine(line);
    Console.WriteLine("  RUN ANALYSIS");
    Console.WriteLine(line);
    if (r.EventFilesAnalyzed > 0)
        Console.WriteLine($"  Gap threshold: {r.GapThresholdMs}ms  " +
            $"← ceil(({r.MaxEventGapMs} + 50) / 50) × 50  " +
            $"[max gap from {r.EventFilesAnalyzed} event files]");
    else
        Console.WriteLine($"  Gap threshold: {r.GapThresholdMs}ms  ← fallback (no event files found)");
    Console.WriteLine($"  Total runs : {r.TotalRuns:N0}");
    Console.WriteLine($"  Long runs  : {r.LongRunCount:N0}  (≥100 samples = ≥5s continuous)  →  stable rest");
    Console.WriteLine($"  Short runs : {r.ShortRunCount:N0}  (<100 samples)  →  transitions / decay");
    Console.WriteLine();

    if (r.LongRuns.Count > 0)
    {
        Console.WriteLine($"  LONG RUNS — stable rest  ({r.LongRuns.Count:N0} samples)");
        PrintStats(r.LongRuns);
        Console.WriteLine();
    }
    else
    {
        Console.WriteLine("  LONG RUNS — none found");
        Console.WriteLine();
    }

    if (r.ShortRuns.Count > 0)
    {
        Console.WriteLine($"  SHORT RUNS — transitions / decay  ({r.ShortRuns.Count:N0} samples)");
        PrintStats(r.ShortRuns);
        Console.WriteLine();
    }
    else
    {
        Console.WriteLine("  SHORT RUNS — none found");
        Console.WriteLine();
    }

    Console.WriteLine(line);
    Console.WriteLine("  HISTOGRAM  (0.01V buckets, all samples)");
    Console.WriteLine(line);
    PrintHistogram(r.Histogram, r.TotalSamples, r.BucketWidth);
    Console.WriteLine(dline);
}

static void PrintStats(VoltageStats s)
{
    Console.WriteLine($"  Min   {s.Min:F3}V   Max   {s.Max:F3}V   Mean  {s.Mean:F3}V   StdDev {s.StdDev:F3}V");
    Console.WriteLine($"  P50   {s.P50:F3}V   P75   {s.P75:F3}V   P90   {s.P90:F3}V");
    Console.WriteLine($"  P95   {s.P95:F3}V   P99   {s.P99:F3}V   P99.9 {s.P999:F3}V");
}

static void PrintHistogram(int[] histogram, int total, double bucketWidth)
{
    if (total == 0) return;
    const int barMaxWidth = 40;

    int maxCount = histogram.Max();

    for (int i = 0; i < histogram.Length; i++)
    {
        double from  = i * bucketWidth;
        double to    = from + bucketWidth;
        int    count = histogram[i];
        double pct   = 100.0 * count / total;
        int    bars  = maxCount > 0 ? (int)Math.Round((double)count / maxCount * barMaxWidth) : 0;
        string bar   = new string('█', bars);

        Console.WriteLine($"  {from:F2}-{to:F2}V  {bar,-40}  {pct,5:F1}%  ({count:N0})");
    }
}

static void PrintUsage()
{
    Console.WriteLine("Usage: idc <subcommand> [input] [options]");
    Console.WriteLine();
    Console.WriteLine("Subcommands:");
    Console.WriteLine("  event        — generate events_* and event_* files from raw input");
    Console.WriteLine("  rest         — generate rest_yyyyMMdd.csv from raw input");
    Console.WriteLine("  process      — event + rest in one pass");
    Console.WriteLine("  analyze-rest — descriptive statistics of rest_*.csv files");
    Console.WriteLine("  analyze-raw  — gap analysis of raw_*.csv files");
    Console.WriteLine("  analyze-event — gap analysis of event_*.csv files (resets between files)");
    Console.WriteLine();
    Console.WriteLine("Input (optional, default: current directory):");
    Console.WriteLine("  <folder>     finds raw_*.csv (or rest_*.csv for analyze-rest) inside");
    Console.WriteLine("  <file>       single file");
    Console.WriteLine("  <glob>       e.g. captures/raw_2026*.csv");
    Console.WriteLine("  -i, --input <path>");
    Console.WriteLine();
    Console.WriteLine("Options (event / rest / process):");
    Console.WriteLine("  -o, --output      <folder>  output folder");
    Console.WriteLine("  -e, --event-end   <V>       voltage < V closes event / rest threshold  (default: 0.3)");
    Console.WriteLine("  -s, --event-start <V>       voltage >= V opens event  (default: 0.5)");
    Console.WriteLine("  -g, --gap         <ms>      gap threshold  (default: 1000)");
    Console.WriteLine("  -m, --max-dur     <ms>      max event duration  (default: 50000)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  idc process                              # cwd, all raw_*.csv");
    Console.WriteLine("  idc analyze-rest captures/               # all rest_*.csv in folder");
    Console.WriteLine("  idc analyze-rest captures/rest_20260312.csv  # single file");
}

// Extension helper
static class StringExtensions
{
    public static bool IsOneOf(this string value, params string[] options)
        => options.Contains(value);
}
