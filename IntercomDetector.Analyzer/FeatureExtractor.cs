/// <summary>
/// Extracts signal processing features from raw samples for each labeled event.
///
/// Features grouped by availability during event processing:
///
/// Available at peak confirmation:
///   RiseTimeMs      — time from last resting sample to first peak (ms)
///   RiseRate        — voltage rise per ms from resting to peak (V/ms)
///
/// Available after first drop past 500ms window:
///   FirstDropRate   — voltage drop per ms measured over the first 500ms+ after peak (V/ms)
///
/// Available at end of event:
///   DecayRateEarly  — V/ms in first third of event duration
///   DecayRateMid    — V/ms in second third of event duration
///   DecayRateLate   — V/ms in third third of event duration
///   DecayRate       — V/ms over full event decay (peak to end)
///   VoltageAt25pct  — voltage at 25% of event duration
///   VoltageAt50pct  — voltage at 50% of event duration
///   VoltageAt75pct  — voltage at 75% of event duration
///   AreaUnderCurve  — trapezoidal approximation of voltage over time (V*s)
/// </summary>
public static class FeatureExtractor
{
    // Voltage threshold below which the signal is considered at rest
    private const double RestingThreshold = 0.3;

    // Minimum window size in ms after peak before computing FirstDropRate
    private const double FirstDropWindowMs = 500;

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

