using System;
using AlterumModelSwap.Contracts;

namespace AlterumModelSwap.Hot;

// Entry point the host looks up by name. Must have a parameterless constructor and must not
// touch Unity in it - Initialize() is where that starts.
//
// Merged build: this is the union of the three feature tracks. Every tick a track added here is
// kept, each in its own try/catch so one feature throwing cannot stop the others:
//   ScreenShot.Tick    (shared tree, still ticked by the bones track; hosts the shortcut registry
//                       tick, the native Controls rows and the ESC menu section)
//   ClipRecorder.Tick  (shared tree, all tracks)
//   LobbyNaming.Tick   (stream D: keeps the published lobby name ending in " (Modded)")
// The per-feature ticks that live inside ModelSwapRuntime.Update/LateUpdate are unaffected by
// this file (see ModelSwapRuntime for the jiggle/emote/cosmetic ordering).
public class HotModule : IHotModule
{
    private ModelSwapRuntime _runtime;
    private IHostContext _host;
    private bool _reelPatchAttempted;
    private bool _diagnosticsOnly;

    public void Initialize(IHostContext host, bool firstActivation)
    {
        _host = host;
        _diagnosticsOnly = AlterumModelSwap.DiagnosticsMode.Enabled;
        if (_diagnosticsOnly)
        {
            try { QcGate.Install(host); }
            catch (Exception e) { host.LogError($"[gate] install threw: {e.Message}"); }
            _runtime = new ModelSwapRuntime(host, firstActivation);
            host.LogInfo("[hot] diagnostics-only active; gameplay features disabled");
            return;
        }
        // Stream D's lobby-name Harmony prefix. Install right after the host is stored (the prefix
        // logs through it) and before the runtime exists: the first publish can happen during the
        // runtime's own startup path.
        try { LobbyNaming.Install(host); }
        catch (Exception e) { host.LogError($"[lobbyname] install threw: {e.Message}"); }
        // Lobby music streaming: a prefix on the music panel's own Load action. A yt-dlp page URL is
        // taken over and streamed by our own FMOD channel; a direct audio link falls through to the
        // game's player untouched. Installed before the runtime so a load in the first frames is seen.
        try { LobbyMusicHooks.Install(host); }
        catch (Exception e) { host.LogError($"[music:ui] install threw: {e.Message}"); }
        // Temporary diagnostic for the game console's runtime disable (remove before shipping).
        try { QcGate.Install(host); }
        catch (Exception e) { host.LogError($"[gate] install threw: {e.Message}"); }
        // Read-only join/appearance trace; model assignment belongs entirely to the game.
        try { JoinTrace.Install(host); }
        catch (Exception e) { host.LogError($"[join:trace] install threw: {e.Message}"); }
        // `[mapbody]`: the read-only map-run body trace (SetupModel postfix + 1 Hz census).
        try { MapBody.Install(host); }
        catch (Exception e) { host.LogError($"[mapbody] install threw: {e.Message}"); }
        // `[results:body]`: re-apply custom bodies to the models the game builds in the Results
        // scene (InstantiatePlayerModel postfix; the attach work runs in the runtime tick).
        try { ResultsBodies.Install(host); }
        catch (Exception e) { host.LogError($"[results:body] install threw: {e.Message}"); }
        // `[scenesync]`: the read-only lobby -> map handoff trace (loader calls on both peers).
        try { SceneSyncTrace.Install(host); }
        catch (Exception e) { host.LogError($"[scenesync] install threw: {e.Message}"); }
        // Menu lifecycle: the pause/settings state every UI lookup keys off. Installed from the
        // host start so the first Pauser the game creates is captured, not discovered by a scan.
        try { MenuScope.Install(host); }
        catch (Exception e) { host.LogError($"[menu:scope] install threw: {e.Message}"); }
        _runtime = new ModelSwapRuntime(host, firstActivation);
        host.LogInfo($"[hot] module initialized (firstActivation={firstActivation})");
    }

