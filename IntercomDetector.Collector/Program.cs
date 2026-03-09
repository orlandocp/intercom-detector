using System.Collections.Concurrent;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// GLOBAL STATE
double previousVoltage = 0;
double previousTimestampMs = 0;
int eventCounter = 0;
var pendingEvents = new ConcurrentDictionary<string, ConcurrentBag<string>>();

// Bolivia is GMT-4, no daylight saving
TimeZoneInfo boliviaZone = TimeZoneInfo.CreateCustomTimeZone(
    "Bolivia", TimeSpan.FromHours(-4), "Bolivia", "Bolivia");

// COLOR HELPERS
string Red(string text) => $"\x1b[38;2;255;153;153m{text}\x1b[0m";
string Blue(string text) => $"\x1b[38;2;153;204;255m{text}\x1b[0m";

DateTime ToBoliviaTime(double timestampMs) =>
    TimeZoneInfo.ConvertTime(
        DateTimeOffset.FromUnixTimeMilliseconds((long)timestampMs).UtcDateTime,
        TimeZoneInfo.Utc, boliviaZone);

bool TryParseSample(string line, out double timestampMs, out double voltage)
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

void DisplaySample(string timeStr, double voltage, double duration,
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

async Task SaveEventAsync(string[] lines, double firstTimestampMs)
{
    var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "events");
    Directory.CreateDirectory(dir);
    DateTime eventTime = ToBoliviaTime(firstTimestampMs);
    string fileName = $"{eventTime:yyyyMMdd_HHmmss}.csv";
    string path = Path.Combine(dir, fileName);
    await File.WriteAllTextAsync(path, "Timestamp,Voltage\n");
    var content = string.Join("\n", lines.Select(l => l.Trim()));
    await File.AppendAllTextAsync(path, content + "\n");
    Console.WriteLine($"💾 Saved: {fileName}");
}

app.MapPost("/buffer", async (HttpContext context) => {
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    if (lines.Length == 0) return;

    var meta = lines[0].Split(',');
    if (meta.Length < 3 || meta[0].Trim() != "META")
    {
        Console.WriteLine("⚠️ Invalid chunk format");
        return;
    }

    string eventId = meta[1].Trim();
    bool isFinal = meta[2].Trim() == "1";
    var dataLines = lines.Skip(1).ToArray();

    var eventLines = pendingEvents.GetOrAdd(eventId, _ => new ConcurrentBag<string>());
    foreach (var line in dataLines)
        eventLines.Add(line);

    Console.WriteLine($"📦 Chunk received | EventId: {eventId} | lines: {dataLines.Length} | total: {eventLines.Count} | final: {isFinal}");

    if (!isFinal) return;

    var allLines = eventLines.ToArray();
    pendingEvents.TryRemove(eventId, out _);

    allLines = allLines
        .Where(l => l.Contains(','))
        .OrderBy(l => {
            var parts = l.Split(',');
            return double.TryParse(parts[0].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double ts) ? ts : 0;
        })
        .ToArray();

    eventCounter++;
    string evId = eventCounter.ToString("D4");

    List<double> drops = new List<double>();
    int upCount = 0, downCount = 0;
    double startVoltage = previousVoltage;
    double firstTimestampMs = 0;

    Console.WriteLine($"\n🧪 EVENT #{evId} ({allLines.Length} samples):");

    foreach (var line in allLines)
    {
        if (!TryParseSample(line, out double tsMs, out double voltage)) continue;
        if (firstTimestampMs == 0) firstTimestampMs = tsMs;

        double duration = previousTimestampMs > 0 ? tsMs - previousTimestampMs : 0;
        string timeStr = ToBoliviaTime(tsMs).ToString("HH:mm:ss.fff");

        if (duration > 0)
        {
            double dv = previousVoltage - voltage;
            DisplaySample(timeStr, voltage, duration, dv, ref drops, ref upCount, ref downCount);
        }

        previousVoltage = voltage;
        previousTimestampMs = tsMs;
    }

    Console.WriteLine($"📊 Summary: {downCount} drops | {upCount} rises | V start: {startVoltage:F3} | V end: {previousVoltage:F3}");
    await SaveEventAsync(allLines, firstTimestampMs);
    Console.WriteLine("─────────────────────────────────────");
});

app.Run("http://0.0.0.0:5000");