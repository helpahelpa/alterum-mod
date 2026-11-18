using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AlterumModelSwap.Contracts;

namespace AlterumModelSwap.Hot;

// The snapshot chain, and the state machine that ties the two halves together.
//
//   pipeline -> dl.mp3 (growing)          (YtDlpPipeline, background threads)
//                |
//                +-- cut every ~30 s of audio ---> snap_N.mp3 (cumulative prefix, off the main thread)
//                                                     |
//                                                     +-- MusicStream.createStream (FMOD)
//
// A snapshot is a *prefix of the same track*, so an absolute track position is a position in every
// snapshot and no frame-aligned segmentation is needed. The engine is handed the next snapshot
// ~0.6 s before the current one runs out and is started at the position the old one had reached, so
// `Channel.getPosition` never steps backwards or jumps (every switch logs its measured
// discontinuity).
//
// Only the newest two snapshots are kept. The one FMOD has open cannot be deleted on Windows (the
// stream holds it read-only), so deletion always skips the open one and is retried on the next cut.
//
// TWO AXES, NOT ONE. The mixer's playback state (Idle/Preflight/Buffering/Playing/Paused/Failed/
// Cancelled) and the producer's progress (`_producerDone`: the finished file is in the cache) are
// independent. A finished download says nothing about playback: it may still be playing, it may be
// paused, and it may never have produced a sound. Conflating them is what made the panel show
// "Loading" for a finished track and made a completed download start a paused track.
internal sealed class MusicStreamHost
{
    internal const string Tag = "[music:host]";
    internal const string TagSnap = "[music:snap]";

    internal const long CutEveryBytes = 480 * 1024;    // ~30 s of 128 kbps audio
    internal const long SwitchLeadMs = 600;            // switch this early, matches the measured open
    internal const long SwitchMarginMs = 1500;         // the next snapshot must reach past the playhead
    private const int MaxSnapshots = 2;
    private const float StallSeconds = 0.30f;
    // A handoff the transport refuses is retried, not treated as the end of the request: 1 s, 2 s,
    // 4 s, then every 8 s, until it succeeds or the request is cancelled.
    private const float HandoffRetrySeconds = 1f;
    private const float HandoffRetryMaxSeconds = 8f;

    // A correction is only committed when the channel reads back this close to the target. A stream
    // seek can land on a frame boundary (~26 ms), and the transport claim is a <=750 ms audio
    // difference, so this is the honest ceiling for "the seek landed where it was asked to".
    internal const long ReadbackToleranceMs = 300;

    // Playback only. `Complete` is gone on purpose: producer completion is `_producerDone`.
    internal enum Phase { Idle, Preflight, Buffering, Playing, Paused, Failed, Cancelled }

    private IHostContext _log;
    private MusicStream _engine;
    private YtDlpPipeline _pipe;
    private string _url = "";
    private string _source = "local";
    private Phase _phase = Phase.Idle;
    private string _lastError = "";
    private bool _errorVisible;       // the error label is ours even when audio kept playing
    private bool _announce;
    private float _stallSince = -1f;
    private float _nextTickLogAt;

    private readonly Stopwatch _clock = Stopwatch.StartNew();   // restarted per request
    private int _snapNewest;            // newest generation cut
    private int _snapOpen;              // generation the engine has open
    private string _snapOpenPath;
    private long _nextCutBytes;

    // Producer / cache side. Independent of `_phase`.
    private bool _producerDone;         // the finished file is in the cache and final
    private string _cachedPath;         // the final playable file for this request
    private string _cachedTitle = "";
    private long _cachedDurationMs;     // 0 when the source did not declare a duration
    private string _deferredHandoff;    // finished file waiting for a resume (paused handoff)
    // A handoff the transport REJECTED. The finished file is final and the old prefix channel is kept
    // alive; this state is what makes the handoff retryable instead of terminal.
    private string _handoffPath;
    private int _handoffFails;
    private float _nextHandoffAt;

    // one cut in flight at a time; the worker owns the copy
    private readonly object _cutGate = new object();
    private readonly ManualResetEventSlim _cutGo = new ManualResetEventSlim(false);
    private Thread _cutter;
    private bool _cutBusy, _cutWant;
    private int _cutGen;
    private long _cutLen;
    // Everything a queued cut needs is captured when it is queued. The worker never reads the live
    // pipeline: by the time it runs, the request it was queued for may already be retired, and a cut
    // that copied the *next* track's bytes (or published its generation into it) is the defect this
    // exists to prevent.
    private string _cutSrc, _cutDst;
    private long _cutToken;

    // Bumped by every Play/Stop/Cancel. Queued work carries the token it was queued under and only
    // publishes or cleans up while that token is still current.
    private long _reqToken;

    // Working directories of requests that are over. The cutter deletes one only after that request's
    // own worker threads have stopped, because those threads write into it until they are gone.
    private sealed class RetiredRequest
    {
        public string Dir;
        public string Why;
        public YtDlpPipeline Pipe;      // null = no writer left to wait for
    }
    private readonly object _retireGate = new object();
    private readonly List<RetiredRequest> _retire = new List<RetiredRequest>();

    // A generation owns exactly one snapshot worker. This is what ends it: without it the loop below
    // is `while (true)`, and every installed generation leaks a waking thread that retains its host
    // and its assembly (so the assembly is never collectable after a hot reload).
    private volatile bool _shutdown;
    private const int CutterJoinMs = 4000;          // what Dispose waits for the join
    private const int ShutdownDrainMs = 250;        // per-thread wait of the final cleanup pass
    private const int ShutdownDrainBudgetMs = 250;  // TOTAL of the final cleanup pass, not per request

    private string _pendingCached;
    private long _startAtMs;                     // where the first channel starts (a follower keeps updating it)
    private bool _startPaused;                   // ...and whether it is created paused (a paused host)
    private float _nextTargetLogAt;
    private long _tPlayMs = -1;
    private int _ticks;

    internal MusicStreamHost(IHostContext log)
    {
        _log = log;
        _engine = new MusicStream();
        _cutter = new Thread(CutterLoop) { IsBackground = true, Name = "alterum-music-snapshots" };
        _cutter.Start();
    }

    // ================================================================ state for the UI / console

    internal Phase Current => _phase;

    // The panel belongs to us while a request of ours is the thing it describes. Failed and
    // Cancelled still own it: the failure sentence and the STOP status have to be visible until the
    // next request or a hand-back to the game's player.
    internal bool OwnsPanel =>
        _phase == Phase.Preflight || _phase == Phase.Buffering || _phase == Phase.Playing ||
        _phase == Phase.Paused || _phase == Phase.Failed || _phase == Phase.Cancelled;

    // Playback (as opposed to presentation): what the network layer and pause/resume act on.
    internal bool OwnsPlayback =>
        _phase == Phase.Preflight || _phase == Phase.Buffering || _phase == Phase.Playing ||
        _phase == Phase.Paused;

    internal bool Playing => _phase == Phase.Playing;
    internal bool Paused => _phase == Phase.Paused;
    internal bool ProducerDone => _producerDone;
    internal bool HasPlayableChannel => _engine != null && _engine.Playable;
    internal string Url => _url;
    internal string LastError => _lastError;
    internal long PlayStartMs => _tPlayMs;
    internal long PositionMs => _engine?.PositionMs ?? 0;
    internal long SnapshotSwitches => _engine?.Switches ?? 0;
    internal long MaxSwitchGapMs => _engine?.MaxSwitchGapMs ?? 0;
    internal int SnapNewest => _snapNewest;
    internal int SnapOpen => _snapOpen;
    internal YtDlpPipeline Pipeline => _pipe;
    internal MusicStream Engine => _engine;
    internal string CachedPath => _cachedPath;
    internal string DeferredHandoff => _deferredHandoff;

