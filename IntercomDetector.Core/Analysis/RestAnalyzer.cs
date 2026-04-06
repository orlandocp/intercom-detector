namespace IntercomDetector.Core.Analysis;

/// <summary>
/// Reads rest_*.csv files and computes descriptive voltage statistics.
/// Uses a direction-flip state machine to skip post-event decay samples
/// and only accept voltages once stable rest oscillation is confirmed.
/// </summary>
public static class RestAnalyzer
{
    private const double DefaultBucketWidthV = 0.01;

    public static RestAnalysisResult Analyze(IEnumerable<string> filePaths, double? bucketWidthV = null)
    {
        var files = filePaths.OrderBy(f => f).ToList();

        bool   bucketIsAuto = !bucketWidthV.HasValue;
        double bucket       = bucketWidthV ?? DefaultBucketWidthV;

        var    filter       = new RestStateFilter();
        int    total        = 0;
        var    valid        = new List<double>();

        foreach (var path in files)
        {
            foreach (var (ts, v, _) in ReadSamples(path))
            {
                total++;
                if (filter.Process(ts, v))
                {
                    foreach (var fv in filter.LastFlushed) valid.Add(fv);
                    valid.Add(v);
                }
            }
        }

        var   stats     = VoltageStats.Compute(valid);
        int[] histogram = BuildHistogram(valid, bucket, stats.Max);

        return new RestAnalysisResult
        {
            SourceFiles   = files,
            DateFrom      = files.Count > 0 ? ParseDate(Path.GetFileName(files.First())) : "",
            DateTo        = files.Count > 0 ? ParseDate(Path.GetFileName(files.Last()))  : "",
            TotalSamples  = total,
            ValidSamples  = valid.Count,
            GapThresholdMs = RestStateFilter.GapThresholdMs,
            BucketWidthV  = bucket,
            BucketIsAuto  = bucketIsAuto,
            Stats         = stats,
            Histogram     = histogram,
        };
    }

    // ── VOLTAGE ZOOM ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams confirmed-rest valid samples whose voltage falls within [fromV, toV].
    /// For each match the callback receives the full context (prev → RESET → … → CONFIRM
    /// → valid… → MATCH) plus 1 post sample.  Return false from the callback to stop.
    /// Each sample that was a "post" for the previous match is re-evaluated as a
    /// potential next match, so the total count equals the histogram bucket count.
    /// </summary>
    public static void StreamZoom(
        IEnumerable<string> filePaths, double fromV, double toV,
        Func<RestZoomMatch, bool> onMatch)
    {
        var scanner = new RestZoomScanner(fromV, toV, onMatch);
        foreach (var path in filePaths.OrderBy(f => f))
        {
            foreach (var (ts, v, timeR) in ReadSamples(path))
                if (!scanner.Feed(path, ts, timeR, v)) return;
            if (!scanner.FlushFile()) return;
        }
    }

    private sealed class RestZoomScanner
    {
        private const long GapMs = RestStateFilter.GapThresholdMs;

        private enum SState { Scanning, PendingConfirm, Confirmed, WaitingPost }
        private enum STrend { None, Up, Down }

        private readonly double                  _fromV;
        private readonly double                  _toV;
        private readonly Func<RestZoomMatch, bool> _callback;
        private bool _stopped = false;

        // Current sample tracking
        private SState _state      = SState.Scanning;
        private STrend _prevTrend  = STrend.None;
        private long   _prevTs     = -1;
        private long   _pendingTs  = -1;  // timestamp of first flip; -1 when not in PendingConfirm
        private double _prevV      = double.NaN;
        private string _prevTimeR  = "";
        private double _lastValidV = double.NaN; // voltage of last sample added to _validHistory
        private int    _matchCount = 0;           // increments each time a match is fired

        // Buffer for UP samples in Confirmed state (retroactively flushed on DOWN flip).
        // Each entry keeps the source file so buffered matches can populate RestZoomMatch.File.
        private readonly List<(string File, RestSampleContext Ctx)> _pendingBuf = new();

        // Buffer for post samples accumulated in WaitingPost state (same-voltage samples
        // following a match, until the first sample with a different voltage arrives).
        private readonly List<RestSampleContext> _postBuf = new();

        // Scan segment buffer (RESET → anchor → scan… → CONFIRM)
        private readonly List<RestSampleContext> _segBuf = new();
        private RestSampleContext? _prevSample = null;

        // Confirmed state: fixed confirm context + growing valid history
        private List<RestSampleContext>         _confirmCtx   = new();
        private readonly List<RestSampleContext> _validHistory = new();

