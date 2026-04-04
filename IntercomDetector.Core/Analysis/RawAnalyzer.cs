using IntercomDetector.Core.IO;

namespace IntercomDetector.Core.Analysis;

/// <summary>
/// Analyzes gap patterns in raw_*.csv files (continuous stream across all files).
/// Optionally correlates zoom-range gaps against labeled events from events_*.csv.
/// </summary>
public static class RawAnalyzer
{
    public static RawAnalysisResult Analyze(IEnumerable<string> filePaths,
        long? zoomFromMs = null, long? zoomToMs = null, long? zoomBucketMs = null)
    {
        var files = filePaths.OrderBy(f => f).ToList();

        // Load events for correlation (auto-detect events_*.csv in same folder)
        string folder     = files.Count > 0 ? Path.GetDirectoryName(Path.GetFullPath(files[0]))! : "";
        var events        = new List<EventEntry>();
        var unknownEvents = new List<UnknownEvent>();
        if (files.Count > 0)
        {
            var eventFiles = Directory.GetFiles(folder, "events_*.csv").OrderBy(f => f);
            foreach (var ef in eventFiles)
            {
                foreach (var (entry, timeR, endTimeR) in ReadEventsRich(ef))
                {
                    events.Add(entry);
                    if (entry.Label is not ("r" or "v" or "c"))
                        unknownEvents.Add(new UnknownEvent(ef, timeR, endTimeR, entry.TimeMs, entry.EndTimeMs, entry.Label));
                }
            }
            events.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        }

        // Build adaptive streams: stitch adjacent files if cross-file gap ≤ threshold,
        // otherwise start a new stream (device was offline / restarted).
        // Threshold = max within-event gap so midnight transitions and cross-midnight
        // events are stitched, while real outages are split.
        var (threshold, thresholdFromEvents) = ComputeStitchThreshold(Path.Combine(folder, "events"));
        var streams = StreamSplitter.AdaptiveStreams(files, threshold);
        var  gaps      = GapAnalyzer.Analyze(streams, zoomFromMs, zoomToMs, zoomBucketMs, outageThresholdMs: threshold);

        // Always enrich unknown events with raw gap count (independent of zoom)
        unknownEvents = unknownEvents
            .Select(u => u with { RawGapCount = CountGapsInWindow(streams, u.TimeMs, u.EndTimeMs) })
            .ToList();

        // Per-bucket correlation — uses resolved zoom bounds so --bucket-only also triggers it.
        List<BucketCorrelation>? bucketCorrelations = null;
        if (gaps.ZoomFromMs.HasValue && gaps.ZoomToMs.HasValue && gaps.ZoomBucketMs.HasValue &&
            gaps.ZoomBuckets != null && events.Count > 0)
        {
            bucketCorrelations = ComputeCorrelation(
                streams, events,
                gaps.ZoomFromMs.Value, gaps.ZoomToMs.Value, gaps.ZoomBucketMs.Value, gaps.ZoomBuckets.Count);
        }

        return new RawAnalysisResult
        {
            SourceFiles               = files,
            DateFrom                  = ParseDate(Path.GetFileName(files.First())),
            DateTo                    = ParseDate(Path.GetFileName(files.Last())),
            EventsLoaded              = events.Count,
            StitchThresholdMs         = threshold,
            StitchThresholdFromEvents = thresholdFromEvents,
            Gaps                      = gaps,
            BucketCorrelations        = bucketCorrelations,
            UnknownEvents             = unknownEvents,
        };
    }

    /// <summary>Fallback stitch threshold when no event files are present.</summary>
    private const long StitchFallbackMs = 1500;

    private static (long ThresholdMs, bool FromEvents) ComputeStitchThreshold(string eventsFolder)
    {
        if (!Directory.Exists(eventsFolder)) return (StitchFallbackMs, false);
        var streams = Directory.GetFiles(eventsFolder, "event_*.csv")
            .Select(SampleFileReader.ReadTimestamps)
            .ToList<IEnumerable<long>>();
        if (streams.Count == 0) return (StitchFallbackMs, false);
        long max = GapAnalyzer.ComputeMaxGap(streams);
        return max > 0 ? (GapAnalyzer.ComputeBound(max), true) : (StitchFallbackMs, false);
    }

