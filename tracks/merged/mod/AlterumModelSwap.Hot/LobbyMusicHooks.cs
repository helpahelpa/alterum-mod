using System;
using System.Collections.Generic;
using System.IO;
using AlterumModelSwap.Contracts;
using AudioPlayer.Core;
using AudioPlayer.UI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// The UI diversion - and the ownership boundary that goes with it.
//
// The game's music player is a panel (`AudioPlayer.UI.AudioPlayerUI`) whose Load button calls
// `OnLoadClicked()`: it validates the URL field and hands it to its own controller, which downloads
// the whole file into memory before FMOD sees it. That is fine for a direct .mp3 and impossible for
// a page URL. So exactly one method is patched on the load path, and it is a UI method - nothing in
// the player's internals (no Audio3DSystem, no UrlAudioDataProvider, no FmodProgrammerInstrumentPlayer).
//
// OWNERSHIP. The panel is either the game's or ours, never both:
//
//   native   our `MusicStreamHost` has no request outstanding. Nothing here writes the panel: no
//            state, title, clock, controls or progress. This is the whole reason pause on a direct
//            link used to do nothing - a stale Complete/Failed host was writing `_state`, the status
//            ("CACHE") and the time over the native player every 0.25 s, and the play/pause prefixes
//            consumed the click for a stream that was not the audible one.
//   ours     exactly one request is outstanding. Every native refresh/state callback for the panel
//            is diverted (RefreshFromController, OnStateChanged, RefreshProgress,
//            OnLoadProgressChanged, OnLoadRetryChanged, OnPlaybackFinished) and the panel is driven
//            through the game's own presentation methods: ApplyState (+ StateChanged, which is what
//            the glyph modifiers listen to), SetStatus, SetTitle, SetTimeText,
//            SetControlsInteractable. Exactly one glyph is actionable: pause while playing, play
//            while paused, neither when there is no playable channel. On a follower the glyphs are
//            actionable too, but a click is a CONTROL REQUEST to the host: the local channel is not
//            committed from the click, only from the state the host publishes. Volume stays local.
//
// Takeover is explicit (the game's own track is stopped through its controller first) and so is
// relinquishment (a direct link stops our stream, tells peers, and hands the panel back).
internal static class LobbyMusicHooks
{
    internal const string Tag = "[music:ui]";

    private static Harmony _harmony;
    private static IHostContext _log;
    private static MusicStreamHost _host;
    private static bool _installed;
    private static float _nextMirror;
    private static string _lastStatus, _lastTitle, _lastError, _lastSig;
    private static int _lastState = -1, _lastUiId;
    private static string _lastTimeText;      // what we last put in the clock widget
    private static string _lastDrift;         // last "something else wrote the panel" line
    private static bool _applyingState;       // re-entrancy guard for our own ApplyState call
    private static int _prefixHits, _directPass, _takeovers, _closeStops, _playHits, _pauseHits,
                       _pauseRefused, _resumeRefused, _nativeStops, _refreshSkips, _seeks, _volumes,
                       _handbacks;

    internal static MusicStreamHost Host => _host;
    internal static bool Installed => _installed;

    // ================================================================ lifecycle