        // WaitingPost: context built up to and including the MATCH row
        private List<RestSampleContext>? _matchCtx    = null;
        private string                   _matchFile   = "";
        private long                     _matchTs     = 0;
        private string                   _matchTimeR  = "";
        private double                   _matchV      = 0;

        // Post sample deferred for re-evaluation after WaitingPost fires
        private (long Ts, string TimeR, double V, string File)? _pendingPost = null;

        public RestZoomScanner(double fromV, double toV, Func<RestZoomMatch, bool> callback)
            => (_fromV, _toV, _callback) = (fromV, toV, callback);

        public bool Feed(string file, long ts, string timeR, double v)
        {
            if (_stopped) return false;

            // Re-evaluate the previous post sample before processing the new one
            if (_pendingPost.HasValue)
            {
                var pp = _pendingPost.Value;
                _pendingPost = null;
                long ppGap = pp.Ts - _prevTs;
                if (!ProcessSample(pp.File, pp.Ts, pp.TimeR, pp.V, ppGap)) return false;
            }

            if (_prevTs < 0) { Store(ts, timeR, v); return true; }
            return ProcessSample(file, ts, timeR, v, ts - _prevTs);
        }

        public bool FlushFile()
        {
            if (_stopped) return false;
            if (_pendingPost.HasValue)
            {
                var pp = _pendingPost.Value;
                _pendingPost = null;
                ProcessSample(pp.File, pp.Ts, pp.TimeR, pp.V, pp.Ts - _prevTs);
            }
            if (!_stopped && _state == SState.WaitingPost)
                FireAndContinue(cutReason: "end of file");
            return !_stopped;
        }

