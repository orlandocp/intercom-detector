namespace IntercomDetector.Core.Pipeline;

/// <summary>
/// R3 — Writes every sample with voltage below restThreshold to the daily rest_yyyyMMdd.csv file.
/// Keeps the StreamWriter open across samples to avoid per-call file open/close overhead.
/// </summary>
public class RestWriter : ISampleProcessor, ISummaryProvider, IAsyncDisposable
{
    private readonly double _restThreshold;

    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _capturesFolder;

    private StreamWriter? _writer;
    private string? _currentDate;

    // Tracks which dates already received a #config line in this session
    private readonly HashSet<string> _configWrittenDates = new();
    private int _samplesWritten = 0;

    public RestWriter(string capturesFolder, double restThreshold = 0.3)
    {
        _restThreshold  = restThreshold;
        _capturesFolder = capturesFolder;
        Directory.CreateDirectory(capturesFolder);
    }

    public async Task ProcessSampleAsync(long timestampMs, double voltage, string timeR)
    {
        if (voltage >= _restThreshold) return;

        await _lock.WaitAsync();
        try
        {
            string date = ToBoliviaTime(timestampMs).ToString("yyyyMMdd");
            await EnsureWriterAsync(date);
            await _writer!.WriteLineAsync($"{timeR},{timestampMs},{voltage:F2}");
            await _writer.FlushAsync();
            _samplesWritten++;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureWriterAsync(string date)
    {
        if (_currentDate == date) return;

        if (_writer != null)
            await _writer.DisposeAsync();

        string filePath = Path.Combine(_capturesFolder, $"rest_{date}.csv");
        bool fileExists = File.Exists(filePath);

        _writer = new StreamWriter(filePath, append: true);
        _currentDate = date;

        if (!fileExists)
            await _writer.WriteLineAsync("TimeR,Time,Voltage");

        if (!_configWrittenDates.Contains(date))
        {
            await _writer.WriteLineAsync($"#config: voltage<{_restThreshold}");
            _configWrittenDates.Add(date);
        }
    }

    public void PrintSummary()
    {
        var nowR = ToBoliviaTime(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString("HH:mm:ss.fff");
        Console.WriteLine($"{nowR} 📊 Summary            | output : {_capturesFolder}");
        Console.WriteLine($"{nowR} 📊 Summary            | config : voltage<{_restThreshold}");
        Console.WriteLine($"{nowR} 📊 Summary            | samples: {_samplesWritten}");
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_writer != null)
                await _writer.DisposeAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static DateTime ToBoliviaTime(long timestampMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime,
            TimeZoneInfo.Utc, BoliviaZone);
}
