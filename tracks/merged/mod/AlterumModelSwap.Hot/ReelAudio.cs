using System;
using System.Collections.Generic;
using AlterumModelSwap.Contracts;
using FMOD;
using FMODUnity;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// The reel's voice: every cue is synthesised at load time from generated samples. There are no
// audio assets in this mod and no asset bundle, so the ONLY inputs are maths: sine/square/triangle
// partials, shaped noise and per-tone envelopes, summed and normalised. Each cue is layered (a
// transient + a body + a tail), never a single beep.
//
// Delivery: FMOD, through the game's own RuntimeManager.CoreSystem, as a 16-bit mono PCM sample
// sound created from a managed byte buffer (MODE.CREATESAMPLE|OPENMEMORY|MODE._3D). AudioClip.Create
// is not
// usable in this build: SetData(Il2CppStructArray<float>) dies inside Il2CppSystem.ReadOnlySpan
// ("Method not found ... GetPinnableReference"), and the PCMReaderCallback route fails with
// "Method unstripping failed" (the managed->native callback is stripped). Nothing is created in an
// asset bundle either.
//
// Generated once per hot generation, released on Dispose. Playback respects the game's audio
// state: nothing is played while the listener is muted or paused, while the game is paused
// (timeScale 0) or while the active scene is still loading.
//
// POSITION: these are not UI sounds. Every reel cue comes out of ONE booth - the machine that
// turned or the machine that refused - on BOTH peers, so each cue is played as a 3D channel at
// that booth's sign face (LobbyPropRuntime.LookCentre). The listener is the game's own FMOD Studio
// listener (the camera); this code never writes listener attributes and never calls
// CoreSystem.update, so the game keeps full ownership of the mix. A cue from the left machine
// arrives on the left and fades to silence at MaxDistance using linear rolloff. The one exception is
// Confirm() below: the pause-menu code's beep is
// a UI confirmation with no booth behind it, so it uses a flat (2D) Sound of its own.
//
// ROUTING LIMIT: these sounds are created on the core system, so they play into the core master
// channel group. That is audible and it is what the game's Studio mixer itself feeds, but the
// channels are NOT on a Studio event bus/VCA: a Studio mixer snapshot or a bus volume does not
// move them. The reel owns no events, so there is no bus to join; AudioListener.pause/volume and
// the pause/scene checks in CanPlay() are what is honoured instead.
internal enum ReelCue { Lever, Tick, Settle, Win, Double, Damage, Debuff, Error }

internal static class ReelAudio
{
    private const int Rate = 44100;
    private const float Master = 0.55f;

    // Keep the booth audible to its operator, then fade out over a short local radius. Linear
    // rolloff reaches silence at MaxDistance; FMOD's inverse rolloff would remain audible past it.
    private const float MinDistance = 2.2f;
    private const float MaxDistance = 12f;
    private const MODE Mode3D = MODE._3D | MODE._3D_LINEARROLLOFF;
    private const MODE Mode2D = MODE._2D;

    private static readonly Dictionary<ReelCue, Sound> Sounds = new Dictionary<ReelCue, Sound>();
    private static readonly Dictionary<ReelCue, float> Seconds = new Dictionary<ReelCue, float>();
    // The cues whose Sound was created with MODE._3D, i.e. the ones a channel can place in the world.
    private static readonly HashSet<ReelCue> Spatial = new HashSet<ReelCue>();
    // The raw PCM each Sound points into (OPENMEMORY): these buffers must outlive the Sound.
    private static readonly List<Il2CppStructArray<byte>> Pinned = new List<Il2CppStructArray<byte>>();
    // Confirm()'s Sound: the SAME pipe sample, created WITHOUT MODE._3D. A Sound's 2D/3D mode is
    // fixed at creation and a 3D channel with no attributes renders at the world origin, so the
    // pause-menu beep owns a flat Sound instead of borrowing the positional one (no second PCM
    // buffer: it reads the same pinned WAV bytes).
    private static Sound _confirm;
    private static Channel _tickChannel;
    private static bool _built, _failed, _mutedLogged, _spatialWarned;