        private void ResetToScanning(long ts, string timeR, double v, long gap, string reason)
        {
            _prevSample = new RestSampleContext(_prevTs, _prevTimeR, null, SampleRole.Prev, _prevV, "—");
            _segBuf.Clear();
            _segBuf.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Reset, v, reason));
            _prevTrend  = STrend.None;
            _state      = SState.Scanning;
            _pendingTs  = -1;
            _pendingBuf.Clear();
            _postBuf.Clear();
            _lastValidV = double.NaN;
            _confirmCtx.Clear();
            _validHistory.Clear();
            Store(ts, timeR, v);
        }

        private bool ProcessSample(string file, long ts, string timeR, double v, long gap)
        {
            // ── Gap reset ─────────────────────────────────────────────────────
            if (gap > GapMs)
            {
                if (_state == SState.WaitingPost)
                    FireAndContinue(cutReason: $"gap > {GapMs}ms");

                ResetToScanning(ts, timeR, v, gap, $"gap > {GapMs}ms");
                return !_stopped;
            }

            // ── WaitingPost ───────────────────────────────────────────────────
            if (_state == SState.WaitingPost)
            {
                if (v == _matchV)
                {
                    // Same voltage as match — keep accumulating posts
                    _postBuf.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Post, v, TrendStr(v)));
                    Store(ts, timeR, v);
                    return true;
                }
                // Voltage changed — this is the last post; fire and defer for re-evaluation
                _postBuf.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Post, v, TrendStr(v)));
                FireAndContinue(cutReason: null);
                // Defer this sample for re-evaluation as a potential next match
                // (do NOT call Store here — _prevTs already points to last accumulated post)
                _pendingPost = (ts, timeR, v, file);
                return !_stopped;
            }

            // ── PendingConfirm timeout ────────────────────────────────────────
            if (_state == SState.PendingConfirm && ts - _pendingTs > GapMs)
            {
                ResetToScanning(ts, timeR, v, gap, $"flip timeout > {GapMs}ms");
                return !_stopped;
            }

            // ── Scanning / PendingConfirm ─────────────────────────────────────
            if (_state == SState.Scanning || _state == SState.PendingConfirm)
            {
                if (v == _prevV)
                {
                    _segBuf.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Scan, v, "—"));
                    Store(ts, timeR, v); return true;
                }

                STrend newTrend    = v > _prevV ? STrend.Up : STrend.Down;
                string newTrendStr = newTrend == STrend.Up ? "UP" : "DOWN";

                if (_prevTrend == STrend.None)
                {
                    _prevTrend = newTrend;
                    _segBuf.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Anchor, v, newTrendStr));
                    Store(ts, timeR, v); return true;
                }

                bool flipped = newTrend != _prevTrend;
                _prevTrend   = newTrend;

                if (!flipped)
                {
                    _segBuf.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Scan, v, newTrendStr));
                    Store(ts, timeR, v); return true;
                }

                if (_state == SState.Scanning)
                {
                    // First flip: mark as Flip in segBuf, enter PendingConfirm
                    _segBuf.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Flip, v, newTrendStr));
                    _pendingTs = ts;
                    _state     = SState.PendingConfirm;
                    Store(ts, timeR, v); return true;
                }

                // PendingConfirm + second flip within timeout → Confirmed
                _segBuf.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Confirm, v, newTrendStr));
                _confirmCtx.Clear();
                if (_prevSample != null) _confirmCtx.Add(_prevSample);
                _confirmCtx.AddRange(_segBuf);
                _validHistory.Clear();
                _pendingTs  = -1;
                _pendingBuf.Clear();
                _lastValidV = v;
                _state      = SState.Confirmed;
                Store(ts, timeR, v); return true;
            }

            // ── Confirmed ─────────────────────────────────────────────────────

            if (v > _prevV)                              // UP — buffer, not valid yet
            {
                _pendingBuf.Add((file, new RestSampleContext(ts, timeR, gap, SampleRole.Valid, v, TrendStr(v))));
                Store(ts, timeR, v); return true;
            }

            if (v == _prevV && _pendingBuf.Count > 0)    // flat while buffering — buffer
            {
                _pendingBuf.Add((file, new RestSampleContext(ts, timeR, gap, SampleRole.Valid, v, "—")));
                Store(ts, timeR, v); return true;
            }

            // DOWN reconfirm or flat-not-buffering: flush pending buffer, checking each for match
            if (_pendingBuf.Count > 0)
            {
                FlushPendingBuf(file, ts, timeR, v, gap);
                if (_stopped) return false;
            }

            // Now compute trend relative to _lastValidV (= last buffered V after flush, or prior valid)
            string trendS = double.IsNaN(_lastValidV) ? "—"
                          : v > _lastValidV ? "UP"
                          : v < _lastValidV ? "DOWN"
                          : "—";

            if (v >= _fromV && v <= _toV)
            {
                int matchNum = ++_matchCount;
                var ctx2 = new List<RestSampleContext>(_confirmCtx.Count + _validHistory.Count + 1);
                ctx2.AddRange(_confirmCtx);
                ctx2.AddRange(_validHistory);
                ctx2.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Match, v, trendS, matchNum));
                _matchCtx   = ctx2;
                _matchFile  = file;
                _matchTs    = ts;
                _matchTimeR = timeR;
                _matchV     = v;
                _lastValidV = v;
                _state      = SState.WaitingPost;
                Store(ts, timeR, v); return true;
            }

            _lastValidV = v;
            _validHistory.Add(new RestSampleContext(ts, timeR, gap, SampleRole.Valid, v, trendS));
            Store(ts, timeR, v); return true;
        }

        /// <summary>
        /// Iterates the UP-pending buffer and flushes each entry to _validHistory.
        /// Entries whose voltage falls within [_fromV, _toV] are fired as matches inline,
        /// with the next buffer entry (or the triggering DOWN reconfirm sample) as the post.
        /// </summary>
        private void FlushPendingBuf(string reconfirmFile, long reconfirmTs, string reconfirmTimeR,
                                     double reconfirmV, long reconfirmGap)
        {
            for (int i = 0; i < _pendingBuf.Count && !_stopped; i++)
            {
                var (bufFile, ctx) = _pendingBuf[i];

                if (ctx.V >= _fromV && ctx.V <= _toV)
                {
                    int matchNum = ++_matchCount;

                    // Collect post samples: same-voltage buffer entries after the match,
                    // then the first different-voltage entry (or DOWN reconfirm when buffer runs out).
                    var postSamples = new List<RestSampleContext>();
                    bool foundDiffV = false;
                    for (int j = i + 1; j < _pendingBuf.Count && !foundDiffV; j++)
                    {
                        var nextCtx = _pendingBuf[j].Ctx;
                        postSamples.Add(new RestSampleContext(nextCtx.Ts, nextCtx.TimeR, nextCtx.GapMs,
                                                             SampleRole.Post, nextCtx.V, nextCtx.Trend));
                        if (nextCtx.V != ctx.V) foundDiffV = true;
                    }
                    if (!foundDiffV)
                    {
                        string rcTrend = reconfirmV < ctx.V ? "DOWN" : reconfirmV > ctx.V ? "UP" : "—";
                        postSamples.Add(new RestSampleContext(reconfirmTs, reconfirmTimeR, reconfirmGap,
                                                             SampleRole.Post, reconfirmV, rcTrend));
                    }

                    var matchCtx = new List<RestSampleContext>(_confirmCtx.Count + _validHistory.Count + 1);
                    matchCtx.AddRange(_confirmCtx);
                    matchCtx.AddRange(_validHistory);
                    matchCtx.Add(new RestSampleContext(ctx.Ts, ctx.TimeR, ctx.GapMs,
                                                      SampleRole.Match, ctx.V, ctx.Trend, matchNum));

                    var zoomMatch = new RestZoomMatch(bufFile, ctx.Ts, ctx.TimeR, ctx.V,
                                                     matchCtx, postSamples, null);
                    if (!_callback(zoomMatch)) { _stopped = true; }

                    _lastValidV = ctx.V;
                    _validHistory.Add(new RestSampleContext(ctx.Ts, ctx.TimeR, ctx.GapMs,
                                                           SampleRole.Valid, ctx.V, ctx.Trend, matchNum));
                }
                else
                {
                    _lastValidV = ctx.V;
                    _validHistory.Add(ctx);
                }
            }
            _pendingBuf.Clear();
        }

        /// <summary>
        /// Fires the callback for the current pending match with all accumulated post samples,
        /// then transitions back to Confirmed — adding the match to _validHistory for future context.
        /// The triggering different-voltage sample is deferred via _pendingPost so it can itself
        /// become a match.
        /// </summary>
        private void FireAndContinue(string? cutReason)
        {
            if (_matchCtx == null) return;
            var ctx = _matchCtx;
            _matchCtx = null;

            var postSamples = new List<RestSampleContext>(_postBuf);
            _postBuf.Clear();

            var zoomMatch = new RestZoomMatch(_matchFile, _matchTs, _matchTimeR, _matchV,
                                              ctx, postSamples, cutReason);
            if (!_callback(zoomMatch)) { _stopped = true; }

            // The match row becomes part of valid history for subsequent matches,
            // preserving MatchNum so the zoom display can label it "prev match N"
            var matchRow = ctx[^1];
            _lastValidV = matchRow.V;
            _validHistory.Add(new RestSampleContext(
                matchRow.Ts, matchRow.TimeR, matchRow.GapMs,
                SampleRole.Valid, matchRow.V, matchRow.Trend, matchRow.MatchNum));

            _state = SState.Confirmed;
            // NOTE: _prevTs/_prevV is already correct — either still at matchTs (no same-voltage
            // posts were buffered) or updated by Store() for each accumulated same-voltage post.
        }

        private string TrendStr(double v)
            => double.IsNaN(_prevV) ? "—" : v > _prevV ? "UP" : v < _prevV ? "DOWN" : "—";

        private void Store(long ts, string timeR, double v)
            => (_prevTs, _prevTimeR, _prevV) = (ts, timeR, v);
    }

    // ── STATE MACHINE ─────────────────────────────────────────────────────────

    /// <summary>
    /// Stateful sequential filter that confirms rest via a voltage direction flip.
    ///
    /// States:
    ///   Scanning  — skipping decay; waiting for the first direction flip
    ///   Confirmed — rest is active; all samples accepted until gap resets state
    ///
    /// Flip detection (needs 3 samples minimum after each reset):
    ///   S1 → stored, no trend yet
    ///   S2 → establishes initial trend (UP or DOWN); flat (V==prevV) is skipped
    ///   S3+ → if trend flips → Confirmed; if same → continue scanning
    /// </summary>
    private sealed class RestStateFilter
    {
        public const long GapThresholdMs = 800;  // gaps wider than this reset the machine

        private enum State { Scanning, PendingConfirm, Confirmed }
        private enum Trend { None, Up, Down }

        private State          _state      = State.Scanning;
        private Trend          _prevTrend  = Trend.None;
        private long           _prevTs     = -1;
        private long           _pendingTs  = -1;  // timestamp of first flip; -1 when not in PendingConfirm
        private double         _prevV      = double.NaN;
        private readonly List<double> _pendingBuf = new(); // buffered UP samples in Confirmed state

        private static readonly double[] _emptyDoubles = Array.Empty<double>();

        /// <summary>
        /// After each Process() call, contains any voltages that were buffered as UP-pending
        /// and are now retroactively valid because a DOWN flip reconfirmed rest.
        /// Empty in all other cases.  Caller must add these to the valid list before the
        /// current sample when Process() returns true.
        /// </summary>
        public double[] LastFlushed { get; private set; } = _emptyDoubles;

        /// <summary>
        /// Process one sample. Returns true if the current voltage is valid confirmed-rest data.
        /// Check LastFlushed immediately after — it may contain additional valid voltages that
        /// were buffered during a UP-pending period and are now retroactively confirmed.
        /// </summary>
        public bool Process(long ts, double v)
        {
            LastFlushed = _emptyDoubles;

            // Very first sample — anchor state, not yet valid
            if (_prevTs < 0)
            {
                Store(ts, v);
                return false;
            }

            long gap = ts - _prevTs;

            // Gap too large → device was offline or event occurred; start over
            if (gap > GapThresholdMs)
            {
                Reset(ts, v);
                return false;
            }

            // In confirmed rest:
            //   UP → buffer the sample (may become valid if DOWN flip arrives in time)
            //   flat while buffering → buffer
            //   DOWN after buffer → flush buffer as retroactively valid, current is valid
            //   flat not buffering → valid
            if (_state == State.Confirmed)
            {
                if (v > _prevV)                          // UP — buffer, not valid yet
                {
                    _pendingBuf.Add(v);
                    Store(ts, v);
                    return false;
                }
                if (v == _prevV && _pendingBuf.Count > 0) // flat while buffering — buffer
                {
                    _pendingBuf.Add(v);
                    Store(ts, v);
                    return false;
                }
                if (v < _prevV && _pendingBuf.Count > 0)  // DOWN reconfirm — flush buffer
                {
                    LastFlushed = _pendingBuf.ToArray();
                    _pendingBuf.Clear();
                    Store(ts, v);
                    return true;
                }
                // flat not buffering, or DOWN with no buffer → valid as-is
                Store(ts, v);
                return true;
            }

            // PendingConfirm timeout: first flip was seen but second flip hasn't come in time
            if (_state == State.PendingConfirm && ts - _pendingTs > GapThresholdMs)
            {
                Reset(ts, v);
                return false;
            }

            // Flat sample: no direction change, advance position without updating trend
            if (v == _prevV)
            {
                Store(ts, v);
                return false;
            }

            Trend newTrend = v > _prevV ? Trend.Up : Trend.Down;

            // First non-flat sample after reset: establish initial trend, keep scanning
            if (_prevTrend == Trend.None)
            {
                _prevTrend = newTrend;
                Store(ts, v);
                return false;
            }

            bool flipped = newTrend != _prevTrend;
            _prevTrend   = newTrend;
            Store(ts, v);

            if (!flipped) return false;

            if (_state == State.Scanning)
            {
                // First flip: enter PendingConfirm, wait for second flip within timeout
                _state     = State.PendingConfirm;
                _pendingTs = ts;
                return false;
            }

            // State == PendingConfirm: second flip within timeout → Confirmed
            _state     = State.Confirmed;
            _pendingTs = -1;
            return true;   // this sample is the first confirmed-rest sample
        }

        private void Store(long ts, double v) { _prevTs = ts; _prevV = v; }

        private void Reset(long ts, double v)
        {
            _state     = State.Scanning;
            _prevTrend = Trend.None;
            _pendingTs = -1;
            _pendingBuf.Clear();
            Store(ts, v);
        }
    }

    // ── HELPERS ───────────────────────────────────────────────────────────────

    private static int[] BuildHistogram(List<double> voltages, double bucket, double max)
    {
        if (voltages.Count == 0 || max <= 0) return Array.Empty<int>();

        int count = (int)Math.Ceiling(max / bucket + 1e-9) + 1;
        var hist  = new int[count];

        foreach (double v in voltages)
        {
            int idx = (int)(v / bucket);
            if (idx >= 0 && idx < hist.Length)
                hist[idx]++;
        }

        return hist;
    }

    private static string ParseDate(string fileName)
    {
        // rest_yyyyMMdd.csv → "yyyy-MM-dd"
        if (fileName.StartsWith("rest_") && fileName.Length >= 13)
        {
            string d = fileName.Substring(5, 8);
            if (d.Length == 8 && d.All(char.IsDigit))
                return $"{d[..4]}-{d[4..6]}-{d[6..8]}";
        }
        return fileName;
    }

    private static IEnumerable<(long Ts, double V, string TimeR)> ReadSamples(string path)
    {
        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("TimeR") || line.StartsWith("#")) continue;

            var parts = line.Split(',');
            if (parts.Length < 3) continue;

            if (!long.TryParse(parts[1].Trim(), out long ts)) continue;
            if (!double.TryParse(parts[2].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double v)) continue;

            yield return (ts, v, parts[0].Trim());
        }
    }
}
