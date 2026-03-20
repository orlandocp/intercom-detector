/// <summary>
/// Reads raw CSV files and extracts samples within a given event time window.
/// Also supports looking back before the event start to find the resting baseline.
///
/// Files are opened with FileShare.ReadWrite so the Collector can continue
/// writing to them while the Analyzer is reading.
/// </summary>
public static class RawReader
{
    private static readonly string CapturesFolder =
        @"d:\Orlando\ws\shelly\shelly\intercom-detector\IntercomDetector.Collector\bin\Debug\net10.0\captures";

    // Bolivia is GMT-4, no daylight saving time
    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    // How far back to look for resting baseline before event start (ms)
    private const long LookbackMs = 5000;

    /// <summary>
    /// Returns all raw samples between startTime and endTime (inclusive).
    /// </summary>
    public static List<RawSample> GetSamples(long startTime, long endTime)
    {
        return ReadSamples(startTime, endTime, silent: false);
    }

    /// <summary>
    /// Finds the last resting sample (voltage below restingThreshold) before eventStartTime
    /// by looking back up to LookbackMs in the raw data.
    /// Returns null if no resting sample is found within the lookback window.
    /// </summary>
    public static RawSample? FindLastRestingSample(long eventStartTime, double restingThreshold = 0.3)
    {
        var samples = ReadSamples(eventStartTime - LookbackMs, eventStartTime, silent: true);

        // Walk backwards to find last sample below threshold
        for (int i = samples.Count - 1; i >= 0; i--)
        {
            if (samples[i].Voltage < restingThreshold)
                return samples[i];
        }

        return null;
    }

    private static List<RawSample> ReadSamples(long fromTime, long endTime, bool silent)
    {
        var samples = new List<RawSample>();

        var startBoliviaDate = ToBoliviaDate(fromTime);
        var endBoliviaDate   = ToBoliviaDate(endTime);

        for (var date = startBoliviaDate; date <= endBoliviaDate; date = date.AddDays(1))
        {
            var fileName = $"raw_{date:yyyyMMdd}.csv";
            var filePath = Path.Combine(CapturesFolder, fileName);

            if (!File.Exists(filePath))
            {
                if (!silent) Console.WriteLine($"  ⚠️  Raw file not found: {fileName}");
                continue;
            }

            //if (!silent) Console.WriteLine($"  ✓  Reading {fileName}...");

            // Open with FileShare.ReadWrite so the Collector can keep writing
            using var fs     = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("TimeR") || line.StartsWith("RESTART") ||
                    line.StartsWith("GAP")   || string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(',');
                if (parts.Length < 3) continue;

                if (!long.TryParse(parts[1].Trim(), out long ts)) continue;
                if (!double.TryParse(parts[2].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double voltage)) continue;

                if (ts < fromTime) continue;
                if (ts > endTime)  break;

                samples.Add(new RawSample(ts, voltage));
            }
        }

        return samples;
    }

    private static DateTime ToBoliviaDate(long timestampMs)
    {
        var utc     = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime;
        var bolivia = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Utc, BoliviaZone);
        return bolivia.Date;
    }
}

/// <summary>A single raw voltage sample with its timestamp.</summary>
public record RawSample(long TimestampMs, double Voltage);
