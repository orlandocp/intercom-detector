using System.Collections.Concurrent;

/// <summary>
/// Handles POST /buffer — receives chunked voltage event data from the Shelly script.
///
/// Each POST contains a META header line followed by sample lines:
///   META,{eventId},{isFinal}
///   {timestamp},{voltage}
///   ...
///
/// Chunks from the same event are aggregated by eventId. When the final chunk
/// arrives (isFinal=1), all chunks are sorted by timestamp, analyzed, and saved
/// to a CSV file under the /events directory.
/// </summary>
public static class BufferEndpoint
{
    // Bolivia is GMT-4, no daylight saving time
    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    // Counter for display purposes — resets on server restart
    private static int _eventCounter = 0;

    // Global voltage state — tracks the last seen voltage across events
    // for computing delta-voltage (dV) between consecutive samples
    private static double _previousVoltage = 0;
    private static double _previousTimestampMs = 0;

    public static void Register(WebApplication app, ConcurrentDictionary<string, ConcurrentBag<string>> pendingEvents)
    {
        app.MapPost("/buffer", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length == 0) return;

            // -- PARSE META HEADER --
            var meta = lines[0].Split(',');
            if (meta.Length < 3 || meta[0].Trim() != "META")
            {
                Console.WriteLine("⚠️ Invalid chunk format");
                return;
            }

            string eventId = meta[1].Trim();
            bool isFinal = meta[2].Trim() == "1";
            var dataLines = lines.Skip(1).ToArray();

            // -- ACCUMULATE CHUNK --
            // Multiple chunks for the same event may arrive concurrently.
            // ConcurrentBag ensures thread-safe accumulation.
            var eventLines = pendingEvents.GetOrAdd(eventId, _ => new ConcurrentBag<string>());
            foreach (var line in dataLines)
                eventLines.Add(line);

            Console.WriteLine($"📦 Chunk received | EventId: {eventId} | lines: {dataLines.Length} | total: {eventLines.Count} | final: {isFinal}");

            // -- WAIT FOR FINAL CHUNK --
            if (!isFinal) return;

            // -- FINALIZE EVENT --
            var allLines = eventLines.ToArray();
            pendingEvents.TryRemove(eventId, out _);

            // Sort by timestamp — chunks may have arrived out of order
            allLines = allLines
                .Where(l => l.Contains(','))
                .OrderBy(l => {
                    var parts = l.Split(',');
                    return double.TryParse(parts[0].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double ts) ? ts : 0;
                })
                .ToArray();

            _eventCounter++;
            string evId = _eventCounter.ToString("D4");

            List<double> drops = new();
            int upCount = 0, downCount = 0;
            double startVoltage = _previousVoltage;
            double firstTimestampMs = 0;

            Console.WriteLine($"\n🧪 EVENT #{evId} ({allLines.Length} samples):");

            foreach (var line in allLines)
            {
                if (!TryParseSample(line, out double tsMs, out double voltage)) continue;
                if (firstTimestampMs == 0) firstTimestampMs = tsMs;

                double duration = _previousTimestampMs > 0 ? tsMs - _previousTimestampMs : 0;
                string timeStr = ToBoliviaTime(tsMs).ToString("HH:mm:ss.fff");

                if (duration > 0)
                {
                    double dv = _previousVoltage - voltage;
                    DisplaySample(timeStr, voltage, duration, dv, ref drops, ref upCount, ref downCount);
                }

                _previousVoltage = voltage;
                _previousTimestampMs = tsMs;
            }

            Console.WriteLine($"📊 Summary: {downCount} drops | {upCount} rises | V start: {startVoltage:F3} | V end: {_previousVoltage:F3}");
            await SaveEventAsync(allLines, firstTimestampMs);
            Console.WriteLine("─────────────────────────────────────");
        });
    }

    // -- HELPERS --

    /// <summary>
    /// Converts a Unix timestamp in milliseconds to Bolivia local time.
    /// </summary>
    private static DateTime ToBoliviaTime(double timestampMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds((long)timestampMs).UtcDateTime,
            TimeZoneInfo.Utc, BoliviaZone);

    /// <summary>
    /// Parses a sample line in the format "timestamp,voltage".
    /// Returns false if the line cannot be parsed.
    /// </summary>
    private static bool TryParseSample(string line, out double timestampMs, out double voltage)
    {
        timestampMs = 0; voltage = 0;
        var parts = line.Trim().Split(',');
        if (parts.Length < 2) return false;
        if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out timestampMs))
        {
            Console.WriteLine($"⚠️  Failed to parse timestamp: '{parts[0]}'");
            return false;
        }
        if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out voltage))
        {
            Console.WriteLine($"⚠️  Failed to parse voltage: '{parts[1]}'");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Displays a single sample in the console with color-coded direction indicator.
    /// dV > 0 means voltage is dropping (previous > current).
    /// dV < 0 means voltage is rising (previous < current).
    /// </summary>
    private static void DisplaySample(string timeStr, double voltage, double duration,
                                      double dv, ref List<double> drops, ref int upCount, ref int downCount)
    {
        double velocity = dv / duration;
        if (dv > 0)
        {
            drops.Add(velocity); downCount++;
            double average = drops.Average();
            Console.WriteLine(Red($"📉 DROPPING | {timeStr} | V: {voltage:F3} | dur: {duration,8:F2}ms | Vel: {velocity:F5} V/ms | Avg: {average:F5} V/ms"));
        }
        else if (dv < 0)
        {
            drops.Clear(); upCount++;
            Console.WriteLine(Blue($"📈 RISING   | {timeStr} | V: {voltage:F3} | dur: {duration,8:F2}ms"));
        }
        else
        {
            Console.WriteLine($"📊 STABLE   | {timeStr} | V: {voltage:F3} | dur: {duration,8:F2}ms");
        }
    }

    /// <summary>
    /// Saves all samples of a completed event to a CSV file.
    /// File is named by Bolivia local time of the first sample: yyyyMMdd_HHmmss.csv
    /// Saved under the /events directory next to the executable.
    /// </summary>
    private static async Task SaveEventAsync(string[] lines, double firstTimestampMs)
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        DateTime eventTime = ToBoliviaTime(firstTimestampMs);
        string fileName = $"{eventTime:yyyyMMdd_HHmmss}.csv";
        string path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, "Timestamp,Voltage\n");
        var content = string.Join("\n", lines.Select(l => l.Trim()));
        await File.AppendAllTextAsync(path, content + "\n");
        Console.WriteLine($"💾 Saved: {fileName}");
    }

    private static string Red(string text) => $"\x1b[38;2;255;153;153m{text}\x1b[0m";
    private static string Blue(string text) => $"\x1b[38;2;153;204;255m{text}\x1b[0m";
}