    public void Update(float unscaledDeltaTime)
    {
        if (_diagnosticsOnly)
        {
            try { SceneCache.Tick(); } catch { }
            try { _runtime?.DiagnosticsTick(unscaledDeltaTime); } catch (Exception e) { _host?.LogError($"[diag] runtime: {e.Message}"); }
            if (_host == null) return;
            try { ScreenShot.Tick(_host, unscaledDeltaTime); } catch (Exception e) { _host.LogError($"[diag] shot: {e.Message}"); }
            try { ProofCam.Tick(_host); } catch (Exception e) { _host.LogError($"[diag] proofcam: {e.Message}"); }
            try { UiDriver.Tick(_host, _runtime); } catch (Exception e) { _host.LogError($"[diag] ui: {e.Message}"); }
            try { SessionControl.Tick(_host, _runtime); } catch (Exception e) { _host.LogError($"[diag] session: {e.Message}"); }
            try { GameConsole.Tick(_host); } catch (Exception e) { _host.LogError($"[diag] qc: {e.Message}"); }
            return;
        }
        // HotLoader initializes the candidate before disposing the old module. Wait until
        // dispatch so the retiring module cannot unpatch the candidate's shared Harmony ID.
        if (!_reelPatchAttempted)
        {
            _reelPatchAttempted = true;
            try { ReelInteraction.InstallInteractionPatch(); }
            catch (Exception e) { _host?.LogError($"[reel:act] install threw: {e.Message}"); }
            // The old generation owns the same Harmony ID and is disposed after Initialize.
            // Install the model wire guard only once the retiring patches have gone away.
            try { SahurWardrobe.Install(_host); }
            catch (Exception e) { _host?.LogError($"[sahur:wardrobe] install threw: {e.Message}"); }
        }
        Perf.Hook();
        // Menu open/close edge first: it invalidates the menu group that the UI lookups below
        // read, and it costs a property read on a held reference (no scan).
        try { MenuScope.Tick(); } catch { }
        try { SceneCache.Tick(); } catch { }
        Perf.BeginFrame();
        // Shortcut registry first, before anything that polls a key: it attaches the store, mirrors
        // the native Controls rows and drives the ESC menu section, so the pollers below read this
        // frame's effective bindings rather than last frame's. Its own try/catch, so a registry
        // failure cannot stop the runtime or the screenshot/clip ticks.
        if (_host != null)
        {
            try { Perf.W("bind.tick", () => ModBindings.Tick(_host, unscaledDeltaTime)); }
            catch (Exception e) { _host.LogError($"[bind] tick threw: {e.Message}"); }
        }
        try { Perf.W("runtime", () => _runtime?.Update(unscaledDeltaTime)); }
        catch (Exception e) { _host?.LogError($"[perf] runtime threw: {e.Message}"); }
        if (_host == null) return;
        try { Perf.W("shot", () => ScreenShot.Tick(_host, unscaledDeltaTime)); }
        catch (Exception e) { _host.LogError($"[shot] tick threw: {e.Message}"); }
        try { Perf.W("clip", () => ClipRecorder.Tick(_host, unscaledDeltaTime)); }
        catch (Exception e) { _host.LogError($"[clip] tick threw: {e.Message}"); }
        // Lobby name: re-assert the " (Modded)" suffix after the game republishes metadata.
        try { Perf.W("lobbyname", () => LobbyNaming.Tick(_host)); }
        catch (Exception e) { _host.LogError($"[lobbyname] tick threw: {e.Message}"); }
        try { Perf.W("music", () => LobbyMusicHooks.Tick(_host)); }
        catch (Exception e) { _host.LogError($"[music:ui] tick threw: {e.Message}"); }
        // `music.request`: status/timing/play/pause/seek/stop/cancel/cache plus the UI drive verbs.
        try { Perf.W("musicreq", () => MusicRequest.Tick(_host, UnityEngine.Time.unscaledTime)); }
        catch (Exception e) { _host.LogError($"[music:req] poll threw: {e.Message}"); }
        // Unattended UI driver (`ui.request`): dumps the menu hierarchy and presses buttons through
        // ModelSwapRuntime's own ActivateOrClick. Inert unless the request file exists.
        try { Perf.W("uidriver", () => UiDriver.Tick(_host, _runtime)); }
        catch (Exception e) { _host.LogError($"[ui] driver threw: {e.Message}"); }
        // Explicit, identity-bound session automation; inert without its local request file.
        try { Perf.W("session", () => SessionControl.Tick(_host, _runtime)); }
        catch (Exception e) { _host.LogError($"[session] tick threw: {e.Message}"); }
        // Quest admin (`quest.request`): state dump plus the game's own completion APIs
        // (QuestService/QuestRunner/QuestsCommandsWrapper). Inert unless the request file exists.
        try { Perf.W("quest", () => QuestAdmin.Tick(_host)); }
        catch (Exception e) { _host.LogError($"[quest] admin threw: {e.Message}"); }
        // The game's own console (QFSW.QC): status/boot/table/help/invoke via `qc.request`.
        try { Perf.W("qc", () => GameConsole.Tick(_host)); }
        catch (Exception e) { _host.LogError($"[qc] activator threw: {e.Message}"); }
        // Read-only appearance trace.
        try { Perf.W("jointrace", () => JoinTrace.Tick(_host, unscaledDeltaTime)); }
        catch (Exception e) { _host.LogError($"[join:trace] tick threw: {e.Message}"); }
        try { SceneSyncTrace.Tick(unscaledDeltaTime); }
        catch (Exception e) { _host.LogError($"[scenesync] tick threw: {e.Message}"); }
    }
    public void LateUpdate()
    {
        if (_diagnosticsOnly) return;
        try { _runtime?.LateUpdate(); }
        catch (Exception e) { _host?.LogError($"[perf] late threw: {e.Message}"); }
        try { Perf.EndFrame(_host, UnityEngine.Time.unscaledDeltaTime); } catch { }
    }
    public void OnGUI()
    {
        if (!_diagnosticsOnly) _runtime?.OnGUI();
    }

