namespace IntercomDetector.Core.Analysis;

/// <summary>A simplified event entry used for gap correlation in raw analysis.</summary>
public record EventEntry(long TimeMs, long EndTimeMs, string Label);

/// <summary>An event with an unrecognized label found in events_*.csv files.</summary>
public record UnknownEvent(string FilePath, string TimeR, string EndTimeR, long TimeMs, long EndTimeMs, string Label)
{
    /// <summary>Number of raw file gaps overlapping this event's time window. Null if correlation did not run.</summary>
    public int? RawGapCount { get; init; }
}

/// <summary>Per-bucket breakdown of how gaps correlate with labeled events.</summary>
public record BucketCorrelation(int R, int V, int C, int Unknown, int Outside)
{
    public int InEvents => R + V + C + Unknown;
    public bool HasAny  => InEvents > 0 || Outside > 0;
}

/// <summary>Result of the raw file gap analysis.</summary>
public class RawAnalysisResult
{
    public List<string>  SourceFiles           { get; init; } = new();
    public string        DateFrom              { get; init; } = "";
    public string        DateTo                { get; init; } = "";
    public int           EventsLoaded          { get; init; }

    /// <summary>Gap threshold used to split raw streams (ceil((maxEventGap+50)/50)×50).</summary>
    public long          StitchThresholdMs     { get; init; }
    /// <summary>True when threshold was derived from event files; false when fallback was used.</summary>
    public bool          StitchThresholdFromEvents { get; init; }

    public GapAnalysisResult        Gaps               { get; init; } = new();
    public List<BucketCorrelation>? BucketCorrelations { get; init; }
    public List<UnknownEvent>       UnknownEvents      { get; init; } = new();
}
