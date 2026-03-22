namespace IntercomDetector.Core.Pipeline;

/// <summary>
/// R2-ext — Writes the samples of a single completed event to
/// captures/events/event_yyyyMMdd_HHmmssSSS.csv.
/// </summary>
public class EventFileWriter
{
    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    private readonly string _eventsFolder;

    public EventFileWriter(string capturesFolder)
    {
        _eventsFolder = Path.Combine(capturesFolder, "events");
        Directory.CreateDirectory(_eventsFolder);
    }

    /// <summary>
    /// Writes the event samples to a per-event CSV file.
    /// </summary>
    public async Task WriteEventAsync(List<(long TimestampMs, double Voltage, string TimeR)> samples, long startTimeMs)
    {
        var startBolivia = ToBoliviaTime(startTimeMs);
        string fileName  = $"event_{startBolivia:yyyyMMdd_HHmmss}{startBolivia.Millisecond:D3}.csv";
        string filePath  = Path.Combine(_eventsFolder, fileName);

        await using var writer = new StreamWriter(filePath, append: false);
        await writer.WriteLineAsync("TimeR,Time,Voltage");

        foreach (var (ts, v, timeR) in samples)
            await writer.WriteLineAsync($"{timeR},{ts},{v:F2}");
    }

    private static DateTime ToBoliviaTime(long timestampMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime,
            TimeZoneInfo.Utc, BoliviaZone);
}
