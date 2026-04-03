using IntercomDetector.Core.IO;

namespace IntercomDetector.Core.Analysis;

/// <summary>
/// Reads rest_*.csv files and computes descriptive statistics of the resting voltage signal.
/// No algorithm assumptions — pure description of what the signal looks like at rest.
/// </summary>
public static class RestAnalyzer
{
    private const long   SamplingIntervalMs = 50;
    private const int    LongRunMinSamples  = 100;  // 100 × 50ms = 5 seconds
    private const double BucketWidth        = 0.01;
    private const int    BucketCount        = 30;   // 0.00V to 0.30V

    public static RestAnalysisResult Analyze(IEnumerable<string> filePaths)
    {
        var files = filePaths.OrderBy(f => f).ToList();

        // ── DERIVE GAP THRESHOLD FROM EVENT FILES ────────────────────────────
        // Auto-detect event_*.csv in the events/ subfolder.
        // Formula: ceil((maxGap + samplingInterval) / samplingInterval) * samplingInterval
        long maxEventGap     = 0;
        int  eventFilesCount = 0;

        if (files.Count > 0)
        {
            string restFolder   = Path.GetDirectoryName(Path.GetFullPath(files[0]))!;
            string eventsFolder = Path.Combine(restFolder, "events");

            if (Directory.Exists(eventsFolder))
            {
                var eventFiles = Directory.GetFiles(eventsFolder, "event_*.csv").OrderBy(f => f).ToList();
                eventFilesCount = eventFiles.Count;

                if (eventFilesCount > 0)
                    maxEventGap = GapAnalyzer.ComputeMaxGap(
                        eventFiles.Select(f => SampleFileReader.ReadTimestamps(f)));
            }
        }

        long gapThresholdMs = maxEventGap > 0
            ? (long)(Math.Ceiling((double)(maxEventGap + SamplingIntervalMs) / SamplingIntervalMs) * SamplingIntervalMs)
            : 800;

        // ── LOAD REST SAMPLES ─────────────────────────────────────────────────
        double? config = null;
        var all = new List<(long Ts, double V)>();

        foreach (var path in files)
        {
            foreach (var (ts, v, cfg) in ReadRestFile(path))
            {
                all.Add((ts, v));
                if (cfg.HasValue) config = cfg;
            }
        }

        // ── SEGMENT INTO RUNS ─────────────────────────────────────────────────
        var runs = new List<RestRun>();

        if (all.Count > 0)
        {
            var currentVoltages = new List<double> { all[0].V };
            long runStart = all[0].Ts;
            long prevTs   = all[0].Ts;

            for (int i = 1; i < all.Count; i++)
            {
                long gap = all[i].Ts - prevTs;
                if (gap > gapThresholdMs)
                {
                    runs.Add(new RestRun
                    {
                        SampleCount = currentVoltages.Count,
                        StartMs     = runStart,
                        EndMs       = prevTs,
                        Voltages    = new List<double>(currentVoltages)
                    });
                    currentVoltages = new List<double>();
                    runStart        = all[i].Ts;
                }
                currentVoltages.Add(all[i].V);
                prevTs = all[i].Ts;
            }
            if (currentVoltages.Count > 0)
                runs.Add(new RestRun
                {
                    SampleCount = currentVoltages.Count,
                    StartMs     = runStart,
                    EndMs       = prevTs,
                    Voltages    = new List<double>(currentVoltages)
                });
        }

        // ── CLASSIFY RUNS ─────────────────────────────────────────────────────
        var longRuns  = runs.Where(r => r.SampleCount >= LongRunMinSamples).ToList();
        var shortRuns = runs.Where(r => r.SampleCount <  LongRunMinSamples).ToList();

        // ── FLATTEN FOR STATS ─────────────────────────────────────────────────
        var vAll   = all.Select(x => x.V).ToList();
        var vLong  = longRuns .SelectMany(r => r.Voltages).ToList();
        var vShort = shortRuns.SelectMany(r => r.Voltages).ToList();

        // ── HISTOGRAM ─────────────────────────────────────────────────────────
        var histogram = new int[BucketCount];
        foreach (var (_, v) in all)
        {
            int bucket = (int)Math.Floor(v / BucketWidth);
            if (bucket >= 0 && bucket < BucketCount)
                histogram[bucket]++;
        }

        return new RestAnalysisResult
        {
            SourceFiles        = files,
            ConfigFilter       = config,
            TotalSamples       = all.Count,
            GapThresholdMs     = gapThresholdMs,
            MaxEventGapMs      = maxEventGap,
            EventFilesAnalyzed = eventFilesCount,
            All                = VoltageStats.Compute(vAll),
            LongRuns           = VoltageStats.Compute(vLong),
            ShortRuns          = VoltageStats.Compute(vShort),
            TotalRuns          = runs.Count,
            LongRunCount       = longRuns.Count,
            ShortRunCount      = shortRuns.Count,
            Histogram          = histogram,
            BucketWidth        = BucketWidth,
        };
    }

    private static IEnumerable<(long Ts, double V, double? Config)> ReadRestFile(string path)
    {
        double? config = null;

        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("#config:"))
            {
                var part = line.Split('<');
                if (part.Length == 2 &&
                    double.TryParse(part[1].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double cfg))
                    config = cfg;
                continue;
            }

            if (line.StartsWith("TimeR")) continue;
            if (line.StartsWith("#"))     continue;

            var parts = line.Split(',');
            if (parts.Length < 3) continue;

            if (!long.TryParse(parts[1].Trim(), out long ts)) continue;
            if (!double.TryParse(parts[2].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double v)) continue;

            yield return (ts, v, config);
        }
    }
}
