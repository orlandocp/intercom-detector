using IntercomDetector.Core;
using IntercomDetector.Core.Pipeline;

/// <summary>
/// Handles POST /raw — receives continuous raw voltage samples from the Shelly
/// raw-capture script and delegates to the SamplePipeline.
/// </summary>
public static class RawEndpoint
{
    // Bolivia is GMT-4, no daylight saving time
    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    private static SamplePipeline _pipeline = null!;
    private static EventProcessor _eventProcessor = null!;

    // Lock to serialize chunks through the pipeline
    private static readonly SemaphoreSlim _chunkLock = new(1, 1);

    // Last accepted timestamp for global order validation
    private static long _lastTimestampMs = 0;

    public static async Task InitAsync(SamplePipeline pipeline, EventProcessor eventProcessor)
    {
        _pipeline       = pipeline;
        _eventProcessor = eventProcessor;
        await eventProcessor.RecoverAsync();
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
                if (_eventProcessor.IsEventActive)
                    await _eventProcessor.CloseConnectionResetAsync();
                return;
            }

            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0)
                            .ToArray();

            if (lines.Length == 0) return;

            await _chunkLock.WaitAsync();
            try
            {
                int    validCount       = 0;
                int    discardedCount   = 0;
                string firstSampleTimeR = "";
                string lastSampleTimeR  = "";

                foreach (var line in lines)
                {
                    if (!TryParseSample(line, out long timestampMs, out double voltage)) continue;

                    string timeR = ToBoliviaTime(timestampMs).ToString("HH:mm:ss.fff");

                    if (validCount == 0 && discardedCount == 0)
                        firstSampleTimeR = timeR;

                    // Order check (pipeline processors do their own checks; we track here
                    // to maintain the global last-seen timestamp before routing)
                    if (_lastTimestampMs > 0 && timestampMs <= _lastTimestampMs)
                    {
                        discardedCount++;
                        continue;
                    }

                    _lastTimestampMs = timestampMs;
                    validCount++;
                    lastSampleTimeR = timeR;

                    await _pipeline.ProcessAsync(timestampMs, voltage, timeR);
                }

                string discardedInfo = discardedCount > 0 ? $" | Discarded: {discardedCount}" : "";
                Console.WriteLine($"{firstSampleTimeR} 📡 Raw chunk          | {lines.Length} lines | {validCount} valid | {firstSampleTimeR}-{lastSampleTimeR}{discardedInfo}");
            }
            finally
            {
                _chunkLock.Release();
            }
        });
    }

    // -- HELPERS --

    private static bool TryParseSample(string line, out long timestampMs, out double voltage)
    {
        timestampMs = 0; voltage = 0;
        var parts = line.Split(',');
        if (parts.Length < 2) return false;
        if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double tsDouble)) return false;
        if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out voltage)) return false;
        timestampMs = (long)tsDouble;
        return true;
    }

    private static DateTime ToBoliviaTime(long timestampMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime,
            TimeZoneInfo.Utc, BoliviaZone);
}
