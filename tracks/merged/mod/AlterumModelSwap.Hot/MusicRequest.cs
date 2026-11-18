using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using AudioPlayer.Core;
using AudioPlayer.UI;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// `music.request` — the unattended console for the streaming player (same contract as
// clip.request / ui.request / menu.request: one command per line, file deleted on pickup, answers in
// the log via `[music:req]` / `[music:*]`).
//
//   status              everything: phase, position, snapshot chain, timings, counters
//   timing              the headline number only (time to first sound vs the pipeline marks)
//   play=<url>          start a stream from a link (no UI involved)
//   pause | resume | seek=<ms> | stop | cancel
//   cache               what is in the finished-track cache, and its size
//   tools               the resolved tools directory, where the root came from, per-file presence
//   native              the game's own controller for this panel (url/state/handle) - who owns it
//   ui                  dump the game's music panel (shown/state/status/error/url field)
//   load=<url>          drive the REAL UI path: write the URL field and press the panel's Load
//   show                open the music panel through its own Show() API
//   canvas=<filter>     restrict the menu capture rig to canvases matching a filter
//   shoot=<tag>         render those canvases into a PNG under ScreensDir (works with a locked desktop)
//   snap                snapshot chain state, and cut one now
internal static class MusicRequest
{
    internal const string Tag = "[music:req]";
    internal const string RequestFileName = "music.request";
    private const float PollSeconds = 0.35f;

    private static float _nextPoll;
    private static string _dir, _path;