    private static string ParseDate(string fileName)
    {
        // raw_yyyyMMdd.csv → "yyyy-MM-dd"
        if (fileName.StartsWith("raw_") && fileName.Length >= 12)
        {
            string d = fileName.Substring(4, 8);
            if (d.Length == 8 && d.All(char.IsDigit))
                return $"{d[..4]}-{d[4..6]}-{d[6..8]}";
        }
        return fileName;
    }

    // ── CORRELATION ───────────────────────────────────────────────────────────

    private static List<BucketCorrelation> ComputeCorrelation(
        IReadOnlyList<IEnumerable<long>> streams, List<EventEntry> events,
        long zoomFrom, long zoomTo, long bucketSize, int bucketCount)
    {
        var r    = new int[bucketCount];
        var v    = new int[bucketCount];
        var c    = new int[bucketCount];
        var unk  = new int[bucketCount];
        var out_ = new int[bucketCount];

        foreach (var stream in streams)
        {
            long prevTs = -1;  // reset per stream — same boundaries as GapAnalyzer
            foreach (long ts in stream)
            {
                if (prevTs >= 0)
                {
                    long gap    = ts - prevTs;
                    long relGap = gap - zoomFrom;
                    if (relGap >= 0 && gap < zoomTo)
                    {
                        int idx = (int)(relGap / bucketSize);
                        if (idx < bucketCount)
                        {
                            var match = FindOverlappingEvent(events, prevTs, ts);
                            if (match != null)
                                switch (match.Label)
                                {
                                    case "r": r[idx]++;   break;
                                    case "v": v[idx]++;   break;
                                    case "c": c[idx]++;   break;
                                    default:  unk[idx]++; break;
                                }
                            else
                                out_[idx]++;
                        }
                    }
                }
                prevTs = ts;
            }
        }

        return Enumerable.Range(0, bucketCount)
            .Select(i => new BucketCorrelation(r[i], v[i], c[i], unk[i], out_[i]))
            .ToList();
    }

    // Counts gaps within [startMs, endMs) across all streams.
    private static int CountGapsInWindow(IReadOnlyList<IEnumerable<long>> streams, long startMs, long endMs)
    {
        int count = 0;
        foreach (var stream in streams)
        {
            long prevTs = -1;
            foreach (long ts in stream)
            {
                if (prevTs >= 0 && prevTs >= startMs && prevTs < endMs)
                    count++;
                prevTs = ts;
            }
        }
        return count;
    }

    private static EventEntry? FindOverlappingEvent(List<EventEntry> events, long gapStart, long gapEnd)
    {
        if (events.Count == 0) return null;
        int lo = 0, hi = events.Count - 1, candidate = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (events[mid].TimeMs < gapEnd) { candidate = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        for (int i = candidate; i >= 0; i--)
        {
            if (events[i].EndTimeMs <= gapStart) break;
            if (events[i].TimeMs < gapEnd && events[i].EndTimeMs > gapStart)
                return events[i];
        }
        return null;
    }

    private static IEnumerable<(EventEntry Entry, string TimeR, string EndTimeR)> ReadEventsRich(string path)
    {
        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("TimeR"))        continue;
            if (line.StartsWith("#"))            continue;

            var parts = line.Split(',');
            if (parts.Length < 11) continue;
            if (parts[10].Trim() != "COMPLETE")  continue;

            if (!long.TryParse(parts[3].Trim(), out long timeMs))    continue;
            if (!long.TryParse(parts[4].Trim(), out long endTimeMs)) continue;

            string label   = parts.Length > 11 ? parts[11].Trim() : "";
            if (string.IsNullOrWhiteSpace(label)) label = "unknown";

            string timeR   = parts[0].Trim();
            string endTimeR = parts[2].Trim();

            yield return (new EventEntry(timeMs, endTimeMs, label), timeR, endTimeR);
        }
    }
}
