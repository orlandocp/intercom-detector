namespace IntercomDetector.Core.Analysis;

/// <summary>Descriptive statistics for V[1] (first event sample) of one label group.</summary>
public record OnsetGroupStats(
    string Label,
    int    Count,
    double V0Mean,
    double V1Min,
    double V1Max,
    double V1Mean,
    double V1StdDev,
    double RiseGapMeanMs
);

/// <summary>One row of the threshold sweep: counts and percentages above a given V[1] cutoff.</summary>
public record ThresholdPoint(
    double Threshold,
    int    RAbove,
    int    VAbove,
    int    CAbove,
    int    NR,
    int    NV,
    int    NC
)
{
    public double RPct => NR > 0 ? 100.0 * RAbove / NR : 0;
    public double VPct => NV > 0 ? 100.0 * VAbove / NV : 0;
    public double CPct => NC > 0 ? 100.0 * CAbove / NC : 0;
}

/// <summary>Result of the onset analysis: per-group V[1] stats and threshold sweep.</summary>
public class OnsetAnalysisResult
{
    public List<OnsetGroupStats> Groups         { get; init; } = new();
    public List<ThresholdPoint>  ThresholdSweep { get; init; } = new();
    public int                   Skipped        { get; init; }
}
