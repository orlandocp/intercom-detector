/// <summary>
/// Handles POST /raw — receives continuous raw voltage samples from the Shelly
/// raw-capture script.
///
/// Responsibilities:
///   - Validate sample order (discard out-of-order samples)
///   - Write valid samples to the daily raw CSV
///   - Delegate event detection to EventTracker
/// </summary>
public static class RawEndpoint
{
    // Bolivia is GMT-4, no daylight saving time
    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    private static readonly string CapturesFolder =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");

    // Single EventTracker instance shared across all requests
    private static readonly EventTracker Tracker = new EventTracker();

    // Lock to prevent concurrent writes to the raw CSV
    private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Called once at server startup to recover any incomplete event from a crash.
    /// </summary>
    public static async Task InitAsync()
    {
        await Tracker.RecoverAsync();
    }

    public static void Register(WebApplication app)
    {
        app.MapPost("/raw", async (HttpContext context) =>
        {
            // -- READ BODY --
            using var reader = new StreamReader(context.Request.Body);
            string body;
            try
            {
                body = await reader.ReadToEndAsync();
            }
            catch (Exception ex) when (ex is System.IO.IOException ||
                                       ex is Microsoft.AspNetCore.Connections.ConnectionResetException)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} ⚡ Connection reset  | chunk lost");
                if (Tracker.IsEventActive)
                    await Tracker.CloseConnectionResetAsync();
                return;
            }

            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0)
                            .ToArray();

            if (lines.Length == 0) return;

            await _fileLock.WaitAsync();
            try
            {
                int    validCount        = 0;
                int    discardedCount    = 0;
                int    markerCount       = 0;
                string firstSampleTimeR  = "";
                string lastSampleTimeR   = "";
                string firstRawTimeR     = "";  // first parseable timestamp regardless of validity

                // -- PRE-SCAN: find first parseable timestamp for incoming log --
                foreach (var line in lines)
                {
                    if (line.StartsWith("RESTART,") || line.StartsWith("GAP,")) continue;
                    if (!TryParseSample(line, out double ts, out _)) continue;
                    firstRawTimeR = ToBoliviaTime(ts).ToString("HH:mm:ss.fff");
                    break;
                }

                foreach (var line in lines)
                {
                    // -- SKIP MARKERS --
                    if (line.StartsWith("RESTART,") || line.StartsWith("GAP,"))
                    {
                        markerCount++;
                        continue;
                    }

                    // -- PARSE SAMPLE --
                    if (!TryParseSample(line, out double timestamp, out double voltage)) continue;

                    string timestampR = ToBoliviaTime(timestamp).ToString("HH:mm:ss.fff");

                    // -- TRACK FIRST SAMPLE FOR PROCESSED LOG --
                    if (validCount == 0 && discardedCount == 0)
                        firstSampleTimeR = timestampR;

                    // -- PROCESS THROUGH EVENT TRACKER --
                    bool isValid = await Tracker.ProcessSampleAsync(timestamp, voltage, timestampR);
                    if (!isValid)
                    {
                        discardedCount++;
                        continue;
                    }

                    validCount++;
                    lastSampleTimeR = timestampR;

                    // -- WRITE TO RAW CSV --
                    var filePath   = GetDailyFilePath(timestamp);
                    bool fileExists = File.Exists(filePath);

                    await using var writer = new StreamWriter(filePath, append: true);

                    if (!fileExists)
                        await writer.WriteLineAsync("TimeR,Time,Voltage");

                    await writer.WriteLineAsync($"{timestampR},{(long)timestamp},{voltage:F2}");
                }

                // -- LOG PROCESSED CHUNK --
                string discardedInfo = discardedCount > 0 ? $" | Discarded: {discardedCount}" : "";
                Console.WriteLine($"{firstSampleTimeR} 📡 Raw chunk          | Samples: {validCount} | {firstSampleTimeR}-{lastSampleTimeR}{discardedInfo}");
            }
            finally
            {
                _fileLock.Release();
            }
        });
    }

    // -- HELPERS --

    private static string GetDailyFilePath(double timestampMs)
    {
        Directory.CreateDirectory(CapturesFolder);
        string date = ToBoliviaTime(timestampMs).ToString("yyyyMMdd");
        return Path.Combine(CapturesFolder, $"raw_{date}.csv");
    }

    private static bool TryParseSample(string line, out double timestampMs, out double voltage)
    {
        timestampMs = 0; voltage = 0;
        var parts = line.Split(',');
        if (parts.Length < 2) return false;
        if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out timestampMs)) return false;
        if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out voltage)) return false;
        return true;
    }

    private static DateTime ToBoliviaTime(double timestampMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds((long)timestampMs).UtcDateTime,
            TimeZoneInfo.Utc, BoliviaZone);
}