            result.Add(Compute(e, samples));
        }

        return result;
    }

    private static EventFeatures Compute(EventEntry e, List<RawSample> samples)
    {
        // -- RESTING BASELINE (shared by OnsetRate and RiseRate) --
        RawSample? restingSample = RawReader.FindLastRestingSample(e.Time, RestingThreshold);

        // -- ONSET RATE --
        // Earliest available feature: V/ms from last resting sample to first event sample
        double onsetRate = 0;
        if (restingSample != null && samples.Count > 0)
        {
            double dt = samples[0].TimestampMs - restingSample.TimestampMs;
            if (dt > 0)
                onsetRate = (samples[0].Voltage - restingSample.Voltage) / dt;
        }

        // -- RISE TIME & RISE RATE --
        // Time and rate from last resting sample to confirmed peak
        double riseTimeMs = 0;
        double riseRate   = 0;
        if (e.Peak1Time > 0 && restingSample != null)
        {
            riseTimeMs = e.Peak1Time - restingSample.TimestampMs;
            riseRate   = riseTimeMs > 0 ? (e.Peak1V - restingSample.Voltage) / riseTimeMs : 0;
        }

        // -- FIRST DROP RATE --
        // Read sample by sample after the confirmed peak.
        // Phase A: wait for the first voltage drop below peak voltage.
        //   If elapsed >= 500ms at drop → stop and compute rate.
        //   If elapsed < 500ms at drop → continue to Phase B.
        // Phase B: keep reading until elapsed >= 500ms → stop and compute rate.
        // Rate = (peakVoltage - lastSampleVoltage) / (lastSampleTimestamp - peakTimestamp)
        double firstDropRate = 0;
        if (e.Peak1Time > 0)
        {
            // Find peak sample index
            int peakIdx = samples.FindIndex(s => s.TimestampMs >= e.Peak1Time);
            if (peakIdx >= 0)
            {
                bool firstDropFound = false;
                RawSample? cutoffSample = null;

                for (int i = peakIdx + 1; i < samples.Count; i++)
                {
                    var s = samples[i];
                    double elapsedMs = s.TimestampMs - e.Peak1Time;

                    if (!firstDropFound && s.Voltage < e.Peak1V)
                    {
                        // Phase A: first drop detected
                        firstDropFound = true;

                        if (elapsedMs >= FirstDropWindowMs)
                        {
                            // Already past 500ms — use this sample
                            cutoffSample = s;
                            break;
                        }
                        // Not yet 500ms — continue to Phase B
                    }

                    if (firstDropFound && elapsedMs >= FirstDropWindowMs)
                    {
                        // Phase B: reached 500ms after first drop — use this sample
                        cutoffSample = s;
                        break;
                    }
                }

                // Compute rate using actual elapsed time for precision
                if (cutoffSample != null)
                {
                    double elapsedTotal = cutoffSample.TimestampMs - e.Peak1Time;
                    if (elapsedTotal > 0)
                        firstDropRate = (e.Peak1V - cutoffSample.Voltage) / elapsedTotal;
                }
            }
        }

        // -- DECAY RATE BY THIRDS --
        // Divide event duration into 3 equal windows and compute V/ms in each
        double decayRateEarly = GetDecayRateInWindow(samples, e.Time, e.EndTime, 0.00, 0.33);
        double decayRateMid   = GetDecayRateInWindow(samples, e.Time, e.EndTime, 0.33, 0.66);
        double decayRateLate  = GetDecayRateInWindow(samples, e.Time, e.EndTime, 0.66, 1.00);

        // -- FULL DECAY RATE --
        double decayDurationMs = e.EndTime - e.Peak1Time;
        double decayRate = decayDurationMs > 0
            ? (e.Peak1V - samples[^1].Voltage) / decayDurationMs
            : 0;

        // -- VOLTAGE AT 25%, 50%, 75% OF EVENT DURATION --
        double voltageAt25 = GetVoltageAtPercent(samples, e.Time, e.EndTime, 0.25);
        double voltageAt50 = GetVoltageAtPercent(samples, e.Time, e.EndTime, 0.50);
        double voltageAt75 = GetVoltageAtPercent(samples, e.Time, e.EndTime, 0.75);

        // -- AREA UNDER CURVE (first 1000ms) --
        // Energy of the signal in the first second — available at e.Time + 1000ms
        double areaEarly  = 0;
        long   earlyLimit = e.Time + 1000;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].TimestampMs > earlyLimit) break;
            double dt   = samples[i].TimestampMs - samples[i - 1].TimestampMs;
            double avgV = (samples[i].Voltage + samples[i - 1].Voltage) / 2.0;
            areaEarly += avgV * dt;
        }
        areaEarly /= 1000.0;

        // -- AREA UNDER CURVE --
        // Trapezoidal approximation normalized to V*seconds
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
            Event                = e,
            OnsetRate            = onsetRate,
            RiseTimeMs           = riseTimeMs,
            RiseRate             = riseRate,
            FirstDropRate        = firstDropRate,
            AreaUnderCurveEarly  = areaEarly,
            DecayRateEarly       = decayRateEarly,
            DecayRateMid         = decayRateMid,
            DecayRateLate        = decayRateLate,
            DecayRate            = decayRate,
            VoltageAt25pct       = voltageAt25,
            VoltageAt50pct       = voltageAt50,
            VoltageAt75pct       = voltageAt75,
            AreaUnderCurve       = area,
            SampleCount          = samples.Count
        };
    }

    /// <summary>
    /// Computes the average voltage drop per ms (V/ms) within a time window
    /// defined as a percentage range of the total event duration.
    /// </summary>
    private static double GetDecayRateInWindow(
        List<RawSample> samples, long startTime, long endTime,
        double fromPct, double toPct)
    {
        long duration  = endTime - startTime;
        long windowStart = startTime + (long)(duration * fromPct);
        long windowEnd   = startTime + (long)(duration * toPct);

        var windowSamples = samples
            .Where(s => s.TimestampMs >= windowStart && s.TimestampMs <= windowEnd)
            .ToList();

        if (windowSamples.Count < 2) return 0;

        double dV = windowSamples[0].Voltage - windowSamples[^1].Voltage;
        double dt = windowSamples[^1].TimestampMs - windowSamples[0].TimestampMs;
        return dt > 0 ? dV / dt : 0;
    }

    /// <summary>
    /// Returns the voltage of the sample closest to the given percentage
    /// of the event duration.
    /// </summary>
    private static double GetVoltageAtPercent(
        List<RawSample> samples, long startTime, long endTime, double percent)
    {
        long targetTs = startTime + (long)((endTime - startTime) * percent);
        var  closest  = samples.MinBy(s => Math.Abs(s.TimestampMs - targetTs));
        return closest?.Voltage ?? 0;
    }
}

/// <summary>Computed signal features for a single event.</summary>
public class EventFeatures
{
    public EventEntry Event                { get; init; } = null!;
    public double     OnsetRate            { get; init; }
    public double     RiseTimeMs           { get; init; }
    public double     RiseRate             { get; init; }
    public double     FirstDropRate        { get; init; }
    public double     AreaUnderCurveEarly  { get; init; }
    public double     DecayRateEarly       { get; init; }
    public double     DecayRateMid   { get; init; }
    public double     DecayRateLate  { get; init; }
    public double     DecayRate      { get; init; }
    public double     VoltageAt25pct { get; init; }
    public double     VoltageAt50pct { get; init; }
    public double     VoltageAt75pct { get; init; }
    public double     AreaUnderCurve { get; init; }
    public int        SampleCount    { get; init; }
}
