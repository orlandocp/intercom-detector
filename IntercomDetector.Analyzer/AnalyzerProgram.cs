Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("INTERCOM SIGNAL ANALYZER");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════");
Console.WriteLine();

// ── PHASE 1 — LOAD EVENTS ───────────────────────────────────────────────────
Console.WriteLine("PHASE 1 — LOAD EVENTS");
Console.WriteLine("───────────────────────────────────────────────────────────────────────────────────");

var events = EventLogReader.ReadAll();

if (events.Count == 0)
{
    Console.WriteLine("No labeled events found. Label some events and try again.");
    return;
}

Console.WriteLine();
Console.WriteLine($"  {"TimeR",-15} {"DurMs",7} {"MaxV",6} {"Peaks",6} {"Peak1TimeR",-15} {"Peak1V",7}  {"Label"}");
Console.WriteLine($"  {"─────────────────────────────────────────────────────────────────────────────"}");
foreach (var e in events)
    Console.WriteLine($"  {e.TimeR,-15} {e.DurMs,7:F0} {e.MaxV,6:F2} {e.Peaks,6} {e.Peak1TimeR,-15} {e.Peak1V,7:F2}  {e.Label}");

Console.WriteLine();
int countR = events.Count(e => e.Label == "r");
int countV = events.Count(e => e.Label == "v");
int countC = events.Count(e => e.Label == "c");
Console.WriteLine($"  Summary: {countR} r | {countV} v | {countC} c | {events.Count} total");

if (countR < 5 || countV < 5 || countC < 5)
{
    Console.WriteLine();
    Console.WriteLine("  ⚠️  At least 5 events of each type recommended for reliable analysis.");
    if (countR < 5) Console.WriteLine($"     Missing {5 - countR} r events");
    if (countV < 5) Console.WriteLine($"     Missing {5 - countV} v events");
    if (countC < 5) Console.WriteLine($"     Missing {5 - countC} c events");
}

Console.WriteLine();

// ── PHASE 2 — EXTRACT FEATURES FROM RAW ─────────────────────────────────────
Console.WriteLine("PHASE 2 — EXTRACT FEATURES FROM RAW");
Console.WriteLine("───────────────────────────────────────────────────────────────────────────────────");
Console.WriteLine();

var features = FeatureExtractor.Extract(events);

// Early detection features — first sample
Console.WriteLine("  Early features — available at first sample:");
Console.WriteLine($"  {"TimeR",-15} {"OnsetRate",11}  {"Label"}");
Console.WriteLine($"  {"──────────────────────────────────────"}");
foreach (var f in features)
    Console.WriteLine($"  {f.Event.TimeR,-15} {f.OnsetRate,11:F6}  {f.Event.Label}");

Console.WriteLine();

// Early detection features — peak + 500ms
Console.WriteLine("  Early features — available at peak confirmation + ~500ms:");
Console.WriteLine($"  {"TimeR",-15} {"RiseMs",8} {"RiseRate",10} {"1stDropRate",12}  {"Label"}");
Console.WriteLine($"  {"───────────────────────────────────────────────────────────────"}");
foreach (var f in features)
    Console.WriteLine($"  {f.Event.TimeR,-15} {f.RiseTimeMs,8:F0} {f.RiseRate,10:F6} {f.FirstDropRate,12:F6}  {f.Event.Label}");

Console.WriteLine();

// Early detection features — peak + 1000ms
Console.WriteLine("  Early features — available at peak confirmation + ~1000ms:");
Console.WriteLine($"  {"TimeR",-15} {"AUC Early",10}  {"Label"}");
Console.WriteLine($"  {"────────────────────────────────────────"}");
foreach (var f in features)
    Console.WriteLine($"  {f.Event.TimeR,-15} {f.AreaUnderCurveEarly,10:F4}  {f.Event.Label}");

Console.WriteLine();

// End-of-event features
Console.WriteLine("  End-of-event features:");
Console.WriteLine($"  {"TimeR",-15} {"EarlyDec",10} {"MidDec",10} {"LateDec",10} {"DecayRate",10} {"V@25%",7} {"V@50%",7} {"V@75%",7} {"AreaV",8}  {"Label"}");
Console.WriteLine($"  {"───────────────────────────────────────────────────────────────────────────────────────────────────"}");
foreach (var f in features)
    Console.WriteLine($"  {f.Event.TimeR,-15} {f.DecayRateEarly,10:F6} {f.DecayRateMid,10:F6} {f.DecayRateLate,10:F6} {f.DecayRate,10:F6} {f.VoltageAt25pct,7:F2} {f.VoltageAt50pct,7:F2} {f.VoltageAt75pct,7:F2} {f.AreaUnderCurve,8:F2}  {f.Event.Label}");

Console.WriteLine();
Console.WriteLine($"  Extracted {features.Count} feature sets ({features.Count(f => f.Event.Label == "r")} r | {features.Count(f => f.Event.Label == "v")} v | {features.Count(f => f.Event.Label == "c")} c)");

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════");
