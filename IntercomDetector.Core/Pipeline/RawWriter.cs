namespace IntercomDetector.Core.Pipeline;

/// <summary>
/// R1 — Writes every valid sample to the daily raw_yyyyMMdd.csv file.
/// </summary>
public class RawWriter : ISampleProcessor
{
    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string _capturesFolder;

    public RawWriter(string capturesFolder)
    {
        _capturesFolder = capturesFolder;
        Directory.CreateDirectory(capturesFolder);
    }

    public async Task ProcessSampleAsync(long timestampMs, double voltage, string timeR)
    {
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
        return Path.Combine(_capturesFolder, $"raw_{date}.csv");
    }

    private static DateTime ToBoliviaTime(long timestampMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime,
            TimeZoneInfo.Utc, BoliviaZone);
}
