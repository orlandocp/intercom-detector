namespace IntercomDetector.Core.IO;

/// <summary>
/// Low-level reader for CSV sample files (raw_*, rest_*, event_*).
/// All files share the same format: TimeR,Time,Voltage
/// </summary>
public static class SampleFileReader
{
    /// <summary>Reads timestamps (ms) from a sample CSV file.</summary>
    public static IEnumerable<long> ReadTimestamps(string path)
    {
        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("TimeR"))        continue;
            if (line.StartsWith("#"))            continue;

            var parts = line.Split(',');
            if (parts.Length < 2) continue;

            if (long.TryParse(parts[1].Trim(), out long ts))
                yield return ts;
        }
    }

    /// <summary>Reads (timestamp ms, voltage) pairs from a sample CSV file.</summary>
    public static IEnumerable<(long Ts, double V)> ReadSamples(string path)
    {
        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("TimeR"))        continue;
            if (line.StartsWith("#"))            continue;

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
