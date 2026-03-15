/// <summary>
/// Extracts signal processing features from raw samples for each labeled event.
///
/// Features computed:
///   RiseTimeMs      — time from last resting sample before event to first peak (ms)
///   DecayRate       — average voltage drop per ms from peak to end of event (V/ms)
///   AreaUnderCurve  — trapezoidal approximation of voltage over time (V*s)
/// </summary>
public static class FeatureExtractor
{
    // Voltage threshold below which the signal is considered at rest
    private const double RestingThreshold = 0.3;

    /// <summary>
    /// Extracts features for all events. Events with no raw samples are skipped.
    /// </summary>
    public static List<EventFeatures> Extract(List<EventEntry> events)
    {
        var result = new List<EventFeatures>();

        foreach (var e in events)
        {
            var samples = RawReader.GetSamples(e.Time, e.EndTime);

            if (samples.Count == 0)
            {
                Console.WriteLine($"  ⚠️  No raw samples found for event {e.TimeR} — skipping");
                continue;
            }

            var features = Compute(e, samples);
            result.Add(features);
        }

        return result;
    }

    private static EventFeatures Compute(EventEntry e, List<RawSample> samples)
    {
        // -- RISE TIME --
        // Time from last resting sample before event start to first peak.
        // Walk back in raw to find last sample below RestingThreshold.
        double riseTimeMs = 0;
        if (e.Peak1Time > 0)
        {
            long lastRestingTs = RawReader.FindLastRestingTimestamp(e.Time, RestingThreshold);
            if (lastRestingTs > 0)
                riseTimeMs = e.Peak1Time - lastRestingTs;
        }

        // -- DECAY RATE --
        // Average voltage drop per ms from peak to end of event.
        double decayDurationMs = e.EndTime - e.Peak1Time;
        double decayRate = decayDurationMs > 0
            ? (e.Peak1V - samples[^1].Voltage) / decayDurationMs
            : 0;

        // -- AREA UNDER CURVE --
        // Trapezoidal approximation: sum of (v1 + v2) / 2 * dt, normalized to V*seconds
        double area = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            double dt   = samples[i].TimestampMs - samples[i - 1].TimestampMs;
            double avgV = (samples[i].Voltage + samples[i - 1].Voltage) / 2.0;
            area += avgV * dt;
        }
        area /= 1000.0;

        return new EventFeatures
        {
            Event          = e,
            RiseTimeMs     = riseTimeMs,
            DecayRate      = decayRate,
            AreaUnderCurve = area,
            SampleCount    = samples.Count
        };
    }
}

/// <summary>Computed signal features for a single event.</summary>
public class EventFeatures
{
    public EventEntry Event          { get; init; } = null!;
    public double     RiseTimeMs     { get; init; }
    public double     DecayRate      { get; init; }
    public double     AreaUnderCurve { get; init; }
    public int        SampleCount    { get; init; }
}
