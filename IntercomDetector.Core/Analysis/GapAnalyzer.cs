namespace IntercomDetector.Core.Analysis;

/// <summary>
/// Pure gap computation logic — no file I/O, no knowledge of file types.
/// Takes one or more timestamp streams and produces gap distribution statistics.
/// Gaps are computed within each stream; boundaries between streams are ignored.
/// </summary>
public static class GapAnalyzer
{
    /// <summary>Shelly Plus Uni hardware ADC sampling rate — used for bound rounding.</summary>
    private const long DefaultSamplingIntervalMs = 50;

    /// <summary>Target number of buckets when auto-computing zoom bucket size from a range or P99.</summary>
    private const int DefaultZoomBucketCount = 15;

    private static readonly (long FromMs, long ToMs)[] DefaultRanges =
    {
        (0,           100),
        (100,         200),
        (200,         500),
        (500,       1_000),
        (1_000,     5_000),
        (5_000,    30_000),
        (30_000,  300_000),
        (300_000, 3_600_000),
        (3_600_000, long.MaxValue),
    };

    /// <summary>
    /// Rounds max gap up to the next multiple of samplingIntervalMs,
    /// giving a safe bound that accounts for RTOS jitter.
    /// </summary>
    public static long ComputeBound(long maxGapMs)
        => (long)(Math.Ceiling((double)(maxGapMs + DefaultSamplingIntervalMs) / DefaultSamplingIntervalMs) * DefaultSamplingIntervalMs);

    /// <summary>Analyzes gaps across multiple independent streams.</summary>
    public static GapAnalysisResult Analyze(
        IEnumerable<IEnumerable<long>> streams,
        long? zoomFromMs = null, long? zoomToMs = null, long? zoomBucketMs = null,
        long? outageThresholdMs = null)
    {
        var  counts  = new int[DefaultRanges.Length];
        var  allGaps = new List<long>();
        long minGap  = long.MaxValue;
        long maxGap  = long.MinValue;

        foreach (var stream in streams)
        {
            long prevTs = -1;
            foreach (long ts in stream)
            {
                if (prevTs >= 0)
                {
                    long gap = ts - prevTs;
                    if (gap > 0)
                    {
                        allGaps.Add(gap);
                        if (gap < minGap) minGap = gap;
                        if (gap > maxGap) maxGap = gap;

                        for (int i = 0; i < DefaultRanges.Length; i++)
                            if (gap >= DefaultRanges[i].FromMs && gap < DefaultRanges[i].ToMs)
                            { counts[i]++; break; }
                    }
                }
                prevTs = ts;
            }
        }

        // Percentiles
        long p50 = 0, p95 = 0, p99 = 0;
        if (allGaps.Count > 0)
        {
            allGaps.Sort();
            p50 = allGaps[(int)Math.Floor(0.50 * allGaps.Count)];
            p95 = allGaps[(int)Math.Floor(0.95 * allGaps.Count)];
            p99 = allGaps[(int)Math.Floor(0.99 * allGaps.Count)];
        }

        int total = allGaps.Count;

        // Outage stats: gaps that exceed the stitch threshold (device was offline)
        OutageStats? outageStats = null;
        if (outageThresholdMs.HasValue && total > 0)
        {
            // allGaps is sorted — binary search for first index > threshold
            int lo = 0, hi = total;
            while (lo < hi) { int mid = (lo + hi) / 2; if (allGaps[mid] <= outageThresholdMs.Value) lo = mid + 1; else hi = mid; }
            if (lo < total)
            {
                long totalOfflineMs = 0;
                for (int i = lo; i < total; i++) totalOfflineMs += allGaps[i];
                outageStats = new OutageStats(total - lo, totalOfflineMs, allGaps[^1], outageThresholdMs.Value);
            }
        }

        var buckets = DefaultRanges
            .Select((r, i) => new GapBucket(r.FromMs, r.ToMs, counts[i]))
            .ToList();

        List<GapBucket>? zoomBuckets = null;
        long? resolvedFrom = zoomFromMs;
        long? resolvedTo   = zoomToMs;

        // Track which params were supplied by the caller vs auto-computed.
        bool fromIsAuto   = !zoomFromMs.HasValue;
        bool toIsAuto     = !zoomToMs.HasValue;
        bool bucketIsAuto = !zoomBucketMs.HasValue;

        // Auto-compute bucket size when not explicitly provided.
        // Basis: explicit range (from+to) > explicit to (from defaults to 0) > P99.
        if (bucketIsAuto && total > 0)
        {
            long basisMs = (zoomFromMs.HasValue && zoomToMs.HasValue) ? zoomToMs.Value - zoomFromMs.Value
                         : zoomToMs.HasValue                          ? zoomToMs.Value
                         : p99;
            if (basisMs > 0)
                zoomBucketMs = ComputeDefaultBucket(basisMs);
        }

        if (zoomBucketMs.HasValue && total > 0)
        {
            resolvedFrom ??= 0;
            resolvedTo   ??= p99 + zoomBucketMs.Value; // include P99 in last bucket
        }

        if (resolvedFrom.HasValue && resolvedTo.HasValue && zoomBucketMs.HasValue)
        {
            long size = zoomBucketMs.Value;
            var  zr   = new List<(long From, long To)>();
            for (long f = resolvedFrom.Value; f < resolvedTo.Value; f += size)
                zr.Add((f, Math.Min(f + size, resolvedTo.Value)));

            var zoomCounts = new int[zr.Count];
            foreach (long gap in allGaps)
            {
                if (gap < resolvedFrom.Value || gap >= resolvedTo.Value) continue;
                int idx = (int)((gap - resolvedFrom.Value) / size);
                if (idx >= 0 && idx < zoomCounts.Length)
                    zoomCounts[idx]++;
            }

            zoomBuckets  = zr.Select((r, i) => new GapBucket(r.From, r.To, zoomCounts[i])).ToList();
            resolvedTo   = zoomToMs ?? (p99 + zoomBucketMs!.Value); // actual upper bound used
        }

        return new GapAnalysisResult
        {
            TotalGaps        = total,
            MinGapMs         = total > 0 ? minGap : 0,
            MaxGapMs         = total > 0 ? maxGap : 0,
            P50GapMs         = p50,
            P95GapMs         = p95,
            P99GapMs         = p99,
            Buckets          = buckets,
            ZoomBuckets      = zoomBuckets,
            ZoomFromMs       = resolvedFrom,
            ZoomToMs         = resolvedTo,
            ZoomBucketMs     = zoomBucketMs,
            Outages          = outageStats,
            ZoomFromIsAuto   = fromIsAuto,
            ZoomToIsAuto     = toIsAuto,
            ZoomBucketIsAuto = bucketIsAuto,
        };
    }

