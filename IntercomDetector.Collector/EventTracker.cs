/// <summary>
/// Tracks voltage events from raw samples received in the /raw endpoint.
///
/// Responsibilities:
///   - Detect event start when voltage crosses above 0.5V
///   - Track peaks within the event
///   - Detect event end when voltage drops below 0.3V
///   - Handle inconsistent events (gap > 1000ms, max duration exceeded, out of order samples)
///   - Write completed events to the daily events_log CSV
///   - Persist active event state to active_event.json for crash recovery
/// </summary>
public class EventTracker
{
    // -- THRESHOLDS --
    private const double EventStartThreshold = 0.5;
    private const double EventEndThreshold   = 0.3;
    private const double GapThresholdMs      = 1000;
    private const double MaxEventDurationMs  = 50000;

    // -- PATHS --
    private static readonly string CapturesFolder =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");

    private static readonly string ActiveEventPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "active_event.json");

    // -- TIMEZONE --
    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    // -- STATE --
    private bool   _eventActive             = false;
    private double _eventStartTime          = 0;
    private string _eventStartTimeR         = "";
    private double _maxVoltage              = 0;
    private int    _peakCount               = 0;
    private double _peak1Time               = 0;
    private string _peak1TimeR              = "";
    private double _peak1Voltage            = 0;
    private double _prevVoltage             = 0;
    private bool   _inPeak                  = false;
    private double _firstPeakStartTime      = 0;
    private string _firstPeakStartTimeR     = "";
    private double _lastEventSampleTime     = 0;
    public  double LastTimestampMs          = 0;

    // -- PUBLIC STATE --
    public bool IsEventActive => _eventActive;

    // -- LOCK --
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    public EventTracker()
    {
        Directory.CreateDirectory(CapturesFolder);
    }

    public async Task RecoverAsync()
    {
        if (!File.Exists(ActiveEventPath)) return;

        var json = await File.ReadAllTextAsync(ActiveEventPath);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return;

        try
        {
            var active = System.Text.Json.JsonSerializer.Deserialize<ActiveEvent>(json);
            if (active == null || active.StartTime == 0) return;

            Console.WriteLine($"{active.StartTimeR} ⚡ Recovery          | marking INCONSISTENT_RESTART");
            await WriteEventLogAsync(active.StartTimeR, active.StartTime, "", 0, 0, 0, 0.0, 0, "", 0, "INCONSISTENT_RESTART");
            await ClearActiveEventAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚡ Recovery failed    | {ex.Message}");
            await ClearActiveEventAsync();
        }
    }

    public async Task CloseConnectionResetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!_eventActive) return;
            var nowR = ToBoliviaTime(LastTimestampMs).ToString("HH:mm:ss.fff");
            Console.WriteLine($"{nowR} ⚡ Connection reset  | chunk lost — closing INCONSISTENT_CONNECTION_RESET");
            await CloseInconsistentAsync(LastTimestampMs, nowR, "INCONSISTENT_CONNECTION_RESET");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ProcessSampleAsync(double timestamp, double voltage, string timestampR)
    {
        await _lock.WaitAsync();
        try
        {
            // ── VALIDATE ORDER ───────────────────────────────────────────────
            if (LastTimestampMs > 0 && timestamp <= LastTimestampMs)
            {
                // Convert expected timestamp to human readable for easier debugging
                string expectedR = ToBoliviaTime(LastTimestampMs).ToString("HH:mm:ss.fff");

                if (_eventActive)
                {
                    Console.WriteLine($"{timestampR} 💬 Out of order       | active event — closing INCONSISTENT_ORDER | expected > {expectedR} | got {timestampR}");
                    await CloseInconsistentAsync(timestamp, timestampR, "INCONSISTENT_ORDER");
                }
                else
                {
                    Console.WriteLine($"{timestampR} 💬 Out of order       | resting state — discarded safely | expected > {expectedR} | got {timestampR}");
                }
                return false;
            }

            // ── CHECK GAP ────────────────────────────────────────────────────
            if (_eventActive && LastTimestampMs > 0)
            {
                double gap = timestamp - LastTimestampMs;
                if (gap > GapThresholdMs)
                {
                    Console.WriteLine($"{timestampR} ⚡ Gap detected       | {gap:F0}ms — closing INCONSISTENT_GAP");
                    await CloseInconsistentAsync(timestamp, timestampR, "INCONSISTENT_GAP");
                }
            }

            LastTimestampMs = timestamp;

            // ── EVENT DETECTION ──────────────────────────────────────────────

            if (!_eventActive)
            {
                if (voltage > EventStartThreshold)
                {
                    _eventActive            = true;
                    _eventStartTime         = timestamp;
                    _eventStartTimeR        = timestampR;
                    _maxVoltage             = voltage;
                    _peakCount              = 0;
                    _peak1Time              = 0;
                    _peak1TimeR             = "";
                    _peak1Voltage           = 0;
                    _prevVoltage            = voltage;
                    _inPeak                 = true;
                    _firstPeakStartTime     = timestamp;
                    _firstPeakStartTimeR    = timestampR;
                    _lastEventSampleTime    = timestamp;

                    await SaveActiveEventAsync(_eventStartTime, _eventStartTimeR);

                    Console.WriteLine($"{timestampR} 🌟 Event started      | V: {voltage:F2}");
                }
            }
            else
            {
                _lastEventSampleTime = timestamp;

                if (voltage > _maxVoltage)
                    _maxVoltage = voltage;

                // ── PEAK DETECTION ───────────────────────────────────────────
                if (voltage > _prevVoltage)
                {
                    // Update first peak start time only while first peak not yet confirmed
                    if (_peakCount == 0)
                    {
                        _firstPeakStartTime  = timestamp;
                        _firstPeakStartTimeR = timestampR;
                    }
                    _inPeak = true;
                }

                // Peak confirmed when voltage drops after a rise
                if (_inPeak && voltage < _prevVoltage)
                {
                    _peakCount++;
                    if (_peakCount == 1)
                    {
                        _peak1Time    = _firstPeakStartTime;
                        _peak1TimeR   = _firstPeakStartTimeR;
                        _peak1Voltage = _prevVoltage;
                    }
                    _inPeak = false;
                    Console.WriteLine($"{_firstPeakStartTimeR} 📈 {"Peak #" + _peakCount,-19}| V: {_prevVoltage:F2}");
                }

                _prevVoltage = voltage;

                // ── NATURAL CLOSE ────────────────────────────────────────────
                if (voltage < EventEndThreshold)
                {
                    await CloseCompleteAsync(timestamp, timestampR);
                }
                // ── FORCED CLOSE (max duration) ──────────────────────────────
                else if ((timestamp - _eventStartTime) > MaxEventDurationMs)
                {
                    Console.WriteLine($"{timestampR} ⚡ Max duration       | closing INCONSISTENT_TIMEOUT");
                    await CloseInconsistentAsync(timestamp, timestampR, "INCONSISTENT_TIMEOUT");
                }
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // -- CLOSE HELPERS --------------------------------------------------------

    private async Task CloseCompleteAsync(double endTime, string endTimeR)
    {
        double durMs = endTime - _eventStartTime;
        Console.WriteLine($"{endTimeR} ✅ Event closed       | {durMs:F0}ms | peaks: {_peakCount} | maxV: {_maxVoltage:F2}");

        await WriteEventLogAsync(
            _eventStartTimeR, _eventStartTime,
            endTimeR, endTime, durMs,
            _peakCount, _maxVoltage,
            _peak1Time, _peak1TimeR, _peak1Voltage,
            "COMPLETE");

        await ClearActiveEventAsync();
        ResetState();
    }

    private async Task CloseInconsistentAsync(double endTime, string endTimeR, string status)
    {
        double durMs = endTime - _eventStartTime;
        Console.WriteLine($"{endTimeR} ❌ Event closed      | {status} | {durMs:F0}ms");

        await WriteEventLogAsync(
            _eventStartTimeR, _eventStartTime,
            endTimeR, endTime, durMs,
            _peakCount, _maxVoltage,
            _peak1Time, _peak1TimeR, _peak1Voltage,
            status);

        await ClearActiveEventAsync();
        ResetState();
    }

    private void ResetState()
    {
        _eventActive            = false;
        _eventStartTime         = 0;
        _eventStartTimeR        = "";
        _maxVoltage             = 0;
        _peakCount              = 0;
        _peak1Time              = 0;
        _peak1TimeR             = "";
        _peak1Voltage           = 0;
        _prevVoltage            = 0;
        _inPeak                 = false;
        _firstPeakStartTime     = 0;
        _firstPeakStartTimeR    = "";
        _lastEventSampleTime    = 0;
    }

    // -- FILE HELPERS ---------------------------------------------------------

    private async Task WriteEventLogAsync(
        string timeR, double time,
        string endTimeR, double endTime, double durMs,
        int peaks, double maxV,
        double peak1Time, string peak1TimeR, double peak1V,
        string status)
    {
        var filePath   = GetEventLogPath(time);
        bool fileExists = File.Exists(filePath);

        await using var writer = new StreamWriter(filePath, append: true);

        if (!fileExists)
            await writer.WriteLineAsync("TimeR,DurMs,EndTimeR,Time,EndTime,Peaks,MaxV,Peak1Time,Peak1TimeR,Peak1V,Status,Label");

        await writer.WriteLineAsync(
            $"{timeR},{durMs,5:F0},{endTimeR},{(long)time}," +
            $"{(long)endTime},{peaks},{maxV:F2}," +
            $"{(long)peak1Time},{peak1TimeR},{peak1V:F2}," +
            $"{status},");
    }

    private string GetEventLogPath(double timestampMs)
    {
        string date = ToBoliviaTime(timestampMs).ToString("yyyyMMdd");
        return Path.Combine(CapturesFolder, $"events_log_{date}.csv");
    }

    private async Task SaveActiveEventAsync(double startTime, string startTimeR)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ActiveEvent
        {
            StartTime  = startTime,
            StartTimeR = startTimeR
        });
        await File.WriteAllTextAsync(ActiveEventPath, json);
    }

    private async Task ClearActiveEventAsync()
    {
        await File.WriteAllTextAsync(ActiveEventPath, "{}");
    }

    private static DateTime ToBoliviaTime(double timestampMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds((long)timestampMs).UtcDateTime,
            TimeZoneInfo.Utc, BoliviaZone);

    private class ActiveEvent
    {
        public double StartTime  { get; set; }
        public string StartTimeR { get; set; } = "";
    }
}
