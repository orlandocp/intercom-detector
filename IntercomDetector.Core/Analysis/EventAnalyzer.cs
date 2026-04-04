using IntercomDetector.Core.IO;

namespace IntercomDetector.Core.Analysis;

/// <summary>
/// Analyzes gap patterns within event_*.csv files.
/// Each file is treated as an independent stream — gaps are not computed across file boundaries.
/// </summary>
public static class EventAnalyzer
{
    public static EventAnalysisResult Analyze(IEnumerable<string> filePaths,
        long? zoomFromMs = null, long? zoomToMs = null, long? zoomBucketMs = null)
    {
        var files = filePaths.OrderBy(f => f).ToList();
        if (files.Count == 0)
            return new EventAnalysisResult();

        // Date range from filenames: event_yyyyMMdd_HHmmssfff.csv
        string dateFrom = ParseDate(Path.GetFileName(files.First()));
        string dateTo   = ParseDate(Path.GetFileName(files.Last()));

        // Each file is one independent stream
        var streams      = files.Select(f => SampleFileReader.ReadTimestamps(f));
        var gaps         = GapAnalyzer.Analyze(streams, zoomFromMs, zoomToMs, zoomBucketMs);
        int totalSamples = files.Sum(f => SampleFileReader.ReadTimestamps(f).Count());

        // Load events_*.csv from parent folder (event files live in captures/events/, summary in captures/)
        string folder       = Path.GetDirectoryName(Path.GetFullPath(files[0]))!;
        string parentFolder = Path.GetDirectoryName(folder) ?? folder;
        var    events       = LoadEvents(parentFolder, out var unknownEvents);

        // Enrich unknown events with gap count from their matching event_*.csv file
        var fileIndex = BuildFileIndex(files);  // TimeMs → file path
        unknownEvents = unknownEvents
            .Select(u =>
            {
                int count = fileIndex.TryGetValue(u.TimeMs, out string? path)
                    ? CountGaps(path)
                    : 0;
                return u with { RawGapCount = count };
            })
            .ToList();

        // Per-bucket correlation
        List<BucketCorrelation>? bucketCorrelations = null;
        if (gaps.ZoomFromMs.HasValue && gaps.ZoomToMs.HasValue && gaps.ZoomBucketMs.HasValue && gaps.ZoomBuckets != null)
        {
            bucketCorrelations = ComputeCorrelation(
                files, events,
                gaps.ZoomFromMs.Value, gaps.ZoomToMs.Value, gaps.ZoomBucketMs.Value, gaps.ZoomBuckets.Count);
        }

        return new EventAnalysisResult
        {
            FileCount          = files.Count,
            DateFrom           = dateFrom,
            DateTo             = dateTo,
            TotalSamples       = totalSamples,
            EventsLoaded       = events.Count,
            Gaps               = gaps,
            BucketCorrelations = bucketCorrelations,
            UnknownEvents      = unknownEvents,
        };
    }

    // Loads all COMPLETE events from events_*.csv in the given folder.
    // Key: TimeMs (exact match against second timestamp of event_*.csv file).
    // Populates unknownEvents with entries whose label is not r/v/c.
    private static Dictionary<long, string> LoadEvents(string folder, out List<UnknownEvent> unknownEvents)
    {
        var result   = new Dictionary<long, string>();
        unknownEvents = new List<UnknownEvent>();
        if (!Directory.Exists(folder)) return result;

        foreach (var path in Directory.GetFiles(folder, "events_*.csv").OrderBy(f => f))
        {
            using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("TimeR") || line.StartsWith("#")) continue;

                var parts = line.Split(',');
                if (parts.Length < 12) continue;
                if (parts[10].Trim() != "COMPLETE") continue;
                if (!long.TryParse(parts[3].Trim(), out long timeMs))    continue;
                if (!long.TryParse(parts[4].Trim(), out long endTimeMs)) continue;

                string label = parts[11].Trim();
                if (string.IsNullOrWhiteSpace(label)) label = "unknown";

                result[timeMs] = label;

                if (label is not ("r" or "v" or "c"))
                    unknownEvents.Add(new UnknownEvent(path, parts[0].Trim(), parts[2].Trim(), timeMs, endTimeMs, label));
            }
        }
        return result;
    }

    // Maps the second timestamp of each event file (= event start TimeMs) to its file path.
    private static Dictionary<long, string> BuildFileIndex(List<string> eventFiles)
    {
        var index = new Dictionary<long, string>();
        foreach (var path in eventFiles)
        {
            int read = 0;
            foreach (long ts in SampleFileReader.ReadTimestamps(path))
                if (++read == 2) { index[ts] = path; break; }
        }
        return index;
    }

    // Counts the number of gaps within a single event file.
    private static int CountGaps(string path)
    {
        int  gaps  = 0;
        long prevTs = -1;
        foreach (long ts in SampleFileReader.ReadTimestamps(path))
        {
            if (prevTs >= 0) gaps++;
            prevTs = ts;
        }
        return gaps;
    }

    private static List<BucketCorrelation> ComputeCorrelation(
        List<string> eventFiles, Dictionary<long, string> events,
        long zoomFrom, long zoomTo, long bucketSize, int bucketCount)
    {
        var r   = new int[bucketCount];
        var v   = new int[bucketCount];
        var c   = new int[bucketCount];
        var unk = new int[bucketCount];

        foreach (var path in eventFiles)
        {
            // Second timestamp in the file is the exact event trigger time — matches events_*.csv Time
            string? label = null;
            int     read  = 0;
            foreach (long ts in SampleFileReader.ReadTimestamps(path))
            {
                if (++read == 2)
                {
                    label = events.TryGetValue(ts, out string? l) ? l : null;
                    break;
                }
            }

            long prevTs = -1;
            foreach (long ts in SampleFileReader.ReadTimestamps(path))
            {
                if (prevTs >= 0)
                {
                    long gap    = ts - prevTs;
                    long relGap = gap - zoomFrom;
                    if (relGap >= 0 && gap < zoomTo)
                    {
                        int idx = (int)(relGap / bucketSize);
                        if (idx < bucketCount)
                            switch (label)
                            {
                                case "r": r[idx]++; break;
                                case "v": v[idx]++; break;
                                case "c": c[idx]++; break;
                                default:  unk[idx]++; break;
                            }
                    }
                }
                prevTs = ts;
            }
        }

        return Enumerable.Range(0, bucketCount)
            .Select(i => new BucketCorrelation(r[i], v[i], c[i], unk[i], 0))
            .ToList();
    }

    private static string ParseDate(string fileName)
    {
        // event_yyyyMMdd_HHmmssfff.csv → "yyyy-MM-dd"
        if (fileName.Length >= 15 && fileName.StartsWith("event_"))
        {
            string d = fileName.Substring(6, 8);
            if (d.Length == 8)
                return $"{d[..4]}-{d[4..6]}-{d[6..8]}";
        }
        return fileName;
    }
}
