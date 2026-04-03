namespace IntercomDetector.Core.Analysis;

/// <summary>A single bucket in a gap distribution histogram.</summary>
public record GapBucket(long FromMs, long ToMs, int Count);

/// <summary>Summary of gaps that exceeded the stitch threshold (device outages).</summary>
public record OutageStats(int Count, long TotalMs, long LongestMs, long ThresholdMs);

/// <summary>Result of a gap analysis over one or more timestamp streams.</summary>
public class GapAnalysisResult
{
    public int  TotalGaps { get; init; }
    public long MinGapMs  { get; init; }
    public long MaxGapMs  { get; init; }
    public long P50GapMs  { get; init; }
    public long P95GapMs  { get; init; }
    public long P99GapMs  { get; init; }

    public List<GapBucket>  Buckets      { get; init; } = new();
    public List<GapBucket>? ZoomBuckets  { get; init; }
    public long?             ZoomFromMs   { get; init; }
    public long?             ZoomToMs     { get; init; }
    public long?             ZoomBucketMs { get; init; }
    public OutageStats?      Outages      { get; init; }

    // Tracks which zoom params were auto-computed vs explicitly supplied by the user.
    public bool ZoomFromIsAuto   { get; init; }
    public bool ZoomToIsAuto     { get; init; }
    public bool ZoomBucketIsAuto { get; init; }
}