    public void Dispose()
    {
        if (_diagnosticsOnly)
        {
            try { QcGate.Dispose(); } catch { }
            _runtime = null;
            return;
        }
        // A recording owns native resources (RenderTexture, camera GameObject) that the next
        // generation cannot see, so the retiring one has to destroy them itself. Same for the
        // mirror, the scaled skeletons, the jiggle offsets and the cosmetic objects; those live
        // in ModelSwapRuntime.Dispose.
        // Mandatory, not optional: LobbyNaming holds a Harmony prefix on the game's publish path.
        // Without this the prefix survives the generation and a retired module keeps rewriting the
        // lobby name (and logging through a dead host). Unpatch before the runtime teardown so the
        // publish path is already clean while the rest of the module unwinds.
        try { LobbyNaming.Dispose(); }
        catch (Exception e) { _host?.LogError($"[lobbyname] dispose threw: {e.Message}"); }
        try { LobbyMusicHooks.Dispose(); }
        catch (Exception e) { _host?.LogError($"[music:ui] dispose threw: {e.Message}"); }
        try { QcGate.Dispose(); } catch { }
        try { MenuScope.Dispose(); } catch { }
        try { JoinTrace.Dispose(); } catch { }
        try { MapBody.Dispose(); } catch { }
        try { ResultsBodies.Dispose(); } catch { }
        try { SceneSyncTrace.Dispose(); } catch { }
        try { ClipRecorder.Abort(_host); } catch { }
        try { ReelInteraction.DisposeInteractionPatch(); } catch { }
        try { _runtime?.Dispose(); } catch { }
        _runtime = null;
    }
}
