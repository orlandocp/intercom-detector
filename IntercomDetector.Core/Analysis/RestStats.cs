namespace IntercomDetector.Core.Analysis;

/// <summary>Descriptive statistics for a set of voltage samples.</summary>
public class VoltageStats
{
    public int    Count   { get; init; }
    public double Min     { get; init; }
    public double Max     { get; init; }
    public double Mean    { get; init; }
    public double StdDev  { get; init; }
    public double P50     { get; init; }
    public double P75     { get; init; }
    public double P90     { get; init; }
    public double P95     { get; init; }
    public double P99     { get; init; }
    public double P999    { get; init; }

    public static VoltageStats Compute(List<double> values)
    {
        if (values.Count == 0)
            return new VoltageStats();

        int n = values.Count;
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
        int idx = (int)Math.Floor(p * sorted.Count);
        idx = Math.Clamp(idx, 0, sorted.Count - 1);
        return sorted[idx];
    }
}

/// <summary>A contiguous run of rest samples (no time gap > threshold between them).</summary>
public class RestRun
{
    public int          SampleCount { get; init; }
    public long         StartMs     { get; init; }
    public long         EndMs       { get; init; }
    public long         DurationMs  => EndMs - StartMs;
    public List<double> Voltages    { get; init; } = new();
}

/// <summary>Full result of a rest file analysis session.</summary>
public class RestAnalysisResult
{
    public List<string> SourceFiles  { get; init; } = new();
    public double?      ConfigFilter { get; init; }
    public int          TotalSamples { get; init; }

    // Gap threshold used for run segmentation — derived dynamically from event files
    public long   GapThresholdMs     { get; init; }
    public long   MaxEventGapMs      { get; init; }  // raw max before formula
    public int    EventFilesAnalyzed { get; init; }

    public VoltageStats All       { get; init; } = new();
    public VoltageStats LongRuns  { get; init; } = new();
    public VoltageStats ShortRuns { get; init; } = new();

    public int TotalRuns     { get; init; }
    public int LongRunCount  { get; init; }
    public int ShortRunCount { get; init; }

    public int[]  Histogram   { get; init; } = Array.Empty<int>();
    public double BucketWidth { get; init; } = 0.01;
}