    // A request that is still growing on disk: the panel must present it as such (elapsed / STREAM,
    // buffer fill on the slider, no seeking), not as a finished track.
    internal bool IsGrowing => !_producerDone;

    internal string DisplayTitle
    {
        get
        {
            var t = _pipe?.TrackInfo?.Title;
            if (!string.IsNullOrEmpty(t)) return t;
            return _cachedTitle ?? "";
        }
    }

    // Real track length when the source told us, else what the open file holds. 0 = unknown.
    internal long TotalMs
    {
        get
        {
            if (_cachedDurationMs > 0) return _cachedDurationMs;
            var info = _pipe?.TrackInfo;
            long declared = info == null ? 0 : (long)(info.DurationSeconds * 1000);
            return declared;
        }
    }

    internal long LengthMs
    {
        get
        {
            long total = TotalMs;
            if (total > 0) return total;
            return _engine?.LengthMs() ?? 0;
        }
    }

    // How much of the track is on disk. The growing panel shows this as the slider fill: it is
    // buffer availability, not playback position. Before a channel exists it is what the producer
    // has written; afterwards it is what the open snapshot actually holds.
    internal double BufferFraction
    {
        get
        {
            long total = TotalMs;
            if (total <= 0) return 0;
            long onDisk = _engine != null && _engine.HasChannel
                ? _engine.SnapshotBytes / YtDlpPipeline.BytesPerMs          // prefix FMOD can read, as ms
                : (_pipe?.BytesOnDisk ?? 0) / YtDlpPipeline.BytesPerMs;     // produced so far, as ms
            double f = (double)onDisk / total;
            return f < 0 ? 0 : (f > 1 ? 1 : f);
        }
    }

    // The three actionable controls the panel may offer, exactly one glyph at a time. Each answers
    // "can this be done right now": never "did a channel/request ever exist" (A3) and never "is
    // the download finished" (A4).
    internal bool CanPlay => _phase == Phase.Paused && HasPlayableChannel;
    internal bool CanPause => _phase == Phase.Playing && HasPlayableChannel;
    // A completion that arrives while paused deliberately keeps only the prefix snapshot open (the
    // finished file is applied on resume, `_deferredHandoff`), so at that instant the track past
    // that prefix is not on offer and a seek there cannot be fulfilled.
    internal bool CanSeek => !IsGrowing && HasPlayableChannel && TotalMs > 0 && _deferredHandoff == null;

    // What the music player's own status line should read. Short words only: the label is a narrow
    // strip and the longest one ("PAUSED") has to stay on one line.
    internal string StatusText()
    {
        switch (_phase)
        {
            case Phase.Preflight: return "READ";
            case Phase.Buffering: return "BUF";
            case Phase.Paused: return "PAUSED";
            case Phase.Playing: return _producerDone ? "CACHE" : "STREAM";
            case Phase.Failed: return "FAIL";
            case Phase.Cancelled: return "STOP";
            default: return "";
        }
    }

    // PlayerState: 0 Idle, 1 Loading, 2 Playing, 3 Paused.
    internal int UiState()
    {
        switch (_phase)
        {
            case Phase.Preflight:
            case Phase.Buffering: return 1;
            case Phase.Playing: return 2;
            case Phase.Paused: return 3;
            default: return 0;
        }
    }

    internal string ErrorText() => _errorVisible ? (_lastError ?? "") : "";

    // ================================================================ entry points

    // Returns false when the link is not ours (direct audio, or empty) so the game's own player
    // handles it unchanged.
    //
    // NOT called directly by an entry point: every mod-side start goes through
    // LobbyMusicHooks.StartRequest, which stops the game's own track and clears the panel before
    // this claims the transport. A second caller of Play/PlayAt would leave two playback owners.
    internal bool Play(string url, string source)
    {
        url = (url ?? "").Trim();
        var kind = YtDlpPipeline.Classify(url);
        if (kind != YtDlpPipeline.UrlKind.Page)
        {
            _log?.LogInfo($"{Tag} not a yt-dlp url (kind={kind}) — the game's own player keeps it");
            return false;
        }

        Stop("new request");
        _clock.Restart();                            // every time mark below is "since the request"
        _url = url;
        _source = source ?? "local";
        _lastError = "";
        _errorVisible = false;
        _snapNewest = 0; _snapOpen = 0; _snapOpenPath = null; _nextCutBytes = 0;
        _tPlayMs = -1; _stallSince = -1f; _startAtMs = 0; _startPaused = false;
        _nextTargetLogAt = 0f;
        _producerDone = false; _cachedPath = null; _cachedTitle = ""; _cachedDurationMs = 0;
        _deferredHandoff = null;
        _handoffPath = null; _handoffFails = 0; _nextHandoffAt = 0f;

        string cached = FindCached(url, out string cachedTitle, out long cachedDurationMs);
        if (cached != null)
        {
            // The ceiling is enforced on THIS path too. A cache hit spawns no producer and writes
            // nothing, so it cannot grow the cache - but it is the one request path that used to skip
            // the checks entirely, which is what let an over-budget cache stay over until the next
            // download. Everything removable is evicted here (the hit itself is never a candidate),
            // and if the cache is still over afterwards the hit plays anyway while a loud line says
            // why no download would be admitted: the residue is in use, and playing it adds no bytes.
            long over = YtDlpPipeline.EnforceCacheCeiling(cached, null, _log, out long cacheTotal);
            if (over > 0)
                _log?.LogWarning($"{Tag} CACHE HIT while the cache is {cacheTotal / (1024 * 1024)}MB, " +
                                 $"{over / (1024 * 1024)}MB over its {YtDlpPipeline.CacheBudgetBytes / (1024 * 1024)}MB " +
                                 "ceiling: everything removable has been evicted. This play adds no bytes; no new " +
                                 "download is admitted until the cache is under the ceiling");
            _phase = Phase.Buffering;
            _pendingCached = cached;
            _producerDone = true;
            _cachedPath = cached;
            _cachedTitle = cachedTitle ?? "";
            _cachedDurationMs = cachedDurationMs;
            _log?.LogInfo($"{Tag} CACHE HIT '{url}' -> '{cached}' ({new FileInfo(cached).Length} bytes, " +
                          $"title='{Short(_cachedTitle, 60)}' duration={_cachedDurationMs}ms); " +
                          "no yt-dlp and no ffmpeg are spawned");
            return true;
        }

        _pipe = new YtDlpPipeline();
        _phase = Phase.Preflight;
        _log?.LogInfo($"{Tag} PLAY url='{Short(url, 120)}' source={_source}; tools='{YtDlpPipeline.ToolsDir}' " +
                      $"(from {YtDlpPipeline.GameRootSource}) cache='{YtDlpPipeline.CacheRoot}'");
        _pipe.Begin(_log, url);
        return true;
    }

    // Follow a session track: start the same canonical URL at the given absolute position.
    // Same rule as Play: LobbyMusicHooks.StartRequest is the only entry point.
    //
    // `startPaused` is what makes a follower's first sound respect a paused host: the channel is
    // created AND verified paused (MusicStream.Start -> VerifyTransport), so not one frame of audio
    // is emitted before the pause. The position is only a seed - LobbyMusicNet keeps calling
    // `SetStartTarget` while the anchors move, and the start uses the value at that moment.
    internal bool PlayAt(string url, long startAtMs, string source, bool startPaused = false)
    {
        if (!Play(url, source)) return false;
        _startAtMs = Math.Max(0, startAtMs);
        _startPaused = startPaused;
        _log?.LogInfo($"{Tag} playAt url='{Short(url, 90)}' anchor={startAtMs}ms paused={startPaused} source={source}");
        _announce = true;
        return true;
    }