    internal static void Install(IHostContext log)
    {
        _log = log;
        if (_installed) { log?.LogInfo($"{Tag} already installed (reload guard)"); return; }
        _host = new MusicStreamHost(log);
        LobbyMusicNet.SetLog(log);
        YtDlpPipeline.LogCacheRoot(log);

        var harmony = new Harmony("com.user0.alterum.music." + Guid.NewGuid().ToString("N"));
        var load = AccessTools.Method(typeof(AudioPlayerUI), "OnLoadClicked", Type.EmptyTypes);
        if (load == null)
        {
            log?.LogWarning($"{Tag} AudioPlayerUI.OnLoadClicked not found - music streaming is off");
            _host = null;
            return;
        }
        int patched = 0, missing = 0;
        void Prefix(System.Reflection.MethodInfo m, string name)
        {
            if (m == null) { missing++; log?.LogInfo($"{Tag} no AudioPlayerUI.{name}; that seam stays native"); return; }
            try
            {
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(LobbyMusicHooks), name));
                patched++;
            }
            catch (Exception e)
            {
                missing++;
                log?.LogWarning($"{Tag} patching {name} failed ({e.GetType().Name}: {e.Message})");
            }
        }

        try
        {
            Prefix(load, nameof(LoadClickedPrefix));
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "OnPlayClicked", Type.EmptyTypes), nameof(PlayClickedPrefix));
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "OnPauseClicked", Type.EmptyTypes), nameof(PauseClickedPrefix));
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "OnCloseClicked", Type.EmptyTypes), nameof(CloseClickedPrefix));
            // The panel's own view of its controller. While we own playback these all describe a
            // track that is not the audible one.
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "RefreshFromController", Type.EmptyTypes), nameof(RefreshFromControllerPrefix));
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "RefreshProgress", Type.EmptyTypes), nameof(RefreshProgressPrefix));
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "OnStateChanged", new[] { typeof(PlayerState) }), nameof(OnStateChangedPrefix));
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "OnLoadProgressChanged", new[] { typeof(float) }), nameof(LoadProgressChangedPrefix));
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "OnLoadRetryChanged", new[] { typeof(int) }), nameof(LoadRetryChangedPrefix));
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "OnPlaybackFinished", Type.EmptyTypes), nameof(PlaybackFinishedPrefix));
            // Listening controls that belong to the audible stream while it is ours.
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "OnVolumeDrag", new[] { typeof(float) }), nameof(VolumeDragPrefix));
            Prefix(AccessTools.Method(typeof(AudioPlayerUI), "OnProgressSliderReleased", Type.EmptyTypes), nameof(ProgressReleasedPrefix));

            _harmony = harmony;
            _installed = true;
            log?.LogInfo($"{Tag} installed: {patched} seam(s) on AudioPlayerUI (OnLoadClicked included; " +
                         $"{missing} unavailable); yt-dlp URLs are streamed, direct audio links stay with the game");
        }
        catch (Exception e)
        {
            try { harmony.UnpatchSelf(); } catch { }
            log?.LogWarning($"{Tag} patch failed ({e.GetType().Name}: {e.Message}) - music player untouched");
            try { _host?.Dispose(); } catch { }
            _host = null;
        }
    }

    internal static bool InLobby => SceneManager.GetActiveScene().name == "Lobby";

    internal static void Tick(IHostContext log)
    {
        if (_log == null) _log = log;
        var h = _host;
        if (h == null) return;
        if (!InLobby)
        {
            // The FMOD stream outlives the scene's player UI; stop it ourselves. This also
            // retires a download that was buffering (or a channel paused) during the transition.
            if (h.OwnsPanel) h.Stop("left Lobby scene");
            LobbyMusicNet.EndLobbyPlayback();
            return;
        }
        try { h.Tick(Time.unscaledTime); } catch (Exception e) { log?.LogWarning($"{Tag} host tick threw {e.GetType().Name}: {e.Message}"); }
        try { LobbyMusicNet.Tick(log, h); } catch (Exception e) { log?.LogWarning($"{Tag} net tick threw {e.GetType().Name}: {e.Message}"); }
        if (Time.unscaledTime < _nextMirror) return;
        _nextMirror = Time.unscaledTime + 0.25f;
        try { Mirror(h); } catch (Exception e) { log?.LogWarning($"{Tag} mirror threw {e.GetType().Name}: {e.Message}"); }
    }

    internal static void Dispose()
    {
        if (_harmony != null)
        {
            try { _harmony.UnpatchSelf(); } catch { }
            _harmony = null;
        }
        _installed = false;
        try { _host?.Dispose(); } catch { }
        _host = null;
        LobbyMusicNet.Reset();
        _lastStatus = null; _lastTitle = null; _lastError = null; _lastSig = null; _lastState = -1;
        _lastTimeText = null; _lastDrift = null; _lastUiId = 0; _applyingState = false;
        try { _log?.LogInfo($"{Tag} uninstalled: prefixes removed, stream torn down " +
                             $"({Counters})"); } catch { }
        _log = null;
    }

    // ================================================================ the load path

    // ONE takeover ritual for every mod-side start: the UI load path, the console `play` request,
    // the host's adoption of an accepted request, and the remote follow. Starting a stream without
    // it leaves two playback owners - the game's controller keeps its own track audible while the
    // host claims the panel and starts writing STOP/title/time/controls over it. Keeping the three
    // steps here (and the host's Play/PlayAt reachable only through here) is what stops a fifth
    // entry point from skipping them.
    //
    // This is the LOCAL STREAM START, and nothing else: it sends no request and publishes nothing. A
    // remote follow calls it directly, which is why following a host's revision cannot recursively
    // ask the host for the same URL again.
    // `atMs` < 0 is a plain start; >= 0 is the follow path's current target. `startPaused` creates
    // the first channel (and verifies it) paused, so a paused host never emits a frame of audio.
    internal static bool StartRequest(string url, string source, long atMs = -1,
                                      AudioPlayerUI ui = null, bool startPaused = false)
    {
        var h = _host;
        if (h == null || !InLobby) return false;
        if (ui == null) ui = FindUi();
        // 1. the game's own track is a different URL: stop it through its controller first,
        //    otherwise its handle keeps feeding the panel the old title and clock.
        StopNative(ui, "takeover");
        // 2. claim the transport.
        bool started = atMs < 0
            ? h.Play(url, source)
            : h.PlayAt(url, atMs, source, startPaused);
        if (!started) return false;
        // 3. the panel starts from nothing, under our phase.
        ClearPanel(ui, h, source + " start");
        _takeovers++;
        return true;
    }

    // What a local load request turned out to be: a direct audio link the game's own player handles
    // (Passthrough), a URL this mod took responsibility for (Sent), or a page link that is ours but
    // could not be submitted (Refused - the caller must NOT fall through to the game's downloader,
    // which cannot stream a page link and would report the wrong reason).
    internal enum IntentOutcome { Passthrough, Sent, Refused }

    private static string _lastRefusal = "";
    // Why the last SubmitIntent refused, for the panel/log line. Empty otherwise.
    internal static string LastRefusal => _lastRefusal;

    // LOCAL INTENT. The player asked for this URL (UI load, console `play`). What happens next is
    // the authority's decision, and it is the same on both sides of the wire:
    //
    //   host       accept it: the takeover ritual, then the revision bump and the immediate publish.
    //   follower   submit it: id + URL to the host, the current session track keeps playing. No
    //              optimistic start, no handoff, no audio transfer - every peer downloads the
    //              accepted URL independently, and this machine starts it when that revision comes
    //              back as an authoritative state frame.
    //
    // Passthrough means the mod is not responsible for the URL (the caller must run the game's own
    // loader). Sent and Refused mean it is: the game's loader must be skipped either way.
    internal static IntentOutcome SubmitIntent(string url, string source, AudioPlayerUI ui = null)
    {
        _lastRefusal = "";
        if (_host == null) return IntentOutcome.Passthrough;
        url = (url ?? "").Trim();
        if (YtDlpPipeline.Classify(url) != YtDlpPipeline.UrlKind.Page) return IntentOutcome.Passthrough;
        if (!InLobby)
        {
            _lastRefusal = "yt-dlp music is only available in the Lobby scene";
            return IntentOutcome.Refused;
        }
        if (LobbyMusicNet.IsAuthority)
        {
            if (AuthoritativeStart(url, source, -1, ui, false)) return IntentOutcome.Sent;
            _lastRefusal = "this machine is the host and could not start a stream for that link " +
                           "(the takeover was refused)";
            _log?.LogWarning($"{Tag} NOT started '{Short(url, 90)}': {_lastRefusal}");
            ShowNote(_lastRefusal);
            return IntentOutcome.Refused;
        }
        if (!LobbyMusicNet.SubmitRequest(url, source))
        {
            // The REAL reason - "no host to ask" here used to be reported as "not a yt-dlp link"
            // while the click fell through to the game's own downloader.
            _lastRefusal = LobbyMusicNet.LastSubmitRefusal.Length > 0
                ? LobbyMusicNet.LastSubmitRefusal
                : "the request could not be submitted to the host";
            _log?.LogWarning($"{Tag} NOT submitted '{Short(url, 90)}': {_lastRefusal}; the current session " +
                             "track keeps playing and the game's downloader is not used");
            ShowNote(_lastRefusal);
            return IntentOutcome.Refused;
        }
        ShowNote("requested from the host: waiting for its verdict");
        return IntentOutcome.Sent;
    }

    // AUTHORITATIVE START. Only the host adopts a URL into the session. The takeover ritual runs
    // first; the revision is bumped and published only once it succeeded, so peers never see a
    // revision for a track this build refused.
    internal static bool AuthoritativeStart(string url, string why, long atMs = -1,
                                            AudioPlayerUI ui = null, bool startPaused = false)
    {
        if (!LobbyMusicNet.IsAuthority) return false;
        bool started;
        // The ritual publishes nothing: the stop of the request it replaces would otherwise reach the
        // peers as "the session stopped" and make them drop a stream they are about to keep.
        LobbyMusicNet.BeginRitual();
        try { started = StartRequest(url, why, atMs, ui, startPaused); }
        finally { LobbyMusicNet.EndRitual(); }
        if (!started)
        {
            LobbyMusicNet.NoteStartRefused(url, "start refused");
            return false;
        }
        LobbyMusicNet.NoteAcceptedStart(url, why);
        return true;
    }

    // A request that is waiting for the host, or one it refused: the session track is untouched, so
    // the note line is the only place this can be said - and only while the panel is ours.
    internal static void NoteRequestRejected(string reason)
    {
        if (_host == null) return;
        ShowNote(reason);
    }

    // A transient sentence on the panel (a rejected/timeout control, a refused request). It is
    // held by the mirror for NoteSeconds so a forced re-render cannot erase it immediately, and it
    // expires on its own - a note never leaves the panel stuck.
    private const float NoteSeconds = 4f;
    private static string _note;
    private static float _noteUntil;

    private static void ShowNote(string text)
    {
        var h = _host;
        if (h == null || string.IsNullOrEmpty(text)) return;
        var ui = FindUi();
        if (ui == null) return;
        if (!h.OwnsPanel) return;                    // the game's own player owns the panel here
        _note = text;
        _noteUntil = Time.unscaledTime + NoteSeconds;
        try { ui.ShowError(text); } catch { }
        try { if (ui._errorText != null) ui._errorText.text = text; } catch { }
        _lastError = text;                           // the mirror's next pass must not fight this
        _log?.LogInfo($"{Tag} note shown: '{Short(text, 110)}'");
    }

    private static string NoteInForce()
    {
        if (string.IsNullOrEmpty(_note)) return null;
        if (Time.unscaledTime >= _noteUntil) { _note = null; return null; }
        return _note;
    }

    private static bool LoadClickedPrefix(AudioPlayerUI __instance)
    {
        _prefixHits++;
        try
        {
            string url = null;
            try { url = __instance?._urlInputField?.text; } catch (Exception e) { _log?.LogWarning($"{Tag} url field: {e.Message}"); }
            var host = _host;
            if (host == null) return true;
            if (string.IsNullOrWhiteSpace(url)) return true;

            var kind = YtDlpPipeline.Classify(url);
            if (kind != YtDlpPipeline.UrlKind.Page)
            {
                _directPass++;
                bool released = Relinquish(__instance, "direct link");
                _log?.LogInfo($"{Tag} LOAD '{Short(url, 90)}' kind={kind} -> the game's own player " +
                              $"(passthrough){(released ? "; our stream released, panel handed back" : "")}");
                return true;                         // direct audio: the game's path, untouched
            }

            // Takeover: the game's own track is a different URL. StartRequest stops it through its
            // controller first, then claims the transport and clears the panel. On a follower this
            // is only the REQUEST: the panel and the audible stream stay on the session track until
            // the host publishes an accepted revision.
            var outcome = SubmitIntent(url, "ui", __instance);
            if (outcome == IntentOutcome.Passthrough) { _directPass++; return true; }
            if (outcome == IntentOutcome.Refused)
            {
                // Ours and refused: the game's downloader cannot stream a page link either, so
                // falling through would replace the real reason with "not a yt-dlp link" while the
                // native player tried to fetch a page. The click is consumed and the session track
                // keeps playing.
                _log?.LogWarning($"{Tag} LOAD '{Short(url, 90)}' NOT loaded: {LastRefusal} " +
                                 "(the game's downloader is skipped for a page link)");
                return false;
            }
            bool isHost = LobbyMusicNet.IsAuthority;
            _log?.LogInfo($"{Tag} LOAD '{Short(url, 90)}' -> " +
                          (isHost
                              ? $"taken over (hit #{_prefixHits}); the game's downloader is skipped, " +
                                "FMOD streams a growing snapshot"
                              : $"submitted to the host (hit #{_prefixHits}); the current session " +
                                "track keeps playing until the accepted revision arrives"));
            return false;                            // skip the game's load path
        }
        catch (Exception e)
        {
            _log?.LogError($"{Tag} OnLoadClicked prefix threw ({e.GetType().Name}: {e.Message}); falling through");
            return true;
        }
    }

    // Stop the game's track through the controller the panel is actually using.
    private static void StopNative(AudioPlayerUI ui, string why)
    {
        IAudioPlayerController ctrl = null;
        try { ctrl = ui?._controller; } catch (Exception e) { _log?.LogWarning($"{Tag} {why}: controller read failed: {e.Message}"); }
        if (ctrl == null)
        {
            _log?.LogInfo($"{Tag} {why}: the panel has no controller to stop");
            return;
        }
        string url = null;
        int state = -1;
        bool handle = false;
        try { url = ctrl.CurrentUrl; } catch { }
        try { state = (int)ctrl.CurrentPlayerState; } catch { }
        try { handle = ctrl.ActiveHandle != null; } catch { }
        if (!handle && state <= 0 && string.IsNullOrEmpty(url))
        {
            _log?.LogInfo($"{Tag} {why}: the game's controller is idle (state={state} url='{Short(url, 60)}'); nothing to stop");
            return;
        }
        try
        {
            ctrl.Stop();
            _nativeStops++;
            _log?.LogInfo($"{Tag} {why}: stopped the game's own track through its controller " +
                          $"(state={state} handle={handle} url='{Short(url, 90)}')");
        }
        catch (Exception e)
        {
            _log?.LogWarning($"{Tag} {why}: controller.Stop() threw {e.GetType().Name}: {e.Message}");
        }
    }

    // A direct link is the game's again: production stops, files are released, peers are told, and
    // the panel is refreshed from its own controller (our prefixes no longer match).
    private static bool Relinquish(AudioPlayerUI ui, string why)
    {
        var h = _host;
        if (h == null) return false;
        if (!h.OwnsPanel && !h.OwnsPlayback) return false;
        long pos = h.PositionMs;
        h.Stop(why);                             // publishes the stop through NoteTransport
        LobbyMusicNet.ResetFollow(why);
        Invalidate();
        _handbacks++;
        try { ui?.RefreshFromController(); }
        catch (Exception e) { _log?.LogInfo($"{Tag} native refresh after hand-back skipped: {e.Message}"); }
        _log?.LogInfo($"{Tag} RELINQUISH '{why}': our stream stopped at {pos}ms and the panel is the game's again " +
                      $"(handbacks={_handbacks})");
        return true;
    }

    // ================================================================ click seams

    // A follower's transport click. Nothing is committed here: the request goes to the host and the
    // local channel moves only when the accepted state comes back. The return value is "the click was
    // a control request" (the Harmony prefix still consumes it), not "the transport is now paused".
    private static bool SubmitFollowerControl(byte action, long targetMs, string what)
    {
        if (!LobbyMusicNet.SubmitControl(action, targetMs, what, out string refusal))
        {
            _log?.LogInfo($"{Tag} {what}: not sent: {refusal}");
            ShowNote(refusal);
            Mirror(_host, force: true);
            return false;
        }
        _log?.LogInfo($"{Tag} {what}: requested from the host; the local transport is unchanged " +
                      "until the accepted state is published");
        Mirror(_host, force: true);
        return true;
    }

    private static bool PlayClickedPrefix(AudioPlayerUI __instance)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel || !h.HasPlayableChannel) return true;
        // A follower owns no transport: the click asks the host, it does not resume locally (that
        // would diverge from the session until the next publication).
        if (!LobbyMusicNet.IsAuthority)
        {
            SubmitFollowerControl(LobbyMusicNet.ControlResume, 0, "PLAY click");
            return false;
        }
        _playHits++;
        if (!h.Resume())
        {
            _resumeRefused++;
            Mirror(h, force: true);                  // nothing changed: keep showing what is true
            _log?.LogWarning($"{Tag} PLAY pressed but FMOD did not resume the channel " +
                             $"(phase={h.Current} pos={h.PositionMs}ms); phase unchanged");
            return false;
        }
        Mirror(h, force: true);
        _log?.LogInfo($"{Tag} PLAY pressed -> our channel resumed at {h.PositionMs}ms " +
                      $"(hit #{_playHits}, real paused={h.Engine.IsPaused}); the commit publishes the " +
                      "session state");
        return false;
    }

    private static bool PauseClickedPrefix(AudioPlayerUI __instance)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel || !h.HasPlayableChannel) return true;
        if (!LobbyMusicNet.IsAuthority)
        {
            SubmitFollowerControl(LobbyMusicNet.ControlPause, 0, "PAUSE click");
            return false;
        }
        _pauseHits++;
        if (!h.Pause())
        {
            _pauseRefused++;
            Mirror(h, force: true);
            _log?.LogWarning($"{Tag} PAUSE pressed but FMOD did not pause the channel " +
                             $"(phase={h.Current} pos={h.PositionMs}ms); phase unchanged");
            return false;
        }
        Mirror(h, force: true);
        _log?.LogInfo($"{Tag} PAUSE pressed -> our channel paused at {h.PositionMs}ms " +
                      $"(hit #{_pauseHits}, real paused={h.Engine.IsPaused}); the commit publishes the " +
                      "session state");
        return false;
    }

    private static bool CloseClickedPrefix(AudioPlayerUI __instance)
    {
        var h = _host;
        if (h == null || (!h.OwnsPanel && !h.OwnsPlayback)) return true;
        _closeStops++;
        long pos = h.PositionMs;
        h.Stop("panel closed");                  // publishes the stop through NoteTransport
        LobbyMusicNet.ResetFollow("panel closed");
        Invalidate();
        _log?.LogInfo($"{Tag} panel closed -> our stream stopped at {pos}ms (stop #{_closeStops}); " +
                      "the game's close path proceeds");
        return true;                                  // native close/hide is the game's again
    }

    // ---- native callbacks that would describe the wrong track

    private static bool RefreshFromControllerPrefix(AudioPlayerUI __instance)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel) return true;
        _refreshSkips++;
        return false;
    }

    private static bool OnStateChangedPrefix(AudioPlayerUI __instance, PlayerState newState)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel) return true;
        // Our own ApplyState call may re-enter here; that transition is the one we asked for.
        if (_applyingState) return true;
        _refreshSkips++;
        _log?.LogInfo($"{Tag} diverted the panel's controller state={newState} while our stream owns it " +
                      $"(real state={h.Current})");
        return false;
    }

    private static bool RefreshProgressPrefix(AudioPlayerUI __instance)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel) return true;
        return false;
    }

    private static bool LoadProgressChangedPrefix(AudioPlayerUI __instance, float p)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel) return true;
        return false;                                 // the game's load progress is another track
    }

    private static bool LoadRetryChangedPrefix(AudioPlayerUI __instance, int attempt)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel) return true;
        _log?.LogInfo($"{Tag} diverted load-retry {attempt} from the game's loader while our stream owns the panel");
        return false;
    }

    private static bool PlaybackFinishedPrefix(AudioPlayerUI __instance)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel) return true;
        return false;                                 // our stream's end is the host's business
    }

    // ---- listening controls, routed to the audible stream

    private static bool VolumeDragPrefix(AudioPlayerUI __instance, float volume)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel || !h.HasPlayableChannel) return true;
        _volumes++;
        bool ok = h.SetVolume(volume);
        _log?.LogInfo($"{Tag} volume slider {volume:0.###} -> our stream ok={ok} (hit #{_volumes})");
        Mirror(h, force: true);
        return false;
    }

    private static bool ProgressReleasedPrefix(AudioPlayerUI __instance)
    {
        var h = _host;
        if (h == null || !h.OwnsPanel) return true;
        // The native release handler is where the drag flag is cleared, so it has to be cleared here
        // too: otherwise the panel would stay "being dragged" for the rest of the track.
        try { __instance._isDraggingProgressSlider = false; } catch { }
        if (h.IsGrowing)
        {
            _log?.LogInfo($"{Tag} progress slider released while the track is still growing: ignored " +
                          "(the slider shows buffer fill, the track is not seekable yet)");
            Mirror(h, force: true);
            return false;
        }
        long total = h.LengthMs;
        if (total <= 0 || !h.HasPlayableChannel) { Mirror(h, force: true); return false; }
        double f = 0;
        try
        {
            var s = __instance?._progressSlider;
            if (s != null && s.maxValue > s.minValue) f = (s.value - s.minValue) / (s.maxValue - s.minValue);
        }
        catch { }
        long ms = (long)(f * total);
        if (!LobbyMusicNet.IsAuthority)
        {
            // Position is host-authoritative: ask for the absolute target, and let the host decide
            // whether its own coverage can hold it. The slider is rebuilt from the session target.
            SubmitFollowerControl(LobbyMusicNet.ControlSeek, ms, "SEEK click");
            return false;
        }
        _seeks++;
        bool ok = h.Seek(ms);
        _log?.LogInfo($"{Tag} progress slider released at {f:0.###} -> seek({ms}ms) ok={ok} " +
                      $"pos={h.PositionMs}ms (hit #{_seeks})");
        Mirror(h, force: true);
        return false;
    }

    // ================================================================ presentation

    internal static void Mirror(MusicStreamHost host, bool force = false)
    {
        if (host == null) return;
        var ui = FindUi();
        if (ui == null) return;

        // Native playback owns the panel. Every widget below belongs to the game's own player in
        // that case, so not one of them is written here.
        if (!host.OwnsPanel) return;

        try
        {
            int uiId = 0;
            try { uiId = ui.GetInstanceID(); } catch { }
            if (uiId != _lastUiId) { _lastUiId = uiId; Invalidate(); }

            // Diagnostic: if any widget we last wrote no longer holds our value, something else is
            // still writing this panel. That is the class of bug this seam exists to prevent, so it
            // has to be visible in the log rather than merely corrected.
            CheckDrift(ui);

            int want = host.UiState();
            bool stateChanged = false;
            if (force || want != _lastState)
            {
                _lastState = want;
                ApplyState(ui, want);
                _lastStatus = null; _lastTitle = null;   // ApplyState writes the game's own texts
                stateChanged = true;
            }

            bool pending = LobbyMusicNet.ControlPending;
            string status = pending ? "SYNC" : host.StatusText();
            var note = NoteInForce();
            string error = note ?? host.ErrorText();
            string title = host.DisplayTitle ?? "";
            bool statusWritten = false;

            if (force || !string.Equals(status, _lastStatus, StringComparison.Ordinal))
            {
                _lastStatus = status;
                try { if (ui._statusText != null) ui._statusText.text = status; statusWritten = true; }
                catch (Exception e) { _log?.LogWarning($"{Tag} status text write failed: {e.Message}"); }
            }

            if (force || !string.Equals(error, _lastError, StringComparison.Ordinal))
            {
                _lastError = error;
                if (string.IsNullOrEmpty(error))
                {
                    try { if (ui._errorText != null) ui._errorText.text = ""; } catch { }
                    try { ui.HideError(); } catch { }
                }
                else
                {
                    try { ui.ShowError(error); } catch (Exception e) { _log?.LogWarning($"{Tag} ShowError failed: {e.Message}"); }
                    try { if (ui._errorText != null) ui._errorText.text = error; } catch { }
                    _log?.LogWarning($"{Tag} error shown to the player: '{error}'");
                }
            }

            if (force || !string.Equals(title, _lastTitle, StringComparison.Ordinal))
            {
                _lastTitle = title;
                try { ui.SetTitle(title); } catch (Exception e) { _log?.LogInfo($"{Tag} SetTitle failed: {e.Message}"); }
            }

            bool growing = host.IsGrowing;
            bool hasChannel = host.HasPlayableChannel;
            long pos = host.PositionMs, total = host.LengthMs;
            bool dragging = false;
            try { dragging = ui._isDraggingProgressSlider; } catch { }

            if (!hasChannel && !growing)
            {
                try { ui.SetTimeText(0, 0); _lastTimeText = null; } catch { }
                if (!dragging) SetSlider(ui, 0, false);
            }
            else if (growing)
            {
                // Growing snapshot: elapsed / STREAM. The slider fills with buffer availability and
                // is not seekable; it is not presented as whole-track completion. Before the first
                // sound there is no elapsed time yet, but the produced prefix is already visible.
                string timeText = MusicStreamHost.Fmt(hasChannel ? pos : 0) + " / STREAM";
                try { if (ui._timeText != null) ui._timeText.text = timeText; _lastTimeText = timeText; }
                catch (Exception e) { _log?.LogInfo($"{Tag} time text write failed: {e.Message}"); }
                if (!dragging) SetSlider(ui, host.BufferFraction, false);
            }
            else
            {
                // The game's own SetTimeText owns the glue between the two stamps, so no exact
                // string is claimed here (the growing branch above writes its text itself).
                try { ui.SetTimeText((int)pos, (int)total); _lastTimeText = null; }
                catch { }
                if (!dragging && total > 0) SetSlider(ui, (double)pos / total, true);
                else if (!dragging) SetSlider(ui, 0, true);
            }

            // Exactly one actionable glyph, and none without a playable channel. A follower offers
            // the same glyphs: each click is a control request to the host, so the panel is not
            // pretending a local move. Volume is local and stays enabled.
            bool play = host.CanPlay, pause = host.CanPause, seek = host.CanSeek;
            try { ui.SetControlsInteractable(play, pause, seek); }
            catch (Exception e) { _log?.LogInfo($"{Tag} SetControlsInteractable failed: {e.Message}"); }
            try { if (ui._progressSlider != null) ui._progressSlider.interactable = seek && !growing; } catch { }

            string sig = $"status={status} state={(PlayerState)want} play={play} pause={pause} seek={seek} " +
                         $"growing={growing} pos={pos} total={total} pending={pending} title='{Short(title, 50)}'";
            if (force || stateChanged || statusWritten || !string.Equals(sig, _lastSig, StringComparison.Ordinal))
            {
                _lastSig = sig;
                bool nativePlay = false, nativePause = false;
                try { nativePlay = ui._playButton != null && ui._playButton.IsInteractable; } catch { }
                try { nativePause = ui._pauseButton != null && ui._pauseButton.IsInteractable; } catch { }
                _log?.LogInfo($"{Tag} mirror {sig} realGlyphs(play={nativePlay} pause={nativePause})");
            }
        }
        catch (Exception e) { _log?.LogWarning($"{Tag} mirror write threw {e.GetType().Name}: {e.Message}"); }
    }

    // Native state presentation. `ApplyState` is the game's own transition (status strings, control
    // interactability), and `StateChanged` is what the panel's PlayerStateVisualModifier glyphs
    // listen to - assigning `_state` directly never reached them, which is how both the play and the
    // pause glyph stayed on screen at once. Re-firing the same state is idempotent for them.
    private static void ApplyState(AudioPlayerUI ui, int state)
    {
        var ps = (PlayerState)state;
        _applyingState = true;
        try { ui.ApplyState(ps); }
        catch (Exception e) { _log?.LogWarning($"{Tag} ApplyState({ps}) threw {e.GetType().Name}: {e.Message}"); }
        finally { _applyingState = false; }
        try { ui.StateChanged?.Invoke(ps); }
        catch (Exception e) { _log?.LogInfo($"{Tag} StateChanged({ps}) invoke skipped: {e.Message}"); }
    }

    private static void CheckDrift(AudioPlayerUI ui)
    {
        string status = null, title = null, time = null;
        try { status = ui._statusText?.text; } catch { }
        try { title = ui._titleText?.text; } catch { }
        try { time = ui._timeText?.text; } catch { }
        var sb = new System.Text.StringBuilder();
        if (_lastStatus != null && status != null && !string.Equals(status, _lastStatus, StringComparison.Ordinal))
            sb.Append($"status='{Short(status, 20)}'≠'{Short(_lastStatus, 20)}' ");
        if (_lastTitle != null && title != null && !string.Equals(title, _lastTitle, StringComparison.Ordinal))
            sb.Append($"title='{Short(title, 30)}'≠'{Short(_lastTitle, 30)}' ");
        if (_lastTimeText != null && time != null && !string.Equals(time, _lastTimeText, StringComparison.Ordinal))
            sb.Append($"time='{time}'≠'{_lastTimeText}' ");
        if (sb.Length == 0) { _lastDrift = null; return; }
        var line = sb.ToString().TrimEnd();
        if (string.Equals(line, _lastDrift, StringComparison.Ordinal)) return;
        _lastDrift = line;
        _log?.LogWarning($"{Tag} panel drift (another writer): {line} - rewriting");
    }

    private static void SetSlider(AudioPlayerUI ui, double frac, bool interactable)
    {
        try
        {
            var s = ui._progressSlider;
            if (s == null) return;
            double f = frac < 0 ? 0 : (frac > 1 ? 1 : frac);
            s.value = (float)(s.minValue + f * (s.maxValue - s.minValue));
            s.interactable = interactable;
        }
        catch (Exception e) { _log?.LogInfo($"{Tag} progress slider write failed: {e.Message}"); }
    }

    // A new request starts from nothing: no title, no clock, no progress, no error from the track
    // before it.
    private static void ClearPanel(AudioPlayerUI ui, MusicStreamHost host, string why)
    {
        _note = null;
        Invalidate();
        if (ui == null) return;
        try
        {
            ui.SetTitle("");
            ui.SetTimeText(0, 0);
            ui.ResetProgress();
            ui.HideError();
            if (ui._statusText != null)
            {
                ui._statusText.enableWordWrapping = false;   // the longest status stays one line
                ui._statusText.text = host.StatusText();
            }
            int want = host.UiState();
            ApplyState(ui, want);
            ui.SetControlsInteractable(false, false, false);
            if (ui._progressSlider != null)
            {
                ui._progressSlider.interactable = false;
                ui._progressSlider.value = ui._progressSlider.minValue;
            }
            _lastStatus = host.StatusText();
            _lastTitle = ""; _lastError = ""; _lastState = want;
            _log?.LogInfo($"{Tag} panel cleared for '{why}': title='' time=0:00 progress=0 error hidden " +
                          $"status='{host.StatusText()}' state={(PlayerState)want}");
        }
        catch (Exception e)
        {
            _log?.LogWarning($"{Tag} clearing the panel failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Invalidate()
    {
        _lastStatus = null; _lastTitle = null; _lastError = null; _lastSig = null; _lastState = -1;
        _lastTimeText = null; _lastDrift = null;
    }

    internal static AudioPlayerUI FindUi()
    {
        try
        {
            var arr = SceneCache.ScanPolled(Il2CppType.Of<AudioPlayerUI>(), 1.0);
            if (arr == null) return null;
            for (int i = 0; i < arr.Length; i++)
            {
                var ui = arr[i]?.TryCast<AudioPlayerUI>();
                if (ui != null) return ui;
            }
        }
        catch { }
        return null;
    }

    // ================================================================ pass-throughs for MusicRequest

    // The console obeys the same authority rule as the panel: on a follower a transport command is
    // refused with a reason instead of quietly diverging from the session. A committed host-side
    // change publishes itself (MusicStreamHost -> LobbyMusicNet.NoteTransport).
    internal static bool Pause()
    {
        if (!LobbyMusicNet.IsAuthority)
            return SubmitFollowerControl(LobbyMusicNet.ControlPause, 0, "console pause");
        return _host?.Pause() ?? false;
    }

    internal static bool Resume()
    {
        if (!LobbyMusicNet.IsAuthority)
            return SubmitFollowerControl(LobbyMusicNet.ControlResume, 0, "console resume");
        return _host?.Resume() ?? false;
    }

    internal static bool Seek(long ms)
    {
        if (!LobbyMusicNet.IsAuthority)
            return SubmitFollowerControl(LobbyMusicNet.ControlSeek, ms, "console seek");
        return _host?.Seek(ms) ?? false;
    }

    // `stop` is the user's stop: production ends, files are released, and the panel says STOP (the
    // same terminal presentation as a cancel). A panel close uses MusicStreamHost.Stop directly,
    // because the game's own close path owns the panel again afterwards.
    internal static void Stop(string why) { _host?.Cancel(why); LobbyMusicNet.ResetFollow(why); }
    internal static void Cancel(string why) { _host?.Cancel(why); LobbyMusicNet.ResetFollow(why); }

    internal static string Counters =>
        $"hits={_prefixHits} takeovers={_takeovers} passthrough={_directPass} play={_playHits} pause={_pauseHits} " +
        $"pauseRefused={_pauseRefused} resumeRefused={_resumeRefused} nativeStops={_nativeStops} " +
        $"closeStops={_closeStops} handbacks={_handbacks} diverted={_refreshSkips} seeks={_seeks} volumes={_volumes}";

    // ================================================================ log helpers

    internal static void Debug(string line) => _log?.LogInfo(line);
    internal static void Warn(string line) => _log?.LogWarning(line);
    internal static void Error(string line) => _log?.LogError(line);

    private static string Short(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
