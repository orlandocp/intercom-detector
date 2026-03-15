/// <summary>
/// Reads all events_log_yyyyMMdd.csv files from the captures folder
/// and returns parsed event entries.
///
/// Only returns events with Status=COMPLETE and a non-empty Label.
/// Events with no label or INCONSISTENT status are ignored.
/// </summary>
public static class EventLogReader
{
    private static readonly string CapturesFolder =
        @"d:\Orlando\ws\shelly\shelly\intercom-detector\IntercomDetector.Collector\bin\Debug\net10.0\captures";

    /// <summary>
    /// Reads all events_log CSVs and returns labeled complete events.
    /// </summary>
    public static List<EventEntry> ReadAll()
    {
        var events = new List<EventEntry>();

        var files = Directory.GetFiles(CapturesFolder, "events_log_*.csv")
                             .OrderBy(f => f)
                             .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("  ⚠️ No events_log files found in captures folder");
            return events;
        }

        foreach (var file in files)
        {
            Console.WriteLine($"  Reading {Path.GetFileName(file)}...");
            var fileEvents = ReadFile(file);
            events.AddRange(fileEvents);
        }

        return events;
    }

    private static List<EventEntry> ReadFile(string path)
    {
        var events   = new List<EventEntry>();
        var lines    = File.ReadAllLines(path);
        int skipped  = 0;

        foreach (var line in lines.Skip(1)) // skip header
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var entry = TryParse(line);
            if (entry == null) { skipped++; continue; }

            events.Add(entry);
        }

        if (skipped > 0)
            Console.WriteLine($"    ⚠️ {skipped} lines could not be parsed");

        return events;
    }

    private static EventEntry? TryParse(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 12) return null;

        // Format: TimeR,DurMs,EndTimeR,Time,EndTime,Peaks,MaxV,Peak1Time,Peak1TimeR,Peak1V,Status,Label
        try
        {
            string timeR     = parts[0].Trim();
            string endTimeR  = parts[2].Trim();
            string status    = parts[10].Trim();
            string label     = parts[11].Trim().ToLowerInvariant();

            if (status != "COMPLETE") return null;
            if (string.IsNullOrEmpty(label)) return null;
            if (label != "r" && label != "v" && label != "c") return null;

            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double durMs)) return null;
            if (!long.TryParse(parts[3].Trim(), out long time)) return null;
            if (!long.TryParse(parts[4].Trim(), out long endTime)) return null;
            if (!int.TryParse(parts[5].Trim(), out int peaks)) return null;
            if (!double.TryParse(parts[6].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double maxV)) return null;
            if (!long.TryParse(parts[7].Trim(), out long peak1Time)) return null;
            if (!double.TryParse(parts[9].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double peak1V)) return null;

            return new EventEntry
            {
                TimeR      = timeR,
                Time       = time,
                EndTimeR   = endTimeR,
                EndTime    = endTime,
                DurMs      = durMs,
                Peaks      = peaks,
                MaxV       = maxV,
                Peak1Time  = peak1Time,
                Peak1TimeR = parts[8].Trim(),
                Peak1V     = peak1V,
                Status     = status,
                Label      = label
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Represents a single labeled event from the events_log CSV.
/// </summary>
public class EventEntry
{
    public string TimeR      { get; init; } = "";
    public long   Time       { get; init; }
    public string EndTimeR   { get; init; } = "";
    public long   EndTime    { get; init; }
    public double DurMs      { get; init; }
    public int    Peaks      { get; init; }
    public double MaxV       { get; init; }
    public long   Peak1Time  { get; init; }
    public string Peak1TimeR { get; init; } = "";
    public double Peak1V     { get; init; }
    public string Status     { get; init; } = "";
    public string Label      { get; init; } = "";
}
