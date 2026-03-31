namespace IntercomDetector.Core.Pipeline;

/// <summary>
/// R2 — State machine that detects events from the raw voltage stream.
///
/// Responsibilities:
///   - Detect event start (voltage >= 0.5V) and end (voltage &lt; 0.3V)
///   - Track peaks within the event
///   - Handle inconsistent events (gap, timeout, out-of-order, connection reset)
///   - Write completed events to the daily events_log_yyyyMMdd.csv
///   - Persist active event state to active_event.json for crash recovery
///   - Collect per-event samples and invoke EventFileWriter for COMPLETE events
/// </summary>
public class EventProcessor : ISampleProcessor
{
    // -- THRESHOLDS --
    private readonly double EventStartThreshold;
    private readonly double EventEndThreshold;
    private readonly double GapThresholdMs;
    private readonly double MaxEventDurationMs;

    // -- TIMEZONE --
    private static readonly TimeZoneInfo BoliviaZone = TimeZoneInfo.CreateCustomTimeZone(
        "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

    // -- PATHS --
    private readonly string _capturesFolder;
    private readonly string _activeEventPath;

    // -- COLLABORATOR --
    private readonly EventFileWriter _fileWriter;

    // -- STATE --
    private bool   _eventActive          = false;
    private long   _eventStartTime       = 0;
    private string _eventStartTimeR      = "";
    private double _maxVoltage           = 0;
    private int    _peakCount            = 0;
    private long   _peak1Time            = 0;
    private string _peak1TimeR           = "";
    private double _peak1Voltage         = 0;
    private double _prevVoltage          = 0;
    private bool   _inPeak               = false;
    private long   _firstPeakStartTime   = 0;
    private string _firstPeakStartTimeR  = "";

    // Samples accumulated for the current event (including one pre-event resting sample)
    private List<(long TimestampMs, double Voltage, string TimeR)> _eventSamples = new();

    // Last sample seen (used as pre-event resting anchor)
    private (long TimestampMs, double Voltage, string TimeR) _lastSample;
    private bool _hasLastSample = false;

    // Last valid timestamp (for order and gap checks)
    public long LastTimestampMs { get; private set; } = 0;

    // -- PUBLIC STATE --
    public bool IsEventActive => _eventActive;

    // -- LOCK --
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Tracks which event_log files already received a #config line in this session
    private readonly HashSet<string> _configWrittenFiles = new();

    public EventProcessor(string capturesFolder,
        double eventStartThreshold = 0.5,
        double eventEndThreshold   = 0.3,
        double gapThresholdMs      = 1000,
        double maxEventDurationMs  = 50000)
    {
        EventStartThreshold = eventStartThreshold;
        EventEndThreshold   = eventEndThreshold;
        GapThresholdMs      = gapThresholdMs;
        MaxEventDurationMs  = maxEventDurationMs;
        _capturesFolder     = capturesFolder;
        _activeEventPath    = Path.Combine(capturesFolder, "active_event.json");
        _fileWriter         = new EventFileWriter(capturesFolder);
        Directory.CreateDirectory(capturesFolder);
    }

    /// <summary>
    /// Called once at startup to recover any incomplete event from a crash.
    /// </summary>
    public async Task RecoverAsync()
    {
        if (!File.Exists(_activeEventPath)) return;

        var json = await File.ReadAllTextAsync(_activeEventPath);
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

    /// <summary>
    /// Called when a connection reset is detected mid-event.
    /// </summary>
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

    public async Task ProcessSampleAsync(long timestampMs, double voltage, string timeR)
    {
        await _lock.WaitAsync();
        try
        {
            // ── VALIDATE ORDER ───────────────────────────────────────────────
            if (LastTimestampMs > 0 && timestampMs <= LastTimestampMs)
            {
                string expectedR = ToBoliviaTime(LastTimestampMs).ToString("HH:mm:ss.fff");
                if (_eventActive)
                {
                    Console.WriteLine($"{timeR} 💬 Out of order       | active event — closing INCONSISTENT_ORDER | expected > {expectedR} | got {timeR}");
                    await CloseInconsistentAsync(timestampMs, timeR, "INCONSISTENT_ORDER");
                }
                else
                {
                    Console.WriteLine($"{timeR} 💬 Out of order       | resting state — discarded safely | expected > {expectedR} | got {timeR}");
                }
                return;
            }

            // ── CHECK GAP ────────────────────────────────────────────────────
            if (_eventActive && LastTimestampMs > 0)
            {
                double gap = timestampMs - LastTimestampMs;
                if (gap > GapThresholdMs)
                {
                    Console.WriteLine($"{timeR} ⚡ Gap detected       | {gap:F0}ms — closing INCONSISTENT_GAP");
                    await CloseInconsistentAsync(timestampMs, timeR, "INCONSISTENT_GAP");
                }
            }

            LastTimestampMs = timestampMs;

            // ── EVENT DETECTION ──────────────────────────────────────────────

            if (!_eventActive)
            {
                if (voltage >= EventStartThreshold)
                {
                    _eventActive         = true;
                    _eventStartTime      = timestampMs;
                    _eventStartTimeR     = timeR;
                    _maxVoltage          = voltage;
                    _peakCount           = 0;
                    _peak1Time           = 0;
                    _peak1TimeR          = "";
                    _peak1Voltage        = 0;
                    _prevVoltage         = voltage;
                    _inPeak              = true;
                    _firstPeakStartTime  = timestampMs;
                    _firstPeakStartTimeR = timeR;

                    _eventSamples.Clear();

                    // Add the last resting sample as the first entry (pre-event context)
                    if (_hasLastSample)
                        _eventSamples.Add(_lastSample);

                    _eventSamples.Add((timestampMs, voltage, timeR));

                    await SaveActiveEventAsync(_eventStartTime, _eventStartTimeR);
                    Console.WriteLine($"{timeR} 🌟 Event started      | V: {voltage:F2}");
                }
                else
                {
                    // Track last resting sample for use as pre-event context
                    _lastSample    = (timestampMs, voltage, timeR);
                    _hasLastSample = true;
                }
            }
            else
            {
                _eventSamples.Add((timestampMs, voltage, timeR));

                if (voltage > _maxVoltage)
                    _maxVoltage = voltage;

                // ── PEAK DETECTION ───────────────────────────────────────────
                if (voltage > _prevVoltage)
                {
                    if (_peakCount == 0)
                    {
                        _firstPeakStartTime  = timestampMs;
                        _firstPeakStartTimeR = timeR;
                    }
                    _inPeak = true;
                }

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
                    Console.WriteLine($"{timeR} 📈 {"Peak #" + _peakCount,-19}| V: {_prevVoltage:F2}");
                }

                _prevVoltage = voltage;

                // ── NATURAL CLOSE ────────────────────────────────────────────
                if (voltage < EventEndThreshold)
                {
                    await CloseCompleteAsync(timestampMs, timeR);
                }
                // ── FORCED CLOSE (max duration) ──────────────────────────────
                else if ((timestampMs - _eventStartTime) > MaxEventDurationMs)
                {
                    Console.WriteLine($"{timeR} ⚡ Max duration       | closing INCONSISTENT_TIMEOUT");
                    await CloseInconsistentAsync(timestampMs, timeR, "INCONSISTENT_TIMEOUT");
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // -- CLOSE HELPERS --------------------------------------------------------

    private async Task CloseCompleteAsync(long endTime, string endTimeR)
    {
        double durMs = endTime - _eventStartTime;
        Console.WriteLine($"{endTimeR} ✅ Event closed       | {durMs:F0}ms | peaks: {_peakCount} | maxV: {_maxVoltage:F2}");

        await WriteEventLogAsync(
            _eventStartTimeR, _eventStartTime,
            endTimeR, endTime, durMs,
            _peakCount, _maxVoltage,
            _peak1Time, _peak1TimeR, _peak1Voltage,
            "COMPLETE");

        // Write per-event file for COMPLETE events
        var samplesToWrite = new List<(long, double, string)>(_eventSamples);
        await _fileWriter.WriteEventAsync(samplesToWrite, _eventStartTime);

        await ClearActiveEventAsync();
        ResetState();
    }

    private async Task CloseInconsistentAsync(long endTime, string endTimeR, string status)
    {
        double durMs = endTime - _eventStartTime;
        Console.WriteLine($"{endTimeR} ❌ Event closed      | {status} | {durMs:F0}ms");

        await WriteEventLogAsync(
            _eventStartTimeR, _eventStartTime,
            endTimeR, endTime, durMs,
            _peakCount, _maxVoltage,
            _peak1Time, _peak1TimeR, _peak1Voltage,
            status);

        // INCONSISTENT events: do not write per-event file
        await ClearActiveEventAsync();
        ResetState();
    }

    private void ResetState()
    {
        _eventActive         = false;
        _eventStartTime      = 0;
        _eventStartTimeR     = "";
        _maxVoltage          = 0;
        _peakCount           = 0;
        _peak1Time           = 0;
        _peak1TimeR          = "";
        _peak1Voltage        = 0;
        _prevVoltage         = 0;
        _inPeak              = false;
        _firstPeakStartTime  = 0;
        _firstPeakStartTimeR = "";
        _eventSamples.Clear();
    }

    // -- FILE HELPERS ---------------------------------------------------------

    private async Task WriteEventLogAsync(
        string timeR, long time,
        string endTimeR, long endTime, double durMs,
        int peaks, double maxV,
        long peak1Time, string peak1TimeR, double peak1V,
        string status)
    {
        var filePath     = GetEventLogPath(time);
        bool fileExists  = File.Exists(filePath);
        bool configSeen  = _configWrittenFiles.Contains(filePath);

        await using var writer = new StreamWriter(filePath, append: true);

        if (!fileExists)
            await writer.WriteLineAsync("TimeR,DurMs,EndTimeR,Time,EndTime,Peaks,MaxV,Peak1Time,Peak1TimeR,Peak1V,Status,Label");

        if (!configSeen)
        {
            await writer.WriteLineAsync(
                $"#config: EventStartV={EventStartThreshold} EventEndV={EventEndThreshold} GapMs={GapThresholdMs} MaxDurMs={MaxEventDurationMs}");
            _configWrittenFiles.Add(filePath);
        }

        await writer.WriteLineAsync(
            $"{timeR},{durMs,5:F0},{endTimeR},{time}," +
            $"{endTime},{peaks},{maxV:F2}," +
            $"{peak1Time},{peak1TimeR},{peak1V:F2}," +
            $"{status},");
    }

    private string GetEventLogPath(long timestampMs)
    {
        string date = ToBoliviaTime(timestampMs).ToString("yyyyMMdd");
        return Path.Combine(_capturesFolder, $"events_log_{date}.csv");
    }

    private async Task SaveActiveEventAsync(long startTime, string startTimeR)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ActiveEvent
        {
            StartTime  = startTime,
            StartTimeR = startTimeR
        });
        await File.WriteAllTextAsync(_activeEventPath, json);
    }

    private async Task ClearActiveEventAsync()
    {
        await File.WriteAllTextAsync(_activeEventPath, "{}");
    }

    private static DateTime ToBoliviaTime(long timestampMs) =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime,
            TimeZoneInfo.Utc, BoliviaZone);

    private class ActiveEvent
    {
        public long   StartTime  { get; set; }
        public string StartTimeR { get; set; } = "";
    }
}
