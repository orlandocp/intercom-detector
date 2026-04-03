namespace IntercomDetector.Core.Analysis;

/// <summary>Result of the event file gap analysis.</summary>
public class EventAnalysisResult
{
    public int    FileCount     { get; init; }
    public string DateFrom      { get; init; } = "";
    public string DateTo        { get; init; } = "";
    public int    TotalSamples  { get; init; }
    public int    EventsLoaded  { get; init; }

    public GapAnalysisResult        Gaps               { get; init; } = new();
    public List<BucketCorrelation>? BucketCorrelations { get; init; }
    public List<UnknownEvent>       UnknownEvents      { get; init; } = new();
}
