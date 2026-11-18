using System;
using System.Diagnostics;
using System.IO;
using AlterumModelSwap.Contracts;
using FMOD;
using FMODUnity;

namespace AlterumModelSwap.Hot;

// The FMOD half of the streaming lobby-music player. Nothing here touches the game's player:
// every call is a normally-callable API on `FMODUnity.RuntimeManager.CoreSystem` /
// `RuntimeManager.StudioSystem`, the same surface ReelAudio uses for its synth cues.
//
// Why a stream and not the game's path: the game's loader is memory-based end to end
// (UrlAudioDataProvider -> byte[] -> createSound(OPENMEMORY)), so it cannot start before the whole
// file exists. A `createStream` over a file path starts decoding on the first mixer pull.
//
// The one hard constraint (measured, see notes/progressive-mp3-and-fmod-api.md): FMOD opens stream
// files read-only and refuses a file another handle is writing (`FMOD_ERR_FILE_BAD`), and it blocks
// the writer while it holds one. So the pipeline writes `dl.mp3`, and the engine only ever opens
// *snapshots*: cumulative prefixes cut by MusicStreamHost. Each snapshot is at offset 0 of the
// track, so an absolute track position IS a position inside every snapshot.
//
// Every result code is logged. A switch is done open-new-paused -> position -> unpause -> stop-old,
// so the two channels overlap for at most one mixer buffer at the same sample position instead of
// leaving a gap.
internal sealed class MusicStream
{
    internal const string Tag = "[music:stream]";

    // 128 kbps CBR = 16 000 B/s. Used only to reason about how much audio a snapshot holds when
    // FMOD's own (estimated, no Xing header) length is unavailable.
    internal const long BytesPerMs = 16;

    // How far a channel's own reported position may sit from the position we asked for before the
    // start/switch is called a failure. An mp3 frame is ~26 ms and a stream seek is allowed to land
    // on a frame boundary, so this is a correctness bound against "playing from the wrong place"
    // (a clamped seek, a rejected one, the head of the file), not a precision claim.
    private const long SeekToleranceMs = 500;

    // The same check, one notch tighter, for a correction: a start/switch is allowed to land 500 ms
    // off (it is proven against "the right place, roughly"), but a correction made to keep two
    // playheads together is judged against the <=750 ms audio claim, so it commits only when the
    // channel reads back within this. A seek that lands elsewhere is a failure, not a success.
    private const long CorrectionToleranceMs = 300;

    private IHostContext _log;

    private Sound _sound;
    private Channel _channel;
    private long _lastChannelPositionMs;
    private bool _haveSound, _haveChannel;

    private string _path;
    private long _snapBytes;      // bytes in the snapshot currently open
    private long _snapLengthMs;   // length FMOD reports for it (frozen at open)

    private ChannelGroup _busGroup;
    private FMOD.Studio.Bus _bus;
    private bool _busGroupValid, _busLocked;
    private float _busVolume = 1f, _busFinalVolume = 1f;
    private bool _busMuted, _busPaused;

    private float _volume = 1f;
    private int _switches;
    private long _maxGapMs;
    private uint _openMs;
    // The pause the host wants on this channel. Start/Switch take it explicitly instead of
    // inheriting whatever the previous channel happened to be: a completed download used to start
    // the finished file unpaused while the host still reported Paused.
    private bool _paused;

    internal bool HasSound => _haveSound;
    internal bool HasChannel => _haveChannel;

    // A channel that can still be acted on (pause/resume/seek), as opposed to one that ever
    // existed: FMOD keeps `_haveChannel` true for the object it created until Stop(), including
    // after the channel has played its snapshot to the end, so an exhausted prefix used to stay
    // "pausable" forever. Paused is answered from our own committed flag - that channel is exactly
    // what resume needs - and a query failure never disables the controls.
    internal bool Playable
    {
        get
        {
            if (!_haveChannel) return false;
            if (_paused) return true;
            var r = _channel.isPlaying(out bool playing);
            return r != RESULT.OK || playing;
        }
    }

    internal bool IsPaused => _paused;
    internal string FilePath => _path;
    internal long SnapshotBytes => _snapBytes;
    internal long SnapshotLengthMs => _snapLengthMs;
    internal int Switches => _switches;
    internal long MaxSwitchGapMs => _maxGapMs;
    internal uint OpenMs => _openMs;

