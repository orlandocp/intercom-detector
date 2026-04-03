namespace IntercomDetector.Core.IO;

/// <summary>
/// Splits an ordered list of CSV files into independent timestamp streams
/// based on the gap between consecutive files.
/// </summary>
public static class StreamSplitter
{
    /// <summary>
    /// Groups files into segments where adjacent files are stitched (same stream)
    /// if their cross-file gap ≤ thresholdMs, otherwise a new stream begins.
    /// Each returned stream reads lazily from disk.
    /// </summary>
    public static IReadOnlyList<IEnumerable<long>> AdaptiveStreams(
        List<string> files, long thresholdMs)
    {
        if (files.Count == 0) return Array.Empty<IEnumerable<long>>();

        var segments = new List<List<string>>();
        var current  = new List<string> { files[0] };
        long lastTs  = LastTimestamp(files[0]);

        for (int i = 1; i < files.Count; i++)
        {
            long firstTs = FirstTimestamp(files[i]);
            if (firstTs >= 0 && lastTs >= 0 && firstTs - lastTs > thresholdMs)
            {
                segments.Add(current);
                current = new List<string>();
            }
            current.Add(files[i]);
            lastTs = LastTimestamp(files[i]);
        }
        if (current.Count > 0) segments.Add(current);

        return segments
            .Select(seg => seg.SelectMany(SampleFileReader.ReadTimestamps))
            .ToList<IEnumerable<long>>();
    }

    private static long FirstTimestamp(string path)
        => SampleFileReader.ReadTimestamps(path).FirstOrDefault(-1L);

    private static long LastTimestamp(string path)
        => SampleFileReader.ReadTimestamps(path).LastOrDefault(-1L);
}