    // The desired first start, updated by the follower on every anchor. Only a request that has no
    // channel yet takes it: once there is a channel the position is corrected through the transport
    // (seek/pause/resume), never by pretending the start was still pending.
    internal void SetStartTarget(long startAtMs, bool paused, float now)
    {
        if (_engine != null && _engine.HasChannel) return;
        if (_phase != Phase.Preflight && _phase != Phase.Buffering && _phase != Phase.Paused) return;
        startAtMs = Math.Max(0, startAtMs);
        bool moved = startAtMs != _startAtMs || paused != _startPaused;
        _startAtMs = startAtMs;
        _startPaused = paused;
        if (!moved || now < _nextTargetLogAt) return;
        _nextTargetLogAt = now + 2f;
        _log?.LogInfo($"{Tag} start target -> {startAtMs}ms paused={paused} coverage={CoverageMs}ms " +
                      $"phase={_phase} (a follower updating the pending start)");
    }

    // How much of the track this machine can already reach: the open snapshot (all snapshots are
    // prefixes of the same track, so this is a position that is playable), the finished file, or what
    // the producer has written so far. Coverage, not position: the reconcile loop needs "can I go
    // there yet", so it can hold a pending target instead of seeking into audio that is not here.
    internal long CoverageMs
    {
        get
        {
            long best = _engine != null && _engine.HasChannel ? _engine.LengthMs() : 0;
            if (_producerDone && _cachedPath != null)
            {
                try { best = Math.Max(best, new FileInfo(_cachedPath).Length / YtDlpPipeline.BytesPerMs); }
                catch { }
            }
            if (best <= 0)
            {
                var path = _snapNewest > 0 ? SnapPath(_snapNewest) : null;
                if (path != null)
                {
                    try { best = new FileInfo(path).Length / YtDlpPipeline.BytesPerMs; } catch { }
                }
                if (best <= 0) best = (_pipe?.BytesOnDisk ?? 0) / YtDlpPipeline.BytesPerMs;
            }
            return best;
        }
    }

    // A seek is committed only when the channel reads back at the target. `MusicStream.Seek` verifies
    // the readback itself; this is the same contract one level up, so a rejected seek cannot be
    // reported as a success by either layer.
    internal bool SeekVerified(long absoluteMs, out long actualMs)
    {
        bool ok = Seek(absoluteMs);
        actualMs = PositionMs;
        if (!ok) return false;
        long delta = actualMs - absoluteMs;
        if (Math.Abs(delta) <= ReadbackToleranceMs) return true;
        _log?.LogWarning($"{Tag} seek readback mismatch at {absoluteMs}ms: channel says {actualMs}ms " +
                         $"(delta={delta}ms, tolerance={ReadbackToleranceMs}ms); not committed");
        return false;
    }

    // The cache key is extractor+id, which only the pre-flight knows, so the lookup is by URL stored
    // next to each finished track. The metadata file is also where the title and the real duration
    // come from when a cached track is replayed: without them the panel would keep whatever the
    // previous track put in those widgets.
    private string FindCached(string url, out string title, out long durationMs)
    {
        title = ""; durationMs = 0;
        try
        {
            if (!Directory.Exists(YtDlpPipeline.DoneRoot)) return null;
            foreach (var meta in Directory.GetFiles(YtDlpPipeline.DoneRoot, "meta.txt", SearchOption.AllDirectories))
            {
                string first = null;
                try { using var f = new StreamReader(meta); first = f.ReadLine(); } catch { }
                if (first == null || !first.StartsWith("url=", StringComparison.Ordinal)) continue;
                if (!string.Equals(first.Substring(4).Trim(), url, StringComparison.Ordinal)) continue;
                var mp3 = Path.Combine(Path.GetDirectoryName(meta), "track.mp3");
                if (!File.Exists(mp3) || new FileInfo(mp3).Length <= YtDlpPipeline.ReadyBytes) continue;
                ReadMeta(meta, out title, out durationMs);
                return mp3;
            }
        }
        catch (Exception e) { _log?.LogWarning($"{Tag} cache scan failed: {e.Message}"); }
        return null;
    }

    private static void ReadMeta(string metaPath, out string title, out long durationMs)
    {
        title = ""; durationMs = 0;
        try
        {
            foreach (var line in File.ReadAllLines(metaPath))
            {
                if (line.StartsWith("title=", StringComparison.Ordinal))
                    title = line.Substring(6).Trim();
                else if (line.StartsWith("duration=", StringComparison.Ordinal))
                {
                    if (TryParseSeconds(line.Substring(9).Trim(), out double secs) && secs > 0)
                        durationMs = (long)(secs * 1000);
                }
            }
        }
        catch { }
    }