    // ================================================================ bus

    // Route our channel through the Studio master bus's channel group, so the game's own master
    // volume/mute/pause applies to it (a channel played on the core system with a default group
    // bypasses Studio entirely and ignores the game's audio settings - the reels' cues accept that,
    // music must not).
    internal bool EnsureBus(IHostContext log)
    {
        _log = log;
        if (_busGroupValid) return true;
        try
        {
            var studio = RuntimeManager.StudioSystem;
            var r = studio.getBus("bus:/", out _bus);
            if (r != RESULT.OK)
            {
                log?.LogError($"{Tag} StudioSystem.getBus('bus:/') -> {r} ({Error.String(r)}); music will play on the core master group");
                return false;
            }
            var lr = _bus.lockChannelGroup();
            _busLocked = lr == RESULT.OK;
            var gr = _bus.getChannelGroup(out _busGroup);
            if (gr != RESULT.OK)
            {
                log?.LogError($"{Tag} bus.getChannelGroup -> {gr} ({Error.String(gr)}); music will play on the core master group");
                return false;
            }
            _busGroupValid = true;
            var vr = _bus.getVolume(out _busVolume, out _busFinalVolume);
            _bus.getMute(out _busMuted);
            _bus.getPaused(out _busPaused);
            log?.LogInfo($"{Tag} bus 'bus:/' locked={_busLocked} getChannelGroup={gr} getVolume={vr} " +
                         $"volume={_busVolume:0.###} final={_busFinalVolume:0.###} muted={_busMuted} paused={_busPaused}");
            // A stream reads its file in chunks; 32 KB is ~2 s of 128 kbps audio, i.e. enough to
            // ride out a file-system hiccup while keeping the first pull small.
            var br = RuntimeManager.CoreSystem.setStreamBufferSize(32 * 1024, TIMEUNIT.RAWBYTES);
            var gbr = RuntimeManager.CoreSystem.getStreamBufferSize(out uint bufBytes, out TIMEUNIT bufUnit);
            log?.LogInfo($"{Tag} setStreamBufferSize(32768 RAWBYTES) -> {br}; now {bufBytes} unit={bufUnit} (get={gbr})");
            return true;
        }
        catch (Exception e)
        {
            log?.LogError($"{Tag} bus resolution threw ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }

    // Re-read the bus state so a report can say what the game's mixer thinks, not just ours.
    internal void RefreshBus()
    {
        if (!_busGroupValid) return;
        try
        {
            _bus.getVolume(out _busVolume, out _busFinalVolume);
            _bus.getMute(out _busMuted);
            _bus.getPaused(out _busPaused);
        }
        catch { }
    }

    internal string BusState => !_busGroupValid
        ? "bus=unattached"
        : $"bus=attached vol={_busVolume:0.###} final={_busFinalVolume:0.###} muted={_busMuted} paused={_busPaused}";

    // ================================================================ open / play / switch

    // Open a snapshot and start it at an absolute track position. `snapshotBytes` is the size the
    // host cut the prefix at (FMOD's own length for a headerless CBR mp3 is an estimate of the same
    // thing; both are logged).
    //
    // Nothing here is committed on the strength of the calls: the candidate channel must read back
    // at the requested position *and* in the requested pause state (see `VerifyTransport`) before it
    // becomes ours. A candidate that fails that is disposed, and a channel that was already working
    // is left exactly as it was - a start that failed must not cost the listener the stream that was
    // still playing.
    internal bool Start(string path, long snapshotBytes, long absoluteMs, bool paused, out string error)
    {
        error = null;
        EnsureBus(_log);                            // the bus group must exist before playSound
        Sound candidateSound = default;
        Channel candidateChannel = default;
        long snapLengthMs;
        uint openMs = 0;
        try
        {
            var core = RuntimeManager.CoreSystem;
            long t0 = Stopwatch.GetTimestamp();
            var r = core.createStream(path, MODE.CREATESTREAM | MODE.UNIQUE | MODE._2D, out candidateSound);
            openMs = Elapsed(t0);
            if (r != RESULT.OK)
            {
                error = $"createStream -> {r} ({Error.String(r)})";
                _log?.LogError($"{Tag} START FAILED {error} file='{V(path)}' bytes={snapshotBytes}; " +
                               "the current channel is kept");
                return false;
            }
            var lr = candidateSound.getLength(out uint lenMs, TIMEUNIT.MS);
            snapLengthMs = lr == RESULT.OK ? lenMs : snapshotBytes / BytesPerMs;

            var pr = core.playSound(candidateSound, _busGroupValid ? _busGroup : default, true, out candidateChannel);
            if (pr != RESULT.OK)
            {
                error = $"playSound -> {pr} ({Error.String(pr)})";
                _log?.LogError($"{Tag} START FAILED {error} file='{V(path)}'; candidate released, " +
                               "the current channel is kept");
                DisposeCandidate(ref candidateSound, ref candidateChannel);
                return false;
            }
            if (_busGroupValid) candidateChannel.setChannelGroup(_busGroup);
            candidateChannel.setVolume(_volume);
            if (!VerifyTransport(candidateChannel, absoluteMs, paused, out string why))
            {
                error = $"candidate channel not in the requested state: {why}";
                _log?.LogError($"{Tag} START FAILED {error} file='{V(path)}' bytes={snapshotBytes}; " +
                               "candidate disposed, the current channel is kept");
                DisposeCandidate(ref candidateSound, ref candidateChannel);
                return false;
            }

            // Proven good: only now does the previous channel (if any) lose its claim on the output.
            Stop();
            _sound = candidateSound; _haveSound = true;
            _channel = candidateChannel; _haveChannel = true;
            _lastChannelPositionMs = absoluteMs;
            _path = path; _snapBytes = snapshotBytes; _snapLengthMs = snapLengthMs; _openMs = openMs;
            _paused = paused;
            _log?.LogInfo($"{Tag} START '{Path.GetFileName(path)}' bytes={snapshotBytes} snapLen={_snapLengthMs}ms " +
                          $"(getLength={lr}) createStream={_openMs}ms playSound={pr} at={Pos()}ms " +
                          $"paused={paused} {BusState}");
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _log?.LogError($"{Tag} START threw {error}; candidate released, the current channel is kept");
            DisposeCandidate(ref candidateSound, ref candidateChannel);
            return false;
        }
    }

    // Hand playback to a newer snapshot without leaving a hole. Both channels are at the same
    // absolute position for at most one mixer buffer; the old one is stopped last - and only after
    // the new one has proven it is really at that position in the requested pause state.
    internal bool Switch(string path, long snapshotBytes, long absoluteMs, bool paused,
                         out long gapMs, out string error)
    {
        gapMs = 0; error = null;
        EnsureBus(_log);
        if (!_haveChannel) return Start(path, snapshotBytes, absoluteMs, paused, out error);
        long t0 = Stopwatch.GetTimestamp();
        uint before = 0;
        var br = _channel.getPosition(out before, TIMEUNIT.MS);
        bool wasPaused = false;
        _channel.getPaused(out wasPaused);

        Sound nextSound = default;
        Channel nextChannel = default;
        RESULT r, pr;
        try
        {
            var core = RuntimeManager.CoreSystem;
            r = core.createStream(path, MODE.CREATESTREAM | MODE.UNIQUE | MODE._2D, out nextSound);
            if (r != RESULT.OK)
            {
                error = $"createStream -> {r} ({Error.String(r)})";
                _log?.LogError($"{Tag} SWITCH FAILED {error} file='{V(path)}' — keeping the current snapshot");
                return false;
            }
            pr = core.playSound(nextSound, _busGroupValid ? _busGroup : default, true, out nextChannel);
            if (pr != RESULT.OK)
            {
                error = $"playSound -> {pr} ({Error.String(pr)})";
                _log?.LogError($"{Tag} SWITCH FAILED {error} file='{V(path)}'; candidate released, keeping the current snapshot");
                DisposeCandidate(ref nextSound, ref nextChannel);
                return false;
            }
            if (_busGroupValid) nextChannel.setChannelGroup(_busGroup);
            nextChannel.setVolume(_volume);
            // `absoluteMs` is the position the old channel had reached; the new snapshot is a prefix
            // of the same track, so the same value is the right offset in it. Reading the result code
            // is not enough: a stream can accept a seek and land somewhere else (clamped to the end of
            // a short snapshot, ignored until the first mixer pull), and a pause can be refused. The
            // candidate has to prove both before it is allowed to replace a channel that works.
            if (!VerifyTransport(nextChannel, absoluteMs, paused, out string why))
            {
                error = $"candidate channel not in the requested state: {why}";
                _log?.LogError($"{Tag} SWITCH FAILED {error} file='{V(path)}' at={absoluteMs}ms; " +
                               "candidate disposed, keeping the current snapshot");
                DisposeCandidate(ref nextSound, ref nextChannel);
                return false;
            }
            _log?.LogInfo($"{Tag} SWITCH candidate ready: oldPaused={wasPaused} wanted={paused} at={absoluteMs}ms");
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            _log?.LogError($"{Tag} SWITCH threw {error}; candidate released, keeping the current snapshot");
            DisposeCandidate(ref nextSound, ref nextChannel);
            return false;
        }

        // Candidate is proven good (position and pause read back as requested), so it is committed
        // here and only here: before this line the old channel is untouched and still playing.
        _paused = paused;
        try { _channel.stop(); } catch { }
        ReleaseSound();
        _sound = nextSound; _haveSound = true;
        _channel = nextChannel; _haveChannel = true;
        _lastChannelPositionMs = absoluteMs;
        _path = path; _snapBytes = snapshotBytes;
        var lr = _sound.getLength(out uint lenMs, TIMEUNIT.MS);
        _snapLengthMs = lr == RESULT.OK ? lenMs : snapshotBytes / BytesPerMs;
        _switches++;

        uint after = 0;
        _channel.getPosition(out after, TIMEUNIT.MS);
        gapMs = (long)after - before;
        if (Math.Abs(gapMs) > Math.Abs(_maxGapMs)) _maxGapMs = gapMs;
        uint costMs = Elapsed(t0);
        _log?.LogInfo($"{Tag} SWITCH #{_switches} '{Path.GetFileName(path)}' bytes={snapshotBytes} snapLen={_snapLengthMs}ms " +
                      $"at={after}ms (was {before}ms, getPosition={br}) discontinuity={gapMs}ms cost={costMs}ms " +
                      $"paused={_paused} {BusState}");
        return true;
    }

    // ================================================================ control

    // Committing the host's phase is only allowed on a true return, and a true return needs a
    // READBACK: `setPaused` reports only that FMOD accepted the call, so the channel is asked for its
    // own pause flag and the phase is committed only when that readback agrees. A refused call or a
    // readback that did not follow leaves `_paused` exactly as it was - nothing is committed, the
    // caller gets false, and the retry goes through the follower's correction backoff.
    internal bool Pause()
    {
        if (!_haveChannel) { _log?.LogInfo($"{Tag} pause ignored: no channel"); return false; }
        var r = _channel.setPaused(true);
        bool now = false;
        var gr = _channel.getPaused(out now);
        bool ok = r == RESULT.OK && gr == RESULT.OK && now;
        if (!ok)
        {
            _log?.LogWarning($"{Tag} pause NOT committed: setPaused={r} ({Error.String(r)}) " +
                             $"getPaused={gr} ({Error.String(gr)}) readbackPaused={now} keptPaused={_paused} at={Pos()}ms");
            return false;
        }
        _paused = true;
        _log?.LogInfo($"{Tag} pause -> {r} ({Error.String(r)}); getPaused={gr} paused={now} at={Pos()}ms");
        return true;
    }

    internal bool Resume()
    {
        if (!_haveChannel) { _log?.LogInfo($"{Tag} resume ignored: no channel"); return false; }
        var r = _channel.setPaused(false);
        bool now = true;
        var gr = _channel.getPaused(out now);
        bool ok = r == RESULT.OK && gr == RESULT.OK && !now;
        if (!ok)
        {
            _log?.LogWarning($"{Tag} resume NOT committed: setPaused={r} ({Error.String(r)}) " +
                             $"getPaused={gr} ({Error.String(gr)}) readbackPaused={now} keptPaused={_paused} at={Pos()}ms");
            return false;
        }
        _paused = false;
        _log?.LogInfo($"{Tag} resume -> {r} ({Error.String(r)}); getPaused={gr} paused={now} at={Pos()}ms");
        return true;
    }

    // `setPosition` reports what it was ASKED for: a stream can accept a seek into audio it has not
    // buffered yet (or that this snapshot does not hold) and land somewhere else. So the channel's
    // own position is read back - twice, short - and only a landing inside
    // `CorrectionToleranceMs` counts as committed. This is deliberately NOT weaker than
    // `VerifyTransport`.
    internal bool Seek(long absoluteMs)
    {
        if (!_haveChannel) return false;
        if (absoluteMs < 0) absoluteMs = 0;
        long t0 = Stopwatch.GetTimestamp();
        var r = _channel.setPosition((uint)absoluteMs, TIMEUNIT.MS);
        uint now = 0;
        var gr = _channel.getPosition(out now, TIMEUNIT.MS);
        for (int i = 0; i < 2 && (gr != RESULT.OK || Math.Abs((long)now - absoluteMs) > CorrectionToleranceMs); i++)
        {
            System.Threading.Thread.Sleep(8);   // FMOD has its own `Thread`, so this is qualified
            gr = _channel.getPosition(out now, TIMEUNIT.MS);
        }
        long delta = (long)now - absoluteMs;
        bool ok = r == RESULT.OK && gr == RESULT.OK && Math.Abs(delta) <= CorrectionToleranceMs;
        if (ok) _lastChannelPositionMs = now;
        _log?.LogInfo($"{Tag} seek({absoluteMs}ms) -> {r} ({Error.String(r)}) in {Elapsed(t0)}ms; " +
                      $"getPosition={gr} now={now}ms delta={delta}ms tolerance={CorrectionToleranceMs}ms " +
                      $"ok={ok} snapLen={_snapLengthMs}ms");
        return ok;
    }

    internal bool SetVolume(float v)
    {
        _volume = Math.Max(0f, Math.Min(4f, v));
        if (!_haveChannel) return false;
        var r = _channel.setVolume(_volume);
        _log?.LogInfo($"{Tag} volume({_volume:0.###}) -> {r}");
        return r == RESULT.OK;
    }

    // True while the mixer is pulling this channel. Both are logged as the "it is really playing"
    // pair: isPlaying alone is true for a channel that is starved or muted.
    internal bool Playing()
    {
        if (!_haveChannel) return false;
        var r = _channel.isPlaying(out bool playing);
        return r == RESULT.OK && playing;
    }

    // `state` is FMOD's OPENSTATE as an int, so callers outside this file do not reference FMOD types
    // (0 READY, 1 LOADING, 2 ERROR, 3 CONNECTING, 4 BUFFERING, 5 SEEKING, 6 PLAYING, 7 SETPOSITION).
    internal bool Starving(out int state, out uint percentBuffered, out bool starving, out bool diskBusy)
    {
        state = 0; percentBuffered = 0; starving = false; diskBusy = false;
        if (!_haveSound) return false;
        var r = _sound.getOpenState(out OPENSTATE st, out percentBuffered, out starving, out diskBusy);
        state = (int)st;
        return r == RESULT.OK;
    }

    internal long PositionMs
    {
        get
        {
            if (!_haveChannel) return 0;
            var r = _channel.getPosition(out uint pos, TIMEUNIT.MS);
            // FMOD invalidates a naturally finished channel; the out-param then reads as 0.
            // Retain the last successful sample, not the failed query's default value.
            if (r == RESULT.OK) _lastChannelPositionMs = pos;
            return _lastChannelPositionMs;
        }
    }

    // Retire the channel and the sound. The file handle goes with `Sound.release`, which is what
    // lets the host delete a snapshot (and is verified by the cancel test).
    internal bool Stop()
    {
        bool had = _haveChannel || _haveSound;
        _switches = 0; _maxGapMs = 0; _paused = false;
        if (_haveChannel) { try { _channel.stop(); } catch { } }
        ReleaseSound();
        _haveChannel = false;
        _path = null; _snapBytes = 0; _snapLengthMs = 0;
        return had;
    }

    // Retire a candidate channel that never became the working one. Its sound must be released (that
    // is what closes the file handle the host needs to delete a snapshot) and its channel stopped
    // first, so no mixer pull outlives the object. Never touches `_sound`/`_channel`.
    private void DisposeCandidate(ref Sound sound, ref Channel channel)
    {
        try { if (channel.hasHandle()) channel.stop(); } catch { }
        try
        {
            if (sound.hasHandle())
            {
                var r = sound.release();
                if (r != RESULT.OK)
                    _log?.LogWarning($"{Tag} candidate sound.release -> {r} ({Error.String(r)})");
            }
        }
        catch (Exception e) { _log?.LogWarning($"{Tag} candidate sound.release threw {e.GetType().Name}: {e.Message}"); }
        sound = default;
        channel = default;
    }

    // The transport calls report their own success, not the channel's state: `setPaused` on a stream
    // that is still opening, and a `setPosition` that the stream clamps or ignores, both return what
    // they were asked for while the channel ends up paused, or at 0 ms, or at the end of a snapshot.
    // So the channel's own state is read back (`getPosition`, `getPaused`) and a start/switch is only
    // allowed to commit when it agrees with the request. `absoluteMs` is an absolute track position,
    // which is also a valid offset in every snapshot (all prefixes start at 0).
    private bool VerifyTransport(Channel channel, long absoluteMs, bool paused, out string why)
    {
        why = null;
        if (absoluteMs > 0)
        {
            var target = (uint)Math.Min(uint.MaxValue, absoluteMs);
            var sr = channel.setPosition(target, TIMEUNIT.MS);
            uint pos = 0;
            var gr = channel.getPosition(out pos, TIMEUNIT.MS);
            // A stream that has not been pulled yet can accept the seek and only report it after the
            // next mixer pull, so a mismatch gets a few short retries before it counts as a failure.
            for (int i = 0; i < 3 && (gr != RESULT.OK || Math.Abs((long)pos - absoluteMs) > SeekToleranceMs); i++)
            {
                System.Threading.Thread.Sleep(8);   // FMOD has its own `Thread`, so this is qualified
                gr = channel.getPosition(out pos, TIMEUNIT.MS);
            }
            long delta = (long)pos - absoluteMs;
            if (sr != RESULT.OK || gr != RESULT.OK || Math.Abs(delta) > SeekToleranceMs)
            {
                why = $"setPosition({absoluteMs}ms) -> {sr} ({Error.String(sr)}), getPosition={gr} " +
                      $"now={pos}ms (delta={delta}ms, tolerance={SeekToleranceMs}ms)";
                _log?.LogWarning($"{Tag} transport check failed: {why}");
                return false;
            }
        }
        var pz = channel.setPaused(paused);
        bool reallyPaused = !paused;
        var pg = channel.getPaused(out reallyPaused);
        if (pz != RESULT.OK || pg != RESULT.OK || reallyPaused != paused)
        {
            why = $"setPaused({paused}) -> {pz} ({Error.String(pz)}), getPaused={pg} now={reallyPaused}";
            _log?.LogWarning($"{Tag} transport check failed: {why}");
            return false;
        }
        return true;
    }

    private void ReleaseSound()
    {
        if (!_haveSound) return;
        try
        {
            var r = _sound.release();
            if (r != RESULT.OK) _log?.LogWarning($"{Tag} sound.release -> {r} ({Error.String(r)}) file='{V(_path)}'");
        }
        catch (Exception e) { _log?.LogWarning($"{Tag} sound.release threw {e.GetType().Name}: {e.Message}"); }
        _haveSound = false;
        _sound = default;
        _channel = default;
    }

    internal void Dispose()
    {
        Stop();
        if (_busLocked)
        {
            try
            {
                var r = _bus.unlockChannelGroup();
                _log?.LogInfo($"{Tag} bus unlocked -> {r}");
            }
            catch { }
            _busLocked = false;
        }
        _busGroupValid = false;
    }

    // ================================================================ helpers

    internal string Pos() => PositionMs.ToString();

    private static uint Elapsed(long t0)
        => (uint)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);

    private static string V(string s) => s == null ? "null" : s.Replace('\n', ' ');

    // Snapshot length as FMOD reports it, or the byte estimate when it does not. Used by the host to
    // decide "the playhead is about to reach the end of what this snapshot holds".
    internal long LengthMs()
    {
        if (_haveChannel && _snapLengthMs > 0) return _snapLengthMs;
        return _snapBytes / BytesPerMs;
    }
}