    internal static void Tick(IHostContext log, float now)
    {
        var host = LobbyMusicHooks.Host;
        if (host == null) return;
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;

        if (!string.Equals(log.ScreensDir, _dir, StringComparison.Ordinal))
        {
            _dir = log.ScreensDir;
            try { _path = string.IsNullOrEmpty(_dir) ? null : Path.Combine(_dir, RequestFileName); } catch { _path = null; }
        }
        var path = _path;
        if (path == null) return;
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text;
        try { text = File.ReadAllText(path); File.Delete(path); }
        catch (Exception e) { log.LogWarning($"{Tag} request unreadable: {e.Message}"); return; }

        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            try { Run(log, host, line); }
            catch (Exception e) { log.LogError($"{Tag} '{line}' threw {e.GetType().Name}: {e.Message}"); }
        }
    }

    private static void Run(IHostContext log, MusicStreamHost host, string line)
    {
        int eq = line.IndexOf('=');
        string verb = (eq < 0 ? line : line.Substring(0, eq)).Trim().ToLowerInvariant();
        string arg = eq < 0 ? "" : line.Substring(eq + 1).Trim();
        log.LogInfo($"{Tag} {line}");

        switch (verb)
        {
            case "status": Status(log, host); return;
            case "timing": log.LogInfo(host.TimingReport()); return;
            case "play":
                if (arg.Length == 0) { log.LogWarning($"{Tag} play: no url"); return; }
                // LOCAL INTENT. On the host this is the authoritative start (takeover ritual +
                // revision bump + immediate publish); on a follower it only submits the request and
                // the current session track keeps playing until the accepted revision arrives.
                var outcome = LobbyMusicHooks.SubmitIntent(arg, "request");
                if (outcome == LobbyMusicHooks.IntentOutcome.Passthrough)
                    log.LogInfo($"{Tag} play: '{arg}' is not a yt-dlp link");
                else if (outcome == LobbyMusicHooks.IntentOutcome.Refused)
                    // The real reason, and no quiet fall-through to the game's own downloader.
                    log.LogWarning($"{Tag} play: '{arg}' was NOT loaded: {LobbyMusicHooks.LastRefusal}; " +
                                   "the current session track keeps playing");
                else
                    log.LogInfo($"{Tag} play: '{arg}' submitted " +
                                (LobbyMusicNet.IsAuthority ? "as the host (authoritative start)" : "to the host (pending)"));
                return;
            case "pause":
                bool paused = LobbyMusicHooks.Pause();
                log.LogInfo($"{Tag} pause -> {(LobbyMusicNet.IsAuthority
                                ? (paused ? "committed" : "refused")
                                : (paused ? "requested from the host" : "not sent"))} " +
                            $"phase={host.Current} pos={host.PositionMs}ms " +
                            $"role={(LobbyMusicNet.IsAuthority ? "host" : "follower")}");
                return;
            case "resume":
                bool resumed = LobbyMusicHooks.Resume();
                log.LogInfo($"{Tag} resume -> {(LobbyMusicNet.IsAuthority
                                ? (resumed ? "committed" : "refused")
                                : (resumed ? "requested from the host" : "not sent"))} " +
                            $"phase={host.Current} pos={host.PositionMs}ms " +
                            $"role={(LobbyMusicNet.IsAuthority ? "host" : "follower")}");
                return;
            case "seek":
                long ms = 0; long.TryParse(arg, out ms);
                bool sought = LobbyMusicHooks.Seek(ms);
                log.LogInfo($"{Tag} seek({ms}ms) -> {(LobbyMusicNet.IsAuthority
                                ? (sought ? "committed" : "refused")
                                : (sought ? "requested from the host" : "not sent"))} " +
                            $"pos={host.PositionMs}ms role={(LobbyMusicNet.IsAuthority ? "host" : "follower")}");
                return;
            case "stop": LobbyMusicHooks.Stop("request stop"); return;
            case "cancel": LobbyMusicHooks.Cancel("request cancel"); return;
            case "cache": Cache(log); return;
            case "tools": Tools(log); return;
            case "native": Native(log); return;
            case "snap": Snap(log, host); return;
            case "ui": Ui(log); return;
            case "load": Load(log, arg); return;
            case "show": Show(log); return;
            case "canvas": MenuProof.Enqueue("canvas=" + arg); log.LogInfo($"{Tag} capture filter set to '{arg}'"); return;
            case "shoot": MenuProof.Enqueue("shoot=" + (arg.Length == 0 ? "music" : arg)); log.LogInfo($"{Tag} capture queued"); return;
            case "rate":
                YtDlpPipeline.LimitRate = arg == "0" ? "" : arg;
                log.LogInfo($"{Tag} rate: yt-dlp --limit-rate '{(YtDlpPipeline.LimitRate.Length == 0 ? "(off)" : YtDlpPipeline.LimitRate)}'");
                return;
            case "hooks": log.LogInfo($"{Tag} hooks: {LobbyMusicHooks.Counters}"); return;
            case "net": log.LogInfo($"{Tag} net: {LobbyMusicNet.Counters}"); return;
            default: log.LogWarning($"{Tag} unknown verb '{line}'"); return;
        }
    }

    private static void Status(IHostContext log, MusicStreamHost host)
    {
        var p = host.Pipeline;
        log.LogInfo($"{Tag} status: phase={host.Current} ownsPanel={host.OwnsPanel} ownsPlayback={host.OwnsPlayback} " +
                    $"producerDone={host.ProducerDone} growing={host.IsGrowing} pos={host.PositionMs}ms " +
                    $"len={host.LengthMs}ms total={host.TotalMs}ms buffer={host.BufferFraction:0.###} " +
                    $"switches={host.SnapshotSwitches} maxGap={host.MaxSwitchGapMs}ms " +
                    $"gens={host.SnapOpen}/{host.SnapNewest}");
        log.LogInfo($"{Tag} status: url='{Short(host.Url, 100)}' title='{Short(host.DisplayTitle, 70)}' " +
                    $"cached='{Short(host.CachedPath ?? "-", 110)}' deferred='{Short(host.DeferredHandoff ?? "-", 60)}'");
        log.LogInfo($"{Tag} status: statusText='{host.StatusText()}' error='{host.LastError}' uiState={host.UiState()} " +
                    $"controls(play={host.CanPlay} pause={host.CanPause} seek={host.CanSeek}) " +
                    $"channel={host.HasPlayableChannel} enginePaused={host.Engine.IsPaused}");
        log.LogInfo($"{Tag} status: transport playing={host.Engine.Playing()} channelOpen={host.Engine.HasChannel} " +
                    $"retiring={host.RetiredCount()}");
        if (p != null) log.LogInfo($"{Tag} status: pipeline {p.Report()}");
        else log.LogInfo($"{Tag} status: pipeline (none - cache playback or idle)");
        log.LogInfo(host.TimingReport());
        log.LogInfo($"{Tag} status: engine {host.Engine.BusState} openMs={host.Engine.OpenMs} " +
                    $"file='{Path.GetFileName(host.Engine.FilePath ?? "-")}' bytes={host.Engine.SnapshotBytes} " +
                    $"snapLen={host.Engine.SnapshotLengthMs}ms");
        log.LogInfo($"{Tag} status: dir='{host.SnapshotDir ?? "-"}' cache='{host.CacheRoot}'");
        log.LogInfo($"{Tag} status: {YtDlpPipeline.DescribeTools()}");
        log.LogInfo($"{Tag} status: {LobbyMusicHooks.Counters} | {LobbyMusicNet.Counters}");
    }

    // The tools folder question, answerable without a player: which directory, where the game root
    // came from, which of the three shipped executables are there, and the exact sentence the panel
    // would show if any are missing.
    private static void Tools(IHostContext log)
    {
        log.LogInfo($"{Tag} tools: {YtDlpPipeline.DescribeTools()}");
        log.LogInfo($"{Tag} cache: root='{YtDlpPipeline.CacheRoot}' source={YtDlpPipeline.CacheRootSource}" +
                    (string.IsNullOrEmpty(YtDlpPipeline.CacheRootWarning) ? "" : $" ignored='{YtDlpPipeline.CacheRootWarning}'"));
        var missing = YtDlpPipeline.MissingTools();
        if (missing.Count == 0) log.LogInfo($"{Tag} tools: complete (yt-dlp.exe, ffmpeg.exe, ffprobe.exe)");
        else log.LogWarning($"{Tag} tools: would report '{YtDlpPipeline.MissingToolsMessage(missing)}'");
    }

    // Who the panel's own controller is, and what it thinks it is playing: the pairing that has to
    // be stopped on takeover and refreshed on relinquishment.
    private static void Native(IHostContext log)
    {
        var ui = LobbyMusicHooks.FindUi();
        if (ui == null) { log.LogWarning($"{Tag} native: no AudioPlayerUI in the scene"); return; }
        var host = LobbyMusicHooks.Host;
        AudioPlayer.Core.IAudioPlayerController ctrl = null;
        try { ctrl = ui._controller; } catch (Exception e) { log.LogWarning($"{Tag} native: controller read threw {e.Message}"); }
        string url = "-"; int state = -1; bool handle = false;
        if (ctrl != null)
        {
            try { url = ctrl.CurrentUrl; } catch { }
            try { state = (int)ctrl.CurrentPlayerState; } catch { }
            try { handle = ctrl.ActiveHandle != null; } catch { }
        }
        log.LogInfo($"{Tag} native: controller={(ctrl == null ? "none" : "present")} url='{Short(url, 110)}' " +
                    $"state={state} handle={handle} panelState={ui._state} modOwnsPanel={host?.OwnsPanel}");
    }

    private static void Cache(IHostContext log)
    {
        var root = YtDlpPipeline.DoneRoot;
        if (!Directory.Exists(root)) { log.LogInfo($"{Tag} cache: empty ('{root}')"); return; }
        var files = Directory.GetFiles(root, "track.mp3", SearchOption.AllDirectories);
        log.LogInfo($"{Tag} cache: {files.Length} track(s) in '{root}'");
        foreach (var f in files)
        {
            long bytes = 0;
            try { bytes = new FileInfo(f).Length; } catch { }
            log.LogInfo($"{Tag}   {f} {bytes} bytes ({(long)bytes / YtDlpPipeline.BytesPerMs}ms)");
        }
        if (Directory.Exists(YtDlpPipeline.StreamRoot))
            foreach (var d in Directory.GetDirectories(YtDlpPipeline.StreamRoot))
                log.LogInfo($"{Tag}   in-flight: {d} ({Directory.GetFiles(d).Length} file(s))");
    }

    private static void Snap(IHostContext log, MusicStreamHost host)
    {
        log.LogInfo($"{Tag} snap: gens open={host.SnapOpen} newest={host.SnapNewest} engine='{Path.GetFileName(host.Engine.FilePath ?? "-")}' " +
                    $"bytes={host.Engine.SnapshotBytes} len={host.Engine.SnapshotLengthMs}ms switches={host.SnapshotSwitches} " +
                    $"maxGap={host.MaxSwitchGapMs}ms");
        var dir = host.SnapshotDir;
        if (dir == null || !Directory.Exists(dir)) { log.LogInfo($"{Tag} snap: no stream dir"); return; }
        foreach (var f in Directory.GetFiles(dir))
        {
            long bytes = 0;
            try { bytes = new FileInfo(f).Length; } catch { }
            log.LogInfo($"{Tag}   {Path.GetFileName(f)} {bytes} bytes");
        }
    }

    private static void Ui(IHostContext log)
    {
        var ui = LobbyMusicHooks.FindUi();
        if (ui == null) { log.LogWarning($"{Tag} ui: no AudioPlayerUI in the scene"); return; }
        string view = "-", urlField = "-", status = "-", error = "-", title = "-", time = "-";
        try { view = ui._view == null ? "null" : (ui._view.activeSelf ? "active" : "inactive"); } catch { }
        try { urlField = ui._urlInputField?.text; } catch { }
        try { status = ui._statusText?.text; } catch { }
        try { error = ui._errorText?.text; } catch { }
        try { title = ui._titleText?.text; } catch { }
        try { time = ui._timeText?.text; } catch { }
        log.LogInfo($"{Tag} ui: shown={ui._isShown} state={ui._state} view={view} " +
                    $"statusText='{Short(status, 80)}' errorText='{Short(error, 120)}' " +
                    $"title='{Short(title, 60)}' time='{time}' urlField='{Short(urlField, 90)}'");
    }

    private static void Load(IHostContext log, string url)
    {
        var ui = LobbyMusicHooks.FindUi();
        if (ui == null) { log.LogWarning($"{Tag} load: no AudioPlayerUI in the scene"); return; }
        try
        {
            if (ui._urlInputField != null) ui._urlInputField.text = url;
            log.LogInfo($"{Tag} load: url field set to '{Short(url, 90)}'; calling the panel's own OnLoadClicked()");
            ui.OnLoadClicked();
            Ui(log);
            LobbyMusicHooks.Mirror(LobbyMusicHooks.Host, force: true);
            Ui(log);
        }
        catch (Exception e) { log.LogError($"{Tag} load threw {e.GetType().Name}: {e.Message}"); }
    }

    private static void Show(IHostContext log)
    {
        var ui = LobbyMusicHooks.FindUi();
        if (ui == null) { log.LogWarning($"{Tag} show: no AudioPlayerUI in the scene"); return; }
        try
        {
            IAudioPlayerController ctrl = null;
            var systems = SceneCache.ScanPolled(Il2CppType.Of<Audio3DSystem>(), 1.0);
            for (int i = 0; i < systems.Length && ctrl == null; i++)
            {
                var s = systems[i]?.TryCast<Audio3DSystem>();
                if (s != null) { try { ctrl = s.TryCast<IAudioPlayerController>(); } catch { } }
            }
            if (ctrl == null)
            {
                var nets = SceneCache.ScanPolled(Il2CppType.Of<AudioPlayer.Network.NetworkedAudioController>(), 1.0);
                for (int i = 0; i < nets.Length && ctrl == null; i++)
                {
                    var n = nets[i]?.TryCast<AudioPlayer.Network.NetworkedAudioController>();
                    if (n != null) { try { ctrl = n.TryCast<IAudioPlayerController>(); } catch { } }
                }
            }
            if (ctrl == null)
            {
                // Last resort: the panel's root game object, shown directly. The status/error
                // widgets are the same objects, so the mirror still lands in the real UI.
                try
                {
                    if (ui._view != null) { ui._view.SetActive(true); log.LogInfo($"{Tag} show: no IAudioPlayerController found; activated _view directly"); }
                }
                catch (Exception e) { log.LogWarning($"{Tag} show: _view.SetActive failed: {e.Message}"); }
                Ui(log);
                return;
            }
            log.LogInfo($"{Tag} show: controller={ctrl.GetType().Name}");
            ui.Show(ctrl, null);
            Ui(log);
        }
        catch (Exception e) { log.LogError($"{Tag} show threw {e.GetType().Name}: {e.Message}"); }
    }

    private static string Short(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
