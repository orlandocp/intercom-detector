namespace IntercomDetector.Core.Analysis;

/// <summary>
/// Analyzes the onset of labeled events by reading the first two rows of each
/// event_*.csv file (rest sample + first event sample) and computing per-group
/// V[1] statistics and a threshold sweep.
///
/// Caller is responsible for resolving file paths; this class only does computation.
/// </summary>
public static class OnsetAnalyzer
{
    private static readonly string[] Labels = { "r", "v", "c" };

    /// <summary>
    /// Analyze onset voltages.
    /// </summary>
    /// <param name="eventFiles">Paths to event_*.csv files.</param>
    /// <param name="logFiles">Paths to events_*.csv log files used to resolve labels.</param>
    public static OnsetAnalysisResult Analyze(
        IEnumerable<string> eventFiles,
        IEnumerable<string> logFiles)
    {
        var labelMap = BuildLabelMap(logFiles);
        var raw      = ReadOnsetRows(eventFiles, labelMap, out int skipped);

        var groups = Labels
            .Select(lbl => BuildGroupStats(lbl, raw.GetValueOrDefault(lbl) ?? new()))
            .Where(g => g.Count > 0)
            .ToList();

        var sweep = BuildThresholdSweep(raw,
            groups.SelectMany(g => new[] { g.V1Min, g.V1Max }));

        return new OnsetAnalysisResult
        {
            Groups         = groups,
            ThresholdSweep = sweep,
            Skipped        = skipped,
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Reads events_log CSVs and returns a map of event start ts → label.</summary>
    private static Dictionary<long, string> BuildLabelMap(IEnumerable<string> logFiles)
    {
        var map = new Dictionary<long, string>();
        foreach (var path in logFiles)
        {
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var p = line.Split(',');
                if (p.Length < 12) continue;
                if (p[10].Trim() != "COMPLETE") continue;
                string lbl = p[11].Trim().ToLowerInvariant();
                if (lbl != "r" && lbl != "v" && lbl != "c") continue;
                if (long.TryParse(p[3].Trim(), out long ts)) map[ts] = lbl;
            }
        }
        return map;
    }

    /// <summary>
    /// Reads the first two data rows (row[0] = rest, row[1] = first event sample)
    /// from each event file and groups results by label.
    /// </summary>
    private static Dictionary<string, List<(double V0, double V1, long RiseGapMs)>> ReadOnsetRows(
        IEnumerable<string> eventFiles,
        Dictionary<long, string> labelMap,
        out int skipped)
    {
        var groups = Labels.ToDictionary(l => l,
            _ => new List<(double V0, double V1, long RiseGapMs)>());
        skipped = 0;

        foreach (var path in eventFiles)
        {
            var rows = ReadFirstTwoRows(path);
            if (rows is null) { skipped++; continue; }

            long eventTs = rows.Value.Ts1;
            if (!labelMap.TryGetValue(eventTs, out string? label)) { skipped++; continue; }

            groups[label].Add((rows.Value.V0, rows.Value.V1, eventTs - rows.Value.Ts0));
        }

        return groups;
    }

    private static (long Ts0, double V0, long Ts1, double V1)? ReadFirstTwoRows(string path)
    {
        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        var rows        = new List<(long Ts, double V)>();
        bool hdrSkipped = false;
        string? line;

        while ((line = reader.ReadLine()) != null && rows.Count < 2)
        {
            if (!hdrSkipped) { hdrSkipped = true; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;

            var p = line.Split(',');
            if (p.Length < 3) continue;
            if (!long.TryParse(p[1].Trim(), out long ts)) continue;
            if (!double.TryParse(p[2].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v)) continue;

            rows.Add((ts, v));
        }

        if (rows.Count < 2) return null;
        return (rows[0].Ts, rows[0].V, rows[1].Ts, rows[1].V);
    }

    private static OnsetGroupStats BuildGroupStats(
        string label, List<(double V0, double V1, long RiseGapMs)> data)
    {
        if (data.Count == 0)
            return new OnsetGroupStats(label, 0, 0, 0, 0, 0, 0, 0);

        double v0Mean  = data.Average(d => d.V0);
        double v1Min   = data.Min(d => d.V1);
        double v1Max   = data.Max(d => d.V1);
        double v1Mean  = data.Average(d => d.V1);
        double v1Std   = StdDev(data.Select(d => d.V1), v1Mean);
        double gapMean = data.Average(d => d.RiseGapMs);

        return new OnsetGroupStats(label, data.Count, v0Mean,
                                   v1Min, v1Max, v1Mean, v1Std, gapMean);
    }

    private static List<ThresholdPoint> BuildThresholdSweep(
        Dictionary<string, List<(double V0, double V1, long RiseGapMs)>> groups,
        IEnumerable<double> boundaryHints)
    {
        var rData = groups.GetValueOrDefault("r") ?? new();
        var vData = groups.GetValueOrDefault("v") ?? new();
        var cData = groups.GetValueOrDefault("c") ?? new();

        var allV1 = rData.Concat(vData).Concat(cData).Select(d => d.V1).ToList();
        if (allV1.Count == 0) return new();

        double tMin = Math.Floor(allV1.Min() * 10) / 10;
        double tMax = Math.Ceiling(allV1.Max() * 10) / 10;

        int nR = rData.Count, nV = vData.Count, nC = cData.Count;
        var points = new List<ThresholdPoint>();

        for (double t = tMin; t <= tMax + 0.001; t = Math.Round(t + 0.1, 1))
        {
            points.Add(new ThresholdPoint(
                t,
                rData.Count(d => d.V1 >= t),
                vData.Count(d => d.V1 >= t),
                cData.Count(d => d.V1 >= t),
                nR, nV, nC));
        }

        return points;
    }

    private static double StdDev(IEnumerable<double> values, double mean)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0
            : Math.Sqrt(list.Sum(x => (x - mean) * (x - mean)) / list.Count);
    }
}
