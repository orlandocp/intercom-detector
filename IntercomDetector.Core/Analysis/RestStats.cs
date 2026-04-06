namespace IntercomDetector.Core.Analysis;

// ── ZOOM TYPES ────────────────────────────────────────────────────────────────

public enum SampleRole { Prev, Reset, Anchor, Scan, Flip, Confirm, Valid, Match, Post }

/// <summary>One sample in a zoom context window.</summary>
public record RestSampleContext(
    long       Ts,
    string     TimeR,
    long?      GapMs,
    SampleRole Role,
    double     V,
    string     Trend,       // "UP", "DOWN", "—", or "gap > Nms"
    int?       MatchNum = null  // set on Match rows and on Valid rows that were previously a Match
);

/// <summary>One streaming zoom match: a valid sample in [fromV, toV] with its context.</summary>
public record RestZoomMatch(
    string                  File,
    long                    MatchTs,
    string                  MatchTimeR,
    double                  MatchV,
    /// <summary>Full context: prev? → RESET → anchor → scan… → CONFIRM → valid… → MATCH.</summary>
    List<RestSampleContext>  Context,
    /// <summary>
    /// Post samples: same-voltage samples immediately following the match (if any),
    /// plus the first sample whose voltage differs.  Empty when cut by gap or eof.
    /// </summary>
    List<RestSampleContext>  PostSamples,
    /// <summary>Non-null when PostSamples is empty: reason for the cut (gap / end of file).</summary>
    string?                  PostCutReason
);

// ── STATISTICS & RESULT ───────────────────────────────────────────────────────

/// <summary>Descriptive statistics for a set of voltage samples.</summary>
public class VoltageStats
{
    public int    Count  { get; init; }
    public double Min    { get; init; }
    public double Max    { get; init; }
    public double Mean   { get; init; }
    public double StdDev { get; init; }
    public double P50    { get; init; }
    public double P75    { get; init; }
    public double P90    { get; init; }
    public double P95    { get; init; }
    public double P99    { get; init; }
    public double P999   { get; init; }

    public static VoltageStats Compute(List<double> values)
    {
        if (values.Count == 0)
            return new VoltageStats();

        int n      = values.Count;
        var sorted = values.OrderBy(v => v).ToList();
        double mean     = values.Sum() / n;
        double variance = values.Sum(v => (v - mean) * (v - mean)) / n;

        return new VoltageStats
        {
            Count  = n,
            Min    = sorted[0],
            Max    = sorted[n - 1],
            Mean   = mean,
            StdDev = Math.Sqrt(variance),
            P50    = Percentile(sorted, 0.50),
            P75    = Percentile(sorted, 0.75),
            P90    = Percentile(sorted, 0.90),
            P95    = Percentile(sorted, 0.95),
            P99    = Percentile(sorted, 0.99),
            P999   = Percentile(sorted, 0.999),
        };
    }

    private static double Percentile(List<double> sorted, double p)
    {
        int idx = Math.Clamp((int)Math.Floor(p * sorted.Count), 0, sorted.Count - 1);
        return sorted[idx];
    }
}

/// <summary>Result of a rest voltage analysis session.</summary>
public class RestAnalysisResult
{
    public List<string> SourceFiles  { get; init; } = new();
    public string       DateFrom     { get; init; } = "";
    public string       DateTo       { get; init; } = "";

    /// <summary>Total samples read from all rest files.</summary>
    public int TotalSamples { get; init; }
    /// <summary>Samples accepted after the direction-flip filter (confirmed rest).</summary>
    public int ValidSamples { get; init; }

    /// <summary>Gap threshold used to reset the state machine (ms).</summary>
    public long   GapThresholdMs { get; init; }
    /// <summary>Voltage bucket width used for the histogram.</summary>
    public double BucketWidthV   { get; init; }
    /// <summary>True when bucket was auto-computed; false when supplied via --bucket.</summary>
    public bool   BucketIsAuto   { get; init; }

    public VoltageStats Stats     { get; init; } = new();
    /// <summary>Histogram counts over valid samples only. Index i covers [i×BucketWidthV, (i+1)×BucketWidthV).</summary>
    public int[]        Histogram { get; init; } = Array.Empty<int>();
}