    internal static string Summary
    {
        get
        {
            if (!_built) return "not built";
            var sb = new System.Text.StringBuilder();
            foreach (var kv in Seconds)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(kv.Key).Append(':').Append(kv.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('s');
            }
            return sb.ToString();
        }
    }

    // How many cues can be positioned and over what distance, so a log can tell a working 3D build
    // from a 2D fallback without guessing at the numbers.
    internal static string SpatialSummary =>
        $"{Spatial.Count}/{Sounds.Count} spatial {MinDistance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}" +
        $"-{MaxDistance.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}m";

    internal static void Ensure(IHostContext log)
    {
        if (_built || _failed) return;
        try
        {
            Add(log, ReelCue.Lever, BuildLever());
            Add(log, ReelCue.Tick, BuildTick());
            Add(log, ReelCue.Settle, BuildSettle());
            Add(log, ReelCue.Win, BuildWin());
            Add(log, ReelCue.Double, BuildDouble());
            Add(log, ReelCue.Damage, BuildDamage());
            Add(log, ReelCue.Debuff, BuildDebuff());
            Add(log, ReelCue.Error, BuildError());
            _built = true;
            // Log the actual interop enum value so runtime evidence identifies the mode used.
            log?.LogInfo($"[reel:audio] built {Sounds.Count} synth sounds rate={Rate}Hz mono PCM16 via=FMODCoreSystem " +
                         $"({SpatialSummary} mode3d=0x{(int)Mode3D:x} min={MinDistance:F1}m max={MaxDistance:F1}m): {Summary}");
        }
        catch (Exception e)
        {
            _failed = true;
            log?.LogWarning($"[reel:audio] synthesis failed, running silent: {e.Message}");
        }
    }

    private static void Add(IHostContext log, ReelCue cue, float[] samples)
    {
        Normalize(samples, 0.92f);
        // Wrap the samples in a RIFF/WAVE container and let FMOD parse it: the raw-PCM
        // CREATESOUNDEXINFO form returned ERR_FORMAT on this build, while a WAV header states the
        // format explicitly and is read by the same OPENMEMORY path.
        var bytes = ToWav(samples, Rate);
        var arr = new Il2CppStructArray<byte>(bytes);
        Pinned.Add(arr);
        var ex = Ex((uint)bytes.Length);
        Sound snd;
        // A MONO sample created with MODE._3D is what a channel's 3D attributes need (a stereo
        // source is not positional), so 3D is the first form tried.
        var res = Create(arr, ref ex, Mode3D, out snd);
        bool spatial = res == RESULT.OK;
        if (!spatial)
        {
            // The 3D flag itself was refused: fall back to the pre-spatial flat form, so the cue is
            // still audible (unpositioned) instead of missing from the mix.
            res = Create(arr, ref ex, Mode2D, out snd);
        }
        if (res != RESULT.OK) throw new InvalidOperationException($"createSound {cue} -> {res}");
        if (spatial)
        {
            // Per-sound default distance, so a channel inherits it at playSound even if the
            // per-channel set at play time were to fail.
            try { snd.set3DMinMaxDistance(MinDistance, MaxDistance); } catch { }
            Spatial.Add(cue);
        }
        Sounds[cue] = snd;
        Seconds[cue] = samples.Length / (float)Rate;
        if (cue == ReelCue.Settle)
        {
            // Confirm() must stay 2D. Created here, from the SAME pinned WAV buffer, so the pause
            // menu's beep costs one extra decoded sample and cannot inherit the 3D mode.
            Sound flat;
            var flatRes = Create(arr, ref ex, Mode2D, out flat);
            if (flatRes == RESULT.OK) _confirm = flat;
            else log?.LogWarning($"[reel:audio] confirm sample could not be created 2D -> {flatRes}; " +
                                 "the pause-menu confirmation would be silent, so Confirm() will refuse instead");
        }
    }

