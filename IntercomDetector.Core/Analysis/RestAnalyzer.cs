namespace IntercomDetector.Core.Analysis;

/// <summary>
/// Reads rest_*.csv files and computes descriptive voltage statistics
/// with a dynamic histogram. No gap/run segmentation — pure signal description.
/// </summary>
public static class RestAnalyzer
{
    /// <summary>Default histogram bucket width in volts when none is supplied.</summary>
    private const double DefaultBucketWidthV = 0.01;

    public static RestAnalysisResult Analyze(IEnumerable<string> filePaths, double? bucketWidthV = null)
    {
        var files = filePaths.OrderBy(f => f).ToList();

        bool bucketIsAuto = !bucketWidthV.HasValue;

        var voltages = new List<double>();
        foreach (var path in files)
            foreach (double v in ReadVoltages(path))
                voltages.Add(v);

        var stats = VoltageStats.Compute(voltages);

        double bucket     = bucketWidthV ?? DefaultBucketWidthV;
        int[]  histogram  = BuildHistogram(voltages, bucket, stats.Max);

        return new RestAnalysisResult
        {
            SourceFiles  = files,
            DateFrom     = files.Count > 0 ? ParseDate(Path.GetFileName(files.First())) : "",
            DateTo       = files.Count > 0 ? ParseDate(Path.GetFileName(files.Last()))  : "",
            TotalSamples = voltages.Count,
            BucketWidthV = bucket,
            BucketIsAuto = bucketIsAuto,
            Stats        = stats,
            Histogram    = histogram,
        };
    }

    // ── HELPERS ───────────────────────────────────────────────────────────────

    private static int[] BuildHistogram(List<double> voltages, double bucket, double max)
    {
        if (voltages.Count == 0 || max <= 0) return Array.Empty<int>();

        // +1e-9 guard against floating-point edge where max/bucket is exact integer
        int count = (int)Math.Ceiling(max / bucket + 1e-9) + 1;
        var hist  = new int[count];

        foreach (double v in voltages)
        {
            int idx = (int)(v / bucket);
            if (idx >= 0 && idx < hist.Length)
                hist[idx]++;
        }

        return hist;
    }

    private static string ParseDate(string fileName)
    {
        // rest_yyyyMMdd.csv → "yyyy-MM-dd"
        if (fileName.StartsWith("rest_") && fileName.Length >= 13)
        {
            string d = fileName.Substring(5, 8);
            if (d.Length == 8 && d.All(char.IsDigit))
                return $"{d[..4]}-{d[4..6]}-{d[6..8]}";
        }
        return fileName;
    }

    private static IEnumerable<double> ReadVoltages(string path)
    {
        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("TimeR") || line.StartsWith("#")) continue;

            var parts = line.Split(',');
            if (parts.Length < 3) continue;

            if (double.TryParse(parts[2].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double v))
                yield return v;
        }
    }
}