    // One explicit culture on the way back in: invariant first, then this machine's culture and the
    // comma-decimal spelling an older build may already have written, so an existing cache entry
    // keeps its declared duration instead of silently becoming unseekable.
    private static bool TryParseSeconds(string raw, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrEmpty(raw)) return false;
        const NumberStyles style = NumberStyles.Float;
        if (double.TryParse(raw, style, CultureInfo.InvariantCulture, out seconds)) return true;
        if (double.TryParse(raw, style, CultureInfo.CurrentCulture, out seconds)) return true;
        return raw.IndexOf(',') >= 0 &&
               double.TryParse(raw.Replace(',', '.'), style, CultureInfo.InvariantCulture, out seconds);
    }

    // Commit only on FMOD's report. A refused pause keeps the panel in Playing, so the glyph stays
    // "pause" and the next click can try again.
    internal bool Pause()
    {
        if (!_engine.HasChannel)
        {
            _log?.LogInfo($"{Tag} pause ignored: no playable channel (phase={_phase})");
            return false;
        }
        if (!_engine.Pause())
        {
            _log?.LogWarning($"{Tag} pause NOT committed: FMOD refused (phase stays {_phase}) at {PositionMs}ms");
            return false;
        }
        _phase = Phase.Paused;
        _announce = true;
        _log?.LogInfo($"{Tag} paused at {PositionMs}ms (producer done={_producerDone}; the download keeps " +
                      $"running, still growing={IsGrowing})");
        LobbyMusicNet.NoteTransport("pause");
        return true;
    }

    internal bool Resume()
    {
        if (!_engine.HasChannel)
        {
            _log?.LogInfo($"{Tag} resume ignored: no playable channel (phase={_phase})");
            return false;
        }
        // A download that finished while paused left the open snapshot alone. Resuming is the
        // moment the finished file is allowed to take over — unpaused, because the user just asked.
        if (_deferredHandoff != null)
        {
            var path = _deferredHandoff;
            _deferredHandoff = null;
            long pos = PositionMs;
            if (HandoffTo(path, pos, false, "resume-after-pause"))
            {
                _phase = Phase.Playing;
                _announce = true;
                RetireStreamDir(_pipe?.TrackDir, "resume-after-pause", _pipe);
                _log?.LogInfo($"{Tag} resumed at {pos}ms through the finished file (deferred handoff applied)");
                LobbyMusicNet.NoteTransport("resume");
                return true;
            }
            _deferredHandoff = path;
            // The finished file is still the track: keep the prefix playing, but make the handoff
            // pending again so this rejection cannot become terminal either (the retry is driven by
            // the completion tick, and the prefix is never reported as the whole track).
            _handoffPath = path;
            _handoffFails++;
            _nextHandoffAt = 0f;                    // retry on the next tick, then back off there
            _log?.LogWarning($"{Tag} deferred handoff failed on resume; falling back to the open snapshot " +
                             "and retrying the handoff (the prefix is not the track)");
        }
        if (!_engine.Resume())
        {
            _log?.LogWarning($"{Tag} resume NOT committed: FMOD refused (phase stays {_phase}) at {PositionMs}ms");
            return false;
        }
        if (_phase == Phase.Paused) _phase = Phase.Playing;
        _announce = true;
        _log?.LogInfo($"{Tag} resumed at {PositionMs}ms (producer done={_producerDone})");
        LobbyMusicNet.NoteTransport("resume");
        return true;
    }

    internal bool SetVolume(float v)
    {
        if (_engine == null) return false;
        bool ok = _engine.SetVolume(v);
        _log?.LogInfo($"{Tag} volume {v:0.###} -> {ok} (phase={_phase})");
        return ok;
    }

    // Forward seeks past the end of the current snapshot first move to the newest snapshot that
    // covers them; the position itself is what makes the switch invisible. While paused the open
    // snapshot is deliberately kept, so a paused seek stays inside it.
    internal bool Seek(long absoluteMs)
    {
        if (!_engine.HasChannel) return false;
        if (_phase != Phase.Paused)
        {
            if (absoluteMs > _engine.LengthMs() - 500 && _snapNewest > _snapOpen) SwitchTo(_snapNewest, "seek", absoluteMs);
        }
        else if (_deferredHandoff != null)
        {
            // Paused completion: only the prefix snapshot is open, so a target past its end is not a
            // position this channel can hold. Refusing keeps the playhead honest; the resume applies
            // the finished file, after which the whole track is seekable again.
            long reach = _engine.LengthMs();
            if (reach > 0 && absoluteMs > reach)
            {
                _log?.LogInfo($"{Tag} seek({absoluteMs}ms) refused: only the {reach}ms prefix snapshot is open " +
                              "while paused; the finished file is applied on resume");
                return false;
            }
        }
        bool ok = _engine.Seek(absoluteMs);
        _announce = true;
        if (ok) LobbyMusicNet.NoteTransport("seek");
        return ok;
    }

    internal void Stop(string why)
    {
        if (_phase != Phase.Idle)
            _log?.LogInfo($"{Tag} stop ('{why}') from phase={_phase} pos={PositionMs}ms done={_producerDone}");
        // Any cut still queued or in flight is now stale, and the request's working directory may only
        // be deleted after its own pumps have stopped writing into it - so the directory is handed to
        // the cutter instead of being cleaned here.
        BumpRequestToken();
        var retired = _pipe;
        _pipe = null;
        try { retired?.Cancel(why); } catch { }
        try { retired?.Dispose(); } catch { }
        try { _engine?.Stop(); } catch { }
        // After the engine let go of its snapshot: the working directory is deleted by the cutter, and
        // only once this request's pumps have stopped writing into it.
        if (retired?.TrackDir != null) RetireStreamDir(retired.TrackDir, why, retired);
        _pendingCached = null;
        _deferredHandoff = null;
        _handoffPath = null; _handoffFails = 0; _nextHandoffAt = 0f;
        _producerDone = false;
        _errorVisible = false;
        _cachedPath = null; _cachedTitle = ""; _cachedDurationMs = 0;
        bool owned = _phase != Phase.Idle;
        // Always Idle: a stop is a release (new request, panel close, or handing the panel back), so
        // a previous failure must not keep the panel claimed by us.
        _phase = Phase.Idle;
        // `_url` is deliberately NOT cleared: it is the relay identity of the last claim, and the
        // "host stopped this track" frame is published by URL (LobbyMusicNet.Publish). It is never
        // read as ownership - see Cancel below.
        _snapNewest = 0; _snapOpen = 0; _snapOpenPath = null; _nextCutBytes = 0;
        if (owned) { _announce = true; LobbyMusicNet.NoteTransport("stop"); }
    }

    internal void Cancel(string why)
    {
        // A host that never had a request must not claim the panel just to show STOP over whatever
        // the game's player is doing. "A request" is the live state below - never a URL left over
        // from a request that was already stopped and handed back.
        bool had = _phase != Phase.Idle || _engine.HasChannel || _pipe != null;
        if (!had)
        {
            _log?.LogInfo($"{Tag} CANCEL '{why}' ignored: nothing of ours to stop (phase={_phase})");
            return;
        }
        _log?.LogInfo($"{Tag} CANCEL '{why}' phase={_phase} stage={_pipe?.Current} pos={PositionMs}ms");
        BumpRequestToken();
        try { _pipe?.Cancel(why); } catch { }
        try { _engine?.Stop(); } catch { }
        _phase = Phase.Cancelled;
        _deferredHandoff = null;
        _handoffPath = null; _handoffFails = 0;          // a cancelled request is over: no retries
        _errorVisible = false;
        _snapOpenPath = null;                          // nothing holds a snapshot now
        if (_pipe?.TrackDir != null) RetireStreamDir(_pipe.TrackDir, "cancel", _pipe);
        _announce = true;
        _log?.LogInfo($"{Tag} stopped: phase={_phase} (status='{StatusText()}'), producer cancelled, files released");
        LobbyMusicNet.NoteTransport("cancel");
    }

    internal bool TakeAnnounce()
    {
        if (!_announce) return false;
        _announce = false;
        return true;
    }

    // ================================================================ tick

    internal void Tick(float now)
    {
        if (_shutdown) return;                 // a disposed generation must not request cuts or log
        _ticks++;
        try { TickPipeline(now); }
        catch (Exception e) { _log?.LogError($"{Tag} pipeline tick threw {e.GetType().Name}: {e.Message}"); }
        try { ServicePlayback(now); }
        catch (Exception e) { _log?.LogError($"{Tag} playback tick threw {e.GetType().Name}: {e.Message}"); }
        try { CheckCompletion(now); }
        catch (Exception e) { _log?.LogError($"{Tag} completion tick threw {e.GetType().Name}: {e.Message}"); }

        if (now >= _nextTickLogAt)
        {
            _nextTickLogAt = now + 1f;
            if (OwnsPlayback) ReportProgress();
        }
    }

    private void TickPipeline(float now)
    {
        if (_pendingCached != null)
        {
            var path = _pendingCached;
            _pendingCached = null;
            var fi = new FileInfo(path);
            long at = Math.Min(_startAtMs, Math.Max(0, (long)fi.Length / YtDlpPipeline.BytesPerMs - 2000));
            if (_engine.Start(path, fi.Length, at, _startPaused, out string err))
            {
                _snapOpenPath = path;
                _phase = _startPaused ? Phase.Paused : Phase.Playing;
                _announce = true;
                _tPlayMs = _clock.ElapsedMilliseconds;
                _log?.LogInfo($"{Tag} cache START in {_tPlayMs}ms (no network, no process) " +
                              $"'{Path.GetFileName(path)}' at={at}ms paused={_startPaused} " +
                              $"title='{Short(_cachedTitle, 60)}' duration={_cachedDurationMs}ms");
                LobbyMusicNet.NoteTransport(_startPaused ? "start-paused" : "start-playing");
            }
            else
            {
                _phase = Phase.Failed; _lastError = err;
                _log?.LogError($"{Tag} cache start failed: {err}");
            }
            return;
        }

        var p = _pipe;
        if (p == null) return;

        if (p.Failed)
        {
            if (!_errorVisible)
            {
                _lastError = p.Message;
                _errorVisible = true;
                _announce = true;
                _log?.LogWarning($"{Tag} pipeline failed [{p.RefusalKind}]: {p.Message}");
                // If audio is already playable, keep playing it: a truncated mp3 plays to its own
                // end and stops cleanly, so the user hears the part that did arrive (measured: a CDN
                // 403 after 997 KB still left 62 s of audio). The playback phase then still says
                // what is true - it is playing - because taking it away would remove the only
                // control that can pause the audio that is audible.
                if (_engine.HasChannel)
                    _log?.LogInfo($"{Tag} keeping the stream alive at {_engine.PositionMs}ms " +
                                  $"(snapshot {(long)_engine.LengthMs()}ms of audio already on disk); " +
                                  $"phase stays {_phase}, only the error text changes");
                else
                {
                    _phase = Phase.Failed;
                    try { _engine.Stop(); } catch { }
                    LobbyMusicNet.NoteTransport("failed");
                }
            }
            return;
        }
        if (p.Current == YtDlpPipeline.Stage.Cancelled)
        {
            if (_phase != Phase.Cancelled) { _phase = Phase.Cancelled; _announce = true; }
            return;
        }
        if (p.Current == YtDlpPipeline.Stage.Preflight) return;

        if (p.Current == YtDlpPipeline.Stage.Downloading || p.Current == YtDlpPipeline.Stage.Complete)
        {
            // Producer completion must not move the playback phase: only the pre-flight → buffering
            // transition does, and never backwards out of Playing/Paused.
            if (_phase == Phase.Preflight) { _phase = Phase.Buffering; LobbyMusicNet.NoteTransport("buffering"); }
            if (_producerDone) return;                       // finished file already finalised
            long bytes = p.BytesOnDisk;
            if (_snapNewest == 0 && bytes >= YtDlpPipeline.ReadyBytes && !_cutBusy && _phase == Phase.Buffering)
                RequestCut("first");
            else if (_snapNewest > 0 && !_cutBusy && bytes >= _nextCutBytes)
                RequestCut("periodic");
        }
    }

    // ---- playback: start / switch / stall

    private void ServicePlayback(float now)
    {
        var e = _engine;
        if (e == null) return;

        // Pause semantics: the consumer's playhead and the displayed elapsed time are frozen. The
        // producer keeps downloading, but no channel is started or replaced while paused: the open
        // snapshot stays exactly as it is until Resume.
        if (_phase == Phase.Paused) return;

        if (!e.HasSound)
        {
            if (_phase != Phase.Buffering) return;
            // The producer may have finished before the first sound. In that case the whole track is
            // already the cache file, and starting from it is better than from a prefix.
            if (_producerDone && _cachedPath != null && File.Exists(_cachedPath))
            {
                long bytes = new FileInfo(_cachedPath).Length;
                long total = bytes / YtDlpPipeline.BytesPerMs;
                long at = Math.Min(_startAtMs, Math.Max(0, total - 2000));
                if (!_engine.Start(_cachedPath, bytes, at, _startPaused, out string cerr))
                {
                    _log?.LogError($"{Tag} first start from the finished file failed: {cerr}");
                    return;
                }
                _snapOpenPath = _cachedPath;
                _snapOpen = 0; _snapNewest = 0;
                _phase = _startPaused ? Phase.Paused : Phase.Playing;
                _announce = true;
                _tPlayMs = _clock.ElapsedMilliseconds;
                _log?.LogInfo($"{Tag} FIRST SOUND at t={_tPlayMs}ms from the finished file " +
                              $"'{Path.GetFileName(_cachedPath)}' at={at}ms paused={_startPaused}; {e.BusState}");
                LobbyMusicNet.NoteTransport(_startPaused ? "start-paused" : "start-playing");
                RetireStreamDir(_pipe?.TrackDir, "started-from-cache", _pipe);
                return;
            }
            if (_snapNewest == 0) return;
            string path = SnapPath(_snapNewest);
            if (path == null || !File.Exists(path)) return;
            long snapBytes = new FileInfo(path).Length;
            long snapMs = snapBytes / YtDlpPipeline.BytesPerMs;
            if (_startAtMs > snapMs - 2000)
            {
                // The anchor is past what this prefix holds: wait for a snapshot that covers it.
                if (!_cutBusy) RequestCut("anchor-not-covered");
                return;
            }
            if (!_engine.Start(path, snapBytes, _startAtMs, _startPaused, out string err))
            {
                _log?.LogError($"{Tag} first start failed: {err}");
                RequestCut("start-failed");
                return;
            }
            _snapOpen = _snapNewest;
            _snapOpenPath = path;
            _nextCutBytes = snapBytes + CutEveryBytes;
            _phase = _startPaused ? Phase.Paused : Phase.Playing;
            _announce = true;
            _tPlayMs = _clock.ElapsedMilliseconds;
            _log?.LogInfo($"{Tag} FIRST SOUND at t={_tPlayMs}ms from '{Path.GetFileName(path)}' ({snapBytes / 1024}KB of " +
                          $"~{(long)((_pipe?.TrackInfo?.DurationSeconds ?? 0) * 1000)}ms track; " +
                          $"dl={_pipe?.BytesOnDisk / 1024}KB so far) at={_startAtMs}ms paused={_startPaused} {e.BusState}");
            LobbyMusicNet.NoteTransport(_startPaused ? "start-paused" : "start-playing");
            return;
        }

        long pos = e.PositionMs;
        long snapLen = e.LengthMs();
        bool nearEnd = snapLen > 0 && pos >= snapLen - SwitchLeadMs;

        bool starvingNow = false;
        if (e.Starving(out int state, out uint buffered, out bool starving, out bool diskBusy))
        {
            starvingNow = starving || state == 4;                // 4 = OPENSTATE.BUFFERING
            if (!starvingNow) _stallSince = -1f;
            else if (_stallSince < 0f) _stallSince = now;
        }

        if (starvingNow && _stallSince > 0f && now - _stallSince >= StallSeconds)
        {
            _log?.LogWarning($"{Tag} STARVING at {pos}ms (buffered={buffered}% diskBusy={diskBusy}); cutting now");
            _stallSince = -1f;
            RequestCut("stall");
        }

        if (nearEnd && _snapNewest > _snapOpen)
        {
            SwitchTo(_snapNewest, "end-of-snapshot", pos);
            return;
        }
        if (nearEnd && !_cutBusy && !_producerDone) RequestCut("end-of-snapshot");

        if (!starvingNow && _phase == Phase.Playing && !e.Playing())
        {
            if (_producerDone)
            {
                if (_handoffPath != null)
                {
                    // The channel that just stopped is still the OLD PREFIX, not the finished track:
                    // its length must never be reported as the track's length and the request is not
                    // over. Retry the handoff (it keeps its own backoff, so a chain of rejections
                    // cannot spin) and keep the request alive.
                    _log?.LogWarning($"{Tag} the prefix channel stopped at {pos}ms while the handoff to the " +
                                     "finished file is still pending; the prefix is not the track, retrying");
                    RetryHandoff(now);
                    return;
                }
                // The finished file is everything this channel can hold: no snapshot follows it.
                long full = e.LengthMs();
                if (full <= 0 || pos >= full - 1000)
                {
                    _log?.LogInfo($"{Tag} PLAYBACK ENDED at {pos}ms of {full}ms (producer done); the track is " +
                                  "stopped, not paused and not loading");
                    _announce = true;
                    Cancel("track finished");
                }
                else
                {
                    _log?.LogWarning($"{Tag} the finished track's channel stopped at {pos}ms of {full}ms; " +
                                     "nothing follows it (phase kept, controls stay honest)");
                }
                return;
            }
            // The truncated snapshot was played to its own end and the channel stopped cleanly.
            if (_snapNewest > _snapOpen) SwitchTo(_snapNewest, "channel-ended", pos);
            else if (!_cutBusy) RequestCut("channel-ended");
        }
    }

    private void SwitchTo(int gen, string why, long pos)
    {
        string path = SnapPath(gen);
        if (path == null || !File.Exists(path)) return;
        long bytes = new FileInfo(path).Length;
        // Refuse a snapshot that does not reach past the playhead: switching there would jump.
        long lenMs = bytes / YtDlpPipeline.BytesPerMs;
        if (pos + SwitchMarginMs > lenMs)
        {
            if (!_cutBusy) RequestCut("need-more-ahead");
            return;
        }
        long t0 = Stopwatch.GetTimestamp();
        if (!_engine.Switch(path, bytes, pos, false, out long gap, out string err))
        {
            _log?.LogWarning($"{Tag} switch to gen={gen} failed ({err}); requesting another cut");
            RequestCut("switch-failed");
            return;
        }
        _snapOpen = gen;
        _snapOpenPath = path;
        _nextCutBytes = Math.Max(bytes + CutEveryBytes, _nextCutBytes);
        long costMs = (long)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        _log?.LogInfo($"{Tag} SWITCH -> gen={gen} '{Path.GetFileName(path)}' {bytes / 1024}KB at={pos}ms " +
                      $"discontinuity={gap}ms cost={costMs}ms why={why} switches={_engine.Switches}");
        DeleteSnapshots(_pipe?.TrackDir, gen, _snapOpenPath);
    }

    // ---- snapshot cutting

    private string SnapPath(int gen)
    {
        var dir = _pipe?.TrackDir;
        return dir == null || gen <= 0 ? null : Path.Combine(dir, $"snap_{gen:D4}.mp3");
    }

    // Every queued worker compares its captured token against this value under `_cutGate`; bumping it
    // under the same gate is what makes "this work belongs to a request that is over" decidable.
    private void BumpRequestToken()
    {
        lock (_cutGate) _reqToken++;
    }

    private void RequestCut(string why)
    {
        if (_shutdown) return;
        var p = _pipe;
        if (p == null) return;
        string dir = p.TrackDir, src = p.DlPath;
        if (dir == null || src == null) return;
        int gen;
        long bytes;
        lock (_cutGate)
        {
            if (_cutBusy) return;
            bytes = p.BytesOnDisk;
            if (bytes < 32 * 1024) return;
            gen = _snapNewest + 1;
            _cutBusy = true;
            _cutWant = true;
            _cutGen = gen;
            _cutLen = bytes;
            // Immutable source and destination, and the request token they belong to: the worker
            // copies exactly these, whatever `_pipe` says by the time it gets there.
            _cutSrc = src;
            _cutDst = Path.Combine(dir, $"snap_{gen:D4}.mp3");
            _cutToken = _reqToken;
            _nextCutBytes = bytes + CutEveryBytes;   // cadence advances with the request, not the cut
        }
        _log?.LogInfo($"{TagSnap} cut#{gen} requested {bytes / 1024}KB why={why} t={_clock.ElapsedMilliseconds}ms");
        try { _cutGo.Set(); } catch (ObjectDisposedException) { }
    }

    // The snapshot worker: one generation owns one of these, and `_shutdown` is what ends it.
    // Everything it touches was captured when the work was queued (see `RequestCut`), so a stop or a
    // new play between queueing and running can only ever make it *discard* its output, never publish
    // it into the request that replaced it.
    private void CutterLoop()
    {
        while (!_shutdown)
        {
            try
            {
                try { _cutGo.Wait(250); } catch (ObjectDisposedException) { break; }
                if (_shutdown) break;
                _cutGo.Reset();
                DrainRetired(0);        // delete working dirs whose writer has finished
                CutOnce();
            }
            catch (Exception e)
            {
                lock (_cutGate) _cutBusy = false;
                LobbyMusicHooks.Warn($"{TagSnap} cutter threw {e.GetType().Name}: {e.Message}");
            }
        }
        // One last pass, bounded: a retired generation should not leave its last request directory
        // behind just because the shutdown raced the pumps. The budget is TOTAL, because the wait is
        // four thread joins per retired request and would otherwise scale the teardown with the
        // number of requests the session played.
        try { DrainRetired(ShutdownDrainMs, ShutdownDrainBudgetMs); } catch { }
    }

    // Copy [0, len) of the captured source into the captured snapshot path. The producer only ever
    // appends, so a captured length is a consistent prefix.
    private void CutOnce()
    {
        string src, dst;
        int gen;
        long len, token;
        lock (_cutGate)
        {
            if (!_cutWant) return;                  // nothing queued (the wait timed out)
            _cutWant = false;
            src = _cutSrc; dst = _cutDst; gen = _cutGen; len = _cutLen; token = _cutToken;
        }
        if (src == null || dst == null) { lock (_cutGate) _cutBusy = false; return; }
        long t0 = Stopwatch.GetTimestamp();
        long copied = 0;
        try { copied = CopyPrefix(src, dst, len, () => _shutdown || TokenMoved(token)); }
        catch (Exception e)
        {
            LobbyMusicHooks.Warn($"{TagSnap} cut#{gen} copy failed ({e.GetType().Name}: {e.Message})");
        }
        long ms = (long)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        bool current;
        long nowToken;
        string openPath;
        lock (_cutGate)
        {
            nowToken = _reqToken;
            current = token == nowToken;
            openPath = _snapOpenPath;
            if (copied > 0 && current) _snapNewest = gen;
            _cutBusy = false;
        }
        if (copied <= 0) return;
        if (!current)
        {
            // Queued for a request that is over: its bytes must not become a snapshot of the one that
            // replaced it, and its generation must not land in that request's `_snapNewest`.
            YtDlpPipeline.TryDelete(dst, _log);
            LobbyMusicHooks.Debug($"{TagSnap} cut#{gen} discarded: queued under token {token}, current is {nowToken} " +
                                  $"(a stop or a new play happened while it ran)");
            return;
        }
        DeleteSnapshots(Path.GetDirectoryName(dst), gen, openPath);   // keep the open one + the newest
        LobbyMusicHooks.Debug($"{TagSnap} cut#{gen} -> {Path.GetFileName(dst)} {copied / 1024}KB in {ms}ms " +
                              $"(worker thread '{Thread.CurrentThread.Name}')");
    }

    private bool TokenMoved(long token)
    {
        lock (_cutGate) return _reqToken != token;
    }

    private static long CopyPrefix(string src, string dst, long len, Func<bool> stop = null)
    {
        // The producer holds dl.mp3 for append while we read a prefix of it; the snapshot itself is
        // written by this thread only and is fully closed before FMOD is pointed at it. `stop` is
        // checked per block (not per byte): a copy whose request was stopped or replaced is discarded
        // by the caller anyway, so there is no reason to keep reading its bytes.
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read,
                                         FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.Read,
                                          64 * 1024, FileOptions.SequentialScan);
        var buf = new byte[64 * 1024];
        long left = len, wrote = 0;
        while (left > 0)
        {
            if (stop != null && stop()) break;
            int n = input.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) break;
            output.Write(buf, 0, n);
            wrote += n; left -= n;
        }
        output.Flush();
        return wrote;
    }

    // Keep exactly the open snapshot and the newest one; anything else goes (a file FMOD still holds
    // cannot be deleted, and that failure is logged rather than retried in a loop). The directory and
    // the paths are parameters on purpose: this also runs from the cutter, where the live `_pipe` may
    // already describe a different request.
    private void DeleteSnapshots(string dir, int keep, string keepOpenPath)
    {
        if (dir == null || !Directory.Exists(dir)) return;
        string keepNewest = keep > 0 ? Path.Combine(dir, $"snap_{keep:D4}.mp3") : null;
        foreach (var f in Directory.GetFiles(dir, "snap_*.mp3"))
        {
            if (string.Equals(f, keepOpenPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (keepNewest != null && string.Equals(f, keepNewest, StringComparison.OrdinalIgnoreCase)) continue;
            YtDlpPipeline.TryDelete(f, _log);
        }
        var all = Directory.GetFiles(dir, "snap_*.mp3").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        int excess = all.Count - MaxSnapshots;
        for (int i = 0; i < excess; i++)
        {
            if (string.Equals(all[i], keepOpenPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (keepNewest != null && string.Equals(all[i], keepNewest, StringComparison.OrdinalIgnoreCase)) continue;
            YtDlpPipeline.TryDelete(all[i], _log);
        }
    }

    // ---- completion: the finished file becomes the cache entry, playback is a separate question

    private void CheckCompletion(float now)
    {
        var p = _pipe;
        if (p == null || p.Current != YtDlpPipeline.Stage.Complete) return;
        if (_cutBusy) return;
        if (_producerDone)
        {
            // The finished file is final, but a handoff the transport REJECTED earlier is not: the
            // old prefix is not the track, so the handoff is retried until it succeeds or the
            // request is cancelled. Returning unconditionally here made one rejection terminal -
            // playback then ran to the end of the prefix and called that prefix the track.
            RetryHandoff(now);
            return;
        }

        string dl = p.DlPath, cached = p.CachedPath;
        long bytes = p.BytesOnDisk;
        if (dl == null || cached == null || !File.Exists(dl)) return;
        long pos = _engine.PositionMs;

        // The finished cache is SHARED (two instances under one account are the normal setup), so the
        // publish is atomic: the bytes go to a name of this process/request's own first and only then
        // does one rename put the complete file under `track.mp3`. Another instance scanning the cache
        // can therefore never pick up a half-written track, and losing the rename to an instance that
        // finished the same link first is not a failure - it is the same track.
        if (!YtDlpPipeline.PublishFinished(dl, cached, _log, out string publishError))
        {
            _log?.LogWarning($"{Tag} moving the finished track into the cache failed: {publishError}");
            return;
        }
        var info = p.TrackInfo;
        try
        {
            // Written with ONE explicit culture (invariant) and read with the same parser (ReadMeta):
            // this file is a protocol between two runs, not a UI string. A current-culture write
            // produced "123,5", which the invariant reader could never parse, so the declared
            // duration was lost and CanSeek went false on every replay of that cache entry.
            string duration = info == null
                ? ""
                : info.DurationSeconds.ToString("R", CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(cached), "meta.txt"),
                $"url={_url}\nextractor={info?.Extractor}\nid={info?.Id}\ntitle={info?.Title}\n" +
                $"duration={duration}\nbytes={bytes}\n");
        }
        catch (Exception e)
        {
            // The track is already published and playable; only the replay key is lost, so this is not
            // a reason to pretend the download is unfinished (which would keep re-trying a move of a
            // file that is no longer there).
            _log?.LogWarning($"{Tag} cache metadata not written: {e.GetType().Name}: {e.Message}");
        }

        // Finalisation is the second place the ceiling is enforced (the producer checks it while the
        // track grows): a track has just joined the shared cache, which is when it can be over the cap.
        // `cached` itself is never a candidate, so the entry that just appeared is kept.
        long freed = 0;
        int evicted = YtDlpPipeline.EvictOldest(cached, _log, out freed);
        int swept = YtDlpPipeline.SweepStreamRoot(p.TrackDir, _log);
        if (evicted > 0 || swept > 0)
            _log?.LogInfo($"{Tag} ceiling at completion: evicted {evicted} older track(s) " +
                          $"({freed / 1024}KB), swept {swept} abandoned stream dir(s)");

        // From here on: producer done. Nothing below may change the playback phase.
        _producerDone = true;
        _cachedPath = cached;
        var infoDone = p.TrackInfo;
        if (infoDone != null && !string.IsNullOrEmpty(infoDone.Title)) _cachedTitle = infoDone.Title;
        if (infoDone != null && infoDone.DurationSeconds > 0) _cachedDurationMs = (long)(infoDone.DurationSeconds * 1000);
        _log?.LogInfo($"{Tag} COMPLETE: cache finalised '{cached}' ({bytes} bytes = " +
                      $"{bytes / YtDlpPipeline.BytesPerMs}ms) phase={_phase} hasChannel={_engine.HasChannel} " +
                      $"paused={_engine.IsPaused} at pos={pos}ms");

        // No sound yet: the finished file is simply what playback will start from (ServicePlayback).
        if (!_engine.HasChannel)
        {
            _log?.LogInfo($"{Tag} no channel yet; the finished file is what the first start will use");
            return;
        }
        // Paused: keep the open snapshot and defer the channel handoff. Cache metadata is final,
        // the playhead is frozen, and nothing resumes by itself.
        if (_phase == Phase.Paused || _engine.IsPaused)
        {
            _deferredHandoff = cached;
            _log?.LogInfo($"{Tag} COMPLETE while paused: handoff deferred to resume " +
                          $"(open='{Path.GetFileName(_snapOpenPath ?? "-")}' newest={_snapNewest}, " +
                          $"both kept; producer is not resumed)");
            return;
        }
        if (!HandoffTo(cached, pos, false, "complete-while-playing"))
        {
            // Preserving the old channel is right; calling the prefix the finished file is not. The
            // handoff stays pending and loud until it succeeds or the request is cancelled.
            _handoffPath = cached;
            _handoffFails++;
            _nextHandoffAt = now + HandoffRetryDelay();
            _log?.LogWarning($"{Tag} HANDOFF REJECTED: the finished file '{Path.GetFileName(cached)}' is kept in " +
                             $"the cache and the open prefix stays alive; retrying in {HandoffRetryDelay():F0}s " +
                             $"(attempt {_handoffFails}); the prefix length is NOT the track length");
            return;
        }
        RetireStreamDir(p.TrackDir, "complete", p);
    }

    // 1 s, 2 s, 4 s, 8 s, then a steady 8 s. Bounded rate, never a permanent give-up.
    private float HandoffRetryDelay()
    {
        float delay = HandoffRetrySeconds;
        for (int i = 1; i < _handoffFails && delay < HandoffRetryMaxSeconds; i++) delay *= 2f;
        return Math.Min(HandoffRetryMaxSeconds, Math.Max(HandoffRetrySeconds, delay));
    }

    // The retry a rejected handoff gets. The old channel is left exactly as it is (never stopped,
    // never cancelled): the finished file replaces it only when the transport accepts it.
    private void RetryHandoff(float now)
    {
        var path = _handoffPath;
        if (path == null) return;
        if (_phase == Phase.Paused || _engine.IsPaused)
        {
            // A paused request keeps only the prefix and applies the finished file on resume.
            _deferredHandoff = path;
            _handoffPath = null;
            _log?.LogInfo($"{Tag} pending handoff deferred while paused; the finished file is applied on resume");
            return;
        }
        if (now < _nextHandoffAt) return;
        long pos = _engine.PositionMs;
        if (!HandoffTo(path, pos, false, "handoff-retry"))
        {
            _handoffFails++;
            _nextHandoffAt = now + HandoffRetryDelay();
            return;
        }
        _handoffPath = null;
        _handoffFails = 0;
        _deferredHandoff = null;                     // a deferred copy is applied now, not still pending
        RetireStreamDir(_pipe?.TrackDir, "complete", _pipe);
    }

    // Point the engine at the finished file, keeping the position (and, only if asked, the pause).
    private bool HandoffTo(string path, long pos, bool paused, string why)
    {
        long bytes;
        try { bytes = new FileInfo(path).Length; } catch (Exception e)
        {
            _log?.LogError($"{Tag} handoff: cannot size '{path}' ({e.GetType().Name}: {e.Message})");
            return false;
        }
        long t0 = Stopwatch.GetTimestamp();
        bool ok = _engine.Switch(path, bytes, pos, paused, out long gap, out string err);
        if (!ok) ok = _engine.Start(path, bytes, pos, paused, out err);
        if (!ok)
        {
            _log?.LogError($"{Tag} handoff to the finished file failed ({err}); the open snapshot is kept");
            return false;
        }
        long costMs = (long)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        _snapOpenPath = path;
        _snapOpen = 0; _snapNewest = 0;
        _log?.LogInfo($"{Tag} CACHE handoff ({why}) -> '{Path.GetFileName(path)}' {bytes / 1024}KB at={pos}ms " +
                      $"paused={paused} enginePaused={_engine.IsPaused} discontinuity={gap}ms cost={costMs}ms " +
                      $"switches={_engine.Switches} maxGap={_engine.MaxSwitchGapMs}ms");
        return true;
    }

    // Hand a request's working directory to the snapshot worker, which deletes it only once that
    // request's own pumps have stopped writing into it (`DrainRetired`). Deleting here would race
    // them: `Stop` returns while the last blocks are still going to disk, and a delete that wins that
    // race leaves the pump writing into a directory that is being removed - or, worse, silently drops
    // the tail and then publishes a shorter file.
    private void RetireStreamDir(string dir, string why, YtDlpPipeline pipe)
    {
        if (string.IsNullOrEmpty(dir)) return;
        lock (_retireGate) _retire.Add(new RetiredRequest { Dir = dir, Why = why, Pipe = pipe });
        try { _cutGo.Set(); } catch (ObjectDisposedException) { }
    }

    // `waitMs` is the per-thread join the writer check may spend: 0 on the periodic pass (either the
    // pumps are already gone, which is the normal case, or the directory waits for the next wake) and
    // a small bound on the final pass during teardown. `budgetMs` (>= 0) bounds the WHOLE pass, so a
    // long list of retired requests cannot multiply the teardown wait by four joins each.
    private void DrainRetired(int waitMs, int budgetMs = -1)
    {
        RetiredRequest[] pending;
        lock (_retireGate)
        {
            if (_retire.Count == 0) return;
            pending = _retire.ToArray();
        }
        var budget = budgetMs >= 0 ? Stopwatch.StartNew() : null;
        foreach (var r in pending)
        {
            int wait = waitMs;
            if (budget != null)
            {
                long left = budgetMs - budget.ElapsedMilliseconds;
                if (left <= 0) break;                    // out of budget: the rest waits for a later pass
                if (wait > left) wait = (int)left;
            }
            if (r.Pipe != null && !r.Pipe.WorkersFinished(wait)) continue;
            lock (_retireGate) _retire.Remove(r);
            CleanupStreamDir(r.Dir, r.Why);
        }
    }

    // Delete one working directory. The caller has established that no writer is left, so everything
    // in it can go; a delete refused by a live handle (FMOD still reading the open snapshot) is
    // logged and left to `YtDlpPipeline.SweepStreamRoot`, which retries it once the directory has
    // been idle long enough.
    private void CleanupStreamDir(string dir, string why)
    {
        if (dir == null || !Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.GetFiles(dir)) YtDlpPipeline.TryDelete(f, _log);
            var left = Directory.GetFiles(dir);
            if (left.Length == 0)
            {
                Directory.Delete(dir, false);
                _log?.LogInfo($"{Tag} snapshot dir removed ({why}): '{dir}'");
            }
            else
            {
                _log?.LogInfo($"{Tag} snapshot dir kept ({why}), {left.Length} file(s): " +
                              $"{string.Join(", ", Array.ConvertAll(left, Path.GetFileName))}");
            }
        }
        catch (Exception e) { _log?.LogWarning($"{Tag} snapshot cleanup ({why}) failed: {e.Message}"); }
    }

    // ================================================================ reporting

    private void ReportProgress()
    {
        var p = _pipe;
        var e = _engine;
        long pos = e?.PositionMs ?? 0;
        long snapLen = e?.LengthMs() ?? 0;
        long dl = p?.BytesOnDisk ?? 0;
        bool starving = e != null && e.Starving(out int st, out uint buffered, out bool starvingNow, out bool diskBusy) && starvingNow;
        _log?.LogInfo($"{Tag} play: pos={pos}ms snapLen={snapLen}ms file={Path.GetFileName(e?.FilePath ?? "-")} " +
                      $"dl={dl / 1024}KB stage={p?.Current} done={_producerDone} gen={_snapOpen}/{_snapNewest} " +
                      $"switches={e?.Switches ?? 0} starving={starving} paused={e?.IsPaused} phase={_phase} " +
                      $"t={_clock.ElapsedMilliseconds}ms");
    }

    internal string TimingReport()
    {
        var p = _pipe;
        if (_tPlayMs < 0)
            return $"{Tag} timing: no sound yet phase={_phase} t={_clock.ElapsedMilliseconds}ms " +
                   $"firstByte={p?.FirstByteMs ?? -1}ms ready={p?.ReadyMs ?? -1}ms preflight={p?.PreflightMs ?? -1}ms";
        return $"{Tag} timing: TIME_TO_FIRST_SOUND={_tPlayMs}ms (preflight={(p?.PreflightMs ?? -1)}ms " +
               $"firstByte={p?.FirstByteMs ?? -1}ms ready128KB={p?.ReadyMs ?? -1}ms spawn={p?.SpawnMs ?? -1}ms) " +
               $"trackMs={TotalMs} switches={_engine?.Switches ?? 0} " +
               $"maxGap={_engine?.MaxSwitchGapMs ?? 0}ms";
    }

    internal string SnapshotDir => _pipe?.TrackDir;
    internal string CacheRoot => YtDlpPipeline.CacheRoot;

    // ================================================================ teardown

    internal void Dispose()
    {
        try { Cancel("generation retired"); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        try { _engine?.Dispose(); } catch { }
        _phase = Phase.Idle;

        // End the snapshot worker. Before this, the loop was `while (true)` and Dispose only signalled
        // its event, so every installed generation left a waking thread behind - and through it the
        // host and the hot-reloaded assembly, which is why nothing was ever collectable again. The
        // flag ends the loop, the join proves it ended, and only then is the event disposed.
        _shutdown = true;
        try { _cutGo.Set(); } catch (ObjectDisposedException) { }
        var cutter = _cutter;
        _cutter = null;                       // the host keeps no reference to a thread it no longer owns
        bool joined = true;
        if (cutter != null)
        {
            try { joined = cutter.Join(CutterJoinMs); }
            catch (ThreadStateException) { joined = true; }      // never started: nothing to join
            if (!joined)
                _log?.LogWarning($"{Tag} snapshot worker did not stop within {CutterJoinMs}ms " +
                                 $"(state={ThreadStateOf(cutter)}, alive={cutter.IsAlive}); it is a background " +
                                 "thread and cannot block process exit, but this generation is not fully retired");
        }
        try { _cutGo.Dispose(); } catch { }
        _log?.LogInfo($"{Tag} disposed; ticks={_ticks} cutter={(cutter == null ? "none" : (joined ? "joined" : "LEAKED"))} " +
                      $"retiredDirs={RetiredCount()}");
    }

    private int RetiredCount() { lock (_retireGate) return _retire.Count; }

    private static string ThreadStateOf(Thread t)
    {
        try { return t.ThreadState.ToString(); } catch { return "?"; }
    }

    // ================================================================ helpers

    internal static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        long s = ms / 1000;
        return $"{s / 60}:{s % 60:00}";
    }

    private static string Short(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
