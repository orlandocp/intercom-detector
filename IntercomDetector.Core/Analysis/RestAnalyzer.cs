namespace IntercomDetector.Core.Analysis;

/// <summary>
/// Reads rest_*.csv files and computes descriptive voltage statistics.
/// Uses a direction-flip state machine to skip post-event decay samples
/// and only accept voltages once stable rest oscillation is confirmed.
/// </summary>
public static class RestAnalyzer
{
    private const double DefaultBucketWidthV = 0.01;

    public static RestAnalysisResult Analyze(IEnumerable<string> filePaths, double? bucketWidthV = null)
    {
        var files = filePaths.OrderBy(f => f).ToList();

        bool   bucketIsAuto = !bucketWidthV.HasValue;
        double bucket       = bucketWidthV ?? DefaultBucketWidthV;

        var    filter       = new RestStateFilter();
        int    total        = 0;
        var    valid        = new List<double>();

        foreach (var path in files)
        {
            foreach (var (ts, v) in ReadSamples(path))
            {
                total++;
                if (filter.Process(ts, v))
                    valid.Add(v);
            }
        }

        var   stats     = VoltageStats.Compute(valid);
        int[] histogram = BuildHistogram(valid, bucket, stats.Max);

        return new RestAnalysisResult
        {
            SourceFiles   = files,
            DateFrom      = files.Count > 0 ? ParseDate(Path.GetFileName(files.First())) : "",
            DateTo        = files.Count > 0 ? ParseDate(Path.GetFileName(files.Last()))  : "",
            TotalSamples  = total,
            ValidSamples  = valid.Count,
            GapThresholdMs = RestStateFilter.GapThresholdMs,
            BucketWidthV  = bucket,
            BucketIsAuto  = bucketIsAuto,
            Stats         = stats,
            Histogram     = histogram,
        };
    }

    // ── STATE MACHINE ─────────────────────────────────────────────────────────

    /// <summary>
    /// Stateful sequential filter that confirms rest via a voltage direction flip.
    ///
    /// States:
    ///   Scanning  — skipping decay; waiting for the first direction flip
    ///   Confirmed — rest is active; all samples accepted until gap resets state
    ///
    /// Flip detection (needs 3 samples minimum after each reset):
    ///   S1 → stored, no trend yet
    ///   S2 → establishes initial trend (UP or DOWN); flat (V==prevV) is skipped
    ///   S3+ → if trend flips → Confirmed; if same → continue scanning
    /// </summary>
    private sealed class RestStateFilter
    {
        public const long GapThresholdMs = 800;  // gaps wider than this reset the machine

        private enum State { Scanning, Confirmed }
        private enum Trend { None, Up, Down }

        private State  _state     = State.Scanning;
        private Trend  _prevTrend = Trend.None;
        private long   _prevTs    = -1;
        private double _prevV     = double.NaN;

        /// <summary>
        /// Process one sample. Returns true if the voltage is valid confirmed-rest data.
        /// </summary>
        public bool Process(long ts, double v)
        {
            // Very first sample — anchor state, not yet valid
            if (_prevTs < 0)
            {
                Store(ts, v);
                return false;
            }

            long gap = ts - _prevTs;

            // Gap too large → device was offline or event occurred; start over
            if (gap > GapThresholdMs)
            {
                Reset(ts, v);
                return false;
            }

            // In confirmed rest: accept all samples until a gap resets us
            if (_state == State.Confirmed)
            {
                Store(ts, v);
                return true;
            }

            // ── Scanning: looking for first direction flip ────────────────────

            // Flat sample: no direction change, advance position without updating trend
            if (v == _prevV)
            {
                Store(ts, v);
                return false;
            }

            Trend newTrend = v > _prevV ? Trend.Up : Trend.Down;

            // First non-flat sample after reset: establish initial trend, keep scanning
            if (_prevTrend == Trend.None)
            {
                _prevTrend = newTrend;
                Store(ts, v);
                return false;
            }

            bool flipped = newTrend != _prevTrend;
            _prevTrend   = newTrend;
            Store(ts, v);

            if (flipped)
            {
                _state = State.Confirmed;
                return true;   // this sample is the first confirmed-rest sample
            }

            return false;
        }

        private void Store(long ts, double v) { _prevTs = ts; _prevV = v; }

        private void Reset(long ts, double v)
        {
            _state     = State.Scanning;
            _prevTrend = Trend.None;
            Store(ts, v);
        }
    }

    // ── HELPERS ───────────────────────────────────────────────────────────────

    private static int[] BuildHistogram(List<double> voltages, double bucket, double max)
    {
        if (voltages.Count == 0 || max <= 0) return Array.Empty<int>();

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

    private static IEnumerable<(long Ts, double V)> ReadSamples(string path)
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

            if (!long.TryParse(parts[1].Trim(), out long ts)) continue;
            if (!double.TryParse(parts[2].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double v)) continue;

            yield return (ts, v);
        }
    }
}