    /// <summary>Convenience overload for a single continuous stream.</summary>
    public static GapAnalysisResult Analyze(
        IEnumerable<long> stream,
        long? zoomFromMs = null, long? zoomToMs = null, long? zoomBucketMs = null,
        long? outageThresholdMs = null)
        => Analyze(new[] { stream }, zoomFromMs, zoomToMs, zoomBucketMs, outageThresholdMs);

    /// <summary>
    /// Computes the default zoom bucket size from P99, targeting ~15 buckets,
    /// rounded up to the nearest 50ms (Shelly sampling interval).
    /// </summary>
    /// <summary>
    /// Computes zoom bucket size from a basis value (P99 or explicit range),
    /// targeting DefaultZoomBucketCount buckets, rounded up to DefaultSamplingIntervalMs.
    /// </summary>
    public static long ComputeDefaultBucket(long basisMs)
    {
        if (basisMs <= 0) return DefaultSamplingIntervalMs;
        long raw = (long)Math.Ceiling((double)basisMs / DefaultZoomBucketCount / DefaultSamplingIntervalMs) * DefaultSamplingIntervalMs;
        return Math.Max(DefaultSamplingIntervalMs, raw);
    }

    /// <summary>Returns the maximum gap observed across multiple independent streams.</summary>
    public static long ComputeMaxGap(IEnumerable<IEnumerable<long>> streams)
    {
        long max = 0;
        foreach (var stream in streams)
        {
            long prevTs = -1;
            foreach (long ts in stream)
            {
                if (prevTs >= 0)
                {
                    long gap = ts - prevTs;
                    if (gap > max) max = gap;
                }
                prevTs = ts;
            }
        }
        return max;
    }
}
