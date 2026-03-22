namespace IntercomDetector.Core.Pipeline;

/// <summary>
/// R3 — Writes every sample with voltage &lt; 0.3V to the daily rest_yyyyMMdd.csv file.
/// </summary>
public class RestWriter : ISampleProcessor
{
    private const double RestThreshold = 0.3;

    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _capturesFolder;

    public RestWriter(string capturesFolder)
    {
        _capturesFolder = capturesFolder;
        Directory.CreateDirectory(capturesFolder);
    }

    public async Task ProcessSampleAsync(long timestampMs, double voltage, string timeR)
    {
        if (voltage >= RestThreshold) return;

        await _lock.WaitAsync();
        try
        {
            var filePath  = GetDailyFilePath(timestampMs);
            bool fileExists = File.Exists(filePath);

            await using var writer = new StreamWriter(filePath, append: true);

            if (!fileExists)
                await writer.WriteLineAsync("TimeR,Time,Voltage");

            await writer.WriteLineAsync($"{timeR},{timestampMs},{voltage:F2}");
        }
        finally
        {
            _lock.Release();
        }
    }

    private string GetDailyFilePath(long timestampMs)
    {
        string date = ToBoliviaTime(timestampMs).ToString("yyyyMMdd");
        return Path.Combine(_capturesFolder, $"rest_{date}.csv");
    }

    private static DateTime ToBoliviaTime(long timestampMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime,
            TimeZoneInfo.Utc, BoliviaZone);
}