    // The pinned WAV bytes and their reported length: both creations (3D and flat) parse the same
    // buffer, so there is exactly one copy of the PCM in this mod.
    private static CREATESOUNDEXINFO Ex(uint byteLength) => new CREATESOUNDEXINFO
    {
        cbsize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(CREATESOUNDEXINFO)),
        length = byteLength,
    };

    // createSound over the pinned WAV bytes. CREATESAMPLE first, stream-from-memory second: the
    // sample form is what this wrapper was designed for, the stream form parses the same WAV without
    // the sample-decode path that once returned ERR_FORMAT here.
    private static RESULT Create(Il2CppStructArray<byte> arr, ref CREATESOUNDEXINFO ex, MODE mode, out Sound snd)
    {
        var res = RuntimeManager.CoreSystem.createSound(arr, MODE.CREATESAMPLE | MODE.OPENMEMORY | mode, ref ex, out snd);
        if (res != RESULT.OK)
            res = RuntimeManager.CoreSystem.createSound(arr, MODE.CREATESTREAM | MODE.OPENMEMORY | mode, ref ex, out snd);
        return res;
    }

    private static byte[] ToWav(float[] samples, int rate)
    {
        var pcm = ToPcm16(samples);
        int n = pcm.Length;
        var b = new byte[44 + n];
        void U32(int at, uint v)
        {
            b[at] = (byte)(v & 0xFF); b[at + 1] = (byte)((v >> 8) & 0xFF);
            b[at + 2] = (byte)((v >> 16) & 0xFF); b[at + 3] = (byte)((v >> 24) & 0xFF);
        }
        void U16(int at, int v)
        {
            b[at] = (byte)(v & 0xFF); b[at + 1] = (byte)((v >> 8) & 0xFF);
        }
        b[0] = (byte)'R'; b[1] = (byte)'I'; b[2] = (byte)'F'; b[3] = (byte)'F';
        U32(4, (uint)(36 + n));
        b[8] = (byte)'W'; b[9] = (byte)'A'; b[10] = (byte)'V'; b[11] = (byte)'E';
        b[12] = (byte)'f'; b[13] = (byte)'m'; b[14] = (byte)'t'; b[15] = (byte)' ';
        U32(16, 16);
        U16(20, 1);            // PCM
        U16(22, 1);            // mono
        U32(24, (uint)rate);
        U32(28, (uint)(rate * 2));
        U16(32, 2);            // block align
        U16(34, 16);           // bits per sample
        b[36] = (byte)'d'; b[37] = (byte)'a'; b[38] = (byte)'t'; b[39] = (byte)'a';
        U32(40, (uint)n);
        Buffer.BlockCopy(pcm, 0, b, 44, n);
        return b;
    }

    private static byte[] ToPcm16(float[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            int v = (int)(Mathf.Clamp(samples[i], -1f, 1f) * 32767f);
            b[i * 2] = (byte)(v & 0xFF);
            b[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return b;
    }

    // Why a cue did not sound, so a silent log is explainable.
    private static bool CanPlay(IHostContext log)
    {
        try
        {
            if (AudioListener.pause) return false;
            if (AudioListener.volume <= 0.001f)
            {
                if (!_mutedLogged) { _mutedLogged = true; log?.LogInfo("[reel:audio] silent: listener muted (volume 0)"); }
                return false;
            }
            if (Time.timeScale <= 0.01f) return false;
            var scene = SceneManager.GetActiveScene();
            if (!scene.isLoaded) return false;
        }
        catch { }
        return true;
    }

    // The reel's own cues, unchanged: CanPlay() still refuses to sound while the game is paused
    // (timeScale 0), because a lever or a win behind the pause menu is not what the pause menu is
    // doing. The confirmation cue below is a separate entry point for exactly that state.
    //
    // `at` is the booth the cue belongs to (LobbyPropRuntime.LookCentre): the cue is placed there in
    // 3D, so BOTH peers hear which machine turned rather than a sound with no location.
    internal static void Cue(IHostContext log, ReelCue cue, Vector3 at)
    {
        Ensure(log);
        if (!_built) return;
        Sound snd;
        if (!Sounds.TryGetValue(cue, out snd)) return;
        if (!CanPlay(log)) return;
        try
        {
            Channel ch;
            // Started PAUSED, placed, then released: a channel that begins unpaused renders one
            // mix quantum at whatever attributes it had (the world origin), which is exactly the
            // "the whole reel sounds from the middle of the map" artefact.
            var res = RuntimeManager.CoreSystem.playSound(snd, default(ChannelGroup), true, out ch);
            if (res != RESULT.OK) { log?.LogWarning($"[reel:audio] play {cue} -> {res}"); return; }
            string space;
            try
            {
                ch.setVolume(Master);
                space = Place(log, cue, ch, at);
            }
            finally
            {
                // Whatever happened above, a started channel is released: silently keeping it paused
                // would turn a placement failure into a missing cue.
                ch.setPaused(false);
            }
            log?.LogInfo($"[reel:audio] cue={cue} dur={Seconds[cue]:F2}s vol={Master:F2} " +
                         $"at={V(at)} space={space} via=FMOD");
        }
        catch (Exception e) { log?.LogWarning($"[reel:audio] play {cue} failed: {e.Message}"); }
    }

    // One tick per call while the strip moves. The pitch follows the strip speed, so the ticks
    // read as a rising whirr and slow into a countable click-click near the settle. Positioned like
    // every other cue: the ticks belong to the booth whose strip is moving.
    internal static void Tick(IHostContext log, float cardsPerSecond, Vector3 at)
    {
        Ensure(log);
        if (!_built) return;
        Sound snd;
        if (!Sounds.TryGetValue(ReelCue.Tick, out snd)) return;
        if (!CanPlay(log)) return;
        try
        {
            float v = Mathf.Abs(cardsPerSecond);
            float pitch = Mathf.Clamp(0.85f + v * 0.055f, 0.85f, 2.4f);
            if (!_tickChannel.hasHandle()) _tickChannel = default(Channel);
            Channel ch;
            var res = RuntimeManager.CoreSystem.playSound(snd, default(ChannelGroup), true, out ch);
            if (res != RESULT.OK) return;
            try
            {
                ch.setPitch(pitch);
                ch.setVolume(Master * Mathf.Clamp(0.35f + v * 0.02f, 0.35f, 0.9f));
                Place(log, ReelCue.Tick, ch, at);
            }
            finally { ch.setPaused(false); }
            _tickChannel = ch;
        }
        catch { }
    }

    // ---- spatial placement -------------------------------------------------------------

    // Put one (paused or playing) channel at a booth and say what actually happened, so the log line
    // carries the evidence: "3d" means FMOD accepted the position, "2d" means this cue has no
    // positional form or the position was unusable, "2d:<why>" carries the RESULT that refused it.
    // Either way the cue keeps playing: fail safe means audible, never silent.
    private static string Place(IHostContext log, ReelCue cue, Channel ch, Vector3 at)
    {
        if (!Spatial.Contains(cue)) return "2d";
        if (!Positional(at))
        {
            WarnOnce(log, $"cue={cue} position is not finite; attempting 2D fallback");
            return FlatFallback(log, cue, ch, "nonfinite");
        }
        try
        {
            // The core Channel API takes exactly what a mono one-shot needs: where it is and how it
            // moves. The game's own Studio listener stays authoritative for the listener - this code
            // never writes listener attributes and never calls CoreSystem.update - so position is
            // all this side contributes. Velocity stays zero (a booth does not move), which is also
            // why no doppler is applied. Coordinates go through RuntimeUtils so the Unity->FMOD
            // handedness conversion is the game's, not a second copy of it.
            var pos = RuntimeUtils.ToFMODVector(at);
            var vel = RuntimeUtils.ToFMODVector(Vector3.zero);
            var res = ch.set3DAttributes(ref pos, ref vel);
            if (res != RESULT.OK)
            {
                // ERR_NEEDS3D here means the sound did not keep its 3D mode: the cue still plays,
                // unpositioned, and this is logged once instead of once per tick.
                WarnOnce(log, $"cue={cue} set3DAttributes -> {res}; attempting 2D fallback");
                return FlatFallback(log, cue, ch, res.ToString());
            }
            var dist = ch.set3DMinMaxDistance(MinDistance, MaxDistance);
            if (dist != RESULT.OK)
            {
                WarnOnce(log, $"cue={cue} set3DMinMaxDistance {MinDistance:F1}/{MaxDistance:F1} -> {dist}");
                return $"3d:dist={dist}";
            }
            return $"3d:{MinDistance:F1}-{MaxDistance:F1}m";
        }
        catch (Exception e)
        {
            WarnOnce(log, $"cue={cue} 3D placement failed: {e.Message}; attempting 2D fallback");
            return FlatFallback(log, cue, ch, "threw");
        }
    }

    private static string FlatFallback(IHostContext log, ReelCue cue, Channel ch, string reason)
    {
        try
        {
            var result = ch.setMode(MODE._2D);
            if (result == RESULT.OK) return "2d:" + reason;
            log?.LogWarning($"[reel:audio] cue={cue} 2D fallback failed: {result}");
            return "fallback-failed:" + result;
        }
        catch (Exception e)
        {
            log?.LogWarning($"[reel:audio] cue={cue} 2D fallback threw: {e.Message}");
            return "fallback-failed:threw";
        }
    }

    private static void WarnOnce(IHostContext log, string what)
    {
        if (_spatialWarned) return;
        _spatialWarned = true;
        log?.LogWarning($"[reel:audio] {what}");
    }

    // A world position is usable when all three components are finite. A destroyed or not-yet-placed
    // booth can hand over NaN, and FMOD rejects such a vector (ERR_INVALID_VECTOR) - the cue would
    // vanish, which is the one failure mode this must not have.
    internal static bool Positional(Vector3 at) => Finite(at.x, at.y, at.z);

    // The rule itself, in plain floats: UnityEngine's Vector3 is a native-backed interop type here,
    // so this is the part that can be exercised without the game process holding it up
    // (tests/reel-audio-spatial).
    internal static bool Finite(float x, float y, float z) =>
        !(float.IsNaN(x) || float.IsInfinity(x) ||
          float.IsNaN(y) || float.IsInfinity(y) ||
          float.IsNaN(z) || float.IsInfinity(z));

    private static string V(Vector3 v) =>
        $"({v.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}," +
        $"{v.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}," +
        $"{v.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)})";

    // The cue a settled card type makes. The wire kind is ReelCardType (ReelDeck.cs) as a byte, and
    // BOTH peers go through this one mapping - the host from ReelLogic.Resolve, a client from the
    // settle frame - so an outcome cannot sound like a different card there than it did here.
    // Returns false for a kind this build does not know (an older/newer host), so the caller can
    // stay silent rather than play someone else's cue.
    internal static bool TryOutcomeCue(byte kind, out ReelCue cue)
    {
        switch ((ReelCardType)kind)
        {
            case ReelCardType.Item: cue = ReelCue.Win; return true;
            case ReelCardType.Double: cue = ReelCue.Double; return true;
            case ReelCardType.Damage: cue = ReelCue.Damage; return true;
            case ReelCardType.Debuff: cue = ReelCue.Debuff; return true;
            case ReelCardType.Blank: cue = ReelCue.Settle; return true;
            default: cue = ReelCue.Settle; return false;
        }
    }

    internal static void Dispose()
    {
        foreach (var kv in Sounds)
        {
            try { if (kv.Value.hasHandle()) kv.Value.release(); } catch { }
        }
        try { if (_confirm.hasHandle()) _confirm.release(); } catch { }
        _confirm = default(Sound);
        Sounds.Clear();
        Seconds.Clear();
        Pinned.Clear();
        Spatial.Clear();
        _tickChannel = default(Channel);
        _built = false;
        _failed = false;
        _spatialWarned = false;
    }

    // ------------------------------------------------------------------ UI confirmation
    //
    // The pause-menu code's sound: the SAME synthesized ReelCue.Settle SAMPLES, through a flat (2D)
    // Sound of its own that reads the same pinned WAV buffer - built once per generation and
    // released with the rest - played through a path that permits `Time.timeScale == 0`, which is
    // the whole point, because the code is typed while the game is paused and Cue() refuses that
    // state. The Sound is separate because a Sound's 2D/3D mode is fixed at creation: reusing the
    // positional Settle Sound would put the beep at the world origin (or nowhere).
    //
    // What it still respects: `AudioListener.pause` (the game paused all audio) and
    // `AudioListener.volume` (the player muted the game). Nothing forces its way through the mix.
    // It stays 2D on purpose: this is a UI confirmation with no booth behind it, and a pause-menu
    // beep that swung to one side of the room would be a bug, not a feature.
    // Returns the reason, so the caller can log one `result=` field instead of a silent nothing.
    internal static string Confirm(IHostContext log)
    {
        Ensure(log);
        if (!_built) return "silent:not-built";
        // The FLAT Sound, never the positional Settle one: this is a menu confirmation, so it must
        // not take a world position (and must not accidentally ride the 3D channel mode).
        Sound snd = _confirm;
        if (!snd.hasHandle()) return "silent:no-flat-sample";
        try
        {
            if (AudioListener.pause) return "silent:listener-paused";
            if (AudioListener.volume <= 0.001f) return "silent:muted";
            var scene = SceneManager.GetActiveScene();
            if (!scene.isLoaded) return "silent:scene-loading";
        }
        catch { }
        try
        {
            Channel ch;
            var res = RuntimeManager.CoreSystem.playSound(snd, default(ChannelGroup), true, out ch);
            if (res != RESULT.OK) { log?.LogWarning($"[reel:audio] confirm -> {res}"); return "error:" + res; }
            try { ch.setVolume(Master); }
            finally { ch.setPaused(false); }
            // "played" is the word the console-unlock evidence grammar reads
            // (tests/console-unlock/verify_evidence.py); the 2D-ness is structural, not a result.
            return "played";
        }
        catch (Exception e)
        {
            log?.LogWarning($"[reel:audio] confirm failed: {e.Message}");
            return "error:" + e.GetType().Name;
        }
    }

    // ---- synthesis ---------------------------------------------------------------------

    private enum Wave { Sine, Square, Triangle, Noise }

    private static float[] Buf(float seconds) => new float[Mathf.Max(1, (int)(seconds * Rate))];

    private static void Normalize(float[] b, float peak)
    {
        float max = 0f;
        for (int i = 0; i < b.Length; i++) { float a = b[i] < 0 ? -b[i] : b[i]; if (a > max) max = a; }
        if (max <= 0.0001f) return;
        float k = peak / max;
        for (int i = 0; i < b.Length; i++) b[i] *= k;
    }

    // A partial with a linear frequency sweep and an exponential decay envelope. Phase is
    // integrated per sample, so a sweep never clicks.
    private static void Tone(float[] b, float at, float dur, float f0, float f1, float amp,
                             Wave wave, float decay = 6f, int seed = 12345)
    {
        int start = (int)(at * Rate);
        int n = Mathf.Min((int)(dur * Rate), b.Length - start);
        if (start < 0 || n <= 0) return;
        float phase = 0f;
        uint s = (uint)(seed * 2654435761u);
        int attack = Mathf.Max(1, (int)(0.004f * Rate));
        int release = Mathf.Max(1, (int)(0.010f * Rate));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float f = f0 + (f1 - f0) * (i / (float)n);
            phase += 2f * Mathf.PI * f / Rate;
            float v;
            switch (wave)
            {
                case Wave.Square: v = Mathf.Sin(phase) >= 0f ? 1f : -1f; break;
                case Wave.Triangle: v = 2f * Mathf.Abs(2f * (phase / (2f * Mathf.PI) - Mathf.Floor(phase / (2f * Mathf.PI) + 0.5f))) - 1f; break;
                case Wave.Noise:
                    s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                    v = ((s & 0xFFFF) / 32768f) - 1f;
                    break;
                default: v = Mathf.Sin(phase); break;
            }
            float env = Mathf.Exp(-decay * t);
            if (i < attack) env *= i / (float)attack;
            if (i > n - release) env *= (n - i) / (float)release;
            b[start + i] += v * amp * env;
        }
    }

    // Filtered noise: a one-pole low-pass over white noise, for wood/plastic knocks and clunks.
    private static void Thump(float[] b, float at, float dur, float amp, float cutoff, float decay, int seed)
    {
        int start = (int)(at * Rate);
        int n = Mathf.Min((int)(dur * Rate), b.Length - start);
        if (start < 0 || n <= 0) return;
        float a = Mathf.Clamp01(cutoff * 2f * Mathf.PI / Rate);
        float y = 0f;
        uint s = (uint)(seed * 40503u + 7u);
        for (int i = 0; i < n; i++)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            float white = ((s & 0xFFFF) / 32768f) - 1f;
            y += a * (white - y);
            float env = Mathf.Exp(-decay * i / (float)Rate);
            b[start + i] += y * amp * env * 3.2f;
        }
    }

    private static float[] BuildLever()
    {
        var b = Buf(0.46f);
        // Ratchet: three filtered clicks as the lever cocks, rising in pitch.
        Thump(b, 0.000f, 0.05f, 0.55f, 2600f, 90f, 11);
        Thump(b, 0.085f, 0.045f, 0.5f, 3100f, 95f, 12);
        Thump(b, 0.165f, 0.040f, 0.45f, 3600f, 100f, 13);
        // Body: the cabinet takes the pull.
        Tone(b, 0.02f, 0.34f, 150f, 78f, 0.42f, Wave.Sine, 9f);
        Tone(b, 0.02f, 0.22f, 300f, 160f, 0.16f, Wave.Triangle, 12f);
        // Metal tail: the lever spring.
        Tone(b, 0.10f, 0.30f, 1850f, 1720f, 0.10f, Wave.Sine, 11f, 21);
        Tone(b, 0.10f, 0.22f, 2760f, 2600f, 0.05f, Wave.Sine, 14f, 22);
        return b;
    }

    private static float[] BuildTick()
    {
        var b = Buf(0.055f);
        Thump(b, 0f, 0.018f, 0.7f, 5200f, 220f, 31);
        Tone(b, 0f, 0.040f, 2300f, 2100f, 0.32f, Wave.Sine, 70f, 32);
        Tone(b, 0f, 0.030f, 3500f, 3300f, 0.12f, Wave.Triangle, 90f, 33);
        return b;
    }

    private static float[] BuildSettle()
    {
        var b = Buf(0.42f);
        // Card on the stop: a hard knock, then the frame ringing.
        Thump(b, 0f, 0.07f, 0.85f, 1800f, 55f, 41);
        Tone(b, 0f, 0.30f, 190f, 120f, 0.55f, Wave.Sine, 11f);
        Tone(b, 0f, 0.16f, 420f, 300f, 0.20f, Wave.Triangle, 16f);
        Tone(b, 0.004f, 0.34f, 1460f, 1380f, 0.11f, Wave.Sine, 12f, 42);
        Tone(b, 0.004f, 0.24f, 2180f, 2050f, 0.06f, Wave.Sine, 15f, 43);
        return b;
    }

    private static float[] BuildWin()
    {
        var b = Buf(1.05f);
        // A three-note bell arpeggio with an octave sparkle, each note two partials.
        float[] f = { 523.25f, 659.25f, 783.99f, 1046.5f };
        for (int i = 0; i < 3; i++)
        {
            Tone(b, i * 0.085f, 0.75f - i * 0.1f, f[i], f[i], 0.30f, Wave.Sine, 5.5f, 50 + i);
            Tone(b, i * 0.085f, 0.55f, f[i] * 2f, f[i] * 2f, 0.10f, Wave.Triangle, 8f, 60 + i);
        }
        Tone(b, 0.26f, 0.75f, 1567.98f, 1567.98f, 0.12f, Wave.Sine, 7f, 70);
        Tone(b, 0.30f, 0.45f, 2093f, 2093f, 0.05f, Wave.Sine, 9f, 71);
        return b;
    }

    private static float[] BuildDouble()
    {
        var b = Buf(1.5f);
        // Two-note fanfare, each note stacked at the fifth and the octave, then a shimmer tail.
        float[] root = { 392.0f, 523.25f };
        float[] when = { 0f, 0.20f };
        for (int i = 0; i < 2; i++)
        {
            float at = when[i], dur = i == 0 ? 0.55f : 1.0f;
            Tone(b, at, dur, root[i], root[i], 0.34f, Wave.Sine, i == 0 ? 6f : 3.4f, 80 + i);
            Tone(b, at, dur * 0.8f, root[i] * 1.5f, root[i] * 1.5f, 0.20f, Wave.Triangle, i == 0 ? 7f : 4f, 90 + i);
            Tone(b, at, dur * 0.7f, root[i] * 2f, root[i] * 2f, 0.12f, Wave.Sine, i == 0 ? 9f : 5f, 100 + i);
        }
        Tone(b, 0.20f, 0.95f, 1046.5f, 1046.5f, 0.14f, Wave.Sine, 4.5f, 110);
        Tone(b, 0.22f, 0.85f, 1567.98f, 1567.98f, 0.08f, Wave.Sine, 5.5f, 111);
        Thump(b, 0.20f, 0.10f, 0.18f, 6000f, 30f, 112);
        return b;
    }

    private static float[] BuildDamage()
    {
        var b = Buf(0.55f);
        // Body hit: sub sweep + a dull crack + a low tail.
        Tone(b, 0f, 0.45f, 120f, 44f, 0.75f, Wave.Sine, 7f);
        Tone(b, 0f, 0.32f, 240f, 90f, 0.32f, Wave.Triangle, 10f);
        Thump(b, 0f, 0.10f, 0.85f, 900f, 40f, 121);
        Thump(b, 0.02f, 0.16f, 0.35f, 320f, 18f, 122);
        return b;
    }

    private static float[] BuildDebuff()
    {
        var b = Buf(0.62f);
        // A detuned buzz sagging in pitch: "something is wrong with you now".
        Tone(b, 0.01f, 0.50f, 230f, 96f, 0.30f, Wave.Square, 4.0f, 131);
        Tone(b, 0.01f, 0.50f, 233f, 99f, 0.18f, Wave.Square, 4.0f, 132);
        Tone(b, 0.02f, 0.40f, 690f, 300f, 0.06f, Wave.Triangle, 6f, 133);
        Thump(b, 0.0f, 0.08f, 0.30f, 1200f, 30f, 134);
        return b;
    }

    private static float[] BuildError()
    {
        var b = Buf(0.40f);
        // Two short refusals, like a card reader: no, then no again in a lower voice.
        Tone(b, 0.00f, 0.10f, 170f, 150f, 0.42f, Wave.Square, 9f, 141);
        Tone(b, 0.00f, 0.10f, 340f, 320f, 0.12f, Wave.Triangle, 10f, 142);
        Tone(b, 0.15f, 0.14f, 120f, 104f, 0.42f, Wave.Square, 8f, 143);
        Tone(b, 0.15f, 0.14f, 240f, 210f, 0.10f, Wave.Triangle, 9f, 144);
        return b;
    }
}
