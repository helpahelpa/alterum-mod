using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using AlterumModelSwap.Contracts;
using HWG.Settings.Controls;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// The game's own console (QFSW.QC / Quantum Console), driven through its own API.
//
// Serialized data in the pin says the console object is present, active and enabled in every scene
// (level3/4/6/8/9 + resources.assets prefabs, `m_IsActive=1`, `m_Enabled=1` - see
// work/qc/QC-ASSETS.md), yet at runtime the lobby component reports `enabled=false` with
// `QuantumConsole.Instance` null. Something in game code switches it off after the scene loads.
// So the only way in is the console's own entry points: `enabled = true` (which runs its Awake /
// OnEnable), then `Activate()`, then `QuantumConsoleProcessor.GenerateCommandTable`.
//
// This file is a probe and an activator, not a console: every call below lands in QFSW.QC. It is
// reflection-only on purpose (no compile-time reference to QFSW.QC.dll), so a signature difference
// degrades into a log line instead of a build break, and the overlay's "is the game console live"
// check keeps working across generations.
//
// Trigger: `<ScreensDir>/qc.request`, one directive per line, polled twice a second.
//   status            - every QuantumConsole in the process, Instance, processor flags, gate state
//   boot              - enable the scene console, Activate(), generate the table, then help
//   table             - call QuantumConsoleProcessor.GenerateCommandTable(true, true) and report
//   help              - InvokeCommand("help") and log the result
//   dump              - write the full command list to <ScreensDir>/qc-commands-live.txt
//   invoke <command>  - InvokeCommand(<command>) and log the result
//
// Read-only test hooks (they never write, execute, open a UI or change an image, so they are also
// available while the console is locked) - the Alterum test control layer. Every verb lives in the
// canonical `alterum.test.*` namespace. Five commands first shipped in the old `test.*` probe
// namespace and keep that spelling as a COMPATIBILITY ALIAS only: test.pose, test.identity,
// test.remote-list, test.wardrobe-list, test.open-wardrobe. An alias is not a second command: it is
// resolved to its canonical verb before dispatch and runs the same handler (same arity, same policy,
// same native row body), so it can never be a second read, a second piece of state or a second
// mutation path.
//   alterum.test.pose                - local actor: effective (visible body) pose, root transform,
//                              module state                             (alias: test.pose)
//   alterum.test.identity [clientId] - the canonical model identity for the local actor, or one known
//                              remote                          (alias: test.identity [clientId])
//   alterum.test.remote-list         - every remote actor this instance knows, plus RemoteModels' own
//                              census                                 (alias: test.remote-list)
//   alterum.test.wardrobe-list       - the wardrobe's own model database, row by row: index, slug/label
//                              and whether the row is one of this mod's entries (custom) or the
//                              game's (native)                      (alias: test.wardrobe-list)
//   alterum.test.assert-model <clientId|local> <slug> - one structured pass/fail: the actor's identity
//                              fields (wanted/advertised/canonical/applied/body/HUD/wardrobe/readiness)
//                              compared with <slug> ('native' for the game's own row)
//   alterum.test.await-model <clientId|local> <slug>  - the same predicate held over the game's frame
//                              loop: returns at once, reports PASS at the first frame boundary where it
//                              holds, or TIMEOUT with the last observed identity/remote state
//   alterum.test.assets                       - the shared model resource caches: every entry (key,
//                              users, idle/leased, age, liveness), each cache's limit/clock/retired
//                              state, and which live body each entry backs
//   alterum.test.assert-assets                - the same snapshot as PASS/FAIL fields (no destroyed
//                              leased asset, no duplicate ownership, idle-only eviction, retired
//                              caches held until their leases release, healthy live bodies)
//   alterum.test.assert-image <path> <maxMagentaPixels> - one existing PNG under the screens dir:
//                              SHA-256, dimensions, pixel count, magenta count and PASS/FAIL
//                              against a ceiling (read-only: nothing is written or rendered)
//   alterum.test.snapshot [tag]               - ONE structured runtime manifest under the screens dir
//                              (schema, command id, UTC timestamp, process identity, hot generation/
//                              MVID and the hot DLL's SHA-256, active scene, local pose, named-point
//                              context, local identity, remote identities/body health, asset-cache and
//                              assertion summary, camera and report state). Writes that one artifact
//                              with an atomic replace and reports its path + SHA-256; it touches no
//                              game, UI, camera or model state (read-only, like assert-image)
//   alterum.test.points                       - the named test points: name, scene, standing
//                              coordinate, yaw, and the intent/source each number is grounded in
//
// Gated setup actions, refused while locked (AEZAKMI), on the same two transports once unlocked:
//   alterum.test.point <name> - move the local player onto a named point through the game's own
//                              authoritative PlayerTeleportator.TeleportOnServer. Host only, and an
//                              explicit failure (no write) when the scene, player, authority or
//                              teleporter is unavailable; never a direct transform mutation
//   alterum.test.open-wardrobe - open the native wardrobe panel through the scene's own
//                              WardrobeInputHandler.TryOpenWardrobe(); never selects a model, never
//                              fabricates a handler/view/transform (alias: test.open-wardrobe)
//   alterum.test.close-wardrobe - close the native wardrobe panel through the same scene handler's
//                              own WardrobeInputHandler.TryCloseWardrobe() (public bool, no
//                              parameters in the pinned interop) - the public entry point behind the
//                              native close action. Never hides the view directly, never writes a
//                              field, never toggles a GameObject/camera/cursor/EventSystem, never
//                              selects a model and has no fallback close path
//
// The layer's gated selection verb, also refused while locked (a selection is a mutation). The variant
// word is part of the command because the two paths are different facts and must not be read as each
// other:
//   alterum.test.select-model <slug> ui        - press the OPEN native wardrobe's own row for <slug>
//                                                through the existing UI.Elements.Button/EventSystem
//                                                machinery (the path a human takes). Requires the
//                                                view attached and open, resolves the slug to the tab
//                                                the view built, and reports an explicit failure when
//                                                the view/tab/slug/Button is unavailable - it never
//                                                falls back to a direct model write or a direct RPC
//   alterum.test.select-model <slug> protocol  - the module's existing authoritative selection helper
//                                                (SahurWardrobe.ApplyEntry -> SetBaseModelIndex), host
//                                                only, with test provenance; no UI and no new RPC path
//
// The layer's gated proof verbs, refused while locked as well (a capture borrows the game's own
// camera for a frame and writes a file; the pose report is gated with its capture sibling so the
// proof family has one gate):
//   alterum.test.capture <tag>                   - one frame of the CURRENT game camera view through the
//                                                  module's existing ScreenShot path: same camera
//                                                  object, same pose, same fov/near/far/cullingMask
//                                                  (none of them written), one RenderTexture render,
//                                                  with cam.targetTexture and RenderTexture.active
//                                                  saved/restored and the RT + temporary texture
//                                                  released in a finally on success and on failure.
//                                                  The PNG is written under the screens dir and
//                                                  reported with its path, byte count and SHA-256.
//                                                  It is not a proof camera and it aims nothing, so
//                                                  nothing needs a pose restored; a ScreenSpaceOverlay
//                                                  canvas is not in any camera, so such a frame cannot
//                                                  contain one (the reason MenuProof exists). Safe
//                                                  single-token tag: 1-48 of A-Z a-z 0-9 '-' '_'.
//   alterum.test.capture-remote <clientId> <tag> - capture one KNOWN remote with a live body
//                                                  through the module's one deterministic evidence
//                                                  path (RemoteModels.TryCaptureRemote: the same
//                                                  camera pose, RenderTexture render and restore
//                                                  the `shot=` request line uses), and report the
//                                                  artifact path together with that file's SHA-256.
//                                                  Every precondition - client id, known remote,
//                                                  resolved live body, safe single-token tag - is
//                                                  an explicit failure that writes nothing. It
//                                                  asserts nothing about the pixels: that verdict is
//                                                  alterum.test.assert-image's job, read-only.
//   alterum.test.camera-remote <clientId>        - report where that same remote-shot path would put
//                                                  the game's own camera and what it would aim at
//                                                  for one KNOWN remote with a live body
//                                                  (RemoteModels.TryResolveRemoteShotView: the focus,
//                                                  away/side offset and look rotation
//                                                  TryCaptureRemote renders). It does NOT borrow the
//                                                  camera, render a frame or write an artifact, so
//                                                  there is nothing to restore: the report names the
//                                                  camera's current facts, the target and the proof
//                                                  pose, with applied=false and artifact=none.
//                                                  Every precondition - client id, known remote,
//                                                  resolved live body, resolvable game camera - is an
//                                                  explicit failure that poses nothing.
//
// The same read-only verbs (the four probes, the await/assert pair, the asset diagnostics, the image
// proof, the snapshot manifest and the points table), the setup actions, the selection verb, the capture
// verb and the camera pose report are also registered as
// REAL Quantum Console commands (<<native QC rows>> below), so after AEZAKMI they can be typed in the
// console instead of written to the request file. Both spellings of the five renamed verbs are
// registered there (canonical row + compatibility alias row, each bound to the same handler), and the
// request transport above is unchanged: both transports call the same handlers.
//
// Access: the console is locked until the pause-menu code AEZAKMI is typed (see PauseCode at the
// bottom of this file). While locked, the tilde binding does nothing, every activation/execution
// verb below is refused, and so are the console's own Activate()/Toggle()/Update()/InvokeCommand()
// seams (QcGate). So is `gateoff`, which would uninstall those seams: while locked, the dispatcher
// refusing is not a substitute for the process-wide gate it promises. Read-only diagnostics -
// status, state, table/dump, sig/sigs, native, gate, out - stay available, and QC itself still
// initializes: the command table is built and the console is left enabled, initialized and CLOSED,
// so unlocking only has to open it.
internal static class GameConsole
{
    internal const string RequestName = "qc.request";
    private const string QcTypeName = "QFSW.QC.QuantumConsole";
    private const string ProcTypeName = "QFSW.QC.QuantumConsoleProcessor";
    private const float PollSeconds = 0.5f;

    private static float _nextPoll;
    private static bool _announced;
    private static Type _qc, _proc;
    private static Component _recheck;
    private static float _recheckAt;
    private static string _recheckWhy;

    // ---- Tilde: the mod's binding -> the game's own console -------------------------------
    //
    // `mod.console.toggle` (Tilde) is dispatched by the mod's binding registry, which is already
    // proven to fire, and the action is the console's own Activate()/Deactivate(). The console's
    // internal key handler is not used: it is the same physical key, but the route is ours, so a
    // non-US layout, a rebound key or a locked desktop does not change whether the console opens.
    //
    // The console also has to be live before a key can matter, and the scene's copy starts
    // `enabled=false` with `Instance` null (see QcGate). So one idempotent Ensure() runs on every
    // scene change (lobby load, map load) and leaves the console enabled and initialized but
    // CLOSED; only Tilde (or `qc.request` `toggle`) opens it.
    private static int _sceneHandle = int.MinValue;
    private static bool _ensureDone = true;
    private static float _ensureAt = -1f;
    private static int _ensureTries;
    private static float _ensureBackoff = 0.5f;
    private static bool _consoleAwoke;
    private static float _confirmAt = -1f;
    private static int _confirmTries;
    private static int _confirmHandle = int.MinValue;
    private const float EnsureFirstDelay = 0.5f;
    private const float EnsureMaxBackoff = 4f;

    // Called from the QcGate hook the moment a QuantumConsole's Awake or OnEnable runs. That is
    // exactly when a live canvas is created (measured: the menu's console object is instantiated
    // ~12 s after the menu scene loads), so a pending ensure stops waiting for its backoff and
    // runs on the next Tick. Never inside the hook itself: re-entering the component from its own
    // Awake is not worth the ordering risk.
    internal static void ConsoleAwoke()
    {
        if (_ensureDone && _sceneHandle != int.MinValue) return;
        _consoleAwoke = true;
    }

    // After a successful ensure, report once the command table has finished generating. Initialize
    // hands the processor a background generation, so the enable line itself can legitimately say
    // commands=0 a few hundred ms before the table is up.
    private static void ConfirmTable(IHostContext host, float now)
    {
        if (_confirmAt < 0f || now < _confirmAt) return;
        if (_sceneHandle != _confirmHandle) { _confirmAt = -1f; return; }
        var c = LiveInstance();
        if (c == null) { _confirmAt = -1f; return; }
        int n = LoadedCount();
        host.LogInfo($"[qc] ensure(confirm) at {Stamp()}: '{c.gameObject.name}' enabled={SafeEnabled(c)} " +
                     $"Instance={Describe(GetStatic(_qc, "Instance"))} commands={n} " +
                     $"TableGenerated={GetStatic(_proc, "TableGenerated")} IsActive={GetProperty(c, "IsActive")}");
        // A console that has just become live is the one moment the native test.* rows can be added
        // (self-guarded: only once per table, and only while the console is unlocked).
        TestRowsSync(host);
        if (n > 0 || ++_confirmTries >= 10) { _confirmAt = -1f; return; }
        _confirmAt = now + 0.5f;
    }

    private static float _capturesAt;
    private static bool _captures;

    // Read by ModBindings.Blocked: while the game's console is open no mod world shortcut may
    // fire, exactly like the mod console's own flag. Cached for 0.1 s (the read is a reflection
    // call into il2cpp and the gate is evaluated several times per frame).
    internal static bool CapturesInput
    {
        get
        {
            float now;
            try { now = Time.unscaledTime; } catch { return _captures; }
            if (now < _capturesAt) return _captures;
            _capturesAt = now + 0.1f;
            _captures = false;
            try
            {
                var c = LiveInstance();
                if (c != null) _captures = IsActive(c);
            }
            catch { }
            return _captures;
        }
    }

    private static float _keyProbeUntil = -1f;
    private static int _keyProbeLines;

    // Diagnostic for the synthetic key path only (bounded: at most ProbeLines lines per request).
    // Answers one question: does an event queued at the engine level reach `wasPressedThisFrame`
    // in the mod's own poll on the following frame?
    private static void KeyProbe(IHostContext host, float now)
    {
        if (_keyProbeUntil < 0f) return;
        if (now > _keyProbeUntil || _keyProbeLines > 12) { _keyProbeUntil = -1f; return; }
        _keyProbeLines++;
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) { host.LogInfo($"[qc] keyprobe f={Time.frameCount}: no keyboard"); return; }
            var ctl = kb[UnityEngine.InputSystem.Key.Backquote];
            host.LogInfo($"[qc] keyprobe f={Time.frameCount} t={now:0.000} backquote: isPressed={ctl?.isPressed} " +
                         $"down={ctl?.wasPressedThisFrame} up={ctl?.wasReleasedThisFrame} " +
                         $"mode={UnityEngine.InputSystem.InputSystem.settings?.updateMode}");
        }
        catch (Exception e) { host.LogInfo($"[qc] keyprobe threw {e.GetType().Name}: {e.Message}"); }
    }

    internal static void Tick(IHostContext host)
    {
        if (host == null) return;
        // First tick of the generation: read the access slot and say what it holds (locked, or
        // restored unlocked by an earlier generation - never a second unlock, never a sound).
        QcAccess.Initialize(host);
        if (!_announced)
        {
            _announced = true;
            try
            {
                host.LogInfo($"[qc] activator ready request={Path.Combine(host.ScreensDir ?? "", RequestName)} " +
                             $"toggle={ModBindings.Get("mod.console.toggle")?.CurrentText}");
            }
            catch { }
        }
        float now;
        try { now = Time.unscaledTime; } catch { return; }
        // Tilde first: a press is one frame wide, and this read skips the gate so it also closes
        // the console it opened. While the console is locked the key is not an entry point at all,
        // so the press is refused before Toggle can see it.
        if (ModBindings.PressedUngated("mod.console.toggle", out string toggleTrigger))
        {
            if (!QcAccess.IsUnlocked) QcAccess.Deny("tilde", "binding");
            else
            {
                try { Toggle(host, toggleTrigger); }
                catch (Exception e) { host.LogWarning($"[qc] toggle threw {e.GetType().Name}: {e.Message}"); }
            }
        }
        // The pause-menu code: arms the text-input capture, resets it on focus loss / menu close /
        // scene change, and drains a recognized code here - on the normal game tick, never inside
        // the text callback.
        try { PauseCode.Tick(host, now); }
        catch (Exception e) { host.LogWarning($"[qc:access] code tick threw {e.GetType().Name}: {e.Message}"); }
        TrackScene(host, now);
        EnsureTick(host, now);
        ConfirmTable(host, now);
        // Native test.* rows: the console's own Initialize builds the command table asynchronously, so
        // the unlock (or a scene's ensure) can land while it is still generating. One retry per second
        // until the rows are in; once they are, TestRowsSync is a field read.
        if (!_testRowsDone && now >= _testRowsRetryAt)
        {
            _testRowsRetryAt = now + 1f;
            TestRowsSync(host);
        }
        // The frame half of `alterum.test.await-model`: pending waits are re-read here, at their own
        // frame cadence, and end on the predicate, the wall-clock deadline or the check ceiling.
        // Never a Thread.Sleep: the game's own frame loop is the clock.
        try { AwaitTick(host, now); }
        catch (Exception e) { host.LogWarning($"[qc:test] await tick threw {e.GetType().Name}: {e.Message}"); }
        // Scenario execution is de-scoped; no scenario state is advanced.
        KeyProbe(host, now);
        if (_releaseKeyAt > 0f && now >= _releaseKeyAt)
        {
            _releaseKeyAt = -1f;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null)
                {
                    UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb,
                        new UnityEngine.InputSystem.LowLevel.KeyboardState());
                    UnityEngine.InputSystem.InputSystem.Update();
                    host.LogInfo("[qc] key: released all");
                }
            }
            catch (Exception e) { host.LogWarning($"[qc] key release threw: {e.Message}"); }
        }
        if (_recheck != null && now >= _recheckAt)
        {
            var c = _recheck;
            _recheck = null;
            host.LogInfo($"[qc] {_recheckWhy} +{RecheckDelay:0.0}s: enabled={SafeEnabled(c)} " +
                         $"activeInHierarchy={SafeActive(c)} Instance={Describe(GetStatic(_qc, "Instance"))} " +
                         $"IsActive={GetProperty(GetStatic(_qc, "Instance"), "IsActive")}");
            Gate(host);
        }
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;

        string path;
        string text;
        try
        {
            path = Path.Combine(host.ScreensDir, RequestName);
            if (!File.Exists(path)) return;
            text = File.ReadAllText(path);
            try { File.Delete(path); } catch { }
        }
        catch (Exception e)
        {
            host.LogWarning($"[qc] request read failed: {e.Message}");
            return;
        }
        if (text == null) return;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            host.LogInfo($"[qc] > {line}");
            try { Run(host, line); }
            catch (Exception e) { host.LogWarning($"[qc] '{line}' threw {e.GetType().Name}: {e.Message}"); }
        }
    }

    private static void Run(IHostContext host, string line)
    {
        int sp = line.IndexOf(' ');
        string verb = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
        string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();

        switch (verb)
        {
            case "status": Status(host); break;
            case "boot": Boot(host); break;
            case "table": Table(host); break;
            case "help": Help(host); break;
            case "dump": Dump(host); break;
            case "invoke": Invoke(host, arg); break;
            case "exec": ExecuteDeveloperCommand(host, arg); break;
            case "probe": Probe(host); break;
            case "enable": Enable(host); break;
            case "ensure": EnsureNow(host); break;
            case "toggle": Toggle(host, "request"); break;
            case "raw": RawEnabled(host, arg); break;
            case "off": Off(host); break;
            case "out": Out(host); break;
            case "state": State(host); break;
            case "say": Say(host, arg); break;
            case "key": KeyInject(host, arg); break;
            case "gate": Gate(host); break;
            case "native": Native(host, arg); break;
            // Removing the seam gate uninstalls the console's own enforcement, so it is gated like
            // `enable`/`boot`/`invoke`: the dispatcher refusing request verbs does not replace the
            // process-wide QuantumConsole/Processor seam gate it promises, and anything else that can
            // reach those seams would then be ungated. Unlocked it stays available, for diagnostics.
            case "gateoff":
                if (RefuseLocked("gateoff")) return;
                QcGate.Dispose();
                host.LogInfo("[qc] gate hooks removed");
                return;
            case "verbose": QcGate.Verbose(arg != "0"); host.LogInfo($"[qc] gate stacks verbose={arg != "0"}"); break;
            case "sig": Sig(host, arg); break;
            case "typetest": TypeTest(host); break;
            case "coldreset": ColdReset(host); break;
            case "sigs": Sigs(host); break;
            // Alterum test control layer: read-only diagnostics. These report the mod's own
            // authoritative identity/remote/pose/wardrobe state and never write, execute or open
            // anything, so they stay available while the console is locked (see RefuseLocked).
            // Canonical spelling first, then the legacy `test.*` alias on the SAME arm: one handler
            // call site, so an alias cannot read anything the canonical verb would not.
            case "alterum.test.pose":
            case "test.pose": TestPose(host); break;
            case "alterum.test.identity":
            case "test.identity": TestIdentity(host, arg); break;
            case "alterum.test.remote-list":
            case "test.remote-list": TestRemoteList(host); break;
            case "alterum.test.wardrobe-list":
            case "test.wardrobe-list": TestWardrobeList(host); break;
            // Model observation: the read-only pair that answers "is this actor wearing <slug>".
            // `assert` is one observation and a verdict; `await` registers a frame-driven wait and
            // returns (its result lines follow from AwaitTick). Neither writes, selects or sends, so
            // both stay available while the console is locked, like the probes above.
            case "alterum.test.assert-model": TestAssertModel(host, arg); break;
            case "alterum.test.await-model": TestAwaitModel(host, arg); break;
            // Asset diagnostics: the same read-only class (they report the caches' own bookkeeping and
            // never acquire, release, trim or load), so they are available while the console is locked.
            case "alterum.test.assets": TestAssets(host); break;
            case "alterum.test.assert-assets": TestAssertAssets(host); break;
            // The image proof: one PNG read and one verdict, so it is in the read-only group above and
            // stays available while the console is locked.
            case "alterum.test.assert-image": TestAssertImage(host, arg); break;
            // The runtime snapshot: one manifest artifact under the screens dir, every field read through
            // the helper that already owns it. Nothing is posed, selected, opened or rendered, so it is in
            // the read-only group (and stays available while locked), like `dump`, which also writes a file.
            case "alterum.test.snapshot": TestSnapshot(host, arg); break;
            // Named points: the table is a read-only diagnostic (it lists coordinates, yaw, intent and
            // the live preconditions), so it stays available while locked. APPLYING one is a setup
            // mutation through the game's own authoritative teleport, so it is gated like the setup
            // action below - and the handler itself requires server authority.
            case "alterum.test.points": TestPoints(host); break;
            case "alterum.test.point":
                if (RefuseLocked("alterum.test.point")) return;
                TestPoint(host, arg);
                break;
            // The layer's setup actions, and verbs that change what is on screen: gated like
            // `enable`/`boot`/`invoke` rather than following the read-only group above. Opening and
            // closing are the two halves of the native wardrobe lifecycle; both go through the
            // scene handler's own method and neither fabricates UI state. Open is canonical +
            // legacy alias on one arm; the gate is told which spelling was typed, so a refusal names
            // the directive the caller actually used.
            case "alterum.test.open-wardrobe":
            case "test.open-wardrobe":
                if (RefuseLocked(verb)) return;
                TestOpenWardrobe(host);
                break;
            case "alterum.test.close-wardrobe":
                if (RefuseLocked("alterum.test.close-wardrobe")) return;
                TestCloseWardrobe(host);
                break;
            // Selection is a mutation (it writes the player's model row and persists it), so it is
            // gated exactly like the setup actions above. `<slug> ui|protocol` picks the path; the
            // handlers behind it are the wardrobe's own row and the module's existing selection
            // helper, never a new one.
            case "alterum.test.select-model":
                if (RefuseLocked("alterum.test.select-model")) return;
                TestSelectModel(host, arg);
                break;
            // The proof family's general capture: one frame of whatever the game camera is showing,
            // through ScreenShot's own render path - gated exactly like `capture-remote` below, for the
            // same reason (it borrows the game's own camera for a frame and writes a file).
            case "alterum.test.capture":
                if (RefuseLocked("alterum.test.capture")) return;
                TestCapture(host, arg);
                break;
            // A capture is a proof, not a diagnostic: it borrows the game's own camera for one frame
            // and writes a file, so it is gated exactly like the mutations above.
            case "alterum.test.capture-remote":
                if (RefuseLocked("alterum.test.capture-remote")) return;
                TestCaptureRemote(host, arg);
                break;
            // The proof family's pose report. It writes no camera and no file, but it is gated with the
            // capture verb it reports on, so the family has one gate and a locked console cannot be
            // asked for proof-camera placement.
            case "alterum.test.camera-remote":
                if (RefuseLocked("alterum.test.camera-remote")) return;
                TestCameraRemote(host, arg);
                break;
            default: host.LogWarning($"[qc] unknown directive '{verb}' (status|boot|table|help|dump|invoke <cmd>|alterum.test.pose|alterum.test.identity [clientId]|alterum.test.remote-list|alterum.test.wardrobe-list|alterum.test.assert-model <clientId|local> <slug>|alterum.test.await-model <clientId|local> <slug>|alterum.test.assets|alterum.test.assert-assets|alterum.test.assert-image <path> <maxMagentaPixels>|alterum.test.snapshot [tag]|alterum.test.points|alterum.test.point <name>|alterum.test.open-wardrobe|alterum.test.close-wardrobe|alterum.test.select-model <slug> ui|protocol|alterum.test.capture <tag>|alterum.test.capture-remote <clientId> <tag>|alterum.test.camera-remote <clientId>|compatibility aliases: test.pose|test.identity [clientId]|test.remote-list|test.wardrobe-list|test.open-wardrobe)"); break;
        }
    }

    // ------------------------------------------------------------------ directives

    // Request verbs that would give access (enable/boot/ensure/raw/probe/toggle/key), run a command
    // (help/invoke/say), apply a named teleport point (alterum.test.point), open or close the native
    // wardrobe (alterum.test.open-wardrobe / alterum.test.close-wardrobe), SELECT a model
    // (alterum.test.select-model), capture evidence (alterum.test.capture /
    // alterum.test.capture-remote), report a proof-camera pose (alterum.test.camera-remote) or
    // REMOVE the console's own enforcement (gateoff) are refused while the console is locked. Read-only diagnostics - status, state, table, dump, sig/sigs,
    // typetest, native, gate, out, coldreset, off,
    // the read-only test probes (alterum.test.pose / identity / remote-list / wardrobe-list and their
    // legacy test.* aliases), the model observation verbs (alterum.test.assert-model /
    // await-model), the asset diagnostics (alterum.test.assets / assert-assets), the image proof
    // (alterum.test.assert-image), the runtime snapshot (alterum.test.snapshot, whose only write is
    // its own manifest under the screens dir - the same class as `dump`) and the named-point
    // table (alterum.test.points) - do not call this:
    // they report, they do not open, close, execute, select, capture, wait on a mutation or uninstall
    // anything.
    private static bool RefuseLocked(string entry)
    {
        if (QcAccess.IsUnlocked) return false;
        QcAccess.Deny(entry, "request");
        return true;
    }

    private static void Status(IHostContext host)
    {
        Resolve(host);
        var consoles = FindConsoles();
        host.LogInfo($"[qc] QuantumConsole components: {consoles.Count}");
        for (int i = 0; i < consoles.Count; i++)
        {
            var c = consoles[i];
            string path = PathOf(c);
            bool scene = false;
            try { scene = c.gameObject.scene.IsValid(); } catch { }
            host.LogInfo($"[qc]   [{i}] '{c.gameObject.name}' path='{path}' scene={scene} " +
                         $"enabled={SafeEnabled(c)} activeInHierarchy={SafeActive(c)}");
        }
        object instance = GetStatic(_qc, "Instance");
        host.LogInfo($"[qc] Instance={(instance == null ? "null" : instance.ToString())}");
        var con = instance as Component;
        if (con != null)
        {
            host.LogInfo($"[qc]   instance '{con.name}' enabled={SafeEnabled(con)} activeInHierarchy={SafeActive(con)}");
            object act = GetProperty(instance, "IsActive");
            host.LogInfo($"[qc]   IsActive={act}");
        }
        ProcStatus(host);
        host.LogInfo($"[qc:access] {QcAccess.Summary} {PauseCode.Summary}");
        host.LogInfo($"[qc:test] {TestCommandsSummary}");
        host.LogInfo($"[qc:test] model observations pending={_awaits.Count}");
        host.LogInfo($"[qc:test] {ScenarioProgressText()}");
    }

    private static void Boot(IHostContext host)
    {
        if (RefuseLocked("boot")) return;
        Resolve(host);
        // Only a console that is already active in its hierarchy: the menu's dormant
        // 'Quantum Console (SRP)' root is never activated from here (its Awake has not run, and
        // waking a prefab root is not this mod's decision). PickConsole applies the same rule.
        UnityEngine.Component target = PickConsole(host, "boot", true, out string diag);
        if (target == null) { host.LogWarning($"[qc] boot: no active scene QuantumConsole ({diag})"); return; }

        host.LogInfo($"[qc] boot target '{target.gameObject.name}' path='{PathOf(target)}' " +
                     $"before enabled={SafeEnabled(target)} activeInHierarchy={SafeActive(target)}");
        SetEnabled(target, true);
        host.LogInfo($"[qc] after enabled=true: enabled={SafeEnabled(target)} activeInHierarchy={SafeActive(target)}");
        host.LogInfo($"[qc] Instance after enable={Describe(GetStatic(_qc, "Instance"))}");

        if (GetStatic(_qc, "Instance") == null)
        {
            // Awake/Initialize are private in the shipped build; invoking them is a fallback only.
            InvokeOn(target, "Awake", 0);
            host.LogInfo($"[qc] after Awake(): enabled={SafeEnabled(target)} Instance={Describe(GetStatic(_qc, "Instance"))}");
            if (GetStatic(_qc, "Instance") == null)
            {
                InvokeOn(target, "Initialize", 0);
                host.LogInfo($"[qc] after Initialize(): enabled={SafeEnabled(target)} Instance={Describe(GetStatic(_qc, "Instance"))}");
            }
        }

        object instance = GetStatic(_qc, "Instance");
        if (instance != null)
        {
            Call(instance, "Activate", 0);
            host.LogInfo($"[qc] Activate() -> IsActive={GetProperty(instance, "IsActive")} " +
                         $"enabled={SafeEnabled(target)}");
        }
        ProcStatus(host);
        if (LoadedCount() <= 0) Table(host);
        if (LoadedCount() > 0) Help(host);
    }

    private static void Table(IHostContext host)
    {
        Resolve(host);
        ProcStatus(host);
        var m = FindMethod(_proc, "GenerateCommandTable", 2);
        if (m == null) { host.LogWarning("[qc] QuantumConsoleProcessor.GenerateCommandTable(bool,bool) not found"); return; }
        // forceReload: true rebuilds the table from the game's own commands, so the mod's test.* rows
        // go with it. Forget them here (nothing to remove: the rebuild drops them) and let the sync
        // below re-add them once the table is back - still unlocked only.
        TestRowsForget();
        try
        {
            m.Invoke(null, new object[] { true, true });
            host.LogInfo("[qc] GenerateCommandTable(true, true) returned");
        }
        catch (Exception e) { host.LogWarning($"[qc] GenerateCommandTable threw: {Describe(e)}"); }
        ProcStatus(host);
        TestRowsSync(host);
    }

    private static void Help(IHostContext host)
    {
        if (RefuseLocked("help")) return;
        Resolve(host);
        object instance = GetStatic(_qc, "Instance");
        object result;
        if (instance != null)
        {
            result = Call(instance, "InvokeCommand", 1, "help");
            host.LogInfo($"[qc] help (instance) -> {Describe(result)}");
        }
        else
        {
            // The processor executes commands on its own: `public static object InvokeCommand(string)`.
            // That is the same entry point the console UI calls, so the table is usable without a
            // live console instance.
            var m = FindMethod(_proc, "InvokeCommand", 1);
            if (m == null) { host.LogWarning("[qc] help: no Instance and no static InvokeCommand"); return; }
            try { result = m.Invoke(null, new object[] { "help" }); }
            catch (Exception e) { host.LogWarning($"[qc] help static threw: {Describe(e)}"); return; }
            host.LogInfo($"[qc] help (static) -> {Describe(result)}");
        }
        Dump(host);
    }

    private const float RecheckDelay = 0.6f;

    // Force the console on and see who objects. Everything that could gate it is read BEFORE the
    // write (IsSupportedState is private but callable) so a refusal names itself in the log.
    // ------------------------------------------------------------------ toggle / scene ready

    // Open or close the game's own console through its own Activate()/Deactivate(). Nothing is
    // reimplemented: Activate shows the shipped UI and focuses its input field, Deactivate hides
    // it; the command pipeline underneath is the game's.
    private static void Toggle(IHostContext host, string trigger)
    {
        // The gate, at the one place every route goes through: the binding, the request verb and
        // the pause code itself. Locked means "does not open", not "opens something else".
        if (!QcAccess.IsUnlocked)
        {
            QcAccess.Deny("toggle", trigger == null ? "unknown" : (trigger == "request" ? "request" : "binding"));
            return;
        }
        Resolve(host);
        var c = TypedConsole(host, out string diag);
        if (c == null) { host.LogWarning($"[qc] toggle {trigger}: no console ({diag})"); return; }

        if (IsActive(c))
        {
            Call(c, "Deactivate", 0);
            host.LogInfo($"[qc] toggle {trigger} -> closed IsActive={IsActive(c)}");
            return;
        }

        if (!EnsureEnabled(host, "toggle", true))
        {
            host.LogWarning("[qc] toggle: console is not live, nothing to open (see ensure log)");
            return;
        }
        Call(c, "Activate", 0);
        FocusInput(c);
        host.LogInfo($"[qc] toggle {trigger} -> open IsActive={IsActive(c)} enabled={SafeEnabled(c)} " +
                     $"Instance={Describe(GetStatic(_qc, "Instance"))} commands={LoadedCount()}");
    }

    // The cheat's open: the same Toggle every other route uses, with its own trigger label so the
    // log says which route opened the console.
    internal static void OpenFromCheat(IHostContext host)
    {
        try { Toggle(host, "pause-code"); }
        catch (Exception e) { host?.LogWarning($"[qc] toggle pause-code threw {e.GetType().Name}: {e.Message}"); }
    }

    // Called from QcGate.Dispose, i.e. once per hot generation, before this assembly is unloaded.
    // The text-input subscription holds a delegate created in THIS assembly, so it has to be
    // removed or the retiring generation stays referenced from the Input System's listener list.
    // The unlock state is untouched: it lives in the AppDomain slot (QcAccess), not here.
    internal static void ShutdownCapture()
    {
        try { PauseCode.Disarm(); } catch { }
        try { QcAccess.Note("capture disarmed (generation teardown)"); } catch { }
    }

    private static void Off(IHostContext host)
    {
        Resolve(host);
        var c = TypedConsole(host, out _);
        if (c == null) { host.LogWarning("[qc] off: no console"); return; }
        Call(c, "Deactivate", 0);
        host.LogInfo($"[qc] off -> IsActive={IsActive(c)}");
    }

    // `qc.request` `ensure`: the same idempotent enable the scene-ready path uses, on demand.
    private static void EnsureNow(IHostContext host)
    {
        if (RefuseLocked("ensure")) return;
        bool gate = QcGate.Installed;
        bool ok = EnsureEnabled(host, "request", true);
        host.LogInfo($"[qc] ensure(request) -> {(ok ? "live" : (gate ? "not live" : "skipped (gate off)"))} " +
                     $"IsActive={GetProperty(LiveInstance(), "IsActive")}");
    }

    // `qc.request` `coldreset`: forget the scene/ensure state so the cold-start path runs again
    // without a launch. Exactly what a fresh process does on its first Tick.
    private static void ColdReset(IHostContext host)
    {
        _sceneHandle = int.MinValue;
        _ensureDone = false;
        _ensureTries = 0;
        _ensureBackoff = EnsureFirstDelay;
        _ensureAt = -1f;
        _recheck = null;
        host.LogInfo($"[qc] coldreset at {Stamp()}: the next Tick re-tracks the scene from scratch");
    }

    // `qc.request` `typetest`: what each acquisition strategy returns *right now*, with the wrapper
    // type of every console in the process. "present but untyped" and "not present yet" are
    // different cold-start failures and only this distinguishes them.
    private static void TypeTest(IHostContext host)
    {
        Resolve(host);
        host.LogInfo($"[qc] typetest at {Stamp()}: type={(_qc == null ? "null" : _qc.AssemblyQualifiedName)}");
        var cands = ScanConsoles(out int transforms);
        host.LogInfo($"[qc] typetest: transforms={transforms} candidates={cands.Count} scene='{ActiveSceneName()}' " +
                     $"Instance={Describe(GetStatic(_qc, "Instance"))} commands={LoadedCount()}");
        for (int i = 0; i < cands.Count; i++)
        {
            var cc = cands[i];
            host.LogInfo($"[qc] typetest: [{i}] '{cc.Name}' path='{cc.Path}' scene={cc.SceneValid} goActive={cc.GoActiveSelf} " +
                         $"activeInHierarchy={cc.ActiveInHierarchy} enabled={cc.Enabled} wrapper={cc.RawType} " +
                         $"typed={(cc.Typed == null ? "NO" : "yes")} via={cc.How}");
            if (cc.Typed == null) continue;
            try
            {
                var got = cc.Typed.gameObject.GetComponent(Il2CppType.From(_qc));
                host.LogInfo($"[qc] typetest: [{i}] GetComponent(Il2CppType) -> {(got == null ? "null" : got.GetType().FullName)}");
            }
            catch (Exception e) { host.LogInfo($"[qc] typetest: [{i}] GetComponent(Il2CppType) threw {e.GetType().Name}"); }
        }
        foreach (var rn in new[] { "Zenject.Installers.ConsoleInstaller", "InputSystem.UI.ConsoleInputHandler" })
        {
            var r = ReferenceFrom(rn, "_console");
            host.LogInfo($"[qc] typetest: {rn}._console -> {(r == null ? "null" : r.GetType().FullName)}");
        }
    }

    // `qc.request` `raw 0|1`: write the component's own `enabled` and report the readback. Used to
    // show that the scene-ready Ensure() really is idempotent (a scene load hands over a disabled
    // console) instead of only reporting a console that happens to be on already.
    private static void RawEnabled(IHostContext host, string arg)
    {
        if (RefuseLocked("raw")) return;
        Resolve(host);
        var c = TypedConsole(host, out string diag);
        if (c == null) { host.LogWarning($"[qc] raw: no console ({diag})"); return; }
        var b = c.TryCast<Behaviour>();
        if (b == null) { host.LogWarning("[qc] raw: not a Behaviour"); return; }
        bool want = arg != "0";
        bool before = b.enabled;
        b.enabled = want;
        host.LogInfo($"[qc] raw: enabled {before}->{b.enabled} (wanted {want}) gate={(QcGate.Installed ? "on" : "off")} " +
                     $"Instance={Describe(GetStatic(_qc, "Instance"))}");
    }

    // Re-armed on every scene change (and, implicitly, on every hot reload: a new generation
    // starts with _sceneHandle = int.MinValue, so the first Tick tracks the current scene again).
    // Every attempt after that re-derives the console from the live scene - nothing is cached, so a
    // console that is destroyed and rebuilt by a scene load is picked up again.
    private static void TrackScene(IHostContext host, float now)
    {
        int handle;
        string name;
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            handle = scene.handle;
            name = scene.name;
        }
        catch { return; }
        if (handle == _sceneHandle) return;
        _sceneHandle = handle;
        _ensureDone = false;
        _ensureTries = 0;
        _ensureBackoff = EnsureFirstDelay;
        _ensureAt = now + EnsureFirstDelay;
        host.LogInfo($"[qc] scene '{name}' (handle={handle}) at {Stamp()}: console enable scheduled in {EnsureFirstDelay:0.00}s");
    }

    // Retry with backoff until the live console exists and is enabled, then stop. No attempt cap:
    // a fresh launch reaches "live canvas exists" at a different time on every machine, and the
    // backoff makes waiting for it cost one line every few seconds instead of every frame.
    private static void EnsureTick(IHostContext host, float now)
    {
        if (_ensureDone || _ensureAt < 0f) return;
        bool woke = _consoleAwoke;
        _consoleAwoke = false;
        if (!woke && now < _ensureAt) return;
        _ensureTries++;
        bool first = _ensureTries <= 2;
        if (EnsureEnabled(host, _ensureTries == 1 ? "scene" : "scene retry", first))
        {
            _ensureDone = true;
            _ensureAt = -1f;
            _confirmHandle = _sceneHandle;
            _confirmTries = 0;
            _confirmAt = now + 0.5f;
            host.LogInfo($"[qc] ensure: console live at {Stamp()} after {_ensureTries} attempt(s) in scene '{ActiveSceneName()}'");
            return;
        }
        _ensureBackoff = Math.Min(_ensureBackoff * 2f, EnsureMaxBackoff);
        _ensureAt = now + _ensureBackoff;
        if (first || _ensureTries % 10 == 0)
            host.LogInfo($"[qc] ensure: no live console yet at {Stamp()} (attempt {_ensureTries}) - retrying in {_ensureBackoff:0.0}s");
    }

    // Idempotent. Turns the scene's own console into a live, initialized, *closed* console:
    // component enabled, Instance assigned, command table generated. It never opens the UI.
    //
    // Guarded on QcGate on purpose: with the gate unpatched, writing `enabled = true` runs the
    // console's own OnEnable, whose compiled-in supported-state check sets it straight back to
    // false. So a session that ran `gateoff` is told once and left alone instead of fighting it.
    private static bool EnsureEnabled(IHostContext host, string why, bool verbose)
    {
        Resolve(host);
        if (_qc == null) return false;
        if (!QcGate.Installed)
        {
            if (verbose) host.LogWarning($"[qc] ensure({why}): gate not installed - not forcing enabled (the console's own OnEnable would revert it)");
            return false;
        }
        // Typed or nothing. An untyped Component proxy cannot be assigned to Instance, has no
        // `enabled`, and answers every reflection lookup with null - which is the cold-start
        // failure this path exists to avoid.
        var c = PickConsole(host, "ensure(" + why + ")", verbose, out string diag);
        if (c == null) { if (verbose) host.LogWarning($"[qc] ensure({why}): no live console ({diag})"); return false; }

        var b = c.TryCast<Behaviour>();
        if (b == null) { host.LogWarning($"[qc] ensure({why}): typed console is not a Behaviour"); return false; }

        bool wasEnabled = b.enabled;
        if (!wasEnabled) b.enabled = true;                    // QcGate skips OnEnable for this type only

        bool setNow = false;
        if (LiveInstance() == null)
        {
            var setter = FindPropertySetter(_qc, "Instance");
            if (setter == null) { host.LogWarning($"[qc] ensure({why}): {QcTypeName}.Instance has no setter"); return false; }
            try
            {
                setter.SetValue(null, c);                     // typed proxy only - never a Component
                setNow = true;
            }
            catch (Exception e)
            {
                host.LogWarning($"[qc] ensure({why}): Instance=this threw: {Describe(e)}");
                return false;
            }
        }

        if (LiveInstance() == null || LoadedCount() <= 0) InvokePrivate(c, "Initialize");
        SilenceOwnToggleKey(host, c, verbose);

        bool live = SafeEnabled(c) && LiveInstance() != null;
        if (verbose || wasEnabled != SafeEnabled(c) || !live)
            host.LogInfo($"[qc] ensure({why}) at {Stamp()}: '{c.gameObject.name}' wrapper={c.GetType().FullName} " +
                         $"enabled {wasEnabled}->{SafeEnabled(c)} Instance={Describe(GetStatic(_qc, "Instance"))} " +
                         $"commands={LoadedCount()} IsActive={GetProperty(c, "IsActive")} live={live} setInstance={setNow} " +
                         $"[{diag}]");
        return live;
    }

    // Exactly one owner for Tilde. The console's own config also listens on Tilde
    // (`ToggleConsoleVisibilityKey`); once our binding calls Activate()/Deactivate(), a second
    // handler on the same key would toggle straight back. So the console's own key is cleared to
    // None in memory and the mod's registry owns the key. If the property is not an enum we say so
    // and leave the console's setting alone - the console's handler is then the only unknown.
    private static void SilenceOwnToggleKey(IHostContext host, Component c, bool verbose)
    {
        try
        {
            object cfg = GetProperty(c, "KeyConfig");
            if (cfg == null) { if (verbose) host.LogInfo("[qc] keys: no KeyConfig on the console"); return; }
            var prop = cfg.GetType().GetProperty("ToggleConsoleVisibilityKey",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop == null) { if (verbose) host.LogInfo("[qc] keys: KeyConfig has no ToggleConsoleVisibilityKey"); return; }
            Type t = prop.PropertyType;
            object before = null;
            try { before = prop.GetValue(cfg); } catch { }
            if (!prop.CanWrite)
            {
                if (verbose) host.LogInfo($"[qc] keys: ToggleConsoleVisibilityKey is read-only ({t.Name}) - left as {before}");
                return;
            }
            if (!t.IsEnum)
            {
                // QFSW.QC.ModifierKeyCombo: a struct of { Key key; bool ctrl/shift/alt }. Its
                // default value is Key.None with no modifiers, which is the console's own "unbound".
                object dflt = t.IsValueType ? Activator.CreateInstance(t) : null;
                if (dflt == null)
                {
                    if (verbose) host.LogInfo($"[qc] keys: ToggleConsoleVisibilityKey type {t.Name} cannot be defaulted - left as {before}");
                    return;
                }
                prop.SetValue(cfg, dflt);
                host.LogInfo($"[qc] keys at {Stamp()}: console's own ToggleVisibility '{KeyText(before)}' -> '{KeyText(prop.GetValue(cfg))}' ({t.Name}, default) - " +
                             "the mod binding is the only Tilde handler");
                return;
            }
            object none = Enum.Parse(t, "None");
            if (before != null && before.Equals(none)) { if (verbose) host.LogInfo($"[qc] keys: console toggle key already {before}"); return; }
            prop.SetValue(cfg, none);
            host.LogInfo($"[qc] keys at {Stamp()}: console's own ToggleVisibility {KeyText(before)} -> {KeyText(prop.GetValue(cfg))} ({t.Name}) - " +
                         "the mod binding is the only Tilde handler");
        }
        catch (Exception e) { if (verbose) host.LogWarning($"[qc] keys: silencing the console toggle key threw: {Describe(e)}"); }
    }

    // ModifierKeyCombo is a struct whose ToString() prints its type name, so a log line that only
    // prints it cannot show whether the key is actually unbound. Print its fields instead.
    private static string KeyText(object o)
    {
        if (o == null) return "null";
        try
        {
            var sb = new StringBuilder();
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (sb.Length > 0) sb.Append(' ');
                object v;
                try { v = f.GetValue(o); } catch { v = "?"; }
                sb.Append(f.Name).Append('=').Append(v);
            }
            return sb.Length == 0 ? o.ToString() : sb.ToString();
        }
        catch { return "?"; }
    }

    // The console's input field has to be focused or typed text goes nowhere. Activate() usually
    // does this itself; doing it here is idempotent and survives a locked desktop losing focus.
    private static void FocusInput(Component c)
    {
        try
        {
            var input = GetProperty(c, "_consoleInput") as TMPro.TMP_InputField;
            if (input == null) return;
            input.Select();
            input.ActivateInputField();
        }
        catch { }
    }

    // The console's own output text, as the player sees it in the log panel.
    private static string ConsoleLogText(Component c)
    {
        try
        {
            object o = GetProperty(c, "_consoleLogText");
            if (o == null) return null;
            var text = o as TMPro.TMP_Text;
            if (text == null && o is Component comp) text = comp.TryCast<TMPro.TMP_Text>();
            if (text != null) return text.text;
            var field = o as TMPro.TMP_InputField;
            if (field == null && o is Component comp2) field = comp2.TryCast<TMPro.TMP_InputField>();
            return field?.text;
        }
        catch { return null; }
    }

    // Tail of the console's own log, which is the console's own rendering of the last result.
    private static void Out(IHostContext host)
    {
        Resolve(host);
        var c = TypedConsole(host, out _);
        if (c == null) { host.LogWarning("[qc] out: no console"); return; }
        string text = ConsoleLogText(c);
        if (string.IsNullOrEmpty(text)) { host.LogWarning("[qc] out: console log text is empty"); return; }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int from = Math.Max(0, lines.Length - 8);
        for (int i = from; i < lines.Length; i++)
        {
            string l = lines[i].TrimEnd();
            if (l.Length > 0) host.LogInfo($"[qc] out| {l}");
        }
    }

    private static bool IsActive(Component c) => c != null && GetProperty(c, "IsActive") is bool b && b;

    // QuantumConsole.Instance, but only while the object Unity gave it still exists: a scene
    // unload destroys the old console and the static may briefly point at a dead object, and a
    // dead proxy throws on every read.
    private static Component LiveInstance()
    {
        object instance = GetStatic(_qc, "Instance");
        var c = instance as Component;
        if (c == null) return null;
        try { if (!c.gameObject) return null; }
        catch { return null; }
        return c;
    }

    private static void Enable(IHostContext host)
    {
        if (RefuseLocked("enable")) return;
        Resolve(host);
        var c = TypedConsole(host, out string diag);
        host.LogInfo($"[qc] enable: {diag}");
        if (c == null) { host.LogWarning("[qc] enable: no console"); return; }
        var b = c.TryCast<Behaviour>();
        host.LogInfo($"[qc] enable before: wrapper={c.GetType().FullName} enabled={(b == null ? -1 : (b.enabled ? 1 : 0))} " +
                     $"activeSelf={SafeActiveSelf(c)} activeInHierarchy={SafeActive(c)} " +
                     $"Instance={Describe(GetStatic(_qc, "Instance"))}");
        host.LogInfo($"[qc] enable fields: supportedState={GetProperty(c, "_supportedState")} " +
                     $"activateOnStartup={GetProperty(c, "_activateOnStartup")} " +
                     $"initialiseOnStartup={GetProperty(c, "_initialiseOnStartup")} " +
                     $"singletonMode={GetProperty(c, "_singletonMode")} " +
                     $"IsActive={GetProperty(c, "IsActive")}");
        object supported = CallPrivate(c, "IsSupportedState", 0);
        host.LogInfo($"[qc] enable IsSupportedState() -> {Describe(supported)}");
        if (b == null) { host.LogWarning("[qc] enable: not a Behaviour"); return; }

        // OnEnable's original is skipped by QcGate for this type only (its first statement is the
        // compiled-out supported-state gate, which would set enabled=false again). Everything after
        // that point is the console's own: AssignInstance -> Initialize -> Activate.
        b.enabled = true;
        host.LogInfo($"[qc] enable after write: enabled={b.enabled} Instance={Describe(GetStatic(_qc, "Instance"))}");

        if (GetStatic(_qc, "Instance") == null)
        {
            var setter = FindPropertySetter(_qc, "Instance");
            if (setter != null)
            {
                try { setter.SetValue(null, c); host.LogInfo($"[qc] enable Instance=this -> {Describe(GetStatic(_qc, "Instance"))}"); }
                catch (Exception e) { host.LogWarning($"[qc] enable Instance=this threw: {Describe(e)}"); }
            }
            else host.LogWarning("[qc] enable: no Instance setter");
        }

        InvokePrivate(c, "Initialize");
        host.LogInfo($"[qc] enable after Initialize(): enabled={b.enabled} IsActive={GetProperty(c, "IsActive")} " +
                     $"Instance={Describe(GetStatic(_qc, "Instance"))} LoadedCommandCount={GetStatic(_proc, "LoadedCommandCount")} " +
                     $"TableGenerated={GetStatic(_proc, "TableGenerated")}");

        if (GetProperty(c, "IsActive") is bool && !(bool)GetProperty(c, "IsActive"))
        {
            object r = Call(c, "Activate", 0);
            host.LogInfo($"[qc] enable Activate() -> {Describe(r)}");
        }
        host.LogInfo($"[qc] enable final: enabled={b.enabled} IsActive={GetProperty(c, "IsActive")} " +
                     $"Instance={Describe(GetStatic(_qc, "Instance"))} panels={Panels(c)}");

        _recheck = c;
        _recheckAt = Time.unscaledTime + RecheckDelay;
        _recheckWhy = "enable";
        Gate(host);
    }

    private static string Panels(Component c)
    {
        try
        {
            var p = GetProperty(c, "_panels");
            var arr = p as Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.UI.Image>;
            if (arr == null) return "null";
            var sb = new StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                var img = arr[i];
                if (img == null) { sb.Append("null,"); continue; }
                sb.Append(img.enabled ? "on" : "off").Append(img.gameObject.activeSelf ? "+act" : "+inact").Append(',');
            }
            return sb.ToString();
        }
        catch (Exception e) { return "<threw " + e.GetType().Name + ">"; }
    }

    private static PropertyInfo FindPropertySetter(Type t, string name)
    {
        try
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return p != null && p.CanWrite ? p : null;
        }
        catch { return null; }
    }

    private static void InvokePrivate(object target, string method)
    {
        var m = FindMethod(target.GetType(), method, 0);
        if (m == null) { try { Debug.LogWarning($"[qc] {method}/0 not found"); } catch { } return; }
        try { m.Invoke(target, null); }
        catch (Exception e) { try { Debug.LogWarning($"[qc] {method} threw: {Describe(e)}"); } catch { } }
    }

    // Disassemble the shipped native body of a console method plus its field offsets. IL2CPP has no
    // managed IL, so this is the only way to read a condition no managed API exposes.
    private static void Native(IHostContext host, string arg)
    {
        Resolve(host);
        var c = TypedConsole(host, out string diag);
        if (c == null) { host.LogWarning($"[qc] native: no console ({diag})"); return; }
        string name = string.IsNullOrEmpty(arg) ? "IsSupportedState" : arg;
        IntPtr klass = IL2CPP.il2cpp_object_get_class(c.Pointer);
        IntPtr m = IL2CPP.il2cpp_class_get_method_from_name(klass, name, 0);
        host.LogInfo($"[qc] native '{name}': klass=0x{klass.ToInt64():X} methodInfo=0x{m.ToInt64():X}");
        if (m == IntPtr.Zero) return;
        host.LogInfo($"[qc] native '{name}': name={System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(m))}");
        IntPtr fp = System.Runtime.InteropServices.Marshal.ReadIntPtr(m);
        host.LogInfo($"[qc] native '{name}': methodPointer=0x{fp.ToInt64():X}");
        var buf = new byte[128];
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(fp, buf, 0, buf.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++) sb.Append(buf[i].ToString("X2")).Append(' ');
            host.LogInfo($"[qc] native '{name}': bytes {sb}");
            string outp = Path.Combine(host.ScreensDir, "qc-native-" + name + ".bin");
            File.WriteAllBytes(outp, buf);
        }
        catch (Exception e) { host.LogWarning($"[qc] native '{name}': read threw {e.GetType().Name}: {e.Message}"); }
    }

    // Type into the console's own input field and submit through its own no-arg InvokeCommand():
    // the same path the UI takes on Return, without the keystrokes.
    private static void Say(IHostContext host, string line)
    {
        if (RefuseLocked("say")) return;
        Resolve(host);
        var c = TypedConsole(host, out _);
        if (c == null) { host.LogWarning("[qc] say: no console"); return; }
        var input = GetProperty(c, "_consoleInput") as TMPro.TMP_InputField;
        if (input == null) { host.LogWarning("[qc] say: no _consoleInput"); return; }
        // A closed console still executes commands, but nothing renders them: open it first so the
        // result lands in the console's own log panel, exactly as typing the line would.
        if (!IsActive(c))
        {
            EnsureEnabled(host, "say", false);
            Call(c, "Activate", 0);
            FocusInput(c);
            host.LogInfo($"[qc] say: console was closed, Activate() -> IsActive={IsActive(c)}");
        }
        input.text = line;
        host.LogInfo($"[qc] say: input field text='{input.text}' IsActive={GetProperty(c, "IsActive")}");
        object r = Call(c, "InvokeCommand", 0);
        host.LogInfo($"[qc] say: InvokeCommand() -> {Describe(r)}");
        Out(host);
    }

    private static float _releaseKeyAt = -1f;

    // QC's key config is polled through the new Input System (QFSW.QC references
    // UnityEngine.InputSystem.Key), so a key state can be queued at the engine level. This is not an
    // OS keystroke: it exercises the console's own Update/key handler, not the desktop input queue.
    private static void KeyInject(IHostContext host, string name)
    {
        // A queued key event is still an activation attempt: it would fire the console's own
        // Toggle key handler and open the UI without a real keystroke. Refused while locked.
        if (RefuseLocked("key")) return;
        UnityEngine.InputSystem.Key k;
        switch ((name ?? "").ToLowerInvariant())
        {
            case "tilde": case "backquote": k = UnityEngine.InputSystem.Key.Backquote; break;
            case "enter": case "return": k = UnityEngine.InputSystem.Key.Enter; break;
            case "escape": k = UnityEngine.InputSystem.Key.Escape; break;
            default: host.LogWarning($"[qc] key: unknown '{name}'"); return;
        }
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) { host.LogWarning("[qc] key: no Keyboard.current"); return; }
        // Deliberately NOT calling InputSystem.Update() here: forcing the update consumes the
        // press inside this call, before the mod's own per-frame poll (HotModule.Update) can read
        // `wasPressedThisFrame`, which is exactly why an earlier `key tilde` toggled nothing.
        // Left queued, the event is applied by the engine's input update at the start of the next
        // frame and the binding sees it like a real press. Still not an OS keystroke.
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb,
            new UnityEngine.InputSystem.LowLevel.KeyboardState(k));
        // Immediate update: log what the device sees right away, then let the probe show whether
        // the engine's own update keeps it. This is the only way to tell "the event was dropped"
        // apart from "the flag was consumed by this call".
        UnityEngine.InputSystem.InputSystem.Update();
        var immediate = kb[UnityEngine.InputSystem.Key.Backquote];
        host.LogInfo($"[qc] key: queued {k} press (engine applies it next frame); after local Update " +
                     $"isPressed={immediate?.isPressed} down={immediate?.wasPressedThisFrame}");
        _keyProbeUntil = Time.unscaledTime + 0.6f;
        _keyProbeLines = 0;
        _releaseKeyAt = Time.unscaledTime + 0.15f;
        host.LogInfo($"[qc] key: keyboard keysPressed now={kb.allKeys.Count}");
    }

    private static void State(IHostContext host)
    {
        Resolve(host);
        var c = TypedConsole(host, out string diag);
        if (c == null) { host.LogWarning($"[qc] state: no console ({diag})"); return; }
        var b = c.TryCast<Behaviour>();
        var go = c.gameObject;
        host.LogInfo($"[qc] state: '{go.name}' enabled={SafeEnabled(c)} activeSelf={SafeActiveSelf(c)} activeInHierarchy={SafeActive(c)} " +
                     $"layer={go.layer} IsActive={GetProperty(c, "IsActive")} Instance={Describe(GetStatic(_qc, "Instance"))}");
        try
        {
            var cv = go.GetComponent(Il2CppType.Of<Canvas>())?.TryCast<Canvas>();
            host.LogInfo($"[qc] state: Canvas={(cv == null ? "none" : $"enabled={cv.enabled} order={cv.sortingOrder} root={cv.isRootCanvas} scale={cv.scaleFactor}")}");
        }
        catch (Exception e) { host.LogWarning($"[qc] state: canvas threw {e.GetType().Name}"); }
        try
        {
            var cg = go.GetComponent(Il2CppType.Of<CanvasGroup>())?.TryCast<CanvasGroup>();
            host.LogInfo($"[qc] state: CanvasGroup={(cg == null ? "none" : $"alpha={cg.alpha} interactable={cg.interactable} blocks={cg.blocksRaycasts} ignore={cg.ignoreParentGroups}")}");
        }
        catch { }
        try
        {
            var rt = go.GetComponent(Il2CppType.Of<RectTransform>())?.TryCast<RectTransform>();
            if (rt != null) host.LogInfo($"[qc] state: rect size={rt.rect.width}x{rt.rect.height} pos={rt.position} scale={rt.localScale} active={rt.gameObject.activeSelf}");
        }
        catch { }
        try
        {
            var cont = GetProperty(c, "_containerRect") as RectTransform;
            host.LogInfo($"[qc] state: _containerRect={(cont == null ? "null" : $"'{cont.gameObject.name}' active={cont.gameObject.activeSelf}/{cont.gameObject.activeInHierarchy} size={cont.rect.width}x{cont.rect.height} pos={cont.position} scale={cont.localScale}")}");
        }
        catch (Exception e) { host.LogWarning($"[qc] state: container threw {e.GetType().Name}"); }
        try
        {
            var panels = GetProperty(c, "_panels") as Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.UI.Image>;
            if (panels != null)
                for (int i = 0; i < panels.Length; i++)
                {
                    var p = panels[i];
                    if (p == null) { host.LogInfo($"[qc] state: panel[{i}] null"); continue; }
                    var prt = p.rectTransform;
                    host.LogInfo($"[qc] state: panel[{i}] '{p.gameObject.name}' img={p.enabled} go={p.gameObject.activeSelf}/{p.gameObject.activeInHierarchy} " +
                                 $"alpha={p.color.a} size={prt.rect.width}x{prt.rect.height} pos={prt.position} parent='{(prt.parent == null ? "null" : prt.parent.gameObject.name)}'");
                }
        }
        catch (Exception e) { host.LogWarning($"[qc] state: panels threw {e.GetType().Name}: {e.Message}"); }
    }

    private static void Gate(IHostContext host)
    {
        string dump = QcGate.Dump();
        foreach (var line in dump.Replace("\r\n", "\n").Split('\n'))
            if (line.Length > 0) host.LogInfo(line);
    }

    // ------------------------------------------------------------------ test.* diagnostics
    //
    // The Alterum test control layer's read-only half: the probes, the model observation pair, the
    // asset diagnostics and the named-point table. They report the mod's own authoritative state
    // through the existing runtime APIs, on the existing `qc.request` transport.
    // Nothing here mutates: no teleport, no UI action, no model/wardrobe write, no RPC.
    // Every read goes through the module that already owns that fact (ModelPresentation for
    // identity, RemoteModels for the remote body/peer ledger, ReelLogic's body-first pose read for
    // the actor's position, SahurWardrobe's own database for the wardrobe list) instead of
    // re-deriving it, so a diagnostic line cannot disagree with what the game is actually showing.
    //
    // They deliberately do NOT call RefuseLocked: the AEZAKMI gate exists to stop the console being
    // opened and commands being executed, and these verbs do neither. They are the same class of
    // read-only diagnostic as `status`/`state`/`dump`, which are available while locked.
    //
    // The layer's mutating verbs, `alterum.test.point`, `alterum.test.open-wardrobe` (alias:
    // `test.open-wardrobe`), `alterum.test.close-wardrobe`, `alterum.test.select-model`,
    // `alterum.test.capture` and `alterum.test.capture-remote`, are
    // NOT part of this group: they sit below with their own gates and their own comments.

    // ---- named test points ---------------------------------------------------------------
    //
    // One table, read by `alterum.test.points` and applied by `alterum.test.point <name>`. Every
    // number is copied from this repo's own live evidence, named in `Source`; a name without a
    // grounded coordinate is left out rather than guessed. Scene is the Unity scene name the point
    // is valid in (the active scene must match before a move). X/Y/Z is the standing/feet pose handed
    // to the game's own teleport (world space) and Yaw is the facing in degrees around +Y, the same
    // convention as Quaternion.Euler(0, yaw, 0).
    private sealed class NamedPoint
    {
        internal string Name, Scene, Intent, Source;
        internal float X, Y, Z, Yaw;
    }

    private static readonly NamedPoint[] Points =
    {
        // The coordinate every camera/hat/mirror driver returns to: `ORIGIN` in tools/tp-verify.sh,
        // described there as "the Lobby spawn eye, from the game's own first engage". The yaw is the
        // view yaw measured at that eye in verify/third-person-camera.md; the feet level is the
        // spawn body's own `feetY` in shots/reel-of-fortune/README.md.
        new NamedPoint
        {
            Name = "lobby-spawn",
            Scene = "Lobby",
            X = 5.354f, Y = 0.040f, Z = -7.568f,
            Yaw = 267.964f,
            Intent = "the lobby spawn standing pose the camera/hat/mirror drivers return to",
            Source = "tracks/merged/tools/tp-verify.sh ORIGIN=5.354,-7.568 (the Lobby spawn eye); " +
                     "tracks/merged/verify/third-person-camera.md [tp-cam:probe] viewYaw=267.964; " +
                     "tracks/merged/shots/reel-of-fortune/README.md [prop:player] feetY=0.04",
        },
        // The clothes/mirror check pose: tp-verify.sh puts the player at x,z (1.58,-10.09), "on the
        // mirror camera's own axis (3 m out)". The mirror's baked world pose (ModelSwapRuntime
        // FixedLobbyPos/FixedLobbyEuler: yaw 41.06) gives the facing back at the glass (41.06+180).
        // The feet level is the same Lobby tent floor the spawn body reports (`feetY=0.04`).
        new NamedPoint
        {
            Name = "lobby-mirror",
            Scene = "Lobby",
            X = 1.58f, Y = 0.040f, Z = -10.09f,
            Yaw = 221.06f,
            Intent = "on the lobby mirror's own axis 3 m out, facing the glass (the clothes/mirror check pose)",
            Source = "tracks/merged/tools/tp-verify.sh teleport=to:1.58,-10.09 (on the mirror " +
                     "camera's own axis, 3 m out); ModelSwapRuntime.cs FixedLobbyPos/FixedLobbyEuler " +
                     "(mirror yaw 41.06 -> facing 221.06); feetY=0.04 from " +
                     "tracks/merged/shots/reel-of-fortune/README.md [prop:player]",
        },
    };

    private static string PointNames()
    {
        var sb = new StringBuilder(48);
        for (int i = 0; i < Points.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Points[i].Name);
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    private static NamedPoint FindPoint(string name)
    {
        for (int i = 0; i < Points.Length; i++)
            if (string.Equals(Points[i].Name, name, StringComparison.OrdinalIgnoreCase)) return Points[i];
        return null;
    }

    // The scene name alone (`ActiveSceneName` adds handle/loaded state, which must not be compared).
    private static string ActiveSceneRawName()
    {
        try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
        catch { return "?"; }
    }

    // `alterum.test.points`: the named-point table, READ-ONLY. It prints each point's scene,
    // standing coordinate, yaw and intent (with the evidence behind the numbers), and the live
    // preconditions an apply would need - authority, local player, PlayerTeleportator under it -
    // without moving anything. That makes a refused `alterum.test.point` diagnosable from this line.
    private static void TestPoints(IHostContext host)
    {
        var local = FindLocalPlayer(host, out string via);
        var teleportator = FindTeleportatorUnder(local, out int scanned, out _);
        string scene = ActiveSceneRawName();
        host.LogInfo($"[qc:test] points at {Stamp()} scene='{ActiveSceneName()}' authority={NetRole.Word()} " +
                     $"local={(local == null ? "none" : via)} " +
                     $"teleport={(teleportator == null ? $"none (scanned={scanned})" : "PlayerTeleportator")} " +
                     $"count={Points.Length}");
        for (int i = 0; i < Points.Length; i++)
        {
            var p = Points[i];
            bool applies = string.Equals(scene, p.Scene, StringComparison.OrdinalIgnoreCase);
            host.LogInfo($"[qc:test] point '{p.Name}' scene='{p.Scene}' pos=({p.X:F3},{p.Y:F3},{p.Z:F3}) " +
                         $"yaw={p.Yaw:F2} sceneMatches={(applies ? "yes" : "no")} intent='{p.Intent}' " +
                         $"source='{p.Source}'");
        }
        host.LogInfo("[qc:test] points: apply one with alterum.test.point <name> - a gated setup mutation, " +
                     "server authority required, active scene must match the point; the move uses " +
                     "PlayerTeleportator.TeleportOnServer and never a transform write. A name whose " +
                     "coordinate is not grounded in this repo's evidence is deliberately absent from this table.");
    }

    // The authoritative mover under the local actor: the same PlayerTeleportator the camera/booth
    // harnesses call, and the only object an apply is allowed to use. Read-only here: one scene scan,
    // no creation and no write. `why` names the missing precondition for a refusal.
    private static Gameplay.PlayerSystem.PlayerTeleportator FindTeleportatorUnder(
        Gameplay.PlayerSystem.Player owner, out int scanned, out string why)
    {
        scanned = 0;
        why = "no local player";
        if (owner == null) return null;
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<Gameplay.PlayerSystem.PlayerTeleportator>());
            scanned = all.Length;
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i].TryCast<Gameplay.PlayerSystem.PlayerTeleportator>();
                if (t == null || t.transform == null) continue;
                if (!MirrorPlayerVisibility.Under(t.transform, owner.transform)) continue;
                why = null;
                return t;
            }
            why = scanned == 0
                ? "no PlayerTeleportator in the scene"
                : $"none of {scanned} PlayerTeleportator(s) is under the local player";
        }
        catch (Exception e) { why = $"PlayerTeleportator scan threw {e.GetType().Name}: {e.Message}"; }
        return null;
    }

    // The local actor's own root, for the before/after line of a setup move. Never written.
    private static string LocalRootPose(Gameplay.PlayerSystem.Player player)
    {
        try
        {
            var tr = player != null ? player.transform : null;
            if (tr == null) return "none";
            Vector3 p = tr.position, e = tr.eulerAngles;
            return $"({p.x:F3},{p.y:F3},{p.z:F3}) rot=({e.x:F2},{e.y:F2},{e.z:F2})";
        }
        catch { return "unreadable"; }
    }

    // `alterum.test.pose` (legacy alias `test.pose`): where the local player is and what the body
    // modules are doing, right now. Answers
    // "is the actor actually where the test thinks it is" without moving anything. Both readings are
    // live, never cached, but they answer different questions:
    //   effective - the pose of the VISIBLE body, through the same body-first read the reel placement
    //               and range use (ReelLogic.TryGetPose). The Netcode Player root sits at the origin in
    //               a map, so reporting the root as "the actor's position" is the classic false pass
    //               this verb exists to catch. `source=` names the object the numbers came from.
    //   root      - the Player object itself, for "is the actor root where the test put it".
    private static void TestPose(IHostContext host)
    {
        host.LogInfo($"[qc:test] pose at {Stamp()} scene='{ActiveSceneName()}'");
        var local = FindLocalPlayer(host, out string via);
        if (local == null)
        {
            host.LogWarning($"[qc:test] pose: no local player ({via})");
            return;
        }

        // The answer first: where the actor is actually drawn. One decision with placement/range, so
        // the pose a test reads cannot disagree with the pose the game acts on.
        try
        {
            if (ReelLogic.TryGetPose(local, out Vector3 ep, out Vector3 ef, out string poseSource))
                host.LogInfo($"[qc:test] pose effective source={poseSource} " +
                             $"pos=({ep.x:F3},{ep.y:F3},{ep.z:F3}) fwd=({ef.x:F3},{ef.y:F3},{ef.z:F3})");
            else host.LogWarning("[qc:test] pose effective: no pose (no body under the actor and no root)");
        }
        catch (Exception ex) { host.LogWarning($"[qc:test] pose: effective pose threw {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            var tr = local.transform;
            Vector3 p = tr.position, e = tr.eulerAngles;
            host.LogInfo($"[qc:test] pose root via={via} client={SafeOwner(local)} " +
                         $"path='{ClothInternals.PathOf(tr)}' " +
                         $"pos=({p.x:F3},{p.y:F3},{p.z:F3}) rot=({e.x:F2},{e.y:F2},{e.z:F2}) " +
                         $"lossyScale=({tr.lossyScale.x:F3},{tr.lossyScale.y:F3},{tr.lossyScale.z:F3}) " +
                         $"active={SafeActiveGo(local.gameObject)}");
        }
        catch (Exception ex) { host.LogWarning($"[qc:test] pose: transform read threw {ex.GetType().Name}: {ex.Message}"); }

        // Module state, from the modules' own names. Read-only: naming a state never starts a build.
        try { host.LogInfo($"[qc:test] pose body wardrobe=[{SahurWardrobe.MapBodyState()}]"); } catch (Exception ex) { host.LogWarning($"[qc:test] pose: wardrobe state threw {ex.Message}"); }
        try { host.LogInfo($"[qc:test] pose body rig=[{SahurRig.MapBodyState()}]"); } catch (Exception ex) { host.LogWarning($"[qc:test] pose: rig state threw {ex.Message}"); }
        try { host.LogInfo($"[qc:test] pose body rigid=[{SahurModel.MapBodyState()}]"); } catch (Exception ex) { host.LogWarning($"[qc:test] pose: rigid state threw {ex.Message}"); }

        // The mod's own built body for the local player, PER MODULE: the rigged and the rigid path both
        // build a ThirdViewPlayerModel under the actor, so a pose/follow test has to see whichever one is
        // on screen. Reporting only the rigged lease reads `built(rig)=none` over a live rigid body.
        try { PoseBuilt(host, "rig", SahurRig.LiveModel, SahurRig.BodyStateName, SahurRig.VisibleSpecSlug); }
        catch (Exception ex) { host.LogWarning($"[qc:test] pose: rig body read threw {ex.Message}"); }
        try { PoseBuilt(host, "rigid", SahurModel.LiveModel, SahurModel.BodyStateName, SahurModel.VisibleSpecSlug); }
        catch (Exception ex) { host.LogWarning($"[qc:test] pose: rigid body read threw {ex.Message}"); }
    }

    // One line per built body: the object a pose/follow test looks at, plus the module's own state word
    // for it (`state`/`slug` are the module's answer, never re-derived here). `none` is a fact as well:
    // a module that is ON but has built nothing is the pending case, not a body.
    private static void PoseBuilt(IHostContext host, string kind, Gameplay.PlayerSystem.ThirdViewPlayerModel body,
                                  string state, string slug)
    {
        try
        {
            if (body != null && body.transform != null)
            {
                Vector3 p = body.transform.position;
                host.LogInfo($"[qc:test] pose built({kind}) path='{ClothInternals.PathOf(body.transform)}' " +
                             $"pos=({p.x:F3},{p.y:F3},{p.z:F3}) active={SafeActiveGo(body.gameObject)} " +
                             $"state={state} slug='{slug}'");
                return;
            }
        }
        catch { }
        host.LogInfo($"[qc:test] pose built({kind})=none");
    }

    // `alterum.test.identity [clientId]` (legacy alias `test.identity`): the canonical identity answer
    // for the local player, or for one
    // remote actor by client id. Goes through ModelPresentation - the one resolver every display
    // surface uses - so the reported slug/db index/provenance/readiness are the same facts those
    // surfaces act on. No clientId means the local actor.
    private static void TestIdentity(IHostContext host, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            var id = ModelPresentation.ResolveLocalReadOnly(host);
            if (id == null) { host.LogWarning("[qc:test] identity local: resolver returned nothing"); return; }
            host.LogInfo($"[qc:test] identity local {ModelPresentation.DescribeIdentity(id)}");
            return;
        }

        if (!ulong.TryParse(arg, out ulong clientId))
        {
            // Not an id: report the actor's identity by its advertised/body slug match, and the ids
            // that exist, so a wrong argument is diagnosable rather than silently empty.
            host.LogWarning($"[qc:test] identity: '{arg}' is not a client id (usage: alterum.test.identity [clientId])");
            TestRemoteList(host);
            return;
        }

        // An id the ledger has NEVER seen must not get an identity. ResolveRemote answers from
        // RemoteModels' remote table, so an unknown id comes back as a plausible "native remote"
        // (empty advertised slug, readiness=native) - a fabricated actor, which is the one thing a
        // diagnostic may not produce. Known ids only, and this instance's own id is answered as the
        // local actor instead of as a remote one.
        if (!RemoteModels.HasRemote(clientId))
        {
            if (IsLocalClientId(host, clientId))
            {
                var self = ModelPresentation.ResolveLocalReadOnly(host);
                host.LogInfo($"[qc:test] identity client={clientId} is THIS instance (registered local client id); " +
                             $"local answer: {(self == null ? "unresolved" : ModelPresentation.DescribeIdentity(self))}");
                return;
            }
            host.LogWarning($"[qc:test] identity client={clientId}: no remote actor with that client id in the ledger " +
                            "(no accepted state from it in this session; see alterum.test.remote-list)");
            TestRemoteList(host);
            return;
        }

        var remote = ModelPresentation.ResolveRemoteReadOnly(host, clientId);
        if (remote == null) { host.LogWarning($"[qc:test] identity client={clientId}: resolver returned nothing"); return; }
        host.LogInfo($"[qc:test] identity client={clientId} {ModelPresentation.DescribeIdentity(remote)}");
        // The body this instance actually built is a separate fact from the advertised slug; say so
        // explicitly, because "advertised but not built" is the pending case the resolver marks.
        try
        {
            var root = RemoteModels.RemoteRoot(clientId);
            host.LogInfo($"[qc:test] identity client={clientId} root=" +
                         $"{(root == null ? "none" : $"'{ClothInternals.PathOf(root)}'")}");
        }
        catch (Exception ex) { host.LogWarning($"[qc:test] identity client={clientId}: root read threw {ex.Message}"); }
    }

    // `alterum.test.remote-list` (legacy alias `test.remote-list`): every actor this instance knows
    // about, from the two ledgers that own that
    // fact - RemoteModels' peer table (handshake verdict) and its remote-body table (what is on
    // screen). RemoteModels.Dump is the module's own read-only census, already written for exactly
    // this question; calling it here keeps one formatter instead of copying its fields.
    private static void TestRemoteList(IHostContext host)
    {
        var ids = new List<ulong>(8);
        try { RemoteModels.CollectRemoteIds(ids); } catch (Exception ex) { host.LogWarning($"[qc:test] remote-list: collect threw {ex.Message}"); }

        NetBridgeStatus st = default;
        bool haveNet = false;
        try
        {
            var net = host.Net;
            if (net != null) { st = net.Status; haveNet = true; }
        }
        catch (Exception ex) { host.LogWarning($"[qc:test] remote-list: net status threw {ex.Message}"); }

        string role = haveNet ? (st.IsServer ? "server" : (st.IsClient ? "client" : "none")) : "no-bridge";
        // `actors` is every client the remote ledger holds a state for; `bodies` is how many of those this
        // instance actually BUILT a body for. They are not the same number (a peer still binding, a
        // clone-blocked model, or a peer advertising its native row has an entry and no body), and the
        // header must not claim bodies that were never built.
        int built = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            try { if (!string.IsNullOrEmpty(RemoteModels.RemoteBodySlug(ids[i]))) built++; } catch { }
        }
        host.LogInfo($"[qc:test] remote-list at {Stamp()} scene='{ActiveSceneName()}' " +
                     $"role={role} local={(haveNet ? st.LocalClientId.ToString() : "?")} " +
                     $"server={(haveNet ? st.ServerClientId.ToString() : "?")} " +
                     $"peers={(haveNet ? st.PeerCount.ToString() : "?")} actors={ids.Count} bodies={built}");

        for (int i = 0; i < ids.Count; i++)
        {
            var id = ModelPresentation.ResolveRemoteReadOnly(host, ids[i]);
            if (id == null) continue;
            host.LogInfo($"[qc:test] remote-list body[{i}] {ModelPresentation.DescribeIdentity(id)}");
        }
        if (haveNet && st.State != NetState.Registered)
            host.LogWarning($"[qc:test] remote-list: bridge state={st.State} (peers/bodies are from the last live session)");

        // The module's own census: peer handshakes, hidden renderers, hats, emote, renderer health.
        try { RemoteModels.Dump(host); }
        catch (Exception ex) { host.LogWarning($"[qc:test] remote-list: dump threw {ex.GetType().Name}: {ex.Message}"); }
    }

    // `alterum.test.wardrobe-list` (legacy alias `test.wardrobe-list`): the wardrobe's own model
    // database, row by row, in the order the wardrobe
    // view builds its tabs from it (`PlayerModelDatabase.Models`). The classification comes from
    // SahurWardrobe, not from naming here:
    //   custom - the row is one of this mod's roster entries. `entry` is the roster index, `slug` the
    //            slug that survives a database rebuild; identity is the entry's own `PlayerModelData`
    //            pointer (SahurWardrobe.DataAt + SameData), the same comparison the pick path uses.
    //   native - a row no entry of this mod owns. The game's own ten rows land here (`Female_1_Emma`
    //            ...; `slug=''` follows the mod's convention where "" means native), and so would a
    //            row another generation left behind - the printed `label` is the row's own name, so a
    //            stray is visible by name instead of being silently taken for a game row.
    // A row that matches an entry by LABEL only is reported as custom with `adopted=pending-name`: the
    // post-hot-reload window in which the entry's row is already in the database but the pointer has not
    // been adopted yet. That is the same name match SahurWardrobe.EntryOwningRow adopts on - flagged
    // here, never adopted, because this verb is read-only.
    //
    // Nothing below registers, adopts, applies or selects: it reads the live database and the module's
    // own state fields, so it is also safe while the console is locked (see RefuseLocked).
    private static void TestWardrobeList(IHostContext host)
    {
        host.LogInfo($"[qc:test] wardrobe-list at {Stamp()} scene='{ActiveSceneName()}'");

        int rows, entries;
        try { rows = SahurWardrobe.DatabaseCount; entries = SahurWardrobe.EntryCount; }
        catch (Exception ex)
        {
            host.LogWarning($"[qc:test] wardrobe-list: wardrobe database read threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        string roster, view, wanted;
        try { roster = SahurWardrobe.RosterReady ? "ready" : "NOT-ready"; } catch { roster = "?"; }
        try { view = SahurWardrobe.ViewAttached ? $"attached tabs={SahurWardrobe.ViewTabCount}" : "none"; } catch { view = "?"; }
        try { wanted = SahurWardrobe.WantedState(); } catch { wanted = "?"; }
        host.LogInfo($"[qc:test] wardrobe-list rows={rows} entries={entries} roster={roster} " +
                     $"registers={SahurWardrobe.Registers} dbGeneration={SahurWardrobe.DatabaseGeneration} " +
                     $"view={view} wanted='{wanted}'");

        if (rows < 0)
        {
            host.LogWarning("[qc:test] wardrobe-list: PlayerModelDatabase.Models is not initialised in this scene " +
                            "(no wardrobe database yet); no rows to list");
            return;
        }

        int custom = 0, native = 0, empty = 0;
        for (int i = 0; i < rows; i++)
        {
            try
            {
                var row = SahurWardrobe.DatabaseRowAt(i);
                if (row == null)
                {
                    empty++;
                    host.LogInfo($"[qc:test] wardrobe-list row index={i} kind=empty slug='' label=''");
                    continue;
                }

                string rowName = "";
                try { rowName = row.name ?? ""; } catch { }

                int owner = -1;
                bool byName = false;
                for (int e = 0; e < entries; e++)
                {
                    var data = SahurWardrobe.DataAt(e);
                    if (data != null && SahurWardrobe.SameData(data, row)) { owner = e; break; }
                }
                if (owner < 0 && rowName.Length > 0)
                    for (int e = 0; e < entries; e++)
                        if (string.Equals(SahurWardrobe.EntryLabel(e), rowName, StringComparison.OrdinalIgnoreCase))
                        {
                            owner = e;
                            byName = true;
                            break;
                        }

                if (owner >= 0)
                {
                    custom++;
                    var spec = SahurWardrobe.SpecAt(owner);
                    host.LogInfo($"[qc:test] wardrobe-list row index={i} kind=custom entry={owner} " +
                                 $"slug='{(spec == null || spec.Slug == null ? "" : spec.Slug)}' " +
                                 $"label='{SahurWardrobe.EntryLabel(owner)}' row='{rowName}'" +
                                 $"{(byName ? " adopted=pending-name" : "")}");
                    continue;
                }

                native++;
                host.LogInfo($"[qc:test] wardrobe-list row index={i} kind=native slug='' label='{rowName}' row='{rowName}'");
            }
            catch (Exception ex)
            {
                host.LogWarning($"[qc:test] wardrobe-list row index={i}: read threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Entries whose row is not in the database yet (first lobby load, or right after a hot reload).
        // Reported separately so "12 rows, 4 custom" cannot be mistaken for a complete roster.
        var pending = new List<string>(entries);
        for (int e = 0; e < entries; e++)
        {
            try
            {
                if (SahurWardrobe.DataAt(e) == null || SahurWardrobe.EntryDatabaseIndex(e) < 0)
                    pending.Add(SahurWardrobe.EntryLabel(e));
            }
            catch { }
        }
        host.LogInfo($"[qc:test] wardrobe-list rows={rows} custom={custom} native={native} empty={empty} " +
                     $"entriesPending={pending.Count}{(pending.Count == 0 ? "" : " [" + string.Join(", ", pending.ToArray()) + "]")}");
    }

    // ------------------------------------------------------------------ model observation
    //
    // `alterum.test.assert-model <clientId|local> <slug>` and
    // `alterum.test.await-model <clientId|local> <slug>`: the read-only half of the layer that answers
    // "is this actor actually wearing <slug>". Both compare the SAME field set, built from the identity
    // resolver's own `ModelIdentity`, so an assert after an await cannot answer differently from the
    // wait that just passed:
    //
    //   wanted      the local actor's persisted record (must name the slug, or carry no custom claim)
    //   advertised  a remote actor's vouched peer slug (the only model fact that crosses the wire)
    //   canonical   the row the resolver calls this actor's model
    //   applied     the game's own applied row (for the LOCAL actor this is the wardrobe list's row and
    //               must be the canonical row; for a remote it is the peer's native home - evidence,
    //               not a check, because this instance does not move it)
    //   body        the EFFECTIVE body slug (`ModelWantedPolicy.EffectiveBody` decides it, so a pending
    //               or failed build reports no slug instead of a false pass)
    //   bodyState   live for a custom row, native for the game's own body
    //   readiness   ready when the canonical row is registered locally, native for a native row
    //   hud         the row `ModelPresentation.HudRow` presents in the portrait slot (a CHECK for the
    //               local actor, evidence for a remote whose portrait the slot does not show)
    //   wardrobe    the row the wardrobe list shows for this actor (`ModelPresentation.WardrobeRow`:
    //               the applied row locally - covered by `applied` - and the occupancy tab remotely)
    //
    // Nothing here writes: no row is registered or adopted, no selection is applied, no index or RPC is
    // sent, no UI object is touched. The reads go through ModelPresentation/RemoteModels (the modules
    // that own the facts) and the display rows through the resolver's own derivation, never a copy.
    // That is why these verbs, like the four probes above, do not call RefuseLocked: reporting is not
    // a way into the console (AEZAKMI).
    //
    // A client id this instance holds no accepted state for is NEVER resolved: `ResolveRemote` would
    // answer a plausible "native remote", which is a fabricated actor. `assert` fails with
    // reason=unknown-remote and the ids that do exist; `await` keeps observing (the id may register on
    // a later frame) and names the absence in every observation.
    //
    // await is the same predicate held over FRAMES. The verb parses, takes the first observation and
    // returns - it never blocks. The registered wait is re-read by AwaitTick on the game's own frame
    // loop at a fixed frame cadence (AwaitCheckFrames), and ends on the first of: the predicate holding
    // (result=PASS), the wall-clock deadline (AwaitTimeoutSeconds, result=TIMEOUT reason=time), or the
    // check ceiling (AwaitMaxChecks, reason=checks). No Thread.Sleep and no fixed sleep anywhere: every
    // conclusion is a fresh read at a frame boundary, and a timeout reports the last observed
    // identity and remote state instead of a bare false.
    //
    // Usage consequence: because await returns at once, an assert written on the NEXT line of the same
    // request file runs before the wait can pass. A test that wants both sends the await, waits for its
    // `result=PASS` line in the session log, and then sends the assert (which re-reads the same fields)
    // - or simply uses the await's own verdict. One wait per (actor, expected), at most AwaitMaxPending
    // at a time; a duplicate line is idempotent and reports the existing wait.

    private const float AwaitTimeoutSeconds = 30f;  // the explicit time condition of one wait
    private const int AwaitCheckFrames = 4;         // evaluate every 4th frame - never twice in one frame
    private const int AwaitMaxChecks = 4000;        // frame-side ceiling: a stalled/tiny-dt loop still ends
    private const int AwaitMaxPending = 4;          // bounded: one line per refused wait, never growth
    private const string NativeRow = "<native>";    // how an expected/observed native row is printed

    // What a verb was asked to observe: `local` or one client id, and the expected row. Parsed once,
    // so assert and await cannot disagree about the same argument line.
    private sealed class ModelTarget
    {
        internal string ActorText = "";   // "local", or the id exactly as typed
        internal string TypedSlug = "";    // the slug exactly as typed (kept for error reports)
        internal bool LocalLiteral;
        internal ulong ClientId;
        internal bool Native;             // the expected row is the game's own
        internal string Expected = "";    // the roster's own spelling of the slug ("" = native)
        internal string ErrorCode;        // usage | bad-actor | unknown-slug
        internal string Error;            // the line to report when ErrorCode is set
    }

    private sealed class ModelField
    {
        internal string Name, Expected, Actual, Note;
        internal bool Required, Ok;
    }

    // One read of an actor's authoritative identity plus the field checks derived from it. `Pass` is
    // the shared predicate of assert and await: every REQUIRED field agrees and the identity resolved.
    private sealed class ModelObservation
    {
        internal ModelTarget Target;
        internal string Actor = "?";      // local | local(client=7) | remote=7
        internal bool Local, Known;
        internal string Why;              // why there is no identity (reported, never guessed)
        internal ModelPresentation.ModelIdentity Identity;
        internal readonly List<ModelField> Fields = new(9);
        internal int Required, Failed;
        internal bool Pass => Known && Identity != null && Required > 0 && Failed == 0;
        internal string Signature
            => (Identity == null ? "none:" + (Why ?? "") : Identity.Signature) + "|" + Target.Expected;
    }

    // One registered wait: the target plus the scheduling that ends it. Frame counters and the
    // deadline are the whole schedule; the state dies with this assembly (hot reload), never leaked.
    private sealed class PendingAwait
    {
        internal ModelTarget Target;
        internal float StartedAt;
        internal int StartedFrame;
        internal float DeadlineAt;
        internal int NextFrame;
        internal int Checks;
        internal string LastSignature;
    }

    private static readonly List<PendingAwait> _awaits = new(AwaitMaxPending);

    // `<clientId|local> <slug>`: the actor word is `local` or one decimal client id; the slug is a
    // roster slug or the literal `native`/`-` for the game's own row. Anything else is an explicit
    // argument failure - a slug nothing can ever match would only turn into an unexplained timeout.
    private static ModelTarget ParseModelTarget(string arg, string verb)
    {
        var t = new ModelTarget();
        string text = (arg ?? "").Trim();
        int sp = text.IndexOf(' ');
        string actor = sp < 0 ? text : text.Substring(0, sp).Trim();
        string slug = sp < 0 ? "" : text.Substring(sp + 1).Trim();
        t.TypedSlug = slug;

        if (actor.Length == 0 || slug.Length == 0)
        {
            t.ErrorCode = "usage";
            t.Error = $"usage alterum.test.{verb}-model <clientId|local> <slug>";
            return t;
        }

        t.ActorText = actor;
        if (string.Equals(actor, "local", StringComparison.OrdinalIgnoreCase)) t.LocalLiteral = true;
        else if (!ulong.TryParse(actor, out t.ClientId))
        {
            t.ErrorCode = "bad-actor";
            t.Error = $"actor {actor} is neither the word local nor a client id";
            return t;
        }

        if (slug == "-" || string.Equals(slug, "native", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(slug, "<native>", StringComparison.OrdinalIgnoreCase))
        {
            t.Native = true;
            t.Expected = "";
            return t;
        }

        var spec = ModelRoster.BySlug(slug);
        if (spec == null || string.IsNullOrEmpty(spec.Slug))
        {
            t.ErrorCode = "unknown-slug";
            t.Error = $"slug {slug} is not in this roster; expected one of " +
                      $"[{string.Join(", ", RosterSlugs())}] or native";
            return t;
        }
        t.Expected = spec.Slug;
        return t;
    }

    // The roster's own slugs, so an unknown-slug failure names what would have matched (bounded: the
    // roster is one line of data, and this runs only on a failed argument).
    private static string[] RosterSlugs()
    {
        var all = ModelRoster.All;
        var slugs = new List<string>(all == null ? 0 : all.Length);
        for (int i = 0; all != null && i < all.Length; i++)
            if (all[i] != null && !string.IsNullOrEmpty(all[i].Slug)) slugs.Add(all[i].Slug);
        return slugs.ToArray();
    }

    private static string ShowExpected(ModelTarget t)
        => t.Native ? NativeRow : (string.IsNullOrEmpty(t.Expected) ? t.TypedSlug : t.Expected);

    // `""` is the native slug in the identity fields, `-` the "none" spelling both of them use;
    // both mean "not one of ours" for a check that expects the game's row.
    private static bool IsNativeRow(string s)
        => string.IsNullOrEmpty(s) || s == "-" || s == NativeRow;

    private static string RowText(string s) => string.IsNullOrEmpty(s) ? NativeRow : s;

    // Free text printed inside `key='...'` must not carry a quote of its own (the log's fields are
    // read by their key= boundaries). Slugs never need this, but labels, user-typed actor words and
    // exception messages do.
    private static string Q(string s) => s == null ? "" : s.Replace("'", "");

    private static bool SameRow(string a, string b)
        => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    // One observation of the target. The local/own-id/unknown-remote/known-remote split is the only
    // place the resolver is called, so an unknown id can never produce a fabricated identity.
    private static ModelObservation ObserveModel(IHostContext host, ModelTarget t)
    {
        var o = new ModelObservation { Target = t };
        if (t.LocalLiteral)
        {
            o.Local = true;
            o.Known = true;
            o.Actor = "local";
            o.Identity = ModelPresentation.ResolveLocalReadOnly(host);
        }
        else if (IsLocalClientId(host, t.ClientId))
        {
            // This instance's own id: answer as the local actor, never as a remote one. Same rule
            // `alterum.test.identity` follows (an unregistered bridge cannot claim that answer).
            o.Local = true;
            o.Known = true;
            o.Actor = $"local(client={t.ClientId})";
            o.Identity = ModelPresentation.ResolveLocalReadOnly(host);
        }
        else if (!RemoteModels.HasRemote(t.ClientId))
        {
            o.Known = false;
            o.Actor = $"remote={t.ClientId}";
            o.Why = "no accepted state from that client id in this session";
        }
        else
        {
            o.Known = true;
            o.Actor = $"remote={t.ClientId}";
            o.Identity = ModelPresentation.ResolveRemoteReadOnly(host, t.ClientId);
        }

        if (o.Identity == null && o.Why == null) o.Why = "the identity resolver returned nothing";
        if (o.Identity != null) BuildModelFields(o, o.Identity);
        return o;
    }

    private static void AddModelField(ModelObservation o, string name, string expected, string actual,
                                      bool ok, bool required, string note)
    {
        o.Fields.Add(new ModelField
        {
            Name = name, Expected = expected, Actual = actual,
            Ok = ok, Required = required, Note = note,
        });
        if (!required) return;
        o.Required++;
        if (!ok) o.Failed++;
    }

    // The field set, from the identity's own words. `expected` is the row we were asked for; every
    // comparison is against an authoritative field, never against a value re-derived here.
    private static void BuildModelFields(ModelObservation o, ModelPresentation.ModelIdentity id)
    {
        var t = o.Target;
        string want = t.Native ? NativeRow : t.Expected;

        if (o.Local)
        {
            // Compatibility "wanted" field now describes the game's applied row, not saved intent.
            string w = id.WantedSlug ?? "-";
            AddModelField(o, "wanted", want, RowText(w),
                          t.Native ? IsNativeRow(w) : SameRow(w, t.Expected), true, null);
        }
        else
        {
            string adv = id.AdvertisedSlug ?? "";
            AddModelField(o, "advertised", want, RowText(adv),
                          t.Native ? string.IsNullOrEmpty(adv) : SameRow(adv, t.Expected), true,
                          "the vouched peer slug");
        }

        AddModelField(o, "canonical", want, RowText(id.CanonicalSlug),
                      t.Native ? string.IsNullOrEmpty(id.CanonicalSlug) : SameRow(id.CanonicalSlug, t.Expected),
                      true, $"label={id.CanonicalLabel} db={(id.CanonicalDb >= 0 ? id.CanonicalDb.ToString() : "-")}");

        // The game's own row. Required for the local actor (it is the wardrobe list's row and must be
        // the canonical row once the module drove the game's selection); evidence for a remote, whose
        // applied row this instance never moves.
        AddModelField(o, "applied", want, RowText(id.AppliedSlug),
                      t.Native ? string.IsNullOrEmpty(id.AppliedSlug) : SameRow(id.AppliedSlug, t.Expected),
                      o.Local, $"row={id.AppliedRow}");

        AddModelField(o, "body", want, RowText(id.BodySlug),
                      t.Native ? string.IsNullOrEmpty(id.BodySlug) : SameRow(id.BodySlug, t.Expected),
                      true, "the effective body");

        string state = string.IsNullOrEmpty(id.BodyState) ? "native" : id.BodyState;
        string wantState = t.Native ? "native" : "live";
        AddModelField(o, "bodyState", wantState, state, SameRow(state, wantState), true, null);

        string readiness = id.Readiness ?? "unknown";
        AddModelField(o, "readiness", t.Native ? "native" : "ready", readiness,
                      SameRow(readiness, t.Native ? "native" : "ready"), true, null);

        // Read the actual visible HUD sprite. Missing surfaces and unknown sprites fail closed;
        // canonical identity alone cannot prove what a user sees. Remote actors have no HUD here.
        string hud = ModelPresentation.HudRow(id);
        AddModelField(o, "hud", want, RowText(hud),
                      t.Native ? id.CanonicalData != null && SameRow(hud, id.CanonicalLabel)
                               : SameRow(hud, t.Expected), o.Local,
                      o.Local ? "observed visible portrait sprite" : "not observable (no remote HUD slot)");

        if (o.Local)
        {
            // The wardrobe list's local row is the game's applied row; the slug check above already
            // proves it is the canonical row. The NAME is display evidence only (a rename window is
            // not a wrong model), so it is never counted in the verdict.
            if (t.Native)
                AddModelField(o, "wardrobe", "<game row>", RowText(id.AppliedRow),
                              !IsNativeRow(id.AppliedRow), false,
                              "row the wardrobe list shows for this actor");
            else
                AddModelField(o, "wardrobe", RowText(id.CanonicalLabel), RowText(id.AppliedRow),
                              SameRow(id.AppliedRow, id.CanonicalLabel), false,
                              "row the wardrobe list shows for this actor");
        }
        else
        {
            string wrow = ModelPresentation.WardrobeRow(id);
            AddModelField(o, "wardrobe", want, RowText(wrow),
                          t.Native ? IsNativeRow(wrow) : SameRow(wrow, t.Expected), true,
                          "tab the occupancy correction moves this entry to");
        }
    }

    private static void EmitModelField(IHostContext host, string tag, ModelField f)
    {
        string line = $"[qc:test] {tag} field {f.Name} ok={(f.Ok ? 1 : 0)} required={(f.Required ? 1 : 0)} " +
                      $"expected='{Q(f.Expected)}' actual='{Q(f.Actual)}'" +
                      (string.IsNullOrEmpty(f.Note) ? "" : $" note='{Q(f.Note)}'");
        if (f.Required && !f.Ok) host.LogWarning(line);
        else host.LogInfo(line);
    }

    private static string FailedFieldNames(ModelObservation o)
    {
        var names = new List<string>(o.Failed);
        for (int i = 0; i < o.Fields.Count; i++)
            if (o.Fields[i].Required && !o.Fields[i].Ok) names.Add(o.Fields[i].Name);
        return names.Count == 0 ? "" : string.Join(",", names.ToArray());
    }

    private static string KnownRemoteIds()
    {
        var ids = new List<ulong>(8);
        try { RemoteModels.CollectRemoteIds(ids); } catch { }
        ids.Sort();
        var text = new List<string>(ids.Count);
        for (int i = 0; i < ids.Count; i++) text.Add(ids[i].ToString());
        return string.Join(",", text.ToArray());
    }

    // The raw remote ledger facts, for a timeout that must say what the last observed state was. Never
    // calls the resolver for an unknown id (that is the fabricated-remote trap).
    private static string RemoteStateText(ulong clientId)
    {
        if (!RemoteModels.HasRemote(clientId)) return "known=0";
        string adv = "", body = "", root = "none";
        try { adv = RemoteModels.RemoteSlug(clientId) ?? ""; } catch { }
        try { body = RemoteModels.RemoteBodySlug(clientId) ?? ""; } catch { }
        try
        {
            var tr = RemoteModels.RemoteRoot(clientId);
            if (tr != null) root = "'" + Q(ClothInternals.PathOf(tr)) + "'";
        }
        catch { }
        return $"known=1 advertised='{Q(adv)}' bodySlug='{Q(body)}' root={root}";
    }

    // `alterum.test.assert-model <clientId|local> <slug>`: one observation, a structured verdict. PASS
    // means every REQUIRED field agrees and the identity resolved. `required=0` fields are evidence
    // only. The verdict is a read of ModelPresentation/RemoteModels; nothing is written.
    private static void TestAssertModel(IHostContext host, string arg)
    {
        var t = ParseModelTarget(arg, "assert");
        if (t.ErrorCode != null) { ReportModelTargetError(host, "assert-model", t); return; }

        host.LogInfo($"[qc:test] assert-model at {Stamp()} scene='{ActiveSceneName()}' " +
                     $"actor='{Q(t.ActorText)}' expected='{Q(ShowExpected(t))}' " +
                     $"kind={(t.Native ? "native" : "custom")}");
        ModelObservation o;
        try { o = ObserveModel(host, t); }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] assert-model result=FAIL actor='{Q(t.ActorText)}' " +
                            $"expected='{Q(ShowExpected(t))}' reason=exception note='{Q(Describe(e))}'");
            return;
        }

        if (!o.Known)
        {
            host.LogWarning($"[qc:test] assert-model result=FAIL actor='{Q(o.Actor)}' expected='{Q(ShowExpected(t))}' " +
                            $"reason=unknown-remote note='{Q(o.Why)}' known=[{KnownRemoteIds()}]");
            return;
        }
        if (o.Identity == null)
        {
            host.LogWarning($"[qc:test] assert-model result=FAIL actor='{Q(o.Actor)}' expected='{Q(ShowExpected(t))}' " +
                            $"reason=unresolved note='{Q(o.Why)}'");
            return;
        }

        host.LogInfo($"[qc:test] assert-model identity {ModelPresentation.DescribeIdentity(o.Identity)}");
        for (int i = 0; i < o.Fields.Count; i++) EmitModelField(host, "assert-model", o.Fields[i]);
        try { host.LogInfo($"[qc:test] assert-model presentation {ModelPresentation.Summary}"); } catch { }
        if (o.Pass)
            host.LogInfo($"[qc:test] assert-model result=PASS actor='{Q(o.Actor)}' expected='{Q(ShowExpected(t))}' " +
                         $"failed=0/{o.Required}");
        else
            host.LogWarning($"[qc:test] assert-model result=FAIL actor='{Q(o.Actor)}' expected='{Q(ShowExpected(t))}' " +
                            $"failed={o.Failed}/{o.Required} failedFields=[{FailedFieldNames(o)}]");
    }

    private static void ReportModelTargetError(IHostContext host, string tag, ModelTarget t)
    {
        host.LogWarning($"[qc:test] {tag} result=FAIL reason={t.ErrorCode} actor='{Q(t.ActorText)}' " +
                        $"expected='{Q(ShowExpected(t))}' note='{Q(t.Error)}'");
    }

    // `alterum.test.await-model <clientId|local> <slug>`: registers the wait and returns at once. The
    // first observation is taken here, so an already-satisfied predicate reports PASS with no wait at
    // all; otherwise AwaitTick owns it. One wait per (actor, expected): a repeated line is idempotent.
    private static void TestAwaitModel(IHostContext host, string arg)
    {
        var t = ParseModelTarget(arg, "await");
        if (t.ErrorCode != null) { ReportModelTargetError(host, "await-model", t); return; }

        float now;
        int frame;
        try { now = Time.unscaledTime; frame = Time.frameCount; }
        catch { host.LogWarning("[qc:test] await-model: no frame clock in this scene; nothing was registered"); return; }

        for (int i = 0; i < _awaits.Count; i++)
        {
            var p = _awaits[i];
            if (p.Target.LocalLiteral == t.LocalLiteral && p.Target.ClientId == t.ClientId &&
                p.Target.Native == t.Native && SameRow(p.Target.Expected, t.Expected))
            {
                host.LogInfo($"[qc:test] await-model already pending actor='{Q(p.Target.ActorText)}' " +
                             $"expected='{Q(ShowExpected(t))}' started=f{p.StartedFrame} " +
                             $"deadline={p.DeadlineAt:0.000}s checks={p.Checks}/{AwaitMaxChecks}");
                return;
            }
        }
        if (_awaits.Count >= AwaitMaxPending)
        {
            host.LogWarning($"[qc:test] await-model refused actor='{Q(t.ActorText)}' expected='{Q(ShowExpected(t))}': " +
                            $"already {_awaits.Count} waits pending (max {AwaitMaxPending}); nothing was registered");
            return;
        }

        host.LogInfo($"[qc:test] await-model at {Stamp()} scene='{ActiveSceneName()}' " +
                     $"actor='{Q(t.ActorText)}' expected='{Q(ShowExpected(t))}' " +
                     $"kind={(t.Native ? "native" : "custom")} " +
                     $"timeout={AwaitTimeoutSeconds:0.0}s maxChecks={AwaitMaxChecks} every={AwaitCheckFrames}f");

        ModelObservation o;
        try { o = ObserveModel(host, t); }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] await-model result=FAIL actor='{Q(t.ActorText)}' " +
                            $"expected='{Q(ShowExpected(t))}' reason=exception note='{Q(Describe(e))}'; " +
                            "nothing was registered");
            return;
        }
        if (o.Pass)
        {
            EmitAwaitPass(host, t, o, frame, 0f, 1);
            return;
        }
        var w = new PendingAwait
        {
            Target = t,
            StartedAt = now,
            StartedFrame = frame,
            DeadlineAt = now + AwaitTimeoutSeconds,
            NextFrame = frame + AwaitCheckFrames,
            Checks = 1,
            LastSignature = o.Signature,
        };
        _awaits.Add(w);
        EmitAwaitObserved(host, w, o, 0f, frame, true);
        host.LogInfo($"[qc:test] await-model registered actor='{Q(w.Target.ActorText)}' " +
                     $"expected='{Q(ShowExpected(t))}' pending={_awaits.Count}; the frame tick reports " +
                     "PASS or TIMEOUT (never blocks the caller)");
    }

    // The frame half: re-read every pending wait at its frame cadence and end it on the predicate, the
    // wall-clock deadline or the check ceiling. `now` is Tick's single per-frame `Time.unscaledTime`.
    private static void AwaitTick(IHostContext host, float now)
    {
        if (_awaits.Count == 0) return;
        int frame;
        try { frame = Time.frameCount; } catch { return; }

        for (int i = _awaits.Count - 1; i >= 0; i--)
        {
            var w = _awaits[i];
            if (frame < w.NextFrame) continue;             // the explicit frame condition
            w.NextFrame = frame + AwaitCheckFrames;
            w.Checks++;

            ModelObservation o;
            try { o = ObserveModel(host, w.Target); }
            catch (Exception e)
            {
                // One bad read ends its own wait with the reason instead of throwing into the frame
                // loop (or retrying the same throw every 4th frame).
                _awaits.RemoveAt(i);
                EmitAwaitFailure(host, w, $"exception note='{Q(Describe(e))}'");
                continue;
            }
            float elapsed = Math.Max(0f, now - w.StartedAt);
            string sig = o.Signature;
            bool changed = sig != w.LastSignature;
            if (changed) w.LastSignature = sig;

            if (o.Pass) { _awaits.RemoveAt(i); EmitAwaitPass(host, w.Target, o, frame, elapsed, w.Checks); continue; }

            bool timeUp = elapsed >= AwaitTimeoutSeconds;
            bool checksUp = w.Checks >= AwaitMaxChecks;
            if (timeUp || checksUp)
            {
                _awaits.RemoveAt(i);
                EmitAwaitTimeout(host, w, o, frame, elapsed, timeUp ? "time" : "checks");
                continue;
            }

            if (changed) EmitAwaitObserved(host, w, o, elapsed, frame, false);
        }
    }

    // Await lines are logged and mirrored to the console panel when one is OPEN (the native row's
    // result arrives after its call returned, so it needs the same surface PrintToConsole serves; a
    // closed console is not written to - the log is the record, and PrintToConsole's "log only"
    // notice would only add noise).
    private static void EmitAwait(IHostContext host, Action<IHostContext> build)
    {
        var sink = new ConsoleSink(host);
        try { build(sink); }
        catch (Exception e) { host.LogWarning($"[qc:test] await-model: emit threw {e.GetType().Name}: {e.Message}"); }
        try
        {
            // A native row prints everything it collected when the handler returns, so mirroring here
            // would print the same line twice in the panel. Only the frame-tick path owns its print.
            if (host is ConsoleSink) return;
            var c = LiveInstance();
            if (c != null && IsActive(c)) PrintToConsole(host, sink.Lines);
        }
        catch { }
    }

    private static void EmitAwaitPass(IHostContext host, ModelTarget t, ModelObservation o,
                                      int frame, float elapsed, int checks)
    {
        EmitAwait(host, sink =>
        {
            sink.LogInfo($"[qc:test] await-model result=PASS actor='{Q(o.Actor)}' expected='{Q(ShowExpected(t))}' " +
                         $"f={frame} elapsed={elapsed:0.000}s checks={checks}");
            if (o.Identity != null)
                sink.LogInfo($"[qc:test] await-model final identity {ModelPresentation.DescribeIdentity(o.Identity)}");
        });
    }

    private static void EmitAwaitObserved(IHostContext host, PendingAwait w, ModelObservation o,
                                          float elapsed, int frame, bool first)
    {
        EmitAwait(host, sink =>
        {
            string pending = o.Identity != null ? $"pending=[{FailedFieldNames(o)}]" : $"known={(o.Known ? 1 : 0)}";
            sink.LogInfo($"[qc:test] await-model observed {(first ? "first " : "")}f={frame} " +
                         $"elapsed={elapsed:0.000}s actor='{Q(o.Actor)}' expected='{Q(ShowExpected(w.Target))}' " +
                         $"{pending} checks={w.Checks}" +
                         (o.Identity != null ? "" : $" note='{Q(o.Why)}'"));
            if (o.Identity != null)
                sink.LogInfo($"[qc:test] await-model observed identity {ModelPresentation.DescribeIdentity(o.Identity)}");
        });
    }

    // The timeout report: WHY it ended, the last observed identity, the fields that never agreed and -
    // for a remote - the raw ledger state. Everything after the result line is the last observation;
    // nothing is re-resolved into a guess.
    private static void EmitAwaitTimeout(IHostContext host, PendingAwait w, ModelObservation o,
                                         int frame, float elapsed, string reason)
    {
        EmitAwait(host, sink =>
        {
            sink.LogWarning($"[qc:test] await-model result=TIMEOUT actor='{Q(o.Actor)}' " +
                            $"expected='{Q(ShowExpected(w.Target))}' f={frame} elapsed={elapsed:0.000}s " +
                            $"checks={w.Checks} reason={reason} pending=[{FailedFieldNames(o)}]");
            sink.LogWarning(o.Identity != null
                ? $"[qc:test] await-model last identity {ModelPresentation.DescribeIdentity(o.Identity)}"
                : $"[qc:test] await-model last identity none ({Q(o.Why ?? "not observed")})");
            for (int i = 0; i < o.Fields.Count; i++)
                if (o.Fields[i].Required && !o.Fields[i].Ok) EmitModelField(sink, "await-model last", o.Fields[i]);
            if (!o.Known) return;
            if (w.Target.LocalLiteral || IsLocalClientId(host, w.Target.ClientId))
                sink.LogWarning($"[qc:test] await-model last local scene='{ActiveSceneName()}' " +
                                $"wardrobe=[{SafeBodyState(() => SahurWardrobe.MapBodyState())}] " +
                                $"rig=[{SafeBodyState(() => SahurRig.MapBodyState())}] " +
                                $"rigid=[{SafeBodyState(() => SahurModel.MapBodyState())}]");
            else
                sink.LogWarning($"[qc:test] await-model last remote {RemoteStateText(w.Target.ClientId)}");
        });
    }

    private static string SafeBodyState(Func<string> f)
    {
        try { return f() ?? "?"; } catch { return "?"; }
    }

    // An observation that threw: the wait ends with the reason, so a broken read cannot look like a
    // model that never converged (and cannot retry the same throw on a frame cadence).
    private static void EmitAwaitFailure(IHostContext host, PendingAwait w, string detail)
    {
        EmitAwait(host, sink => sink.LogWarning(
            $"[qc:test] await-model result=FAIL actor='{Q(w.Target.ActorText)}' " +
            $"expected='{Q(ShowExpected(w.Target))}' checks={w.Checks} {detail}"));
    }

    // ------------------------------------------------------------------ asset diagnostics
    //
    // `alterum.test.assets` and `alterum.test.assert-assets`: the read-only view of the shared model
    // resource caches (`ModelAssetCache<T>` in SahurRig and SahurModel). An entry holds the parsed
    // geometry/materials/textures and is leased by every body that draws it - the local body, one
    // lease per remote body built from the same slug, and wardrobe/preview bodies. A cross-player
    // lifetime bug is therefore a lease/eviction fact and not a rendering one: an asset destroyed
    // while a lease still pins it (a body drawing destroyed materials), one value owned under two keys
    // (destroyed twice while two bodies draw it), or a retired cache that dropped an entry a late lease
    // still holds. `assets` prints that picture; `assert-assets` turns it into explicit fields.
    //
    // What is read: the snapshot comes from the cache's own bookkeeping (copied, see ModelAssetCache.cs)
    // and each value is inspected only for liveness - a destroyed Unity object is not a null reference,
    // only Unity's ==/!= says so, so every Unity reference reachable from an entry is compared against
    // null, and every Material is asked the two questions RemoteModels.Dump already asks (supported
    // shader, base map present). Nothing is acquired, released, loaded, selected, destroyed or trimmed
    // by either verb, which is also why they stay available while the console is locked (AEZAKMI, see
    // RefuseLocked).
    //
    // Ownership is attributed where the read-only layer can see it: the cache instance (its owner
    // module) and each entry's own user count. WHICH remote or preview lease owns an entry is not
    // observable - those tokens live in RemoteModels/SahurWardrobe/RigLease private state - so leases
    // beyond the live bodies this generation can name are reported as `unattributed`, never failed,
    // and a live body (`body-*`) is the checkable half: it must be backed by a leased, alive entry,
    // because that lease is what keeps the asset from being evicted under it. History is the same
    // shape: eviction is read as a consistent state (an entry in the table is alive; an over-limit
    // table has nothing idle to evict), never as a log of past Trims.

    // A cache value's Unity references, one level deep: the entry values are plain managed bags of
    // Mesh/Texture2D/Material fields, so this is exactly the resource set a lease pins.
    private sealed class AssetProbe
    {
        internal int UnityRefs;         // references found
        internal int Dead;              // of those, destroyed (Unity's == says so)
        internal int Materials;         // Material slots among them
        internal int MaterialsHealthy;  // supported shader + base map present
        internal int ReadErrors;        // references a read could not classify
        internal readonly List<string> DeadNames = new(4);
        internal readonly List<string> BadMaterials = new(4);
        // An unreadable reference is not evidence of health, so a read error fails liveness too.
        internal bool Alive => Dead == 0 && ReadErrors == 0;
    }

    // One cache of this generation plus the probe of every entry, index-aligned with the snapshot: the
    // printer and the assert read the same probes, so neither can see a different asset than the other.
    private sealed class AssetCacheView
    {
        internal ModelAssetCacheSnapshot Snapshot;
        internal readonly List<AssetProbe> Probes = new(4);
    }

    // A body this generation can name, and the cache entry that should be backing it.
    private sealed class AssetBody
    {
        internal string Name = "";   // body-local-rig | body-local-rigid | body-remote-<id>
        internal string State = "?";  // the module's own word (live/native/pending/failed/built/no-body)
        internal string Slug = "";
        internal bool Live;           // a body object is on screen: it owns a lease
        internal bool Found;          // an entry with this slug exists in the cache that should hold it
        internal string Owner = "";   // which cache held it
        internal int Users = -1;      // that entry's user count (-1 = not found)
        internal bool Alive;          // that entry's value is alive
    }

    private static AssetProbe ProbeAssetValue(object value)
    {
        var probe = new AssetProbe();
        if (value == null) { probe.Dead = 1; probe.DeadNames.Add("value"); return probe; }
        FieldInfo[] fields;
        try { fields = value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance); }
        catch (Exception) { probe.ReadErrors++; return probe; }
        for (int i = 0; i < fields.Length; i++)
        {
            object slot;
            try { slot = fields[i].GetValue(value); }
            catch (Exception) { probe.ReadErrors++; continue; }
            ProbeSlot(probe, fields[i].Name, slot);
        }
        return probe;
    }

    // One field, or one element of a field's list/array (Textures, Materials). Only Unity objects and
    // their collections are followed; numbers, strings and vectors are left alone.
    private static void ProbeSlot(AssetProbe probe, string name, object slot)
    {
        if (slot == null) return;
        if (slot is UnityEngine.Object uo)
        {
            probe.UnityRefs++;
            bool dead;
            try { dead = uo == null; }
            catch (Exception) { probe.ReadErrors++; return; }
            if (dead)
            {
                probe.Dead++;
                if (probe.DeadNames.Count < 4) probe.DeadNames.Add(name);
                return;
            }
            ProbeMaterial(probe, name, uo);
            return;
        }
        var list = slot as System.Collections.IList;
        if (list == null) return;
        // Only a collection of Unity objects can hold a resource this lease pins (Textures is a
        // List<Texture2D>, Materials a Material[]); int[]/Vector3[]/Quaternion[] are mesh data and
        // walking their elements would only cost time.
        Type element = list.GetType().IsArray
            ? list.GetType().GetElementType()
            : (list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : null);
        if (element != null && element != typeof(object) &&
            !typeof(UnityEngine.Object).IsAssignableFrom(element)) return;
        int n;
        try { n = list.Count; } catch (Exception) { probe.ReadErrors++; return; }
        for (int i = 0; i < n; i++)
        {
            object item;
            try { item = list[i]; } catch (Exception) { probe.ReadErrors++; continue; }
            ProbeSlot(probe, name + "[" + i + "]", item);
        }
    }

    // The material rule RemoteModels.Dump uses, so "healthy" means the same thing in both places.
    private static void ProbeMaterial(AssetProbe probe, string name, UnityEngine.Object uo)
    {
        var mat = uo.TryCast<Material>();
        if (mat == null) return;
        probe.Materials++;
        try
        {
            if (mat.shader != null && mat.shader.isSupported &&
                (!mat.HasProperty("_BaseMap") || mat.GetTexture("_BaseMap") != null))
            {
                probe.MaterialsHealthy++;
                return;
            }
        }
        catch (Exception) { probe.ReadErrors++; return; }
        if (probe.BadMaterials.Count < 4) probe.BadMaterials.Add(name);
    }

    // Both caches, as this generation sees them. Reading the modules' own accessors first is what makes
    // the set deterministic: a cache is a private static of its module and only exists once that
    // module's type has initialised, and `CacheCount` is the module's own read (the same touch
    // `alterum.test.pose` performs). It creates no asset and changes no model state.
    private static List<AssetCacheView> ReadAssetCaches(IHostContext host)
    {
        try { _ = SahurRig.CacheCount; }
        catch (Exception e) { host?.LogWarning($"[qc:test] assets: SahurRig cache access threw {Describe(e)}"); }
        try { _ = SahurModel.CacheCount; }
        catch (Exception e) { host?.LogWarning($"[qc:test] assets: SahurModel cache access threw {Describe(e)}"); }

        var views = new List<AssetCacheView>(2);
        List<ModelAssetCacheDiagnostics> live;
        try { live = ModelAssetCacheRegistry.Live(); }
        catch (Exception e)
        {
            host?.LogWarning($"[qc:test] assets: cache registry read threw {Describe(e)}");
            return views;
        }
        for (int i = 0; i < live.Count; i++)
        {
            var view = new AssetCacheView();
            try { view.Snapshot = live[i].Capture(); }
            catch (Exception e)
            {
                host?.LogWarning($"[qc:test] assets: cache snapshot threw {Describe(e)}");
                continue;
            }
            if (view.Snapshot == null) continue;
            for (int j = 0; j < view.Snapshot.Entries.Count; j++)
                view.Probes.Add(ProbeAssetValue(view.Snapshot.Entries[j].Value));
            views.Add(view);
        }
        views.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Snapshot.Owner, b.Snapshot.Owner);
            return c != 0 ? c : a.Snapshot.Instance.CompareTo(b.Snapshot.Instance);
        });
        return views;
    }

    // The bodies this generation can name. The local bodies come from their module's own state word;
    // remote bodies from RemoteModels' built-body slug (empty = no body built for that peer).
    private static List<AssetBody> CollectAssetBodies(IHostContext host, List<AssetCacheView> views)
    {
        var bodies = new List<AssetBody>(4);
        AddLocalAssetBody(host, views, bodies, "rig", "SahurRig");
        AddLocalAssetBody(host, views, bodies, "rigid", "SahurModel");

        var ids = new List<ulong>(8);
        try { RemoteModels.CollectRemoteIds(ids); }
        catch (Exception e) { host?.LogWarning($"[qc:test] assets: remote id collection threw {Describe(e)}"); }
        ids.Sort();
        for (int i = 0; i < ids.Count; i++)
        {
            string slug = "";
            try { slug = RemoteModels.RemoteBodySlug(ids[i]) ?? ""; } catch { }
            var b = new AssetBody
            {
                Name = "body-remote-" + ids[i],
                Slug = slug,
                Live = slug.Length > 0,
                State = slug.Length > 0 ? "built" : "no-body",
            };
            if (b.Live) BindAssetBody(b, views, null);
            bodies.Add(b);
        }
        return bodies;
    }

    private static void AddLocalAssetBody(IHostContext host, List<AssetCacheView> views, List<AssetBody> bodies,
                                          string kind, string owner)
    {
        var b = new AssetBody { Name = "body-local-" + kind };
        try
        {
            if (kind == "rig")
            {
                b.Live = SahurRig.BodyLive;
                b.State = SahurRig.BodyStateName ?? "?";
                b.Slug = SahurRig.VisibleSpecSlug ?? "";
            }
            else
            {
                b.Live = SahurModel.BodyLive;
                b.State = SahurModel.BodyStateName ?? "?";
                b.Slug = SahurModel.VisibleSpecSlug ?? "";
            }
        }
        catch (Exception e)
        {
            host?.LogWarning($"[qc:test] assets: {b.Name} state read threw {Describe(e)}");
            b.Live = false;
            b.State = "unreadable";
        }
        if (b.Live) BindAssetBody(b, views, owner);
        bodies.Add(b);
    }

    // Which cache entry backs this body, if any. `owner` restricts the search to the cache of the
    // module that builds the body (the local rig/rigid path); null means either cache, because a remote
    // body is built by SahurRig or SahurModel depending on the bake and only the entry says which.
    private static void BindAssetBody(AssetBody b, List<AssetCacheView> views, string owner)
    {
        if (string.IsNullOrEmpty(b.Slug)) return;
        for (int c = 0; c < views.Count; c++)
        {
            var s = views[c].Snapshot;
            if (owner != null && !SameRow(s.Owner, owner)) continue;
            for (int i = 0; i < s.Entries.Count; i++)
            {
                if (!SameRow(s.Entries[i].Key, b.Slug)) continue;
                b.Found = true;
                b.Owner = s.Owner;
                b.Users = s.Entries[i].Users;
                b.Alive = views[c].Probes[i].Alive;
                return;
            }
        }
    }

    private static int CountLiveBodies(List<AssetBody> bodies)
    {
        int n = 0;
        for (int i = 0; i < bodies.Count; i++) if (bodies[i].Live) n++;
        return n;
    }

    private static bool HasCacheOwner(List<AssetCacheView> views, string owner)
    {
        for (int i = 0; i < views.Count; i++) if (SameRow(views[i].Snapshot.Owner, owner)) return true;
        return false;
    }

    // The hot assembly's own load generation. The host loads every reload into a fresh collectible
    // AssemblyLoadContext named "AlterumHot#<n>" (HotLoader), so the name is the generation id and the
    // MVID identifies the build. Both are read-only facts about this layer, never about the game.
    private static string HotAlcName()
    {
        try
        {
            var asm = typeof(GameConsole).Assembly;
            var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm);
            return alc == null || string.IsNullOrEmpty(alc.Name) ? "default" : alc.Name;
        }
        catch { return "?"; }
    }

    private static string HotMvid()
    {
        try { return typeof(GameConsole).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8); }
        catch { return "?"; }
    }

    // `alterum.test.assets`: the whole cache/lease picture in the order a lifetime bug is read - which
    // cache (owner, limit, clock, retired), every entry (key, users, idle/leased, age, liveness), then
    // the bodies this generation can name and whether each one's asset is pinned by a lease.
    private static void TestAssets(IHostContext host)
    {
        host.LogInfo($"[qc:test] assets at {Stamp()} scene='{ActiveSceneName()}' " +
                     $"generation='{Q(HotAlcName())}' mvid={HotMvid()}");
        var views = ReadAssetCaches(host);
        int entries = 0, leased = 0, idle = 0, retired = 0, deadLeased = 0;

        for (int c = 0; c < views.Count; c++)
        {
            var s = views[c].Snapshot;
            host.LogInfo($"[qc:test] assets cache owner='{Q(s.Owner)}' value='{Q(s.ValueType)}' id={s.Instance} " +
                         $"limit={s.Limit} count={s.Count} leased={s.Leased} idle={s.Idle} users={s.Users} " +
                         $"clock={s.Clock} retired={(s.Retired ? 1 : 0)}");
            retired += s.Retired ? 1 : 0;
            for (int i = 0; i < s.Entries.Count; i++)
            {
                var e = s.Entries[i];
                var p = views[c].Probes[i];
                if (e.Users > 0 && !p.Alive) deadLeased++;
                host.LogInfo($"[qc:test] assets cache owner='{Q(s.Owner)}' entry[{i}] key='{Q(e.Key)}' " +
                             $"users={e.Users} state={(e.Users == 0 ? "idle" : "leased")} used={e.Used} age={e.Age} " +
                             $"unityRefs={p.UnityRefs} dead={p.Dead} alive={(p.Alive ? 1 : 0)} " +
                             $"materialSlots={p.Materials} healthy={p.MaterialsHealthy}" +
                             (p.ReadErrors > 0 ? $" readErrors={p.ReadErrors}" : "") +
                             (p.DeadNames.Count > 0 ? $" deadRefs=[{string.Join(",", p.DeadNames.ToArray())}]" : "") +
                             (p.BadMaterials.Count > 0
                                 ? $" unhealthyMaterials=[{string.Join(",", p.BadMaterials.ToArray())}]"
                                 : ""));
            }
            entries += s.Count; leased += s.Leased; idle += s.Idle;
        }
        if (views.Count == 0)
            host.LogWarning("[qc:test] assets: no cache in this generation's registry " +
                            "(neither body module has initialised) - there is nothing to report");

        // The modules' own totals (the same two accessors the wardrobe logs), so "which cache holds how
        // much" is readable without walking entries.
        string rigged = "?", rigid = "?";
        try { rigged = $"{SahurRig.CacheCount}/{SahurRig.CacheBytes / 1048576.0:F2}MB"; } catch { }
        try { rigid = $"{SahurModel.CacheCount}/{SahurModel.CacheBytes / 1048576.0:F2}MB"; } catch { }
        host.LogInfo($"[qc:test] assets modules rigged={rigged} rigid={rigid}");

        var bodies = CollectAssetBodies(host, views);
        for (int i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            if (!b.Live)
            {
                host.LogInfo($"[qc:test] assets body {b.Name} state='{Q(b.State)}' slug='{Q(b.Slug)}' live=0");
                continue;
            }
            host.LogInfo($"[qc:test] assets body {b.Name} state='{Q(b.State)}' slug='{Q(b.Slug)}' live=1 " +
                         $"cache='{Q(b.Owner)}' entry={(b.Found ? "present" : "missing")} " +
                         (b.Found ? $"users={b.Users} alive={(b.Alive ? 1 : 0)}" : "users=- alive=0"));
        }

        host.LogInfo($"[qc:test] assets summary caches={views.Count} entries={entries} leased={leased} " +
                     $"idle={idle} retiredCaches={retired} deadLeased={deadLeased} " +
                     $"liveBodies={CountLiveBodies(bodies)}");
    }

    // One verdict's field set. Same `field name ok= required= expected= actual= note=` shape as the
    // model observation (EmitModelField), so a reader parses one format for every assert in the layer.
    private sealed class AssetVerdict
    {
        internal readonly List<ModelField> Fields = new(12);
        internal int Required, Failed;
        internal bool Pass => Required > 0 && Failed == 0;

        internal void Add(string name, string expected, string actual, bool ok, bool required, string note)
        {
            Fields.Add(new ModelField
            {
                Name = name, Expected = expected, Actual = actual, Ok = ok, Required = required, Note = note,
            });
            if (!required) return;
            Required++;
            if (!ok) Failed++;
        }

        internal string FailedNames()
        {
            var names = new List<string>(Failed);
            for (int i = 0; i < Fields.Count; i++)
                if (Fields[i].Required && !Fields[i].Ok) names.Add(Fields[i].Name);
            return string.Join(",", names.ToArray());
        }
    }

    // `alterum.test.assert-assets`: the cache invariants as explicit PASS/FAIL fields, each one a
    // sentence of the cache's own contract (ModelAssetCache.cs). The builder is shared with the
    // `alterum.test.snapshot` manifest ([assets] assert=...), so the two can never disagree; the assert
    // verb prints every field, the manifest keeps the verdict and the failed field names:
    //   caches              both body modules' caches exist in this generation
    //   noDestroyedLeased   a lease pins a LIVE asset: users>0 => every Unity object reachable from the
    //                       entry is still alive (no body drawing destroyed materials)
    //   noDuplicateOwnership one entry owns one value: no negative user count (a release without its
    //                       acquire) and no two keys holding the same value instance (a double destroy)
    //   idleOnlyEviction    eviction is idle-only and the limit is soft while bodies are alive: an entry
    //                       still in the table is alive, and an over-limit table has nothing idle in it
    //   retiredHeld         a retired cache holds exactly its unreleased leases: no idle entry, every
    //                       survivor leased and alive (the release itself is a later observation)
    //   materialsHealthy    every leased entry's materials have a supported shader and a base map
    //   body-*              every live body this generation can name is backed by a leased, alive
    //                       entry; a native/pending body has no lease to check, so it is evidence-only
    //   leaseAttribution    evidence: leases this layer can name vs the entry user counts it cannot
    //   sampled             evidence: entries seen at all (a PASS over zero entries is visibly vacuous)
    // PASS means every REQUIRED field holds. Nothing is acquired, released, trimmed or reloaded.
    private static AssetVerdict BuildAssetVerdict(AssetState st)
    {
        var views = st.Views;
        var bodies = st.Bodies;
        var v = new AssetVerdict();

        var owners = new List<string>(views.Count);
        for (int c = 0; c < views.Count; c++) owners.Add(views[c].Snapshot.Owner);
        v.Add("caches", "SahurModel,SahurRig", owners.Count == 0 ? "none" : string.Join(",", owners.ToArray()),
              HasCacheOwner(views, "SahurModel") && HasCacheOwner(views, "SahurRig"), true,
              "the caches of both body modules, as this generation's registry holds them");

        // Liveness per entry, collected once: the same numbers feed three fields below.
        int deadLeased = 0, deadInTable = 0, negativeUsers = 0, sharedValues = 0;
        var deadKeys = new List<string>(2);
        var sharedKeys = new List<string>(2);
        for (int c = 0; c < views.Count; c++)
        {
            var s = views[c].Snapshot;
            for (int i = 0; i < s.Entries.Count; i++)
            {
                var e = s.Entries[i];
                var p = views[c].Probes[i];
                if (e.Users < 0) negativeUsers++;
                if (!p.Alive)
                {
                    deadInTable++;
                    if (e.Users > 0)
                    {
                        deadLeased++;
                        if (deadKeys.Count < 4) deadKeys.Add($"{s.Owner}:{e.Key}(users={e.Users})");
                    }
                }
                for (int j = i + 1; j < s.Entries.Count; j++)
                {
                    if (!ReferenceEquals(e.Value, s.Entries[j].Value)) continue;
                    sharedValues++;
                    if (sharedKeys.Count < 4) sharedKeys.Add($"{s.Owner}:{e.Key}={s.Entries[j].Key}");
                }
            }
        }

        v.Add("noDestroyedLeased", "deadLeasedAssets=0", $"deadLeasedAssets={deadLeased}", deadLeased == 0, true,
              deadKeys.Count == 0 ? "every lease pins a live asset"
                                  : $"destroyed under a lease: [{string.Join(",", deadKeys.ToArray())}]");

        v.Add("noDuplicateOwnership", "negativeUsers=0 sharedValues=0",
              $"negativeUsers={negativeUsers} sharedValues={sharedValues}",
              negativeUsers == 0 && sharedValues == 0, true,
              sharedKeys.Count == 0 ? "one value per entry: one acquire, one release"
                                    : $"same value under two keys: [{string.Join(",", sharedKeys.ToArray())}]");

        var overIdle = new List<string>(2);
        for (int c = 0; c < views.Count; c++)
        {
            var s = views[c].Snapshot;
            if (s.Count <= s.Limit || s.Idle == 0) continue;
            if (overIdle.Count < 4) overIdle.Add($"{s.Owner}:count={s.Count}/limit={s.Limit}/idle={s.Idle}");
        }
        v.Add("idleOnlyEviction", "deadInTable=0 overLimitIdle=0",
              $"deadInTable={deadInTable} overLimitIdle={overIdle.Count}",
              deadInTable == 0 && overIdle.Count == 0, true,
              overIdle.Count > 0
                  ? $"an over-limit table still had idle entries to evict: [{string.Join(",", overIdle.ToArray())}]"
                  : "every entry the table keeps is alive, and an over-limit table has nothing idle in it");

        int retiredBad = 0;
        var retiredNotes = new List<string>(2);
        for (int c = 0; c < views.Count; c++)
        {
            var s = views[c].Snapshot;
            if (!s.Retired) continue;
            bool bad = s.Idle > 0;
            for (int i = 0; i < s.Entries.Count; i++)
                if (s.Entries[i].Users <= 0 || !views[c].Probes[i].Alive) bad = true;
            if (!bad) continue;
            retiredBad++;
            if (retiredNotes.Count < 4) retiredNotes.Add($"{s.Owner}:count={s.Count}/idle={s.Idle}");
        }
        v.Add("retiredHeld", "retiredBad=0", $"retiredCaches={st.Retired} retiredBad={retiredBad}", retiredBad == 0, true,
              retiredNotes.Count > 0
                  ? $"a retired cache kept something no lease holds: [{string.Join(",", retiredNotes.ToArray())}]"
                  : "a disposed cache holds exactly its unreleased leases (the release is a later read)");

        int materialSlots = 0, materialsHealthy = 0;
        var badMaterials = new List<string>(2);
        for (int c = 0; c < views.Count; c++)
        {
            var s = views[c].Snapshot;
            for (int i = 0; i < s.Entries.Count; i++)
            {
                if (s.Entries[i].Users <= 0) continue;
                var p = views[c].Probes[i];
                materialSlots += p.Materials;
                materialsHealthy += p.MaterialsHealthy;
                for (int k = 0; k < p.BadMaterials.Count && badMaterials.Count < 4; k++)
                    badMaterials.Add(s.Entries[i].Key + ":" + p.BadMaterials[k]);
            }
        }
        v.Add("materialsHealthy", "unhealthyMaterials=0",
              $"unhealthyMaterials={materialSlots - materialsHealthy} leasedMaterialSlots={materialSlots}",
              materialsHealthy >= materialSlots, true,
              badMaterials.Count == 0
                  ? "every leased asset's materials: supported shader, base map present"
                  : $"unhealthy material(s): [{string.Join(",", badMaterials.ToArray())}]");

        for (int i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            if (!b.Live)
            {
                v.Add(b.Name, "n/a", $"state='{b.State}' slug='{b.Slug}'", true, false,
                      "no live custom body of this kind: no lease, nothing to check");
                continue;
            }
            bool ok = b.Found && b.Users > 0 && b.Alive;
            v.Add(b.Name, "entry=present users>0 alive=1",
                  b.Found
                      ? $"entry=present cache='{b.Owner}' users={b.Users} alive={(b.Alive ? 1 : 0)}"
                      : $"entry=missing slug='{b.Slug}'",
                  ok, true,
                  ok ? "the live body's asset is pinned by a lease and alive"
                     : "a live body whose asset is not a live leased entry (evicted or destroyed under it)");
        }

        int named = 0;
        for (int i = 0; i < bodies.Count; i++)
            if (bodies[i].Live && bodies[i].Found && bodies[i].Users > 0) named++;
        int unattributed = Math.Max(0, st.Users - named);
        v.Add("leaseAttribution", "unattributed=0",
              $"leases={st.Users} namedBodies={named} unattributed={unattributed}", unattributed == 0, false,
              "preview/remote leases the read-only layer cannot name are reported here, never failed");

        v.Add("sampled", "entries>0", $"entries={st.Entries}", st.Entries > 0, false,
              "a PASS over zero entries is vacuous: nothing was loaded in this generation yet");

        return v;
    }

    // The one read the asset diagnostics share: both caches, the bodies this generation can name, their
    // totals and the verdict built from them. `alterum.test.assets`, `alterum.test.assert-assets` and
    // the `alterum.test.snapshot` manifest all come through here (or through its two read helpers), so
    // no two of them can report a different cache state - and the manifest cannot claim a PASS the
    // assert verb would not print. Nothing is acquired, released, trimmed or reloaded.
    private sealed class AssetState
    {
        internal List<AssetCacheView> Views;
        internal List<AssetBody> Bodies;
        internal AssetVerdict Verdict;
        internal int Entries, Leased, Idle, Users, Retired;
        internal int LiveBodies => CountLiveBodies(Bodies);
    }

    private static AssetState ReadAssetState(IHostContext host)
    {
        var st = new AssetState { Views = ReadAssetCaches(host) };
        st.Bodies = CollectAssetBodies(host, st.Views);
        for (int c = 0; c < st.Views.Count; c++)
        {
            var s = st.Views[c].Snapshot;
            st.Entries += s.Count; st.Leased += s.Leased; st.Idle += s.Idle; st.Users += s.Users;
            st.Retired += s.Retired ? 1 : 0;
        }
        st.Verdict = BuildAssetVerdict(st);
        return st;
    }

    private static void TestAssertAssets(IHostContext host)
    {
        host.LogInfo($"[qc:test] assert-assets at {Stamp()} scene='{ActiveSceneName()}' " +
                     $"generation='{Q(HotAlcName())}' mvid={HotMvid()}");
        var st = ReadAssetState(host);

        for (int c = 0; c < st.Views.Count; c++)
        {
            var s = st.Views[c].Snapshot;
            host.LogInfo($"[qc:test] assert-assets cache owner='{Q(s.Owner)}' id={s.Instance} limit={s.Limit} " +
                         $"count={s.Count} leased={s.Leased} idle={s.Idle} users={s.Users} " +
                         $"retired={(s.Retired ? 1 : 0)}");
        }

        var v = st.Verdict;
        for (int i = 0; i < v.Fields.Count; i++) EmitModelField(host, "assert-assets", v.Fields[i]);
        string scope = $"caches={st.Views.Count} entries={st.Entries} leased={st.Leased} idle={st.Idle} " +
                       $"leases={st.Users} retiredCaches={st.Retired} liveBodies={st.LiveBodies}";
        if (v.Pass)
            host.LogInfo($"[qc:test] assert-assets result=PASS {scope} failed=0/{v.Required}");
        else
            host.LogWarning($"[qc:test] assert-assets result=FAIL {scope} " +
                            $"failed={v.Failed}/{v.Required} failedFields=[{v.FailedNames()}]");
    }

    // `alterum.test.open-wardrobe` (legacy alias `test.open-wardrobe`): the open half of the layer's
    // wardrobe setup pair. It runs the GAME's own
    // native wardrobe lifecycle - the scene's `InputSystem.UI.WardrobeInputHandler.TryOpenWardrobe()`,
    // the same entry point the wardrobe button reaches - and nothing else: no handler field is
    // written, no view/UI object or transform is fabricated, no camera or cursor state is poked, and
    // no model is selected. When no scene-owned active handler exists it reports that and returns;
    // the scene is untouched.
    //
    // Gated: `qc.request` refuses it via RefuseLocked and a native row is refused by
    // RunNativeTestVerb's own unlocked check, because unlike the read-only verbs above this one
    // changes what is on screen. UiDriver owns the handler resolution (one implementation, shared with
    // the `ui.request` `wardrobe` line), so the two transports cannot disagree about what it did.
    private static void TestOpenWardrobe(IHostContext host)
    {
        host.LogInfo($"[qc:test] open-wardrobe at {Stamp()} scene='{ActiveSceneName()}'");
        bool opened = UiDriver.TryOpenNativeWardrobe(out string detail);
        if (opened)
            host.LogInfo($"[qc:test] open-wardrobe ok: native WardrobeInputHandler.TryOpenWardrobe {detail}; " +
                         "selection untouched");
        else
            host.LogWarning($"[qc:test] open-wardrobe failed: {detail}; nothing was opened, moved or selected");
    }

    // `alterum.test.close-wardrobe`: the close half of the layer's wardrobe setup pair. It runs the
    // SAME scene-owned handler's own `InputSystem.UI.WardrobeInputHandler.TryCloseWardrobe()` - the
    // pinned interop declares it `public bool TryCloseWardrobe()` with no parameters, and it is the
    // public entry point behind the native close action (`OnCloseWardrobePerformed`); SahurWardrobe's
    // own close diagnostic already calls the same method - and nothing else. That method owns the
    // native close lifecycle (the optional view's hide and the lobby UI/camera restore). This verb
    // never calls a view Hide(), never writes a field, never toggles a
    // GameObject/camera/cursor/EventSystem and never selects a model; there is no fallback close
    // path, so `false` from the native method is reported as a failed close, not a fabricated one.
    //
    // Gated: `qc.request` refuses it via RefuseLocked and a native row is refused by
    // RunNativeTestVerb's own unlocked check, because it changes what is on screen. UiDriver owns the
    // handler selection (the same scene-owned ACTIVE rule as the open helper), so the two transports
    // cannot target a different handler, and the open/close pair cannot disagree about which
    // handler is the panel's.
    private static void TestCloseWardrobe(IHostContext host)
    {
        host.LogInfo($"[qc:test] close-wardrobe at {Stamp()} scene='{ActiveSceneName()}'");
        bool closed = UiDriver.TryCloseNativeWardrobe(out string detail);
        if (closed)
            host.LogInfo($"[qc:test] close-wardrobe ok: native WardrobeInputHandler.TryCloseWardrobe {detail}; " +
                         "selection untouched");
        else
            host.LogWarning($"[qc:test] close-wardrobe failed: {detail}; nothing else was changed (no view hide, " +
                            "field write or GameObject/camera/cursor/EventSystem toggle is used as a fallback)");
    }

    // `alterum.test.point <name>`: the named-point setup action. It moves the local actor onto a point
    // from the `Points` table through the GAME's own authoritative teleport -
    // PlayerTeleportator.TeleportOnServer(target, rotation, true), the same call the camera and booth
    // harnesses make. It never writes a transform, never falls back to KinematicCharacterMotor or
    // player.transform, and never creates a teleporter.
    //
    // Gated twice: `qc.request` refuses it via RefuseLocked and a native row is refused by
    // RunNativeTestVerb's own unlocked check (a move is a mutation). On top of the gate it requires
    // server authority (`NetRole.IsServer`), matching the `...OnServer` name and the game's own
    // server-owned teleport. Every precondition is an explicit failure naming itself - no player, no
    // authority, wrong scene, no PlayerTeleportator - and in each case nothing moves. Argument
    // handling is failure-soft: an empty, unknown or multi-word name is one usage/warning line and a
    // return, never a throw and never a guess.
    private static void TestPoint(IHostContext host, string arg)
    {
        string asked = (arg ?? "").Trim();
        host.LogInfo($"[qc:test] point at {Stamp()} scene='{ActiveSceneName()}' authority={NetRole.Word()}");
        if (asked.Length == 0)
        {
            host.LogWarning($"[qc:test] point: usage alterum.test.point <name> (known: {PointNames()}); nothing was moved");
            return;
        }

        // Exact name, case-insensitive; no prefix matching and no slug-style fuzzy resolution.
        var p = FindPoint(asked);
        if (p == null)
        {
            host.LogWarning($"[qc:test] point '{asked}': no named point with that name (known: {PointNames()}); " +
                            "nothing was moved");
            return;
        }

        // Authority first: the teleport is `TeleportOnServer`, so a client has no business running it.
        if (!NetRole.IsServer)
        {
            host.LogWarning($"[qc:test] point '{p.Name}' refused: no authority - this instance is not the server " +
                            $"(authority={NetRole.Word()}). The move needs the game's authoritative teleport; this " +
                            "verb never falls back to a direct transform write");
            return;
        }

        string scene = ActiveSceneRawName();
        if (!string.Equals(scene, p.Scene, StringComparison.OrdinalIgnoreCase))
        {
            host.LogWarning($"[qc:test] point '{p.Name}' refused: the point is in scene '{p.Scene}' but the " +
                            $"active scene is '{scene}'; nothing was moved");
            return;
        }

        var local = FindLocalPlayer(host, out string via);
        if (local == null)
        {
            host.LogWarning($"[qc:test] point '{p.Name}' refused: no local player to move ({via}); nothing was moved");
            return;
        }

        var teleportator = FindTeleportatorUnder(local, out int scanned, out string why);
        if (teleportator == null)
        {
            host.LogWarning($"[qc:test] point '{p.Name}' refused: {why} (scanned={scanned}); nothing was moved - " +
                            "a transform write is not a fallback");
            return;
        }

        var target = new Vector3(p.X, p.Y, p.Z);
        var rot = Quaternion.Euler(0f, p.Yaw, 0f);
        string from = LocalRootPose(local);
        try
        {
            teleportator.TeleportOnServer(target, rot, true);
        }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] point '{p.Name}' failed: PlayerTeleportator.TeleportOnServer threw " +
                            $"{e.GetType().Name}: {e.Message}; no transform write was attempted");
            return;
        }
        host.LogInfo($"[qc:test] point '{p.Name}' -> pos=({p.X:F3},{p.Y:F3},{p.Z:F3}) yaw={p.Yaw:F2} " +
                     $"scene='{scene}' authority=server local={via} " +
                     $"teleport='{ClothInternals.PathOf(teleportator.transform)}' " +
                     $"via=PlayerTeleportator.TeleportOnServer from={from} " +
                     "(gated setup mutation; `alterum.test.pose` reports the result)");
    }

    // `alterum.test.select-model <slug> <ui|protocol>`: the layer's selection verb, and another
    // mutating one. The variant is a separate word on purpose - the two paths are different facts and
    // a run must never be readable as the other:
    //   ui        presses the OPEN native wardrobe's own row for the slug through the existing
    //             UI.Elements.Button/EventSystem machinery (SahurWardrobe.TrySelectModelViaUi). It
    //             requires the view attached and open and resolves the slug to the tab the view built;
    //             every missing precondition is an explicit failure, and nothing falls back to a
    //             direct model write, a direct RPC or SahurWardrobe.ApplyEntry.
    //   protocol  wraps the module's EXISTING authoritative selection helper with test provenance
    //             (SahurWardrobe.TrySelectModelViaProtocol -> ApplyEntry -> SetBaseModelIndex). No UI
    //             required, no new RPC path, and host only: the write is the host's authority.
    // Both are gated by the caller (`RefuseLocked` on the request transport, RunNativeTestVerb's
    // unlocked check for a native row), because a selection is a mutation.
    private static void TestSelectModel(IHostContext host, string arg)
    {
        host.LogInfo($"[qc:test] select-model at {Stamp()} scene='{ActiveSceneName()}'");

        // `<slug> <variant>`: split on the LAST space, so the variant is the last word and a slug
        // that is a label form ("TC2 Demoman") still reaches the module's resolver whole.
        string text = (arg ?? "").Trim();
        int sp = text.LastIndexOf(' ');
        string slug = sp < 0 ? text : text.Substring(0, sp).Trim();
        string variant = sp < 0 ? "" : text.Substring(sp + 1).Trim().ToLowerInvariant();
        if (slug.Length == 0 || variant.Length == 0)
        {
            host.LogWarning("[qc:test] select-model: usage alterum.test.select-model <slug> ui|protocol");
            return;
        }

        if (variant == "ui")
        {
            bool pressed = SahurWardrobe.TrySelectModelViaUi(host, slug, out string detail);
            if (pressed) host.LogInfo($"[qc:test] select-model ui: {detail}");
            else host.LogWarning($"[qc:test] select-model ui failed: {detail}; no model was selected by this verb");
            return;
        }

        if (variant == "protocol")
        {
            bool applied = SahurWardrobe.TrySelectModelViaProtocol(host, slug, out string detail);
            if (applied) host.LogInfo($"[qc:test] select-model protocol: {detail}");
            else host.LogWarning($"[qc:test] select-model protocol failed: {detail}; nothing was applied");
            return;
        }

        host.LogWarning($"[qc:test] select-model: unknown variant '{variant}' " +
                        "(usage: alterum.test.select-model <slug> ui|protocol)");
    }

    // `alterum.test.capture <tag>`: the proof family's general one-frame capture. It is the layer's
    // EXISTING evidence path and nothing more - ScreenShot.Capture, the same call the `shot=` request
    // line and the scenario's capture steps make - taken with the CURRENT game camera pose and
    // settings. No second renderer, no proof camera, no permanent camera: this handler adds a tag
    // check, one call and the artifact report, so there is no second place a frame could come from.
    //
    // What it borrows, and what puts it back (the whole list is ScreenShot.Capture's):
    //   * the game camera itself, rendered once into ScreenShot's own RenderTexture with the camera's
    //     live fov / near / far / cullingMask / clear flags. The transform is never written, so the
    //     player's pose cannot be left moved - this verb has no pose to restore because it aims
    //     nothing (the remote proof verb is the one that borrows the transform).
    //   * cam.targetTexture is saved and restored, and RenderTexture.active is saved and restored, in
    //     a finally - on the success path and on every failure path (a throw between the swap and the
    //     restore re-restores in that same finally).
    //   * the RenderTexture is Released + Destroyed and the temporary Texture2D is Destroyed in the
    //     same finally, so no render state or Unity object outlives the call.
    // Nothing else is touched: no model, no wardrobe/UI action, no teleport, no console state, no
    // camera setting, no GameObject, no RPC. If ScreenShot.Capture ever started writing the camera it
    // would have to restore it here instead; it does not, which is why this verb can reuse it as-is.
    //
    // Known limitation, documented instead of worked around: a ScreenSpaceOverlay canvas is not in any
    // camera, so a frame captured this way cannot contain one (the exact reason MenuProof exists). The
    // verb reports the camera facts it rendered with so a reader can tell what the frame is, and it
    // asserts nothing about the pixels - that verdict is `alterum.test.assert-image`, read-only.
    //
    // Gated like the other capture (it borrows the game's own camera and writes a file): `qc.request`
    // refuses it via RefuseLocked, and a native row is only installed after AEZAKMI while
    // RunNativeTestVerb re-checks the gate.
    //
    // Every precondition is an explicit failure that writes nothing, and each names itself:
    //   * arity - exactly one tag; the tag names the artifact, so a second token is a usage error
    //   * tag - RemoteModels.IsSafeCaptureTag (1-48 chars of A-Z a-z 0-9 '-' '_'), the SAME predicate
    //     `alterum.test.capture-remote` and the remote shot's own path builder use, so a tag can never
    //     name a path outside the screens directory
    //   * game camera - ScreenShot.FindGameCamera, the layer's one resolver (what `shot=`, the scenario
    //     captures and the `[camera]` section of `alterum.test.snapshot` read)
    // The artifact name is the helper's own `shot_<instance>_<tag>_<timestamp>_<n>.png` (which also
    // sanitizes and truncates the tag inside that name), so two invocations cannot overwrite each
    // other; the reported path is the authoritative one, the byte count is the file's own length and
    // the SHA-256 is computed from the bytes that were just written. The helper's one-frame throttle
    // is reported by name rather than hidden, so "no artifact" always has a reason.
    private static void TestCapture(IHostContext host, string arg)
    {
        var parts = (arg ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1)
        {
            host.LogWarning("[qc:test] capture: usage alterum.test.capture <tag> " +
                            "(exactly one tag, without a space); nothing was captured");
            return;
        }

        string tag = parts[0];
        if (!RemoteModels.IsSafeCaptureTag(tag))
        {
            host.LogWarning($"[qc:test] capture: tag '{tag}' is not a safe artifact name " +
                            $"(1-{RemoteModels.MaxCaptureTag} characters of A-Z a-z 0-9 '-' '_'); " +
                            "nothing was captured");
            return;
        }

        // Resolved once and then handed to the capture, so the camera reported below is the camera that
        // rendered - not a second resolution that could pick a different camera.
        Camera cam;
        try { cam = ScreenShot.FindGameCamera(host); }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] capture: the game camera scan threw {Describe(e)}; " +
                            "nothing was captured");
            return;
        }
        if (cam == null)
        {
            host.LogWarning("[qc:test] capture: no game camera to render (menu/loading, or one that is " +
                            "already rendering into a RenderTexture); nothing was captured");
            return;
        }

        // Camera facts BEFORE the render, from the object that renders. ScreenShot.Capture writes
        // neither the transform nor the settings, so these are also the facts the frame was taken
        // with - stated explicitly (`transformWritten=false`) so the guarantee is readable, not
        // inferred.
        try
        {
            Vector3 p = cam.transform.position, e = cam.transform.eulerAngles;
            host.LogInfo($"[qc:test] capture tag='{tag}' at {Stamp()} scene='{ActiveSceneName()}' " +
                         $"source=game-camera camera='{ClothInternals.PathOf(cam.transform)}' " +
                         $"pose=({p.x:F3},{p.y:F3},{p.z:F3}) euler=({e.x:F2},{e.y:F2},{e.z:F2}) " +
                         $"fov={cam.fieldOfView:F1} near={cam.nearClipPlane:F3} far={cam.farClipPlane:F1} " +
                         $"mask=0x{cam.cullingMask:X8} pixels={cam.pixelWidth}x{cam.pixelHeight} " +
                         "transformWritten=false");
        }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] capture: the camera pose could not be read ({Describe(e)}); " +
                            "nothing was captured");
            return;
        }

        string why;
        string path;
        try { path = ScreenShot.Capture(host, tag, cam, out why); }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] capture failed tag='{tag}': ScreenShot.Capture threw " +
                            $"{Describe(e)}; nothing was captured");
            return;
        }
        if (string.IsNullOrEmpty(path))
        {
            host.LogWarning($"[qc:test] capture failed tag='{tag}': {why ?? "no artifact"}; " +
                            "nothing was captured");
            return;
        }

        // The artifact is the helper's, so its location is the helper's own ScreensDir join. The
        // containment check is the claim this verb makes about it (`write only under ScreensDir`),
        // asserted against the canonical paths instead of assumed from the name.
        long bytes = -1;
        string sha = null, note = null;
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(host.ScreensDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                host.LogWarning($"[qc:test] capture failed tag='{tag}': artifact '{full}' is not inside " +
                                $"the screens dir '{root}', so it is not reported as evidence");
                return;
            }
            byte[] data = File.ReadAllBytes(full);
            bytes = data.Length;
            sha = Sha256Hex(data);
        }
        catch (Exception e) { note = Describe(e); }

        if (sha == null)
        {
            host.LogWarning($"[qc:test] capture failed tag='{tag}': artifact '{path}' could not be read " +
                            $"back ({note ?? "unknown"}), so no hash is reported; nothing else was written");
            return;
        }

        host.LogInfo($"[qc:test] capture result=ok tag='{tag}' artifact='{path}' bytes={bytes} " +
                     $"sha256={sha} underScreensDir=1 restored=targetTexture,active-render-texture,temporary-objects");
        RecordCaptureEvidence(host, path, sha, null);
    }

    // `alterum.test.capture-remote <clientId> <tag>`: the layer's proof verb for a remote body. It is
    // one of the gated proof verbs that write an artifact. It runs the module's ONE deterministic
    // evidence path -
    // RemoteModels.TryCaptureRemote, the same camera pose, RenderTexture render and restore the `shot=`
    // request line uses - so this verb cannot photograph something the remote path would not, and then
    // reports the PNG it wrote together with that file's SHA-256. It asserts nothing about the pixels:
    // an image verdict is a different verb and would need its own reference frame.
    //
    // Gated like the other mutations (it poses the game's own camera for a frame and writes a file):
    // `qc.request` refuses it via RefuseLocked and a native row is refused by RunNativeTestVerb's own
    // unlocked check.
    //
    // Every precondition is an explicit failure that writes nothing, and each names itself:
    //   * arity - exactly `<clientId> <tag>`; the tag names the artifact, so a space is a usage error
    //   * client id - a number; anything else prints the remote list, so a wrong argument is diagnosable
    //   * known remote - RemoteModels.HasRemote. An id this instance holds no accepted state for has no
    //     actor behind it, and the module's own guard would only say "no built remote"
    //   * live body - a resolved root (ours for a custom row, the game's own ThirdViewPlayerModel for a
    //     native one) that is active in its hierarchy; advertised-only, still-building and
    //     clone-blocked are refusals, never a picture of whatever happens to be behind the actor
    //   * tag - RemoteModels.IsSafeCaptureTag, the same predicate that builds the file name, so an
    //     unsafe tag cannot leave the screens directory
    // The artifact name is deterministic for a (clientId, tag) pair - `capture-remote-<tag>-c<id>.png`
    // in this instance's screens dir - so a test can assert on one path. A failed capture never
    // publishes this run's file; a previous file that could not be cleared is named in `why` rather
    // than assumed absent (TryCaptureRemote clears it first).
    private static void TestCaptureRemote(IHostContext host, string arg)
    {
        var parts = (arg ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            host.LogWarning("[qc:test] capture-remote: usage alterum.test.capture-remote <clientId> <tag> " +
                            "(one client id and one tag, neither with a space); nothing was captured");
            return;
        }

        if (!ulong.TryParse(parts[0], out ulong clientId))
        {
            host.LogWarning($"[qc:test] capture-remote: '{parts[0]}' is not a client id " +
                            "(usage: alterum.test.capture-remote <clientId> <tag>); nothing was captured");
            TestRemoteList(host);
            return;
        }

        string tag = parts[1];
        if (!RemoteModels.IsSafeCaptureTag(tag))
        {
            host.LogWarning($"[qc:test] capture-remote client={clientId}: tag '{tag}' is not a safe artifact " +
                            $"name (1-{RemoteModels.MaxCaptureTag} characters of A-Z a-z 0-9 '-' '_'); " +
                            "nothing was captured");
            return;
        }

        if (!RemoteModels.HasRemote(clientId))
        {
            host.LogWarning($"[qc:test] capture-remote client={clientId}: no remote actor with that client id " +
                            "in this instance's ledger (no accepted state from it in this session; see " +
                            "alterum.test.remote-list); nothing was captured");
            TestRemoteList(host);
            return;
        }

        // The body actually on screen. This is the same root the shot path frames its camera on, so the
        // check and the capture cannot disagree about which object is being photographed.
        Transform root = null;
        try { root = RemoteModels.RemoteRoot(clientId); } catch { }
        if (root == null)
        {
            host.LogWarning($"[qc:test] capture-remote client={clientId}: no body is resolved for that client " +
                            "yet (advertised only, still building, or already removed); nothing was captured");
            return;
        }
        bool live = false;
        try { live = root.gameObject != null && root.gameObject.activeInHierarchy; } catch { }
        if (!live)
        {
            host.LogWarning($"[qc:test] capture-remote client={clientId}: the body is not active in its " +
                            "hierarchy, so a capture would not contain it; nothing was captured");
            return;
        }

        string file = $"capture-remote-{tag}-c{clientId}";
        host.LogInfo($"[qc:test] capture-remote client={clientId} tag='{tag}' at {Stamp()} " +
                     $"scene='{ActiveSceneName()}' artifactName='{file}.png' " +
                     "(gated proof via the remote shot path)");

        bool ok = RemoteModels.TryCaptureRemote(host, clientId, file, 2.6f, 0f,
                                                out string path, out string sha256, out string why);
        if (!ok || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sha256))
        {
            host.LogWarning($"[qc:test] capture-remote failed client={clientId} tag='{tag}': " +
                            $"{why ?? "no artifact or hash"}; nothing was captured");
            return;
        }

        long bytes;
        try { bytes = new FileInfo(path).Length; }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] capture-remote failed client={clientId} tag='{tag}': artifact " +
                            $"'{path}' could not be read back ({Describe(e)}), so it is not reported as evidence");
            return;
        }
        host.LogInfo($"[qc:test] capture-remote result=ok client={clientId} tag='{tag}' artifact='{path}' " +
                     $"bytes={bytes} sha256={sha256}");
        RecordCaptureEvidence(host, path, sha256, clientId);
    }

    // Tie each PNG to this synchronous observation; a snapshot is evidence, not a UI acceptance claim.
    private static void RecordCaptureEvidence(IHostContext host, string path, string sha, ulong? remote)
    {
        try
        {
            string tag = "proof-" + Guid.NewGuid().ToString("N");
            var sink = new ConsoleSink(host);
            TestSnapshot(sink, tag);
            if (remote.HasValue) TestCameraRemote(sink, remote.Value.ToString(CultureInfo.InvariantCulture));
            TestRemoteList(sink); // includes the renderer/material census where observable
            string relative = Path.GetRelativePath(host.ScreensDir, path);
            TestAssertImage(sink, relative + " 0");
            string snapshot = Path.Combine(host.ScreensDir, "alterum-snapshot-" + tag + ".manifest.txt");
            string state = File.Exists(snapshot) ? File.ReadAllText(snapshot) : "snapshot=unavailable\n";
            string content = "schema=alterum.capture/1\nartifact='" + path + "'\nsha256=" + sha +
                "\nobservation=post-render-synchronous\nuiAcceptance=unverified\n" + state +
                "\n[proof-diagnostics]\n" + string.Join("\n", sink.Lines) + "\n";
            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            string manifest = path + ".manifest.txt";
            if (!WriteArtifactAtomic(manifest, bytes, out string why))
                host.LogWarning($"[qc:test] capture evidence incomplete artifact='{path}' reason='{why}'");
            else host.LogInfo($"[qc:test] capture evidence manifest='{manifest}' sha256={Sha256Hex(bytes)}");
        }
        catch (Exception e) { host.LogWarning($"[qc:test] capture evidence incomplete artifact='{path}': {Describe(e)}"); }
    }

    // `alterum.test.camera-remote <clientId>`: the proof family's pose report. It answers where the
    // module's existing remote-shot path would put the game's own camera and what it would aim at, for
    // one KNOWN remote with a live body - WITHOUT borrowing the camera, rendering a frame or writing an
    // artifact.
    //
    // Why it reports instead of applying: the one application in this layer with a restoration path
    // proven by use is RemoteModels.TryCaptureRemote's synchronous frame (write pose -> cam.Render() ->
    // restore the saved transform, RenderTexture and temporary texture released in a finally). A pose
    // write outside that render would be a camera mutation with no evidence behind it and a restoration
    // path nothing else exercises, so this verb resolves the numbers that path computes
    // (RemoteModels.TryResolveRemoteShotView - the same focus, away/side offset and look rotation) and
    // leaves the player's camera exactly where the game wrote it. The report says so explicitly:
    // applied=false, render=false, artifact=none, restored=not-needed.
    //
    // Gated like the capture verb it reports on (AEZAKMI): the proof family shares one gate. The
    // request transport refuses it through RefuseLocked; a native row is refused by RunNativeTestVerb's
    // own unlocked check.
    //
    // Every precondition is an explicit failure that poses nothing and names itself:
    //   * arity - exactly one client id; a second token is a usage error
    //   * client id - a number; anything else prints the remote list, so a wrong argument is diagnosable
    //   * known remote - RemoteModels.HasRemote (the same ledger the capture verb reads)
    //   * live body - a resolved root that is active in its hierarchy; advertised-only, still-building
    //     and clone-blocked are refusals, never a pose for whatever happens to be behind the actor
    //   * resolvable shot view - a built body and the game's own camera, resolved by the shared path;
    //     its `why` is reported verbatim
    // Output is deterministic for a client id: one line with the camera's current facts, one with the
    // framed body, target and proof pose, and one result line a test can assert on.
    private static void TestCameraRemote(IHostContext host, string arg)
    {
        var parts = (arg ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1)
        {
            host.LogWarning("[qc:test] camera-remote: usage alterum.test.camera-remote <clientId> " +
                            "(exactly one client id); nothing was posed or reported");
            return;
        }

        if (!ulong.TryParse(parts[0], out ulong clientId))
        {
            host.LogWarning($"[qc:test] camera-remote: '{parts[0]}' is not a client id " +
                            "(usage: alterum.test.camera-remote <clientId>); nothing was posed or reported");
            TestRemoteList(host);
            return;
        }

        if (!RemoteModels.HasRemote(clientId))
        {
            host.LogWarning($"[qc:test] camera-remote client={clientId}: no remote actor with that client id " +
                            "in this instance's ledger (no accepted state from it in this session; see " +
                            "alterum.test.remote-list); nothing was posed or reported");
            TestRemoteList(host);
            return;
        }

        // The body actually on screen, resolved exactly the way the capture verb resolves it.
        Transform root = null;
        try { root = RemoteModels.RemoteRoot(clientId); } catch { }
        if (root == null)
        {
            host.LogWarning($"[qc:test] camera-remote client={clientId}: no body is resolved for that client " +
                            "yet (advertised only, still building, or already removed); nothing was posed or reported");
            return;
        }
        bool live = false;
        try { live = root.gameObject != null && root.gameObject.activeInHierarchy; } catch { }
        if (!live)
        {
            host.LogWarning($"[qc:test] camera-remote client={clientId}: the body is not active in its " +
                            "hierarchy, so the shot path would not frame it; nothing was posed or reported");
            return;
        }

        string slug = "", bodySlug = "";
        try { slug = RemoteModels.RemoteSlug(clientId) ?? ""; } catch { }
        try { bodySlug = RemoteModels.RemoteBodySlug(clientId) ?? ""; } catch { }
        host.LogInfo($"[qc:test] camera-remote client={clientId} at {Stamp()} scene='{ActiveSceneName()}' " +
                     $"slug='{slug}' bodySlug='{bodySlug}' source=remote-shot " +
                     "applied=false render=false artifact=none");

        if (!RemoteModels.TryResolveRemoteShotView(host, clientId, 2.6f, 0f, out var view, out string why))
        {
            host.LogWarning($"[qc:test] camera-remote failed client={clientId}: {why ?? "no shot view"}; " +
                            "nothing was posed or reported");
            return;
        }

        try
        {
            Camera cam = view.Camera;
            Vector3 camPos = cam.transform.position, camEuler = cam.transform.eulerAngles;
            Vector3 target = view.Focus, pos = view.Position, euler = view.Rotation.eulerAngles;
            host.LogInfo($"[qc:test] camera-remote client={clientId} " +
                         $"camera='{ClothInternals.PathOf(cam.transform)}' " +
                         $"cameraPose=({camPos.x:F3},{camPos.y:F3},{camPos.z:F3}) " +
                         $"cameraEuler=({camEuler.x:F2},{camEuler.y:F2},{camEuler.z:F2}) " +
                         $"fov={cam.fieldOfView:F1} near={cam.nearClipPlane:F3} far={cam.farClipPlane:F1} " +
                         $"mask=0x{cam.cullingMask:X8} pixels={cam.pixelWidth}x{cam.pixelHeight}");
            host.LogInfo($"[qc:test] camera-remote client={clientId} " +
                         $"body='{ClothInternals.PathOf(view.Body)}' " +
                         $"target=({target.x:F3},{target.y:F3},{target.z:F3}) " +
                         $"proofPose=({pos.x:F3},{pos.y:F3},{pos.z:F3}) " +
                         $"proofEuler=({euler.x:F2},{euler.y:F2},{euler.z:F2}) " +
                         $"distance={view.Distance:F2} side={view.Side:F2} " +
                         "applied=false restored=not-needed");
            host.LogInfo($"[qc:test] camera-remote result=ok client={clientId} source=remote-shot");
        }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] camera-remote failed client={clientId}: pose report threw " +
                            $"{e.GetType().Name}: {e.Message}; nothing was posed");
        }
    }

    // `alterum.test.assert-image <path> <maxMagentaPixels>`: the read-only verdict for an artifact
    // another verb produced. It reads ONE existing PNG under this instance's screens directory,
    // hashes and decodes it, counts magenta pixels, and reports PASS/FAIL against a caller-supplied
    // ceiling. It writes nothing, creates nothing, renders nothing and touches no Unity object, so it
    // belongs to the read-only group and stays available while the console is locked.
    //
    // Decoding goes through PngDecoder.DecodeRgbaBottomUp - NOT ImageConversion.LoadImage or
    // Texture2D - because that path mutates Unity image state (and throws MissingMethodException on
    // this build; see PngDecoder's header). The decoder validates the signature, every chunk length,
    // IHDR (dimensions, bit depth 8, colour type, no interlace), inflates the IDAT stream itself and
    // rejects a truncated or mis-sized payload; a malformed file is reported as reason=invalid-png,
    // never as a half-read image.
    //
    // Magenta is the criterion this layer's live render checks already use
    // (`r > 200 and b > 200 and g < 60`), extended with the opacity those checks got for free by
    // converting each capture to RGB:
    //     alpha >= 200  and  red > 200  and  green < 60  and  blue > 200
    // PngDecoder writes alpha=255 for RGB (colour type 2) and greyscale (0) rows, so the captures
    // this layer produces always satisfy the alpha arm. Magenta is the missing-shader colour, so the
    // threshold is a CEILING: PASS iff magentaCount <= maxMagentaPixels. The verdict is exactly that
    // inequality; it says nothing about body occupancy or framing (a different proof).
    //
    // Path policy: the argument must be a RELATIVE path below host.ScreensDir. An absolute/rooted
    // path, any path whose canonical form is not a file inside that directory, a non-.png name, a
    // missing file, a reparse point (link) or a directory is refused by name before a byte is read.
    // Path.GetFullPath canonicalises `.`/`..` first and the containment test compares against the
    // directory WITH its trailing separator, so a sibling such as `screens2` cannot pass as a prefix.
    // Every directory component between the screens dir and the file is then checked for a reparse
    // point, so a junction/symlink parent cannot redirect a lexically-contained path outside it.
    private const int MagentaAlphaFloor = 200;   // near-opaque: alpha must be >= this
    private const int MagentaRedFloor = 200;     // red must be > this
    private const int MagentaGreenCeiling = 60;  // green must be < this
    private const int MagentaBlueFloor = 200;    // blue must be > this

    private static void TestAssertImage(IHostContext host, string arg)
    {
        host.LogInfo($"[qc:test] assert-image at {Stamp()} scene='{ActiveSceneName()}' " +
                     $"criterion='alpha>={MagentaAlphaFloor} red>{MagentaRedFloor} " +
                     $"green<{MagentaGreenCeiling} blue>{MagentaBlueFloor}'");

        // `<path> <maxMagentaPixels>`: the ceiling is the LAST token, so a path may contain a space
        // (the native row receives two delegate arguments and joins them at that space).
        string text = (arg ?? "").Trim();
        int sp = text.LastIndexOfAny(new[] { ' ', '\t' });
        string rel = sp <= 0 ? null : text.Substring(0, sp).Trim();
        string threshold = sp <= 0 || sp == text.Length - 1 ? null : text.Substring(sp + 1).Trim();
        if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(threshold))
        {
            ImageFail(host, "usage", "usage alterum.test.assert-image <path> <maxMagentaPixels> " +
                                     "(one PNG path under the screens dir and one non-negative ceiling)");
            return;
        }

        if (!long.TryParse(threshold, NumberStyles.Integer, CultureInfo.InvariantCulture, out long max))
        {
            ImageFail(host, "non-numeric-threshold", $"'{threshold}' is not an integer " +
                                                       "(usage: alterum.test.assert-image <path> <maxMagentaPixels>)");
            return;
        }
        if (max < 0)
        {
            ImageFail(host, "negative-threshold", $"{max} is negative; the ceiling must be 0 or more");
            return;
        }

        var c = CheckImage(host, rel, max);
        if (c.Pass)
        {
            host.LogInfo($"[qc:test] assert-image result=PASS {c.Scope}");
            return;
        }
        if (c.Reason == MagentaCeilingReason)
        {
            host.LogWarning($"[qc:test] assert-image result=FAIL reason={c.Reason} {c.Scope} " +
                            $"over={c.Magenta - c.Max}");
            return;
        }
        ImageFail(host, c.Reason, c.Note);
    }

    private static void ImageFail(IHostContext host, string reason, string note)
        => host.LogWarning($"[qc:test] assert-image result=FAIL reason={reason} note='{Q(note)}'");

    // The one image proof: the path policy, the read, the hash, the decode and the magenta ceiling.
    // It prints nothing, so `alterum.test.assert-image` and the scenario's image step report the SAME
    // verdict in their own line shape - a scenario cannot carry a second, drifting copy of the
    // ceiling, the criterion or the path policy. `Reason` is the named rejection code when the read
    // did not produce a verdict at all; `Scope` is the exact field list both callers print for one.
    private const string MagentaCeilingReason = "magenta-ceiling";

    private sealed class ImageCheck
    {
        internal bool Pass;
        internal string Reason;                 // rejection code; null when the read produced a verdict
        internal string Note;                   // the rejection's own text
        internal string Rel, File, Sha;
        internal long Bytes = -1, Pixels = -1, Magenta = -1, Max = -1;
        internal int Width = -1, Height = -1;
        internal string Scope;                  // path/file/bytes/sha256/width/height/pixels/magenta/ceiling
    }

    // Every rejection below names itself and reads nothing further, exactly as the verb's original
    // body did; the only change is that the reason travels back to the caller instead of being printed
    // here. The parent directories are walked component by component and a reparse point in any of
    // them (`link-in-path`) is refused before the file is opened, not only a link on the PNG itself.
    private static ImageCheck CheckImage(IHostContext host, string rel, long max)
    {
        var c = new ImageCheck { Rel = rel, Max = max };
        if (string.IsNullOrEmpty(rel))
        {
            c.Reason = "usage";
            c.Note = "no path given (usage: alterum.test.assert-image <path> <maxMagentaPixels>)";
            return c;
        }

        string screens;
        try { screens = host.ScreensDir; } catch { screens = null; }
        if (string.IsNullOrEmpty(screens))
        {
            c.Reason = "no-screens-dir";
            c.Note = "this instance has no configured screens directory; nothing was read";
            return c;
        }
        if (Path.IsPathRooted(rel))
        {
            c.Reason = "absolute-path";
            c.Note = $"'{rel}' is rooted; the path must be relative to the screens dir";
            return c;
        }

        string root, full;
        try
        {
            root = Path.GetFullPath(screens);
            string sep = Path.DirectorySeparatorChar.ToString();
            if (!root.EndsWith(sep, StringComparison.Ordinal)) root += sep;
            full = Path.GetFullPath(Path.Combine(root, rel));
        }
        catch (Exception e)
        {
            c.Reason = "bad-path";
            c.Note = $"'{rel}' could not be resolved: {Describe(e)}";
            return c;
        }
        c.File = full;

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            c.Reason = "path-escape";
            c.Note = $"'{rel}' resolves to '{full}', which is not a file below '{root}'";
            return c;
        }
        if (!string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase))
        {
            c.Reason = "not-a-png";
            c.Note = $"'{rel}' does not name a .png file";
            return c;
        }

        bool exists;
        try { exists = File.Exists(full); }
        catch (Exception e)
        {
            c.Reason = "unreadable";
            c.Note = $"'{full}' could not be checked: {Describe(e)}";
            return c;
        }
        if (!exists)
        {
            c.Reason = "missing-file";
            c.Note = $"'{full}' does not exist";
            return c;
        }

        // A lexically contained path can still leave the screens dir through a junction or symlink
        // in a parent directory. Walk every component we are about to traverse and refuse a reparse
        // point there; the file's own attributes are checked separately below.
        try
        {
            string belowRoot = full.Substring(root.Length);
            string[] parts = belowRoot.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            string dir = root;
            for (int i = 0; i + 1 < parts.Length; i++)
            {
                dir = Path.Combine(dir, parts[i]);
                var attrs = File.GetAttributes(dir);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    c.Reason = "link-in-path";
                    c.Note = $"'{dir}' is a reparse point (link); only a plain directory path below " +
                             "the screens dir is read";
                    return c;
                }
            }
        }
        catch (Exception e)
        {
            c.Reason = "unreadable";
            c.Note = $"'{full}' directory components could not be checked: {Describe(e)}";
            return c;
        }

        try
        {
            var attrs = File.GetAttributes(full);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                c.Reason = "not-a-regular-file";
                c.Note = $"'{full}' is a reparse point (link); only a plain file under the screens dir is read";
                return c;
            }
        }
        catch (Exception e)
        {
            c.Reason = "unreadable";
            c.Note = $"'{full}' attributes could not be read: {Describe(e)}";
            return c;
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch (Exception e)
        {
            c.Reason = "unreadable";
            c.Note = $"'{full}' could not be read: {Describe(e)}";
            return c;
        }
        if (bytes.Length == 0)
        {
            c.Reason = "invalid-png";
            c.Note = $"'{full}' is empty";
            return c;
        }
        c.Bytes = bytes.Length;
        c.Sha = Sha256Hex(bytes);

        int width, height;
        long pixels, magenta;
        try
        {
            byte[] rgba = PngDecoder.DecodeRgbaBottomUp(bytes, out width, out height);
            pixels = (long)width * height;
            magenta = 0;
            // Four bytes per pixel, straight from the decoder (bottom-up row order, which a whole-image
            // count does not care about). No Unity call is involved at any point.
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                if (rgba[i + 3] < MagentaAlphaFloor) continue;
                if (rgba[i] <= MagentaRedFloor) continue;
                if (rgba[i + 1] >= MagentaGreenCeiling) continue;
                if (rgba[i + 2] <= MagentaBlueFloor) continue;
                magenta++;
            }
        }
        catch (Exception e)
        {
            c.Reason = "invalid-png";
            c.Note = $"'{full}' sha256={c.Sha} bytes={bytes.Length}: {Describe(e)}";
            return c;
        }

        c.Width = width;
        c.Height = height;
        c.Pixels = pixels;
        c.Magenta = magenta;
        c.Scope = $"path='{Q(rel)}' file='{Q(full)}' bytes={bytes.Length} sha256={c.Sha} " +
                  $"width={width} height={height} pixels={pixels} magenta={magenta} maxMagentaPixels={max}";
        c.Pass = magenta <= max;
        if (!c.Pass) c.Reason = MagentaCeilingReason;
        return c;
    }

    // Lower-case hex SHA-256 of the artifact's own bytes: the same representation `capture-remote`
    // reports, so a test can compare one string against the other without re-reading the file.
    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ runtime snapshot
    //
    // `alterum.test.snapshot [tag]`: the deterministic answer to "what state is this instance in,
    // right now", written as ONE structured manifest under this instance's screens directory and
    // reported with that artifact's path and SHA-256. It is a read-only diagnostic in the same sense
    // as `alterum.test.assets`: every field comes from the helper that already owns that fact, and no
    // game, UI, camera or model state is touched. The only write is the manifest file itself, which is
    // why it stays available while the console is locked - like `dump`, which also writes a file and is
    // in the same read-only group (see RefuseLocked).
    //
    // Why a file and not one more log line: a manifest a test can diff, hash, archive and compare
    // across two instances. Its format is line-oriented (`key=value`, `#` comment, `[section]`), the
    // same shape the layer's other artifacts and log fields use. A fact this generation cannot read is
    // written as `unavailable` (plus `<key>Why='...'`), never as a plausible default.
    //
    // Sources, one per section - no second state model:
    //   [process]  Environment.ProcessId / ProcessPath, the process start time and the
    //              `-alterumInstance=` argument (the host's own instance label)
    //   [hot]      HotAlcName (the load generation), the assembly's own full MVID, the shadow-copy path
    //              the host loaded it from, and that file's size + SHA-256
    //   [scene]    SceneManager.GetActiveScene plus the frame clock
    //   [net]      the host's INetBridge.Status and NetRole.Word (the same reads `remote-list` uses)
    //   [local]    FindLocalPlayer (the layer's one resolver), the root transform, and
    //              ReelLogic.TryGetPose (the body-first effective pose `alterum.test.pose` reports)
    //   [points]   the one named-point table (`Points`), each point's scene match and the measured
    //              distance from the local root when the point applies to this scene
    //   [identity] ModelPresentation.ResolveLocalReadOnly + DescribeIdentity - the resolver every
    //              display surface uses, in its own field vocabulary
    //   [remotes]  RemoteModels.CollectRemoteIds / RemoteBodySlug / RemoteRoot and the same
    //              ResolveRemoteReadOnly `alterum.test.identity` uses; body health is the resolved
    //              root's liveness plus the lease facts the asset snapshot already carries
    //   [assets]   ReadAssetState: the cache totals and the SAME AssetVerdict object
    //              `alterum.test.assert-assets` prints, so a manifest cannot claim a PASS that verb
    //              would not print
    //   [camera]   ScreenShot.FindGameCamera - the resolver the screenshot/capture path uses
    //   [report]   QcAccess.Summary (lock/storage/denials), QcGate.Installed, TestCommandsSummary
    //              (the native rows) and the pending `await-model` waits
    //
    // Artifact: `<ScreensDir>/alterum-snapshot-<tag>.manifest.txt`, deterministic per tag (so a test
    // can assert one path without parsing a log), replaced atomically - a flushed temp sibling then a
    // single replace/move - so a reader never sees a half-written manifest and a failed write leaves
    // the previous one intact. The tag goes through RemoteModels.IsSafeCaptureTag, the same predicate
    // that names a capture artifact: letters, digits, '-' and '_' only, so no tag can leave ScreensDir.

    private const string SnapshotSchema = "alterum.snapshot/1";
    private const string SnapshotDefaultTag = "runtime";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static int _snapshotSeq;

    private static void TestSnapshot(IHostContext host, string arg)
    {
        string typed = (arg ?? "").Trim();
        string tag = typed.Length == 0 ? SnapshotDefaultTag : typed;
        // One token: the tag names the artifact, so a space is a usage error (the same rule the capture
        // verb applies), and the name is validated before a path is built.
        if (typed.IndexOf(' ') >= 0 || typed.IndexOf('\t') >= 0)
        {
            host.LogWarning($"[qc:test] snapshot: usage alterum.test.snapshot [tag] (at most one tag, and " +
                            "the tag carries no space); nothing was written");
            return;
        }
        if (!RemoteModels.IsSafeCaptureTag(tag))
        {
            host.LogWarning($"[qc:test] snapshot: tag '{Q(tag)}' is not a safe artifact name " +
                            $"(1-{RemoteModels.MaxCaptureTag} characters of A-Z a-z 0-9 '-' '_'); " +
                            "nothing was written");
            return;
        }

        string screens;
        try { screens = host.ScreensDir; } catch { screens = null; }
        if (string.IsNullOrEmpty(screens))
        {
            host.LogWarning("[qc:test] snapshot: this instance has no configured screens directory; " +
                            "nothing was written");
            return;
        }

        string artifact;
        try { artifact = Path.Combine(screens, $"alterum-snapshot-{tag}.manifest.txt"); }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] snapshot: artifact path failed: {Describe(e)}; nothing was written");
            return;
        }

        int pid = -1, frame = -1;
        string pidWhy = "unavailable in this runtime", frameWhy = "no frame clock in this scene";
        try { pid = Environment.ProcessId; pidWhy = null; } catch (Exception e) { pidWhy = Describe(e); }
        try { frame = Time.frameCount; frameWhy = null; } catch (Exception e) { frameWhy = Describe(e); }

        DateTime nowUtc = DateTime.UtcNow;
        string utc = nowUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Inv);
        string stamp = nowUtc.ToString("yyyyMMdd'T'HHmmssfff'Z'", Inv);
        string commandId = $"snapshot-{stamp}-p{pid}-f{frame}-s{++_snapshotSeq}";

        var m = new SnapshotManifest();
        string summary;
        try { BuildSnapshot(host, m, tag, commandId, utc, artifact, pid, pidWhy, frame, frameWhy, out summary); }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] snapshot result=FAIL commandId='{commandId}' reason=build-failed " +
                            $"note='{Q(Describe(e))}'; nothing was written");
            return;
        }

        // Hash the exact bytes the atomic write lands, so the reported SHA-256 always belongs to the
        // file at that path (the same rule the remote capture follows).
        byte[] bytes = new UTF8Encoding(false).GetBytes(m.Body);
        string sha = Sha256Hex(bytes);
        if (!WriteArtifactAtomic(artifact, bytes, out string why))
        {
            host.LogWarning($"[qc:test] snapshot result=FAIL commandId='{commandId}' reason=write-failed " +
                            $"artifact='{Q(artifact)}' note='{Q(why)}'; the previous manifest (if any) is unchanged");
            return;
        }

        host.LogInfo($"[qc:test] snapshot result=ok commandId='{commandId}' tag='{tag}' {summary} " +
                     $"artifact='{Q(artifact)}' bytes={bytes.Length} sha256={sha}");
    }

    // Everything the manifest holds, read through the existing helpers, in one place. `summary` is the
    // one-line digest the log carries (the artifact is the record; the log line is the pointer to it).
    private static void BuildSnapshot(IHostContext host, SnapshotManifest m, string tag, string commandId,
                                      string utc, string artifact, int pid, string pidWhy,
                                      int frame, string frameWhy, out string summary)
    {
        float now = -1f;
        try { now = Time.unscaledTime; } catch { }

        m.Comment("alterum.test.snapshot - one structured runtime snapshot of this instance.");
        m.Comment("Format: key=value, one fact per line; 'single quotes' hold free text; # is a comment;");
        m.Comment("[name] starts a section. `unavailable` means the fact could not be read in this");
        m.Comment("generation and the sibling <key>Why names the reason - never a default or a guess.");
        m.Raw("schema", SnapshotSchema);
        m.Raw("command", "alterum.test.snapshot");
        m.Text("commandId", commandId);
        m.Raw("transport", host is ConsoleSink ? "native" : "request");
        m.Raw("tag", tag);
        m.Raw("utc", utc);
        m.Text("artifact", artifact);

        // ---- process identity -------------------------------------------------------------
        m.Section("process");
        if (pid >= 0) m.Number("pid", pid); else m.Absent("pid", pidWhy);
        m.Text("instance", string.IsNullOrEmpty(host.InstanceName) ? "main" : host.InstanceName);
        if (TryFact(() => Environment.ProcessPath, out string exe, out string exeWhy)) m.Text("exe", exe);
        else m.Absent("exe", exeWhy);
        if (TryFact(ProcessStartUtc, out string started, out string startedWhy)) m.Raw("startUtc", started);
        else m.Absent("startUtc", startedWhy);
        try
        {
            string arg = null;
            var argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i] ?? "";
                if (!a.StartsWith("-alterumInstance=", StringComparison.OrdinalIgnoreCase)) continue;
                arg = a.Substring("-alterumInstance=".Length);
                break;
            }
            if (string.IsNullOrEmpty(arg)) m.Raw("instanceArg", "none");
            else m.Text("instanceArg", arg);
        }
        catch (Exception e) { m.Absent("instanceArg", Describe(e)); }

        // ---- the hot generation and the DLL that is running -------------------------------
        m.Section("hot");
        string alc = HotAlcName();
        m.Text("generation", alc);
        string mvid = null;
        if (TryFact(() => typeof(GameConsole).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                    out mvid, out string mvidWhy)) m.Raw("mvid", mvid);
        else m.Absent("mvid", mvidWhy);

        string dllSha = null;
        string asmPath = null, asmWhy = null;
        try
        {
            asmPath = typeof(GameConsole).Assembly.Location;
            if (string.IsNullOrEmpty(asmPath))
            {
                asmPath = null;
                asmWhy = "the loaded assembly reports no file location";
            }
        }
        catch (Exception e) { asmWhy = Describe(e); }
        if (asmPath == null) { m.Absent("assembly", asmWhy); m.Absent("dllBytes", asmWhy); m.Absent("dllSha256", asmWhy); }
        else
        {
            m.Text("assembly", asmPath);
            // The shadow copy the host loaded, read with every share flag: the runtime holds the
            // file open, so an exclusive read is not available (and not needed - this is a read).
            // Read (and hash) first, write after: a key is never both measured and `unavailable`.
            byte[] dll = null;
            string dllWhy = null;
            try
            {
                dll = ReadFileShared(asmPath);
                dllSha = Sha256Hex(dll);
            }
            catch (Exception e)
            {
                // A read without a hash is not a measured pair: report both as unavailable rather
                // than writing the byte count next to an empty hash.
                dll = null;
                dllWhy = "the loaded file could not be read and hashed: " + Describe(e);
            }
            if (dll == null)
            {
                m.Absent("dllBytes", dllWhy);
                m.Absent("dllSha256", dllWhy);
            }
            else
            {
                m.Number("dllBytes", dll.Length);
                m.Raw("dllSha256", dllSha);
            }
        }

        // ---- effective host configuration (never a disk-profile guess) --------------------
        m.Section("config");
        var hostAssembly = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "AlterumModelSwap");
        try
        {
            // Older permanent hosts returned BasePlugin.Config, not their named-instance file.
            // Fail closed if only Hot was updated: this observation needs the corrected host too.
            var activePath = hostAssembly?.GetType("AlterumModelSwap.HostPlugin")?
                .GetProperty("ActiveConfigPath", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as string;
            if (string.IsNullOrEmpty(activePath) || activePath != host.ConfigPath)
                m.Absent("path", "permanent host lacks a verified active-profile ConfigPath; host update required");
            else m.Text("path", activePath);
            m.Flag("showLocalPlayer", host.Config.MirrorShowPlayer);
            m.Raw("source", "in-memory-host-context");
        }
        catch (Exception e)
        {
            m.Absent("path", Describe(e));
            m.Absent("showLocalPlayer", Describe(e));
        }

        m.Section("loaded");
        foreach (string name in new[] { "AlterumModelSwap", "AlterumModelSwap.Contracts" })
        {
            try
            {
                var assembly = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == name);
                if (assembly == null || string.IsNullOrEmpty(assembly.Location))
                {
                    m.Absent(name + ".sha256", "loaded assembly has no readable file location");
                    continue;
                }
                m.Text(name + ".path", assembly.Location);
                m.Raw(name + ".mvid", assembly.ManifestModule.ModuleVersionId.ToString("N"));
                m.Raw(name + ".sha256", Sha256Hex(ReadFileShared(assembly.Location)));
                m.Raw(name + ".hashSource", "loaded-assembly-backing-file-not-memory");
            }
            catch (Exception e) { m.Absent(name + ".sha256", Describe(e)); }
        }

        // ---- scene ------------------------------------------------------------------------
        m.Section("scene");
        string sceneName = null, sceneWhy = null;
        int sceneHandle = 0;
        bool sceneLoaded = false;
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            sceneName = scene.name ?? "";
            sceneHandle = scene.handle;
            sceneLoaded = scene.isLoaded;
        }
        catch (Exception e) { sceneWhy = Describe(e); }
        if (sceneName == null) m.Absent("name", sceneWhy);
        else
        {
            m.Text("name", sceneName);
            m.Number("handle", sceneHandle);
            m.Flag("loaded", sceneLoaded);
        }
        if (frame >= 0) m.Number("frame", frame); else m.Absent("frame", frameWhy);
        if (now >= 0f) m.Raw("unscaledTime", now.ToString("0.000", Inv));
        else m.Absent("unscaledTime", "no frame clock in this scene");

        // ---- the net bridge (local network identity) --------------------------------------
        m.Section("net");
        NetBridgeStatus net = default;
        bool haveNet = false;
        string netWhy = "this host context has no bridge";
        try
        {
            var bridge = host.Net;
            if (bridge != null) { net = bridge.Status; haveNet = true; netWhy = null; }
        }
        catch (Exception e) { netWhy = Describe(e); }
        if (!haveNet) m.Absent("state", netWhy);
        else
        {
            m.Raw("state", net.State.ToString());
            m.Raw("authority", NetRole.Word());
            m.Number("localClientId", (long)net.LocalClientId);
            m.Number("serverClientId", (long)net.ServerClientId);
            m.Number("peers", net.PeerCount);
            m.Number("generation", net.Generation);
            m.Number("sessionEpoch", net.SessionEpoch);
        }

        // ---- local actor: pose and the named-point context --------------------------------
        m.Section("local");
        var local = FindLocalPlayer(host, out string via);
        m.Flag("found", local != null);
        m.Text("source", local == null ? "none" : via);

        Vector3 rootPos = default;
        bool haveRoot = false;
        string noLocal = "no local player in this scene";
        if (local == null)
        {
            m.Absent("clientId", noLocal);
            m.Absent("rootPos", noLocal);
            m.Absent("posePos", noLocal);
        }
        else
        {
            try { m.Number("clientId", (long)local.OwnerClientId); }
            catch (Exception e) { m.Absent("clientId", Describe(e)); }
            // Read the whole root in ONE guarded pass and write it after it: a partially written group
            // cannot leave the same key both measured and `unavailable`.
            string rootPath = null, rootWhy = null;
            Vector3 rootEuler = default;
            try
            {
                var tr = local.transform;
                if (tr == null) rootWhy = "the local player has no transform";
                else
                {
                    rootPos = tr.position;
                    rootEuler = tr.eulerAngles;
                    rootPath = ClothInternals.PathOf(tr);
                    haveRoot = true;
                }
            }
            catch (Exception e) { rootWhy = Describe(e); }
            if (!haveRoot)
            {
                m.Absent("rootPath", rootWhy);
                m.Absent("rootPos", rootWhy);
            }
            else
            {
                if (rootPath != null) m.Text("rootPath", rootPath);
                else m.Absent("rootPath", "the transform path could not be read");
                m.Vec("rootPos", rootPos);
                m.Vec("rootEuler", rootEuler);
                m.Flag("active", SafeActiveGo(local.gameObject));
            }

            // The pose the actor is actually DRAWN at, through the same body-first read placement and
            // range use. Reported next to the root so "the root is not the body" stays visible. Read
            // first, write after (a throw cannot leave a key both measured and unavailable).
            Vector3 posePos = default, poseFwd = default;
            string poseSource = null, poseWhy = null;
            bool havePose = false;
            try { havePose = ReelLogic.TryGetPose(local, out posePos, out poseFwd, out poseSource); }
            catch (Exception e) { poseWhy = Describe(e); }
            if (!havePose) m.Absent("posePos", poseWhy ?? "no pose (no body under the actor and no root)");
            else
            {
                m.Text("poseSource", poseSource);
                m.Vec("posePos", posePos);
                m.Vec("poseFwd", poseFwd);
            }
        }

        // Observations only: do not resolve a POV by changing it or infer visibility from a PNG.
        m.Section("view");
        try
        {
            var controllers = local == null ? null : local.GetComponentsInChildren(Il2CppType.Of<Cameras.SwitchActionProcessors.PlayerPOVController>(), true);
            if (controllers == null || controllers.Length != 1)
                m.Absent("pov", "no unique POV controller under the owned local actor");
            else m.Text("pov", controllers[0].TryCast<Cameras.SwitchActionProcessors.PlayerPOVController>().CurrentPOV.ToString());
        }
        catch (Exception e) { m.Absent("pov", Describe(e)); }
        try
        {
            if (local == null) m.Absent("rendererStats", "no local actor");
            else
            {
                var renderers = local.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                int enabled = 0, active = 0;
                for (int i = 0; renderers != null && i < renderers.Length; i++)
                {
                    var renderer = renderers[i].TryCast<Renderer>();
                    if (renderer == null) continue;
                    if (renderer.enabled) enabled++;
                    if (renderer.enabled && renderer.gameObject.activeInHierarchy) active++;
                }
                m.Number("renderers", renderers?.Length ?? 0);
                m.Number("enabled", enabled);
                m.Number("activeEnabled", active);
                m.Text("scope", "all renderers under local actor; not proof of body visibility or camera admission");
            }
        }
        catch (Exception e) { m.Absent("rendererStats", Describe(e)); }

        m.Section("points");
        m.Number("count", Points.Length);
        string nearest = "none";
        float nearestDistance = 0f;
        for (int i = 0; i < Points.Length; i++)
        {
            var point = Points[i];
            string key = $"point[{i}]";
            m.Text(key + ".name", point.Name);
            m.Text(key + ".scene", point.Scene);
            m.Vec(key + ".pos", point.X, point.Y, point.Z);
            m.Raw(key + ".yaw", point.Yaw.ToString("0.00", Inv));
            bool matches = sceneName != null && SameRow(sceneName, point.Scene);
            m.Flag(key + ".sceneMatches", matches);
            if (!matches)
            {
                m.Absent(key + ".distanceFromRoot", sceneName == null
                    ? "the active scene could not be read"
                    : "the active scene is not this point's scene");
                continue;
            }
            if (!haveRoot) { m.Absent(key + ".distanceFromRoot", "the local actor's root could not be read"); continue; }
            float distance = Vector3.Distance(rootPos, new Vector3(point.X, point.Y, point.Z));
            m.Raw(key + ".distanceFromRoot", distance.ToString("0.000", Inv));
            if (nearest == "none" || distance < nearestDistance) { nearest = point.Name; nearestDistance = distance; }
        }
        m.Text("nearest", nearest);

        // ---- local model identity ---------------------------------------------------------
        m.Section("identity");
        m.Raw("hudSource", "visible-sprite-observer/1");
        string localSlug = "unavailable";
        ModelPresentation.ModelIdentity localIdentity = null;
        string identityWhy = null;
        try { localIdentity = ModelPresentation.ResolveLocalReadOnly(host); }
        catch (Exception e) { identityWhy = Describe(e); }
        if (localIdentity == null) m.Absent("local", identityWhy ?? "the identity resolver returned nothing");
        else
        {
            // One line in the resolver's own field vocabulary (one formatter, no re-derivation) plus
            // the canonical slug on its own, so a test can match a slug without parsing the line.
            m.Text("local", ModelPresentation.DescribeIdentity(localIdentity));
            localSlug = string.IsNullOrEmpty(localIdentity.CanonicalSlug) ? NativeRow : localIdentity.CanonicalSlug;
            m.Text("localSlug", localSlug);
        }

        // ---- every remote this instance knows, plus body health ---------------------------
        m.Section("remotes");
        var ids = new List<ulong>(8);
        bool idsOk = true;
        string idsWhy = null;
        try { RemoteModels.CollectRemoteIds(ids); ids.Sort(); }
        catch (Exception e) { idsOk = false; idsWhy = Describe(e); }
        int built = 0;
        for (int i = 0; i < ids.Count; i++)
            try { if (!string.IsNullOrEmpty(RemoteModels.RemoteBodySlug(ids[i]))) built++; } catch { }
        // A ledger read that threw leaves a partial list, which is not a census: report the refusal
        // instead of a count that would read as "this instance knows N actors".
        if (idsOk) { m.Number("count", ids.Count); m.Number("built", built); }
        else { m.Absent("count", idsWhy); m.Absent("built", idsWhy); }

        // The asset state is read ONCE, before the per-remote lease facts and the [assets] section:
        // both come from the same snapshot, so a body cannot be "live" in one section and "missing"
        // in the other.
        AssetState assets = null;
        string assetsWhy = null;
        try { assets = ReadAssetState(host); }
        catch (Exception e) { assetsWhy = Describe(e); }

        for (int i = 0; idsOk && i < ids.Count; i++)
        {
            ulong clientId = ids[i];
            string key = $"remote[{i}]";
            m.Number(key + ".clientId", (long)clientId);
            ModelPresentation.ModelIdentity rid = null;
            string ridWhy = null;
            try { rid = ModelPresentation.ResolveRemoteReadOnly(host, clientId); }
            catch (Exception e) { ridWhy = Describe(e); }
            if (rid == null) m.Absent(key + ".identity", ridWhy ?? "the identity resolver returned nothing");
            else
            {
                m.Text(key + ".identity", ModelPresentation.DescribeIdentity(rid));
                m.Text(key + ".slug", string.IsNullOrEmpty(rid.CanonicalSlug) ? NativeRow : rid.CanonicalSlug);
            }

            string bodySlug = null, slugWhy = null;
            try { bodySlug = RemoteModels.RemoteBodySlug(clientId); }
            catch (Exception e) { slugWhy = Describe(e); }
            if (bodySlug != null) m.Text(key + ".bodySlug", bodySlug);
            else m.Absent(key + ".bodySlug", slugWhy);

            Transform root = null;
            string rootWhy = null;
            try { root = RemoteModels.RemoteRoot(clientId); }
            catch (Exception e) { rootWhy = Describe(e); }
            if (root == null)
            {
                m.Absent(key + ".rootPath", rootWhy ?? "no body is resolved for that client id");
                m.Flag(key + ".live", false);
                m.Text(key + ".health", "no-body");
            }
            else
            {
                string remotePath = null, pathWhy = null;
                try { remotePath = ClothInternals.PathOf(root); }
                catch (Exception e) { pathWhy = Describe(e); }
                if (remotePath != null) m.Text(key + ".rootPath", remotePath);
                else m.Absent(key + ".rootPath", pathWhy);
                bool live = SafeActiveGo(root.gameObject);
                m.Flag(key + ".live", live);
                m.Text(key + ".health", live ? "live" : "not-live");
            }

            var body = FindAssetBody(assets, "body-remote-" + clientId.ToString(Inv));
            if (body == null) m.Absent(key + ".asset", assetsWhy ?? "no asset body entry for this client id");
            else if (!body.Found)
            {
                m.Raw(key + ".asset", "missing");
                m.Absent(key + ".assetUsers", "no cache entry holds this body's slug");
            }
            else
            {
                m.Raw(key + ".asset", "present");
                m.Number(key + ".assetUsers", body.Users);
                m.Flag(key + ".assetAlive", body.Alive);
            }
        }

        // ---- the asset caches and the assertion verdict -----------------------------------
        m.Section("assets");
        if (assets == null)
        {
            m.Absent("caches", assetsWhy);
            m.Absent("assert", assetsWhy);
        }
        else
        {
            m.Number("caches", assets.Views.Count);
            m.Number("entries", assets.Entries);
            m.Number("leased", assets.Leased);
            m.Number("idle", assets.Idle);
            m.Number("leases", assets.Users);
            m.Number("retiredCaches", assets.Retired);
            m.Number("liveBodies", assets.LiveBodies);
            var verdict = assets.Verdict;
            m.Raw("assert", verdict.Pass ? "PASS" : "FAIL");
            m.Number("assertRequired", verdict.Required);
            m.Number("assertFailed", verdict.Failed);
            m.Text("assertFailedFields", verdict.FailedNames());
            // The verdict's own `sampled` evidence field, restated as a number: a PASS over zero entries
            // is vacuous, and a manifest reader must be able to see that without cross-referencing.
            m.Flag("assertSampled", assets.Entries > 0);
            for (int c = 0; c < assets.Views.Count; c++)
            {
                var s = assets.Views[c].Snapshot;
                string key = $"cache[{c}]";
                m.Text(key + ".owner", s.Owner);
                m.Number(key + ".id", s.Instance);
                m.Number(key + ".limit", s.Limit);
                m.Number(key + ".count", s.Count);
                m.Number(key + ".leased", s.Leased);
                m.Number(key + ".idle", s.Idle);
                m.Number(key + ".users", s.Users);
                m.Flag(key + ".retired", s.Retired);
            }
            for (int b = 0; b < assets.Bodies.Count; b++)
            {
                var body = assets.Bodies[b];
                string key = $"body[{b}]";
                m.Text(key + ".name", body.Name);
                m.Flag(key + ".live", body.Live);
                m.Text(key + ".state", body.State);
                m.Text(key + ".slug", body.Slug);
                if (body.Found)
                {
                    m.Text(key + ".cache", body.Owner);
                    m.Number(key + ".users", body.Users);
                    m.Flag(key + ".alive", body.Alive);
                }
                else m.Absent(key + ".cache", body.Live ? "no cache entry holds this body's slug" : "no live body");
            }
        }

        // ---- the camera the game is rendering through -------------------------------------
        m.Section("camera");
        Camera cam = null;
        string camWhy = null;
        try
        {
            cam = ScreenShot.FindGameCamera(host);
            if (cam == null) camWhy = "no enabled camera of this scene renders the game view";
        }
        catch (Exception e) { camWhy = Describe(e); }
        m.Flag("available", cam != null);
        if (cam == null)
        {
            // Same key set as the measured branch: a reader can tell every camera fact is absent
            // for the SAME reason, instead of finding some keys missing altogether.
            m.Absent("path", camWhy);
            m.Absent("pos", camWhy);
            m.Absent("euler", camWhy);
            m.Absent("fov", camWhy);
            m.Absent("near", camWhy);
            m.Absent("far", camWhy);
            m.Absent("mask", camWhy);
            m.Absent("pixels", camWhy);
            m.Absent("targetTexture", camWhy);
        }
        else
        {
            // The whole camera read is one guarded pass, written after it succeeds: a group of facts
            // that belongs to one object is either all measured or all `unavailable`, never half a pose.
            string camPath = null, camReadWhy = null, targetName = "none";
            Vector3 camPos = default, camEuler = default;
            float fov = 0f, near = 0f, far = 0f;
            int mask = 0, pixelWidth = 0, pixelHeight = 0;
            try
            {
                camPath = ClothInternals.PathOf(cam.transform);
                camPos = cam.transform.position;
                camEuler = cam.transform.eulerAngles;
                fov = cam.fieldOfView;
                near = cam.nearClipPlane;
                far = cam.farClipPlane;
                mask = cam.cullingMask;
                pixelWidth = cam.pixelWidth;
                pixelHeight = cam.pixelHeight;
                var target = cam.targetTexture;
                if (target != null) targetName = string.IsNullOrEmpty(target.name) ? "unnamed" : target.name;
            }
            catch (Exception e) { camReadWhy = Describe(e); }

            if (camReadWhy != null)
            {
                m.Absent("path", camReadWhy);
                m.Absent("pos", camReadWhy);
                m.Absent("euler", camReadWhy);
                m.Absent("fov", camReadWhy);
                m.Absent("near", camReadWhy);
                m.Absent("far", camReadWhy);
                m.Absent("mask", camReadWhy);
                m.Absent("pixels", camReadWhy);
                m.Absent("targetTexture", camReadWhy);
            }
            else
            {
                if (camPath != null) m.Text("path", camPath);
                else m.Absent("path", "the camera transform path could not be read");
                m.Vec("pos", camPos);
                m.Vec("euler", camEuler);
                m.Raw("fov", fov.ToString("0.0", Inv));
                m.Raw("near", near.ToString("0.000", Inv));
                m.Raw("far", far.ToString("0.0", Inv));
                m.Raw("mask", "0x" + mask.ToString("X8", Inv));
                m.Raw("pixels", pixelWidth.ToString(Inv) + "x" + pixelHeight.ToString(Inv));
                m.Text("targetTexture", targetName);
            }
        }

        // ---- the test layer's own report state --------------------------------------------
        m.Section("report");
        m.Text("qc", QcAccess.Summary);
        m.Raw("gate", QcGate.Installed ? "installed" : "not-installed");
        m.Text("nativeRows", TestCommandsSummary);
        m.Text("scenario", ScenarioSummaryText());
        m.Number("awaits", _awaits.Count);
        for (int i = 0; i < _awaits.Count; i++)
        {
            var wait = _awaits[i];
            string key = $"await[{i}]";
            if (wait?.Target == null) { m.Absent(key + ".actor", "the pending wait holds no target"); continue; }
            m.Text(key + ".actor", wait.Target.ActorText);
            m.Text(key + ".expected", ShowExpected(wait.Target));
            m.Number(key + ".checks", wait.Checks);
            m.Number(key + ".startedFrame", wait.StartedFrame);
            if (now < 0f) { m.Absent(key + ".elapsed", "no frame clock in this scene"); continue; }
            m.Raw(key + ".elapsed", Math.Max(0f, now - wait.StartedAt).ToString("0.000", Inv));
            m.Raw(key + ".remaining", Math.Max(0f, wait.DeadlineAt - now).ToString("0.000", Inv));
        }

        summary = $"pid={(pid >= 0 ? pid.ToString(Inv) : "unavailable")} generation='{alc}' " +
                  $"mvid={(mvid ?? "unavailable")} scene='{(sceneName ?? "unavailable")}' " +
                  $"local='{localSlug}' " +
                  $"remotes={(idsOk ? ids.Count.ToString(Inv) : "unavailable")} " +
                  $"assets={(assets == null ? "unavailable" : (assets.Verdict.Pass ? "PASS" : "FAIL"))} " +
                  $"camera={(cam == null ? "unavailable" : "available")} nearest='{nearest}' " +
                  $"dllSha256={(dllSha ?? "unavailable")}";
    }

    // The manifest's writer: a formatter, not a second state model. Every value is read and named by
    // the verb above; this type only decides the line shape, so the format stays one place.
    private sealed class SnapshotManifest
    {
        private readonly StringBuilder _sb = new(4096);

        internal string Body => _sb.ToString();

        internal void Comment(string text)
            => _sb.Append("# ").Append((text ?? "").Replace('\r', ' ').Replace('\n', ' ')).Append('\n');

        internal void Section(string name)
        {
            if (_sb.Length > 0) _sb.Append('\n');
            _sb.Append('[').Append(name).Append("]\n");
        }

        internal void Raw(string key, string value) => _sb.Append(key).Append('=').Append(value ?? "").Append('\n');
        internal void Number(string key, long value) => Raw(key, value.ToString(Inv));
        internal void Flag(string key, bool value) => Raw(key, value ? "1" : "0");
        internal void Text(string key, string value) => Raw(key, "'" + Line(value) + "'");

        // A fact that could not be read: the literal token plus the reason, never a default that would
        // read as a measurement.
        internal void Absent(string key, string why)
        {
            Raw(key, "unavailable");
            if (!string.IsNullOrEmpty(why)) Text(key + "Why", why);
        }

        internal void Vec(string key, Vector3 v) => Vec(key, v.x, v.y, v.z);

        internal void Vec(string key, float x, float y, float z)
            => Raw(key, "(" + F(x) + "," + F(y) + "," + F(z) + ")");

        private static string F(float v) => v.ToString("0.000", Inv);

        // Free text lives inside `key='...'`: a quote of its own or a line break would end the field,
        // so both are removed rather than escaped (the same rule the log's Q() fields follow).
        private static string Line(string s)
            => (s ?? "").Replace("'", "").Replace('\r', ' ').Replace('\n', ' ');
    }

    // A guarded manifest read: success hands back the value, a throw (or a null) hands back the reason.
    // The caller writes `unavailable` + `<key>Why` from it, so a failed read is visible as a failed read.
    private static bool TryFact(Func<string> read, out string value, out string why)
    {
        value = null;
        why = null;
        try
        {
            value = read();
            if (value != null) return true;
            why = "the read returned nothing";
            return false;
        }
        catch (Exception e) { why = Describe(e); return false; }
    }

    private static string ProcessStartUtc()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().ToString("O", Inv);
    }

    // Read a file another process (the runtime, the loader) already holds open: every share flag, so a
    // loaded DLL can still be hashed. Read-only; nothing is opened for write.
    private static byte[] ReadFileShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        fs.CopyTo(buffer);
        return buffer.ToArray();
    }

    // Atomic artifact write: a temp sibling flushed to disk, then ONE replace/move over the destination.
    // The destination is never deleted first, so a reader either sees the previous manifest or the new
    // one - never a missing or half-written file. This is the same primitive SahurWardrobe's wanted
    // record uses (it is private to that module, so the ten lines are repeated here rather than a new
    // shared state: the contract is the rename, not the helper).
    private static bool WriteArtifactAtomic(string path, byte[] bytes, out string why)
    {
        why = null;
        string tmp = path + ".tmp";
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);        // on disk before the rename, not only in the OS cache
            }
            if (!File.Exists(path)) File.Move(tmp, path);
            else
            {
                try { File.Replace(tmp, path, null, true); }
                catch (Exception) { File.Move(tmp, path, true); }
            }
            return true;
        }
        catch (Exception e)
        {
            why = Describe(e);
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }

    // ------------------------------------------------------------------ scenario control
    //
    // `alterum.test.scenario <name>`: the deterministic half of the layer that the single verbs only
    // supply the parts for. One named, allowlisted regression sequence - open the native wardrobe,
    // select a model through its own UI row, wait for the identity, assert it, read the asset caches,
    // capture, assert the image, switch to a second model, assert again, close the wardrobe and assert
    // the caches once more - executed as a FRAME-DRIVEN STATE MACHINE, with one manifest at the end.
    //
    // Why a scenario and not a shell script of `qc.request` lines: the request file has no way to wait
    // for an outcome before sending the next line (the same reason `await-model` cannot be followed by
    // an assert on the next line), so a sequence written that way measures the sleeps somebody guessed
    // at rather than the game. Here every wait is a re-read on the game's own frame loop at an explicit
    // cadence, every step has an explicit deadline, and the run stops at the first step that does not
    // hold. There is no Thread.Sleep, no blocking loop and no wall-clock guess anywhere in it.
    //
    // Authority and gate: the run SEQUENCES MUTATIONS (open/close the native wardrobe, press wardrobe
    // rows, borrow the camera for a capture), so it is refused while the console is locked (AEZAKMI):
    // `qc.request` refuses it through RefuseLocked, and a native row is refused by RunNativeTestVerb's
    // own unlocked check - plus one more check inside the handler, so a third transport could not skip
    // the gate either. It is also HOST ONLY: the identity it asserts includes the game's applied row
    // and the wardrobe's database, and on a client both arrive from the host over the wire, so a client
    // run would measure the RPC round trip rather than this layer. A client is refused up front, with
    // its own authority word in the line, exactly as `alterum.test.point` refuses.
    //
    // Idempotency / retry safety:
    //   * one run at a time. A second `alterum.test.scenario` while one is active is REFUSED (one line
    //     naming the active commandId, step and last observation); it never starts a second state
    //     machine, never re-issues an open/selection and never rewrites the active run's steps. Asking
    //     for the SAME name while it is running is still one refusal, not a restart.
    //   * a run owns its mutations: each call step is performed once and its verdict recorded; the
    //     waits only re-read. A capture is retried at the step's cadence only while it produced NO
    //     artifact (the existing ScreenShot helper returns null when it is throttled or skipped, and
    //     writes nothing in that case).
    //   * nothing is persisted between generations. The state lives in this assembly, so a hot reload
    //     ends a run (and writes no manifest for it); the manifest path is deterministic, so a re-run
    //     replaces the previous manifest atomically rather than appending to it.
    //
    // The step set (`model-lifetime`), each with its own deadline and its own verdict:
    //   1 preconditions     scene=Lobby, a local player, and two distinct roster rows (the roster's
    //                       first entry plus the first non-demo entry after it). A failure ends the
    //                       run here, before anything is opened or selected.
    //   2 open-wardrobe     WardrobeInputHandler.TryOpenWardrobe() via UiDriver - the same helper
    //                       `alterum.test.open-wardrobe` calls.
    //   3 await-wardrobe-open  wait until SahurWardrobe reports the view attached with at least one
    //                       built row on screen (its own accessors, no fabricated state).
    //   4 select-a          SahurWardrobe.TrySelectModelViaUi - the exact call
    //                       `alterum.test.select-model <slug> ui` makes. Never the protocol path: a
    //                       scenario that wants the UI path must fail when the UI path fails.
    //   5 await-a           wait until ObserveModel's required fields agree (the same predicate
    //                       `alterum.test.await-model` holds).
    //   6 assert-a          one observation, the same fields `alterum.test.assert-model` prints.
    //   7 assets-a          ReadAssetState/BuildAssetVerdict - the same verdict
    //                       `alterum.test.assert-assets` reports, while model A's body is live.
    //   8 capture-a         ScreenShot.Capture (the existing camera borrow and PNG write).
    //   9 assert-image-a    CheckImage - the same PngDecoder/magenta verdict as
    //                       `alterum.test.assert-image`, ceiling 0 (the layer's missing-shader check).
    //  10 select-b          the same UI call for model B: the switch that exercises asset teardown and
    //                       rebuild.
    //  11 await-b, 12 assert-b, 13 capture-b, 14 assert-image-b - the same four steps for B.
    //  15 close-wardrobe    WardrobeInputHandler.TryCloseWardrobe() via UiDriver - the same helper
    //                       `alterum.test.close-wardrobe` calls; no view/field/GameObject fallback.
    //  16 await-wardrobe-closed  wait until no built row is on screen any more.
    //  17 assert-assets     the cache invariants again after the switch and the close (nothing was
    //                       destroyed under a lease, no duplicate ownership, retired caches held).
    //
    // UI acceptance is NOT claimed by this run, and the artifact says so in its own `[limits]` section:
    // the lobby EventSystem carries a bare BaseInputModule, so no UI pointer event is processed at all
    // (not even for the game's own rows). `TrySelectModelViaUi` therefore invokes `Button.Click` plus
    // the button's `ClickedSafe`/`ClickedSafeReferenced`/`ClickedUnsafe` callbacks directly - the
    // layer's existing click machinery - and a PASS proves that the wardrobe row's own callback path
    // selected the model, NOT that a native pointer/EventSystem event was delivered. That gap is the
    // open correctness item in `plans/test-control-layer-todo.md`; this command records it instead of
    // hiding it, and `uiAcceptance=unproven` is written into every manifest.
    //
    // The artifact: `alterum-scenario-<name>.manifest.txt` under this instance's screens directory,
    // written with the snapshot manifest's own writer (`SnapshotManifest`) and the layer's atomic
    // replace (`WriteArtifactAtomic`), and reported with its exact-byte SHA-256. It holds one section
    // per planned step (status PASS/FAIL/TIMEOUT/SKIP, its timeout, checks, elapsed, the last state it
    // observed and the artifact it produced), an `[artifacts]` index with each PNG's SHA-256, byte
    // count, dimensions and magenta count, the runtime context (process, hot generation/MVID/DLL hash,
    // scene, authority, net identity), the model pair the run chose, and the `[limits]` section above.
    // Every step the run did not reach is written as SKIP with the step it stopped at, so the manifest
    // is a complete account of the plan, not only of the part that ran.

    private const string ScenarioSchema = "alterum.scenario/1";
    private const string ScenarioModelLifetime = "model-lifetime";
    private static readonly string[] ScenarioNames = { ScenarioModelLifetime };

    private const int ScenarioCheckFrames = 4;               // evaluate a step every 4th frame
    private const int ScenarioMaxChecks = 4000;              // frame-side ceiling per step
    private const int ScenarioMaxTransitionsPerTick = 32;    // more than any plan has steps
    private const float ScenarioOpenTimeoutSeconds = 15f;    // wardrobe open -> rows on screen
    private const float ScenarioModelTimeoutSeconds = 30f;   // a selection -> the identity agreeing
    private const float ScenarioCaptureTimeoutSeconds = 10f; // capture retry (throttle/render skip)
    private const float ScenarioCloseTimeoutSeconds = 15f;   // close -> no rows on screen
    private const float ScenarioRunSlackSeconds = 30f;       // the run deadline beyond the step sum
    private const float ScenarioRunMaxSeconds = 600f;        // hard ceiling for the run deadline
    private const long ScenarioMaxMagentaPixels = 0;         // magenta is the missing-shader colour

    // One planned step. `Status` starts "not-run" and is only ever written by the frame that decided
    // it, so a manifest cannot report a step the run did not reach as if it had run.
    private sealed class ScenarioStepState
    {
        internal string Id, Kind, Intent;
        internal float TimeoutSeconds;
        internal string Status = "not-run";      // PASS | FAIL | TIMEOUT | SKIP | not-run
        internal string Reason;                  // the named failure code, never a bare false
        internal string Observed = "none";       // the last state this step read
        internal float StartedAt = -1f, EndedAt = -1f;
        internal int StartedFrame = -1, EndedFrame = -1, Checks;
        internal string Artifact;                // the artifact this step produced or checked
        internal string ArtifactSha256;
        internal long ArtifactBytes = -1;
    }

    // One artifact the run produced, kept so the manifest's index and the image step cannot disagree
    // about which file was hashed and how many magenta pixels it holds.
    private sealed class ScenarioArtifact
    {
        internal string Path, Kind, Sha256, Note;
        internal long Bytes = -1;
        internal int Width = -1, Height = -1;
        internal long Pixels = -1, Magenta = -1, MaxMagenta = -1;
        internal string ImageVerdict = "not-checked";
    }

    private sealed class ScenarioRun
    {
        internal string Name, CommandId, StartedUtc, Transport;
        internal int Pid = -1, StartFrame = -1, EndFrame = -1;
        internal float StartedAt, EndedAt = -1f, TimeoutSeconds, DeadlineAt;
        internal readonly List<ScenarioStepState> Steps = new(20);
        internal readonly List<ScenarioArtifact> Artifacts = new(4);
        internal int Index;
        internal int Checks;                     // checks spent on the current step
        internal int NextCheckFrame;
        internal string ModelA = "", LabelA = "", ModelB = "", LabelB = "";
        internal string LastCapturePath;
        internal string Verdict = "running";     // PASS | FAIL | TIMEOUT
        internal string Reason;
        internal string LastObservation = "none";
        internal string Artifact;                // the manifest path (null when the write failed)
        internal string ArtifactSha256;
        internal bool Finished;

        internal ScenarioStepState Current => Index >= 0 && Index < Steps.Count ? Steps[Index] : null;
    }

    private static ScenarioRun _scenario;       // the one active run (null when none is running)
    private static ScenarioRun _scenarioLast;   // the last finished run, for progress reporting
    private static int _scenarioSeq;

    private static string ScenarioNamesText() => string.Join(", ", ScenarioNames);

    // The canonical name for an allowlisted scenario, or null. The one table: dispatch validation and
    // the run's own name both come through here, so a spelling cannot start a differently-named run.
    private static string CanonicalScenarioName(string name)
    {
        for (int i = 0; i < ScenarioNames.Length; i++)
            if (string.Equals(ScenarioNames[i], name, StringComparison.OrdinalIgnoreCase)) return ScenarioNames[i];
        return null;
    }

    // `alterum.test.scenario <name>`: validate, refuse, or start. Every refusal is one line naming
    // itself and writes nothing; a started run is advanced at once (its first step may complete and the
    // rest of the plan waits on the frame loop).
    private static void TestScenario(IHostContext host, string arg)
    {
        string asked = (arg ?? "").Trim();
        float now;
        int frame;
        try { now = Time.unscaledTime; frame = Time.frameCount; }
        catch
        {
            host.LogWarning("[qc:test] scenario: no frame clock in this scene, so a frame-driven run " +
                            "cannot be sequenced; nothing was started");
            return;
        }

        if (asked.Length == 0)
        {
            host.LogWarning($"[qc:test] scenario: usage alterum.test.scenario <name> " +
                            $"(known: {ScenarioNamesText()}); nothing was started; {ScenarioProgressText()}");
            return;
        }

        string name = CanonicalScenarioName(asked);
        if (name == null)
        {
            host.LogWarning($"[qc:test] scenario '{Q(asked)}' refused: unknown name (known: " +
                            $"{ScenarioNamesText()}); a scenario name is an allowlisted word, never a " +
                            "path or a free-form sequence; nothing was started");
            return;
        }

        // One run at a time. The active run owns its mutations and the identity it is asserting on; a
        // second state machine stepped in would read the first one's open/selection half-applied.
        if (_scenario != null && !_scenario.Finished)
        {
            host.LogWarning($"[qc:test] scenario refused name='{name}': '{_scenario.Name}' is already " +
                            $"running ({ScenarioStateText(_scenario)}); a second run is not started and " +
                            "the active one is unchanged");
            return;
        }

        // AEZAKMI is checked by the caller (RefuseLocked on the request transport, the unlocked check
        // inside a native row). This is the layer's defence in depth for any third transport: a
        // sequence of mutations is exactly what the gate exists to refuse.
        if (!QcAccess.IsUnlocked)
        {
            host.LogWarning("[qc:test] scenario refused: the console is locked (type AEZAKMI in the " +
                            "pause menu); nothing was sequenced");
            return;
        }

        // Host only: the run asserts the game's applied row and the wardrobe's own database, which a
        // client receives from the host. On a client that would measure the RPC round trip, so the
        // refusal is explicit rather than a timeout three steps later.
        if (!NetRole.IsServer)
        {
            host.LogWarning($"[qc:test] scenario refused name='{name}': host only - this instance is " +
                            $"authority={NetRole.Word()}, and the run asserts the host's own applied row " +
                            "and wardrobe database; nothing was sequenced");
            return;
        }

        int pid = -1;
        try { pid = Environment.ProcessId; } catch { }
        DateTime nowUtc = DateTime.UtcNow;
        var run = new ScenarioRun
        {
            Name = name,
            CommandId = $"scenario-{name}-{nowUtc:yyyyMMdd'T'HHmmssfff'Z'}-p{pid}-f{frame}-s{++_scenarioSeq}",
            StartedUtc = nowUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Inv),
            Transport = host is ConsoleSink ? "native" : "request",
            Pid = pid,
            StartedAt = now,
            StartFrame = frame,
            NextCheckFrame = frame,
        };
        run.Steps.AddRange(ScenarioPlan(name));
        float sum = 0f;
        for (int i = 0; i < run.Steps.Count; i++) sum += Math.Max(0f, run.Steps[i].TimeoutSeconds);
        run.TimeoutSeconds = Math.Min(ScenarioRunMaxSeconds, sum + ScenarioRunSlackSeconds);
        run.DeadlineAt = now + run.TimeoutSeconds;
        _scenario = run;

        var ids = new List<string>(run.Steps.Count);
        for (int i = 0; i < run.Steps.Count; i++) ids.Add(run.Steps[i].Id);
        host.LogInfo($"[qc:test] scenario start commandId='{run.CommandId}' name='{name}' " +
                     $"transport={run.Transport} authority={NetRole.Word()} scene='{ActiveSceneName()}' " +
                     $"pid={pid} frame={frame} steps={run.Steps.Count} timeout={run.TimeoutSeconds:0.0}s " +
                     $"cadence={ScenarioCheckFrames}f plan=[{string.Join(", ", ids.ToArray())}] " +
                     "uiAcceptance=unproven");

        // The first advance runs here: a precondition step that fails reports at once, and every wait
        // step registers and returns (never blocks this call).
        ScenarioAdvance(host, now, frame);
    }

    // The frame half: called from Tick right after AwaitTick. One active run, its own cadence, its own
    // deadlines; anything the machine throws ends the run with the reason instead of escaping into the
    // game's frame loop.
    private static void ScenarioTick(IHostContext host, float now)
    {
        var run = _scenario;
        if (run == null || run.Finished) return;
        int frame;
        try { frame = Time.frameCount; } catch { return; }
        try { ScenarioAdvance(host, now, frame); }
        catch (Exception e)
        {
            ScenarioEnd(host, run, "FAIL", $"the state machine threw {Describe(e)}", now, frame);
        }
    }

    // The step machine. It advances only on a frame boundary and only at the plan's cadence; a step
    // that needs more than one frame stays current and is re-read later. Nothing here sleeps.
    private static void ScenarioAdvance(IHostContext host, float now, int frame)
    {
        var run = _scenario;
        if (run == null || run.Finished) return;

        // The run's own deadline (every step timeout plus slack): a run that somehow never reaches a
        // step deadline still ends, with the last observation recorded.
        if (now >= run.DeadlineAt)
        {
            ScenarioEnd(host, run, "TIMEOUT", $"the run deadline ({run.TimeoutSeconds:0.0}s) passed", now, frame);
            return;
        }

        for (int guard = 0; guard < ScenarioMaxTransitionsPerTick; guard++)
        {
            var step = run.Current;
            if (step == null) { ScenarioEnd(host, run, "PASS", null, now, frame); return; }

            // The explicit frame condition: a step is never evaluated twice in one frame, and a wait is
            // re-read no faster than ScenarioCheckFrames.
            if (frame < run.NextCheckFrame) return;
            run.NextCheckFrame = frame + ScenarioCheckFrames;

            if (step.StartedAt < 0f)
            {
                step.StartedAt = now;
                step.StartedFrame = frame;
                step.Checks = 0;
                run.Checks = 0;
                step.Status = "running";
                host.LogInfo($"[qc:test] scenario step {run.Index + 1}/{run.Steps.Count} " +
                             $"id='{step.Id}' kind={step.Kind} status=RUNNING " +
                             $"timeout={(step.TimeoutSeconds > 0f ? step.TimeoutSeconds.ToString("0.0", Inv) + "s" : "none")} " +
                             $"intent='{step.Intent}'");
            }
            run.Checks++;
            step.Checks = run.Checks;

            bool done;
            bool pass;
            string observed = null, reason = null;
            try { done = ScenarioStep(host, run, step, out pass, out observed, out reason); }
            catch (Exception e)
            {
                done = true; pass = false;
                observed = $"the step threw {Describe(e)}";
                reason = "exception";
            }

            if (!string.IsNullOrEmpty(observed)) { step.Observed = observed; run.LastObservation = observed; }

            if (!done)
            {
                // Still waiting. Two explicit bounds: the step's own deadline and the check ceiling.
                // A step without a timeout is synchronous by construction, so this is always a wait.
                if (step.TimeoutSeconds > 0f && now >= step.StartedAt + step.TimeoutSeconds)
                {
                    step.Status = "TIMEOUT";
                    step.Reason = "timeout";
                    step.EndedAt = now;
                    step.EndedFrame = frame;
                    host.LogWarning($"[qc:test] scenario step {run.Index + 1}/{run.Steps.Count} " +
                                    $"id='{step.Id}' status=TIMEOUT timeout={step.TimeoutSeconds:0.0}s " +
                                    $"checks={step.Checks} observed='{Q(step.Observed)}'");
                    ScenarioEnd(host, run, "TIMEOUT", $"step {run.Index + 1} '{step.Id}' timed out", now, frame);
                    return;
                }
                if (run.Checks >= ScenarioMaxChecks)
                {
                    step.Status = "TIMEOUT";
                    step.Reason = "checks";
                    step.EndedAt = now;
                    step.EndedFrame = frame;
                    host.LogWarning($"[qc:test] scenario step {run.Index + 1}/{run.Steps.Count} " +
                                    $"id='{step.Id}' status=TIMEOUT reason=checks checks={step.Checks} " +
                                    $"observed='{Q(step.Observed)}'");
                    ScenarioEnd(host, run, "TIMEOUT", $"step {run.Index + 1} '{step.Id}' used its check ceiling", now, frame);
                    return;
                }
                // A wait stays current; its observation is recorded on every read, and the step's
                // timeout line carries the last one. Nothing is logged per frame.
                return;
            }

            step.EndedAt = now;
            step.EndedFrame = frame;
            step.Status = pass ? "PASS" : "FAIL";
            step.Reason = pass ? null : (reason ?? "failed");
            float elapsed = Math.Max(0f, step.EndedAt - step.StartedAt);
            string line = $"[qc:test] scenario step {run.Index + 1}/{run.Steps.Count} id='{step.Id}' " +
                          $"status={step.Status} elapsed={elapsed:0.000}s checks={step.Checks}" +
                          (step.Reason == null ? "" : $" reason='{Q(step.Reason)}'") +
                          $" observed='{Q(step.Observed)}'";
            if (pass) host.LogInfo(line); else host.LogWarning(line);

            if (!pass)
            {
                ScenarioEnd(host, run, "FAIL", $"step {run.Index + 1} '{step.Id}': {step.Reason}", now, frame);
                return;
            }

            run.Index++;
            run.Checks = 0;
            // A synchronous step hands the next one the same frame; a following wait then registers
            // and returns (its first read happens on this frame, as await-model's does).
            run.NextCheckFrame = frame;
        }

        host.LogWarning($"[qc:test] scenario '{_scenario?.Name}': more than " +
                        $"{ScenarioMaxTransitionsPerTick} steps in one frame; the run is paused until the " +
                        "next frame (the plan is not the problem, the guard is)");
    }

    // One step's action. A `true` return means the step is DECIDED (pass or fail); `false` means it is
    // a wait that is not satisfied yet and stays current. `observed` is the state the step read, which
    // is what the timeout report and the manifest carry.
    private static bool ScenarioStep(IHostContext host, ScenarioRun run, ScenarioStepState step,
                                     out bool pass, out string observed, out string reason)
    {
        pass = false; observed = null; reason = null;
        switch (step.Id)
        {
            case "preconditions":
                return ScenarioPreconditions(host, run, out pass, out observed, out reason);

            case "open-wardrobe":
            {
                if (ScenarioRefused(out observed, out reason)) return true;
                bool opened = UiDriver.TryOpenNativeWardrobe(out string detail);
                observed = detail;
                pass = opened;
                reason = opened ? null : "open-failed";
                return true;
            }

            case "await-wardrobe-open":
            {
                bool ready = WardrobeRowsOnScreen(out string state);
                observed = state;
                if (!ready) return false;
                pass = true;
                return true;
            }

            case "select-a":
                if (ScenarioRefused(out observed, out reason)) return true;
                return ScenarioSelect(host, run.ModelA, out pass, out observed, out reason);

            case "select-b":
                if (ScenarioRefused(out observed, out reason)) return true;
                return ScenarioSelect(host, run.ModelB, out pass, out observed, out reason);

            case "await-a":
                return ScenarioAwaitModel(host, run.ModelA, out pass, out observed, out reason);

            case "await-b":
                return ScenarioAwaitModel(host, run.ModelB, out pass, out observed, out reason);

            case "assert-a":
                return ScenarioAssertModel(host, run.ModelA, out pass, out observed, out reason);

            case "assert-b":
                return ScenarioAssertModel(host, run.ModelB, out pass, out observed, out reason);

            case "assets-a":
            case "assert-assets":
                return ScenarioAssertAssets(host, out pass, out observed, out reason);

            case "capture-a":
                if (ScenarioRefused(out observed, out reason)) return true;
                return ScenarioCapture(host, run, step, "a", out pass, out observed, out reason);

            case "capture-b":
                if (ScenarioRefused(out observed, out reason)) return true;
                return ScenarioCapture(host, run, step, "b", out pass, out observed, out reason);

            case "assert-image-a":
            case "assert-image-b":
                return ScenarioAssertImage(host, run, step, out pass, out observed, out reason);

            case "close-wardrobe":
            {
                if (ScenarioRefused(out observed, out reason)) return true;
                bool closed = UiDriver.TryCloseNativeWardrobe(out string detail);
                observed = detail;
                pass = closed;
                reason = closed ? null : "close-failed";
                return true;
            }

            case "await-wardrobe-closed":
            {
                bool rowsOnScreen = WardrobeRowsOnScreen(out string state);
                observed = state;
                if (rowsOnScreen) return false;
                pass = true;
                return true;
            }

            default:
                observed = $"no state machine arm for step '{step.Id}'";
                reason = "unknown-step";
                return true;
        }
    }

    // The run's own preconditions, re-checked at the moment a MUTATING step acts. The gate and the host
    // authority were true when the run started, but the layer's promise is about the moment of the
    // mutation, not about the invocation: if either changed, the step fails (and the run stops) instead
    // of opening, pressing or capturing through a gate that no longer holds.
    private static bool ScenarioRefused(out string observed, out string reason)
    {
        observed = null; reason = null;
        if (!QcAccess.IsUnlocked)
        {
            observed = "the console is locked at the moment of this mutation (AEZAKMI is not in effect)";
            reason = "gate-locked";
            return true;
        }
        if (!NetRole.IsServer)
        {
            observed = $"this instance is authority={NetRole.Word()} at the moment of this mutation";
            reason = "not-host";
            return true;
        }
        return false;
    }

    // The plan for one allowlisted scenario. Every step's timeout is part of the plan (and therefore of
    // the run's deadline): a wait without a deadline is how a regression suite hangs.
    private static List<ScenarioStepState> ScenarioPlan(string name)
    {
        var steps = new List<ScenarioStepState>(18);
        if (string.Equals(name, ScenarioModelLifetime, StringComparison.Ordinal))
        {
            AddScenarioStep(steps, "preconditions", "precondition", 0f,
                "scene=Lobby, a local player, and two distinct roster rows (the roster's first entry plus " +
                "the first non-demo entry after it); nothing is opened or selected before all three hold");
            AddScenarioStep(steps, "open-wardrobe", "call", 0f,
                "open the native wardrobe through WardrobeInputHandler.TryOpenWardrobe (the same helper " +
                "alterum.test.open-wardrobe uses)");
            AddScenarioStep(steps, "await-wardrobe-open", "wait", ScenarioOpenTimeoutSeconds,
                "wait until SahurWardrobe reports the view attached with at least one built row on screen");
            AddScenarioStep(steps, "select-a", "call", 0f,
                "press the open wardrobe's own row for model A through SahurWardrobe.TrySelectModelViaUi " +
                "(the exact call alterum.test.select-model <slug> ui makes; never the protocol path)");
            AddScenarioStep(steps, "await-a", "wait", ScenarioModelTimeoutSeconds,
                "wait until ObserveModel's required fields agree for model A (the await-model predicate)");
            AddScenarioStep(steps, "assert-a", "assert", 0f,
                "one observation of the local identity against model A (the assert-model field set)");
            AddScenarioStep(steps, "assets-a", "assert", 0f,
                "the asset-cache invariants while model A's body is live (the assert-assets verdict)");
            AddScenarioStep(steps, "capture-a", "capture", ScenarioCaptureTimeoutSeconds,
                "capture the game view through ScreenShot.Capture and record the PNG path, bytes and SHA-256");
            AddScenarioStep(steps, "assert-image-a", "image", 0f,
                "the magenta ceiling over capture-a through the existing PngDecoder path (assert-image); " +
                "reports the file's SHA-256, dimensions, pixels and magenta count");
            AddScenarioStep(steps, "select-b", "call", 0f,
                "press the open wardrobe's own row for model B through SahurWardrobe.TrySelectModelViaUi " +
                "(the switch that tears down A's body and builds B's)");
            AddScenarioStep(steps, "await-b", "wait", ScenarioModelTimeoutSeconds,
                "wait until ObserveModel's required fields agree for model B");
            AddScenarioStep(steps, "assert-b", "assert", 0f,
                "one observation of the local identity against model B");
            AddScenarioStep(steps, "capture-b", "capture", ScenarioCaptureTimeoutSeconds,
                "capture the switched body through ScreenShot.Capture and record the PNG path, bytes and SHA-256");
            AddScenarioStep(steps, "assert-image-b", "image", 0f,
                "the magenta ceiling over capture-b through the same PngDecoder path");
            AddScenarioStep(steps, "close-wardrobe", "call", 0f,
                "close the native wardrobe through WardrobeInputHandler.TryCloseWardrobe (the same helper " +
                "alterum.test.close-wardrobe uses; no view/field/GameObject fallback)");
            AddScenarioStep(steps, "await-wardrobe-closed", "wait", ScenarioCloseTimeoutSeconds,
                "wait until no built wardrobe row is on screen any more");
            AddScenarioStep(steps, "assert-assets", "assert", 0f,
                "the asset-cache invariants again after the switch and the close: nothing destroyed under " +
                "a lease, no duplicate ownership, retired caches held");
        }
        return steps;
    }

    private static void AddScenarioStep(List<ScenarioStepState> steps, string id, string kind, float timeout,
                                        string intent)
        => steps.Add(new ScenarioStepState { Id = id, Kind = kind, TimeoutSeconds = timeout, Intent = intent });

    // The run's first step: the facts that must be true BEFORE anything is opened or selected. A
    // missing one ends the run here (and the manifest records the reason), rather than letting an open
    // or a selection fail three steps later with a less useful message.
    private static bool ScenarioPreconditions(IHostContext host, ScenarioRun run,
                                              out bool pass, out string observed, out string reason)
    {
        pass = false; reason = null;
        string scene = ActiveSceneRawName();
        Gameplay.PlayerSystem.Player local = null;
        string via = "none";
        try { local = FindLocalPlayer(host, out via); }
        catch (Exception e)
        {
            observed = $"local player read threw {Describe(e)}";
            reason = "exception";
            return true;
        }

        // Model A is the roster's first entry (ModelRoster.Default), and model B is the first
        // non-demo entry after it. Derived, not hardcoded: a roster edit moves the pair with it
        // instead of leaving a slug that no longer exists.
        var specA = ModelRoster.Default;
        ModelSpec specB = null;
        var all = ModelRoster.All;
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var s = all[i];
            if (s == null || string.IsNullOrEmpty(s.Slug) || s.DemoOnly) continue;
            if (specA != null && string.Equals(s.Slug, specA.Slug, StringComparison.OrdinalIgnoreCase)) continue;
            specB = s;
            break;
        }

        var problems = new List<string>(4);
        if (!string.Equals(scene, "Lobby", StringComparison.OrdinalIgnoreCase))
            problems.Add($"scene='{scene}' (the wardrobe controller is Lobby-only)");
        if (local == null) problems.Add($"no local player ({via})");
        if (specA == null) problems.Add("the roster has no default entry (ModelRoster.Default is null)");
        else if (specB == null) problems.Add("the roster has no second non-demo entry");

        observed = $"scene='{scene}' active='{ActiveSceneName()}' local={(local == null ? "none" : via)} " +
                   $"a='{(specA == null ? "-" : Q(specA.Slug))}' b='{(specB == null ? "-" : Q(specB.Slug))}'";
        if (problems.Count > 0)
        {
            reason = "precondition: " + string.Join("; ", problems.ToArray());
            return true;
        }

        run.ModelA = specA.Slug; run.LabelA = specA.Label ?? "";
        run.ModelB = specB.Slug; run.LabelB = specB.Label ?? "";
        observed += $" aLabel='{Q(run.LabelA)}' bLabel='{Q(run.LabelB)}'";
        pass = true;
        return true;
    }

    // `select-model ... ui`: one call into the module's existing UI path. `false` from the module is a
    // refused precondition (view closed, tab not built, no Button), never a reason to fall back to the
    // protocol path - a scenario that wants the UI path must fail when the UI path fails.
    private static bool ScenarioSelect(IHostContext host, string slug,
                                       out bool pass, out string observed, out string reason)
    {
        pass = false; reason = null;
        if (string.IsNullOrEmpty(slug))
        {
            observed = "no model resolved by the preconditions step";
            reason = "no-model";
            return true;
        }
        bool pressed = SahurWardrobe.TrySelectModelViaUi(host, slug, out string detail);
        observed = detail;
        pass = pressed;
        reason = pressed ? null : "ui-select-refused";
        return true;
    }

    // The wait half of assert-model: the same ParseModelTarget/ObserveModel pair and therefore the same
    // predicate and the same field set. `false` means "not yet"; a target that cannot exist (unknown
    // slug) is decided as a failure at once instead of waiting out the deadline.
    private static bool ScenarioAwaitModel(IHostContext host, string slug,
                                           out bool pass, out string observed, out string reason)
    {
        pass = false; reason = null;
        var target = ParseModelTarget("local " + slug, "scenario");
        if (target.ErrorCode != null)
        {
            observed = target.Error;
            reason = target.ErrorCode;
            return true;
        }
        ModelObservation o;
        try { o = ObserveModel(host, target); }
        catch (Exception e)
        {
            observed = $"observation threw {Describe(e)}";
            reason = "exception";
            return true;
        }
        observed = ScenarioObservationText(o, target);
        if (!o.Pass) return false;
        pass = true;
        return true;
    }

    // The assert half: one observation, one verdict, the same fields the verb prints.
    private static bool ScenarioAssertModel(IHostContext host, string slug,
                                            out bool pass, out string observed, out string reason)
    {
        pass = false; reason = null;
        var target = ParseModelTarget("local " + slug, "scenario");
        if (target.ErrorCode != null)
        {
            observed = target.Error;
            reason = target.ErrorCode;
            return true;
        }
        ModelObservation o;
        try { o = ObserveModel(host, target); }
        catch (Exception e)
        {
            observed = $"observation threw {Describe(e)}";
            reason = "exception";
            return true;
        }
        observed = ScenarioObservationText(o, target);
        pass = o.Pass;
        if (!pass) reason = $"assert-model failed=[{FailedFieldNames(o)}]";
        return true;
    }

    private static string ScenarioObservationText(ModelObservation o, ModelTarget t)
    {
        if (o == null) return "no observation";
        if (o.Identity == null)
            return $"actor='{Q(o.Actor)}' known={(o.Known ? 1 : 0)} expected='{Q(ShowExpected(t))}' " +
                   $"reason=unresolved note='{Q(o.Why ?? "the identity resolver returned nothing")}'";
        return $"identity {ModelPresentation.DescribeIdentity(o.Identity)} " +
               $"failed=[{FailedFieldNames(o)}] pass={(o.Pass ? 1 : 0)}";
    }

    // The asset verdict, through the same read and the same builder `alterum.test.assert-assets` uses.
    private static bool ScenarioAssertAssets(IHostContext host, out bool pass, out string observed,
                                             out string reason)
    {
        pass = false; reason = null;
        var st = ReadAssetState(host);
        var v = st.Verdict;
        observed = $"caches={st.Views.Count} entries={st.Entries} leased={st.Leased} idle={st.Idle} " +
                   $"retiredCaches={st.Retired} liveBodies={st.LiveBodies} " +
                   $"failed={v.Failed}/{v.Required} failedFields=[{v.FailedNames()}]";
        pass = v.Pass;
        if (!pass) reason = $"assert-assets failed=[{v.FailedNames()}]";
        return true;
    }

    // A capture step: the existing ScreenShot path, retried only while it produced no artifact (its
    // helper returns null when it is throttled or skipped, and writes nothing in that case). A returned
    // path is hashed from its own bytes, so the manifest's artifact hash always belongs to that file.
    private static bool ScenarioCapture(IHostContext host, ScenarioRun run, ScenarioStepState step, string slot,
                                        out bool pass, out string observed, out string reason)
    {
        pass = false; reason = null;
        string tag = $"scenario-{run.Name}-{slot}";
        string path;
        try { path = ScreenShot.Capture(host, tag); }
        catch (Exception e)
        {
            observed = $"ScreenShot.Capture threw {Describe(e)}";
            reason = "capture-threw";
            return true;
        }
        if (path == null)
        {
            observed = $"no artifact yet (tag='{Q(tag)}'; the capture helper is throttled or skipped this frame)";
            return false;
        }

        var art = NoteScenarioArtifact(run, path, "capture");
        if (art.Sha256 == null)
        {
            observed = $"artifact='{Q(path)}' could not be read back: {Q(art.Note)}";
            reason = "artifact-unreadable";
            return true;
        }
        run.LastCapturePath = path;
        step.Artifact = path;
        step.ArtifactBytes = art.Bytes;
        step.ArtifactSha256 = art.Sha256;
        observed = $"artifact='{Q(path)}' bytes={art.Bytes} sha256={art.Sha256}";
        pass = true;
        return true;
    }

    // An image step: the shared CheckImage core over the capture artifact the previous step produced,
    // with the layer's own ceiling. The artifact entry is updated with the numbers, so `[artifacts]` and
    // the step verdict cannot disagree.
    private static bool ScenarioAssertImage(IHostContext host, ScenarioRun run, ScenarioStepState step,
                                            out bool pass, out string observed, out string reason)
    {
        pass = false; reason = null;
        string path = run.LastCapturePath;
        if (string.IsNullOrEmpty(path))
        {
            observed = "no capture artifact in this run";
            reason = "no-artifact";
            return true;
        }
        string rel;
        try { rel = Path.GetFileName(path); }
        catch (Exception e)
        {
            observed = $"artifact path '{Q(path)}' has no file name: {Describe(e)}";
            reason = "bad-artifact-path";
            return true;
        }

        var c = CheckImage(host, rel, ScenarioMaxMagentaPixels);
        step.Artifact = path;
        if (c.Bytes >= 0) step.ArtifactBytes = c.Bytes;
        if (c.Sha != null) step.ArtifactSha256 = c.Sha;
        var art = FindScenarioArtifact(run, path);
        if (art != null)
        {
            art.ImageVerdict = c.Pass ? "PASS" : (c.Reason ?? "FAIL");
            if (c.Width >= 0)
            {
                art.Width = c.Width; art.Height = c.Height;
                art.Pixels = c.Pixels; art.Magenta = c.Magenta; art.MaxMagenta = c.Max;
            }
        }

        if (c.Scope != null)
        {
            observed = c.Scope;
        }
        else
        {
            observed = $"path='{Q(rel)}' reason={c.Reason} note='{Q(c.Note)}'";
            reason = c.Reason ?? "invalid-png";
            return true;
        }

        if (c.Pass) { pass = true; return true; }
        reason = MagentaCeilingReason;
        observed += $" over={c.Magenta - c.Max}";
        return true;
    }

    // The wardrobe's own view state, read through SahurWardrobe's existing accessors - the same view and
    // the same built rows TrySelectModelViaUi presses, so the wait and the press cannot observe different
    // objects. No row is pressed, no field is written and no state is fabricated here.
    private static bool WardrobeRowsOnScreen(out string observed)
    {
        bool attached;
        int tabs;
        int live = 0;
        try
        {
            attached = SahurWardrobe.ViewAttached;
            tabs = SahurWardrobe.ViewTabCount;
            for (int i = 0; i < tabs; i++)
            {
                var tab = SahurWardrobe.ViewTabAt(i);
                if (tab == null) continue;
                try { if (tab.gameObject != null && tab.gameObject.activeInHierarchy) live++; } catch { }
            }
        }
        catch (Exception e)
        {
            observed = $"the wardrobe view could not be read: {Describe(e)}";
            return false;
        }
        observed = $"attached={(attached ? 1 : 0)} tabs={tabs} liveRows={live}";
        return attached && live > 0;
    }

    private static ScenarioArtifact NoteScenarioArtifact(ScenarioRun run, string path, string kind)
    {
        var art = new ScenarioArtifact { Path = path, Kind = kind };
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            art.Bytes = bytes.Length;
            art.Sha256 = Sha256Hex(bytes);
        }
        catch (Exception e) { art.Note = Describe(e); }
        run.Artifacts.Add(art);
        return art;
    }

    private static ScenarioArtifact FindScenarioArtifact(ScenarioRun run, string path)
    {
        for (int i = run.Artifacts.Count - 1; i >= 0; i--)
            if (string.Equals(run.Artifacts[i].Path, path, StringComparison.Ordinal)) return run.Artifacts[i];
        return null;
    }

    // The end of a run: freeze the verdict, mark everything the run did not reach, write ONE manifest
    // atomically, report it, and hand the state machine's slot back (so the next invocation is a fresh
    // run while the last result stays readable through `alterum.test.scenario` with no argument).
    private static void ScenarioEnd(IHostContext host, ScenarioRun run, string verdict, string reason,
                                    float now, int frame)
    {
        if (run == null || run.Finished) return;
        run.Finished = true;
        run.Verdict = verdict;
        run.Reason = reason;
        run.EndedAt = now;
        run.EndFrame = frame;

        // Steps the run never reached: SKIP, with the step the run stopped at. A step the machine was in
        // the middle of when the run deadline fired is the TIMEOUT one; everything after it is SKIP.
        string stoppedAt = run.Current == null ? "the end of the plan" : $"step {run.Index + 1} '{run.Current.Id}'";
        for (int i = 0; i < run.Steps.Count; i++)
        {
            var s = run.Steps[i];
            if (s.Status != "not-run" && s.Status != "running") continue;
            if (i == run.Index && verdict == "TIMEOUT")
            {
                s.Status = "TIMEOUT";
                if (s.Reason == null) s.Reason = reason ?? "timeout";
                s.EndedAt = now;
                s.EndedFrame = frame;
                continue;
            }
            s.Status = "SKIP";
            if (s.Reason == null)
                s.Reason = i == run.Index ? (reason ?? verdict) : $"not run: the run stopped at {stoppedAt}";
        }

        string screens = null, why = null;
        try { screens = host.ScreensDir; } catch (Exception e) { why = Describe(e); }
        if (string.IsNullOrEmpty(screens) && why == null)
            why = "this instance has no configured screens directory";

        if (screens != null)
        {
            try
            {
                string artifact = Path.Combine(screens, $"alterum-scenario-{run.Name}.manifest.txt");
                byte[] bytes = new UTF8Encoding(false).GetBytes(ScenarioManifest(run, host, artifact, now, frame));
                string sha = Sha256Hex(bytes);
                if (WriteArtifactAtomic(artifact, bytes, out string writeWhy))
                {
                    run.Artifact = artifact;
                    run.ArtifactSha256 = sha;
                    why = null;
                }
                else why = writeWhy;
            }
            catch (Exception e) { why = Describe(e); }
        }

        _scenario = null;
        _scenarioLast = run;

        int passed = 0, failed = 0, skipped = 0;
        for (int i = 0; i < run.Steps.Count; i++)
        {
            string st = run.Steps[i].Status;
            if (st == "PASS") passed++;
            else if (st == "FAIL" || st == "TIMEOUT") failed++;
            else skipped++;
        }
        float elapsed = Math.Max(0f, run.EndedAt - run.StartedAt);
        string summary = $"commandId='{run.CommandId}' name='{run.Name}' transport={run.Transport} " +
                         $"steps={run.Steps.Count} passed={passed} failed={failed} skipped={skipped} " +
                         $"elapsed={elapsed:0.000}s last='{Q(run.LastObservation)}'";
        string tail = (string.IsNullOrEmpty(run.Reason) ? "" : $" reason='{Q(run.Reason)}'") +
                      (run.Artifact != null
                          ? $" artifact='{Q(run.Artifact)}' sha256={run.ArtifactSha256}"
                          : $" artifact=none note='{Q(why ?? "not written")}'");
        string acceptance = " uiAcceptance=unproven";
        if (verdict == "PASS") host.LogInfo($"[qc:test] scenario result=PASS {summary}{tail}{acceptance}");
        else host.LogWarning($"[qc:test] scenario result={verdict} {summary}{tail}{acceptance}");
    }

    // The manifest body: the same writer and line shape as `alterum.test.snapshot`, one section per
    // planned step, the artifact index and the limits this run does not claim. Every value comes from
    // the step records or from the helper that already owns that fact; nothing is re-derived here.
    private static string ScenarioManifest(ScenarioRun run, IHostContext host, string artifact,
                                           float now, int frame)
    {
        var m = new SnapshotManifest();
        m.Comment("alterum.test.scenario - one deterministic regression run of the Alterum test control layer.");
        m.Comment("Format: key=value, one fact per line; 'single quotes' hold free text; # is a comment;");
        m.Comment("[name] starts a section. `unavailable` means the fact could not be read, and the");
        m.Comment("sibling <key>Why names the reason - never a default or a guess.");
        m.Comment("A step is PASS/FAIL/TIMEOUT/SKIP; every step in the plan has a section, so the");
        m.Comment("manifest accounts for the whole run, not only the part that executed.");

        int passed = 0, failed = 0, skipped = 0;
        for (int i = 0; i < run.Steps.Count; i++)
        {
            string st = run.Steps[i].Status;
            if (st == "PASS") passed++;
            else if (st == "FAIL" || st == "TIMEOUT") failed++;
            else skipped++;
        }
        float elapsed = Math.Max(0f, (run.EndedAt < 0f ? now : run.EndedAt) - run.StartedAt);

        m.Section("scenario");
        m.Raw("schema", ScenarioSchema);
        m.Raw("command", "alterum.test.scenario");
        m.Text("commandId", run.CommandId);
        m.Raw("transport", run.Transport);
        m.Text("name", run.Name);
        m.Raw("verdict", run.Verdict);
        if (!string.IsNullOrEmpty(run.Reason)) m.Text("reason", run.Reason);
        m.Text("startedUtc", run.StartedUtc);
        DateTime endUtc = DateTime.UtcNow;
        m.Text("finishedUtc", endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Inv));
        m.Raw("elapsed", elapsed.ToString("0.000", Inv));
        m.Number("startedFrame", run.StartFrame);
        if (run.EndFrame >= 0) m.Number("finishedFrame", run.EndFrame); else m.Absent("finishedFrame", "the run has no end frame");
        m.Raw("timeout", run.TimeoutSeconds.ToString("0.0", Inv));
        m.Number("steps", run.Steps.Count);
        m.Number("passed", passed);
        m.Number("failed", failed);
        m.Number("skipped", skipped);
        m.Text("lastObservation", run.LastObservation);
        m.Text("artifact", artifact);
        m.Number("artifacts", run.Artifacts.Count);

        m.Section("context");
        if (run.Pid >= 0) m.Number("pid", run.Pid); else m.Absent("pid", "Environment.ProcessId is unavailable in this runtime");
        m.Text("instance", string.IsNullOrEmpty(host.InstanceName) ? "main" : host.InstanceName);
        string alc = HotAlcName();
        m.Text("generation", alc);
        if (TryFact(() => typeof(GameConsole).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                    out string mvid, out string mvidWhy)) m.Raw("mvid", mvid);
        else m.Absent("mvid", mvidWhy);
        string asmPath = null, asmWhy = null;
        try
        {
            asmPath = typeof(GameConsole).Assembly.Location;
            if (string.IsNullOrEmpty(asmPath)) asmWhy = "the loaded assembly reports no file location";
        }
        catch (Exception e) { asmWhy = Describe(e); }
        if (asmPath == null) { m.Absent("assembly", asmWhy); m.Absent("dllSha256", asmWhy); }
        else
        {
            m.Text("assembly", asmPath);
            try
            {
                byte[] dll = ReadFileShared(asmPath);
                m.Number("dllBytes", dll.Length);
                m.Raw("dllSha256", Sha256Hex(dll));
            }
            catch (Exception e) { m.Absent("dllSha256", "the loaded file could not be read: " + Describe(e)); }
        }
        m.Text("scene", ActiveSceneName());
        m.Raw("authority", NetRole.Word());
        m.Text("qc", QcAccess.Summary);
        m.Text("nativeRows", TestCommandsSummary);
        NetBridgeStatus net = default;
        bool haveNet = false;
        string netWhy = "this host context has no bridge";
        try
        {
            var bridge = host.Net;
            if (bridge != null) { net = bridge.Status; haveNet = true; netWhy = null; }
        }
        catch (Exception e) { netWhy = Describe(e); }
        if (!haveNet) m.Absent("netState", netWhy);
        else
        {
            m.Raw("netState", net.State.ToString());
            m.Number("netLocalClientId", (long)net.LocalClientId);
            m.Number("netPeers", net.PeerCount);
        }
        if (frame >= 0) m.Number("frame", frame); else m.Absent("frame", "no frame clock in this scene");
        if (now >= 0f) m.Raw("unscaledTime", now.ToString("0.000", Inv));
        else m.Absent("unscaledTime", "no frame clock in this scene");

        m.Section("models");
        if (string.IsNullOrEmpty(run.ModelA)) m.Absent("a", "the preconditions step resolved no model A");
        else { m.Text("a", run.ModelA); m.Text("aLabel", run.LabelA); }
        if (string.IsNullOrEmpty(run.ModelB)) m.Absent("b", "the preconditions step resolved no model B");
        else { m.Text("b", run.ModelB); m.Text("bLabel", run.LabelB); }
        m.Raw("source", "roster: first entry (a), then the first non-demo entry after it (b)");

        for (int i = 0; i < run.Steps.Count; i++)
        {
            var s = run.Steps[i];
            m.Section($"step.{i + 1}");
            m.Number("index", i + 1);
            m.Text("id", s.Id);
            m.Raw("kind", s.Kind);
            m.Text("intent", s.Intent);
            m.Raw("status", s.Status);
            if (!string.IsNullOrEmpty(s.Reason)) m.Text("reason", s.Reason);
            if (s.TimeoutSeconds > 0f) m.Raw("timeout", s.TimeoutSeconds.ToString("0.0", Inv));
            else m.Raw("timeout", "none");
            m.Number("checks", s.Checks);
            if (s.StartedFrame >= 0) m.Number("startedFrame", s.StartedFrame); else m.Absent("startedFrame", "the step did not start");
            if (s.EndedFrame >= 0) m.Number("endedFrame", s.EndedFrame); else m.Absent("endedFrame", "the step did not end");
            if (s.StartedAt >= 0f)
                m.Raw("elapsed", Math.Max(0f, (s.EndedAt < 0f ? now : s.EndedAt) - s.StartedAt).ToString("0.000", Inv));
            else m.Absent("elapsed", "the step did not start");
            m.Text("observed", s.Observed);
            if (s.Artifact != null)
            {
                m.Text("artifact", s.Artifact);
                if (s.ArtifactBytes >= 0) m.Number("artifactBytes", s.ArtifactBytes);
                if (s.ArtifactSha256 != null) m.Raw("artifactSha256", s.ArtifactSha256);
            }
            else m.Raw("artifact", "none");
        }

        m.Section("artifacts");
        if (run.Artifacts.Count == 0) m.Absent("count", "this run produced no artifact");
        for (int i = 0; i < run.Artifacts.Count; i++)
        {
            var a = run.Artifacts[i];
            string key = $"artifact[{i}]";
            m.Text(key + ".path", a.Path);
            m.Raw(key + ".kind", a.Kind);
            if (a.Bytes >= 0) m.Number(key + ".bytes", a.Bytes); else m.Absent(key + ".bytes", a.Note ?? "not read");
            if (a.Sha256 != null) m.Raw(key + ".sha256", a.Sha256); else m.Absent(key + ".sha256", a.Note ?? "not read");
            m.Raw(key + ".imageVerdict", a.ImageVerdict);
            if (a.Width >= 0)
            {
                m.Number(key + ".width", a.Width);
                m.Number(key + ".height", a.Height);
                m.Number(key + ".pixels", a.Pixels);
                m.Number(key + ".magenta", a.Magenta);
                m.Number(key + ".maxMagentaPixels", a.MaxMagenta);
            }
        }

        // What this run does NOT prove. Recorded in the artifact itself so a PASS cannot be read as UI
        // acceptance by anyone who only opens the manifest later.
        m.Section("limits");
        m.Text("eventSystem",
               "the lobby EventSystem carries a bare BaseInputModule: no UI pointer event is processed at " +
               "all (not even for the game own rows). The ui steps invoke Button.Click plus the button " +
               "ClickedSafe/ClickedSafeReferenced/ClickedUnsafe callbacks directly - the layer existing " +
               "click machinery - so native pointer/EventSystem delivery is NOT proven by this run");
        m.Raw("uiAcceptance", "unproven");
        m.Text("uiAcceptanceNote",
               "a PASS on select-a/select-b means the wardrobe row own callback path selected the model; " +
               "it is not UI acceptance. The open correctness item is in plans/test-control-layer-todo.md " +
               "(replace the direct callback invocation with real native input processing)");
        m.Text("sceneScope",
               "the wardrobe controller is Lobby-only, so this scenario refuses any other scene instead of " +
               "captioning a run that could not open the panel");
        m.Text("authorityScope",
               "host only: the run asserts the game own applied row and the wardrobe database, which a " +
               "client receives from the host over the wire");
        return m.Body;
    }

    // Progress, current step, commandId, timeout and the last observed state. One set of fields, two
    // spellings: the status log line and the snapshot's [report] section both come through
    // ScenarioProgressText/ScenarioSummaryText, so the console and the manifest cannot describe the same
    // run differently.
    private static string ScenarioStateText(ScenarioRun run)
    {
        if (run == null) return "name='none'";
        float now = -1f;
        try { now = Time.unscaledTime; } catch { }
        float elapsed = run.StartedAt >= 0f && now >= 0f
            ? Math.Max(0f, (run.Finished && run.EndedAt >= 0f ? run.EndedAt : now) - run.StartedAt)
            : -1f;
        string stepText = run.Current == null
            ? $"steps={run.Steps.Count}"
            : $"step={run.Index + 1}/{run.Steps.Count} '{run.Current.Id}' status={run.Current.Status}";
        return $"name='{run.Name}' commandId='{run.CommandId}' {stepText} " +
               $"verdict={(run.Finished ? run.Verdict : "running")}" +
               (string.IsNullOrEmpty(run.Reason) ? "" : $" reason='{Q(run.Reason)}'") +
               $" elapsed={(elapsed < 0f ? "unavailable" : elapsed.ToString("0.000", Inv) + "s")} " +
               $"remaining={(run.Finished ? "0.000s" : now < 0f ? "unavailable" : Math.Max(0f, run.DeadlineAt - now).ToString("0.000", Inv) + "s")} " +
               $"timeout={run.TimeoutSeconds.ToString("0.0", Inv)}s " +
               $"artifact={(run.Artifact == null ? "none" : "'" + Q(run.Artifact) + "'")} " +
               $"last='{Q(run.LastObservation)}'";
    }

    // The one-line state for a manifest value or a status field: which run (the active one, else the last
    // finished one) and its fields.
    private static string ScenarioSummaryText()
    {
        var run = _scenario != null && !_scenario.Finished ? _scenario : _scenarioLast;
        if (run == null) return "none (no run in this generation)";
        return (run.Finished ? "last " : "active ") + ScenarioStateText(run);
    }

    private static string ScenarioProgressText() => "scenario=" + ScenarioSummaryText();

    private static AssetBody FindAssetBody(AssetState state, string name)
    {
        if (state == null || state.Bodies == null) return null;
        for (int i = 0; i < state.Bodies.Count; i++)
            if (SameRow(state.Bodies[i].Name, name)) return state.Bodies[i];
        return null;
    }

    // ------------------------------------------------------------------ native QC rows
    //
    // The same verbs, the same setup actions, the selection verb and the capture verb are also real
    // Quantum Console commands, so after AEZAKMI a human can type `alterum.test.pose`,
    // `alterum.test.identity [clientId]`, `alterum.test.remote-list`,
    // `alterum.test.wardrobe-list`,
    // `alterum.test.assert-model <clientId|local> <slug>`,
    // `alterum.test.await-model <clientId|local> <slug>`, `alterum.test.assets`,
    // `alterum.test.assert-assets`, `alterum.test.assert-image <path> <maxMagentaPixels>`,
    // `alterum.test.snapshot [tag]`, `alterum.test.points`, `alterum.test.point <name>`,
    // `alterum.test.open-wardrobe`, `alterum.test.close-wardrobe`,
    // `alterum.test.select-model <slug> ui|protocol`,
    // `alterum.test.capture <tag>` or
    // `alterum.test.capture-remote <clientId> <tag>` in the console instead of writing qc.request.
    // The five legacy spellings (`test.pose`, `test.identity`, `test.remote-list`,
    // `test.wardrobe-list`, `test.open-wardrobe`) are registered as compatibility aliases too: same
    // arity, same policy, same row body (the delegate canonicalizes the name before dispatch), and
    // they are additional ROWS, never a second entry for one name - QC keys its table by (name,
    // argument count), so both spellings coexist and neither replaces nor shadows the other. The
    // request transport is unchanged and keeps working locked or not: both transports call the
    // methods above, so they cannot answer differently.
    //
    // Route, and why it is the only one:
    //   * `[Command]` is impossible here. Il2CppInterop strips attributes, and the processor scans
    //     the GAME's assemblies (LoadCommandsFromAssembly / LoadCommandsFromType over il2cpp
    //     metadata); a method declared in this hot assembly is not in that metadata, so no attribute
    //     on it could ever be discovered.
    //   * QFSW.QC does expose a runtime registration API in this build, read out of the pinned
    //     interop assembly, so this file stays reflection-only on QFSW.QC like the rest of the
    //     console code here:
    //        QuantumConsoleProcessor.TryAddCommand(CommandData) / TryRemoveCommand(CommandData)
    //        LambdaCommandData(Delegate lambda, string name, string description)
    //     The delegate is the bridge from il2cpp's table back into managed code, and it is the same
    //     conversion the AEZAKMI capture uses for Keyboard.onTextInput (DelegateSupport
    //     .ConvertDelegate). QC then treats the row like any of the game's 109: `help` lists it,
    //     autocomplete resolves it, and its own parser produces the arguments.
    //   * `alterum.test.identity` is registered twice, with 0 and 1 parameters (and its legacy alias
    //     `test.identity` the same way - two rows, two arities each):
    //     QC resolves an overload by
    //     argument count, which is exactly what the documented "[clientId]" means at the prompt.
    //     `alterum.test.select-model` takes 2 (slug, variant), because its syntax carries the path,
    //     `alterum.test.capture` takes 1 (the tag), because the tag is the only thing the command
    //     names and the camera is the game's own,
    //     `alterum.test.capture-remote` takes 2 (clientId, tag) for the same reason - the tag is an
    //     argument of the command, not a word the handler may guess at - `alterum.test.assert-model` /
    //     `alterum.test.await-model` take 2 (actor, slug), the image proof takes 2 (path, ceiling) for
    //     the same reason - the path and the threshold are arguments of the command, not words the
    //     handler may guess at - the runtime snapshot takes 0 or 1 (its optional tag) for the same
    //     reason `alterum.test.identity` takes 0 or 1, the asset diagnostics take 0
    //     (`alterum.test.assets`, `alterum.test.assert-assets`), `alterum.test.open-wardrobe` and
    //     `alterum.test.close-wardrobe` take 0, the points table takes 0
    //     (`alterum.test.points`) and the named-point action takes 1 (name, `alterum.test.point`).
    //     `alterum.test.scenario` takes 1 (the allowlisted scenario name): the name is an argument of
    //     the command, exactly like `alterum.test.point`'s point name, and the handler rejects anything
    //     outside its own list.
    //     Each 0/1/2-argument bridge is converted on its own, so a refused signature is one warning
    //     line and never takes the other arities down with it.
    //
    // Policy:
    //   * Read-only except five: the rows are the read-only diagnostics above (the four probes, each
    //     also present under its legacy alias, the
    //     observation pair `alterum.test.assert-model`/`await-model`, the asset pair
    //     `alterum.test.assets`/`assert-assets`, the image proof `alterum.test.assert-image`, the
    //     runtime snapshot `alterum.test.snapshot` and the points table `alterum.test.points`),
    //     `alterum.test.point` (the gated named-point setup action: the game's own
    //     PlayerTeleportator.TeleportOnServer, host only), `alterum.test.open-wardrobe` (the gated
    //     setup action, the game's own WardrobeInputHandler.TryOpenWardrobe(); its legacy alias
    //     `test.open-wardrobe` is gated by the same row body, not by a second check),
    //     `alterum.test.close-wardrobe` (the gated close action, the same handler's own public
    //     WardrobeInputHandler.TryCloseWardrobe() - no view/field/GameObject/camera/cursor/EventSystem
    //     fallback),
    //     `alterum.test.select-model` (the gated selection verb: the wardrobe's own row for `ui`, the
    //     module's existing selection helper for `protocol`), `alterum.test.capture` (the gated
    //     one-frame capture of the CURRENT game camera view through ScreenShot.Capture - the same
    //     camera object, pose and settings, with targetTexture/RenderTexture.active restored and the
    //     RT + temporary texture released in a finally - which writes the PNG and reports its path,
    //     bytes and SHA-256), `alterum.test.capture-remote` (the
    //     gated proof verb: the module's one deterministic remote evidence path, which poses the
    //     game's camera for a frame, writes the PNG and reports its SHA-256) and
    //     `alterum.test.scenario` (the gated regression sequence: the same helpers and handlers the
    //     single verbs above call, run as a frame-driven state machine, ending in one manifest with
    //     step verdicts and artifact hashes). No row opens the console,
    //     executes a command, touches an actor or writes a file outside those mutations - the one
    //     read-only row that writes at all is `alterum.test.snapshot`, and its single write is its own
    //     manifest under the screens dir (exactly like `dump`'s qc-commands-live.txt).
    //   * Locked: rows are added ONLY once QcAccess says unlocked, so while the pause code has not
    //     been typed the mod contributes nothing at all to the command table (and a locked console
    //     therefore has no route into these handlers). After AEZAKMI they are ordinary table entries
    //     and execution stays where it was: QcGate's QuantumConsole.InvokeCommand() / processor seams.
    //     The setup actions are checked again inside the row body (defence in depth), so a row that
    //     outlived the gate still cannot open, close or move anything while locked. The selection row
    //     and the capture row are checked the same way, so neither can select or capture anything
    //     while locked.
    //     The read-only rows need no second check inside their handlers (they cannot write); the blanket
    //     unlocked check above still applies to every row, and the request transport keeps them available
    //     while locked.
    //   * Teardown: a row holds a delegate created in THIS assembly, so QcGate.Dispose removes every
    //     row it added (hot reload, `gateoff`) - the same rule PauseCode's text listener follows.
    //
    // Failure is contained and named: a build that refuses the API, the delegate conversion or a
    // single row logs one line with the reason and leaves the other rows and the request transport
    // alone. `status` prints the state (`native rows=...`). What the install itself observes is
    // logged too (each TryAddCommand result and the row count), so a session log says whether the
    // table really carries the rows; the first invocation is the only step left to a live session.

    private static IHostContext _testRowHost;
    internal static bool TryInvokeTestRow(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase row,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object> args)
    {
        if (row == null || _testRows == null || _testRowHost == null) return false;
        foreach (object entry in _testRows)
        {
            if (entry is not Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase owned || owned.Pointer != row.Pointer) continue;
            if (!QcAccess.IsUnlocked) { QcAccess.Deny("test-row", "qc-invoke"); return true; }
            string verb = Str(entry, "CommandName");
            var parts = new List<string>();
            if (args != null) for (int i = 0; i < args.Length; i++) parts.Add(args[i]?.ToString() ?? "");
            RunNativeTestVerb(_testRowHost, verb, string.Join(" ", parts));
            return true;
        }
        return false;
    }

    private static object[] _testRows;      // the rows this generation added, in install order
    private static bool _testRowsDone;      // the sync already ran for the live table
    private static string _testRowsWhy;     // why it did not (kept for `status`)
    private static float _testRowsRetryAt = -1f;   // 1 Hz retry until the table accepts the rows

    internal static string TestCommandsSummary =>
        _testRows != null
            ? $"native rows={_testRows.Length} (QC command table){(_testRowsWhy == null ? "" : $" {_testRowsWhy}")}"
            : $"native rows=none ({_testRowsWhy ?? "not attempted"})";

    // Idempotent, and cheap enough to call from every ensure confirm: one field read once the rows are
    // in. Call sites: the ensure confirm (a console that just became live), the pause-code unlock (the
    // table was live and confirmed before the gate opened), and the forced table rebuild.
    internal static void TestRowsSync(IHostContext host)
    {
        if (_testRowsDone || host == null) return;
        // The rows exist only behind the gate. Locked means the mod adds nothing to the table.
        if (!QcAccess.IsUnlocked) { _testRowsWhy = "locked"; return; }
        // Resolve quietly: this is retried once a second until the table is ready, and a missing
        // QFSW.QC type would otherwise print the same warning every second. One line, then stop.
        Resolve(null);
        if (_proc == null)
        {
            _testRowsDone = true;
            _testRowsWhy = "no processor type";
            host.LogWarning("[qc:test] native rows: QFSW.QC.QuantumConsoleProcessor not found in this build; " +
                            "the test verbs stay on the qc.request transport only");
            return;
        }
        // Both reads, both required: a row added before the table exists has nowhere to go, and one
        // added while the generator is still running can be overwritten when it publishes.
        if (!(GetStatic(_proc, "TableGenerated") is bool generated) || !generated) { _testRowsWhy = "table not generated"; return; }
        int have = LoadedCount();
        if (have <= 0) { _testRowsWhy = "table empty"; return; }
        _testRowsDone = true;
        try { InstallTestRows(host, have); }
        catch (Exception e)
        {
            _testRowsWhy = "install threw";
            host.LogWarning($"[qc:test] native rows: install threw {Describe(e)}; the qc.request transport is unaffected");
        }
    }

    private static void InstallTestRows(IHostContext host, int before)
    {
        Type lambdaType = FindType("QFSW.QC.LambdaCommandData");
        ConstructorInfo rowCtor = lambdaType == null
            ? null
            : FindCtor(lambdaType, typeof(Il2CppSystem.Delegate), typeof(string), typeof(string));
        MethodInfo add = FindMethod(_proc, "TryAddCommand", 1, includeNonPublic: false);
        if (rowCtor == null || add == null)
        {
            _testRowsWhy = lambdaType == null ? "no QFSW.QC.LambdaCommandData in this build"
                         : rowCtor == null ? "no LambdaCommandData(Delegate, string, string)"
                         : "no QuantumConsoleProcessor.TryAddCommand(CommandData)";
            host.LogWarning($"[qc:test] native rows: {_testRowsWhy}; the test verbs stay on the " +
                            "qc.request transport only");
            return;
        }

        // One row per (name, argument count) - the console's own resolution rule. Lists, not arrays:
        // every row is appended only if its own delegate converts, so a refused bridge cannot make the
        // table report claim a row that was never offered (see the per-row helpers below).
        //
        // Canonical `alterum.test.*` rows come first, then the five legacy spellings as
        // compatibility-only aliases. Both sets go through the same helpers and the same
        // `RunNativeTestVerb`, which canonicalizes the name before its switch: an alias is a second
        // NAME for one handler, never a second handler, a second gate, a second read or a second
        // mutation path.
        var names = new List<string>(25);
        var labels = new List<string>(25);
        var descriptions = new List<string>(25);
        var arities = new List<int>(25);
        var handlers = new List<object>(25);

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.pose", "alterum.test.pose",
            "Read-only: local actor pose (effective body + root), body modules and built bodies",
            () => RunNativeTestVerb(host, "alterum.test.pose", null));

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.remote-list", "alterum.test.remote-list",
            "Read-only: every remote actor this instance knows, plus RemoteModels' own census",
            () => RunNativeTestVerb(host, "alterum.test.remote-list", null));

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.identity", "alterum.test.identity",
            "Read-only: canonical model identity for the local actor",
            () => RunNativeTestVerb(host, "alterum.test.identity", null));

        // The 1-argument overload of the same command: QC resolves an overload by argument count, and
        // the documented `[clientId]` is exactly that.
        AddOneArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.identity", "alterum.test.identity <clientId>",
            "Read-only: canonical model identity for one known remote client id",
            clientId => RunNativeTestVerb(host, "alterum.test.identity", clientId));

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.wardrobe-list", "alterum.test.wardrobe-list",
            "Read-only: the wardrobe's own model database rows (index, slug/label, native/custom)",
            () => RunNativeTestVerb(host, "alterum.test.wardrobe-list", null));

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.open-wardrobe", "alterum.test.open-wardrobe",
            "Gated setup: open the native wardrobe via WardrobeInputHandler.TryOpenWardrobe (no selection)",
            () => RunNativeTestVerb(host, "alterum.test.open-wardrobe", null));

        // Compatibility aliases: the five spellings the layer first shipped. Compatibility-only -
        // the canonical spelling above is the one to type; these exist so an existing script, note or
        // habit keeps working. Same arity, same policy, same row body (their delegate canonicalizes
        // the name), so they cannot select, open, move or read anything the canonical row would not.
        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "test.pose", "test.pose",
            "Compatibility alias for alterum.test.pose (same read-only handler; prefer the canonical name)",
            () => RunNativeTestVerb(host, "test.pose", null));

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "test.remote-list", "test.remote-list",
            "Compatibility alias for alterum.test.remote-list (same read-only handler; prefer the canonical name)",
            () => RunNativeTestVerb(host, "test.remote-list", null));

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "test.identity", "test.identity",
            "Compatibility alias for alterum.test.identity (same read-only handler; prefer the canonical name)",
            () => RunNativeTestVerb(host, "test.identity", null));

        AddOneArgTestRow(host, names, labels, descriptions, arities, handlers,
            "test.identity", "test.identity <clientId>",
            "Compatibility alias for alterum.test.identity <clientId> (same read-only handler)",
            clientId => RunNativeTestVerb(host, "test.identity", clientId));

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "test.wardrobe-list", "test.wardrobe-list",
            "Compatibility alias for alterum.test.wardrobe-list (same read-only handler; prefer the canonical name)",
            () => RunNativeTestVerb(host, "test.wardrobe-list", null));

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "test.open-wardrobe", "test.open-wardrobe",
            "Compatibility alias for alterum.test.open-wardrobe (same gated handler; prefer the canonical name)",
            () => RunNativeTestVerb(host, "test.open-wardrobe", null));

        // The wardrobe close row: a gated setup action like `alterum.test.open-wardrobe`, converted on
        // its own (0-argument row) so a refused signature is one warning line, never a half-installed
        // table. Its handler calls the same scene handler's TryCloseWardrobe() the request verb does,
        // so the row cannot close anything through a different path.
        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.close-wardrobe", "alterum.test.close-wardrobe",
            "Gated setup: close the native wardrobe via WardrobeInputHandler.TryCloseWardrobe (the " +
            "native close lifecycle; no view/field/GameObject/camera/cursor/EventSystem fallback)",
            () => RunNativeTestVerb(host, "alterum.test.close-wardrobe", null));

        // The selection row's delegate is converted on its own, so a bridge that refuses it must not
        // take the rows above down with it - the request transport answers either way. The observation
        // pair below follows the same rule (one conversion per row).
        try
        {
            handlers.Add(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string, string>>(
                new Action<string, string>((slug, variant) =>
                    RunNativeTestVerb(host, "alterum.test.select-model", slug + " " + variant))));
            names.Add("alterum.test.select-model");
            labels.Add("alterum.test.select-model <slug> <ui|protocol>");
            descriptions.Add("Gated selection: press the open wardrobe's own row for <slug> (ui), or apply " +
                             "it through the module's existing selection path (protocol, host only)");
            arities.Add(2);
        }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] native row 'alterum.test.select-model': delegate bridge refused " +
                            $"({Describe(e)}); that row stays on the qc.request transport only");
        }

        // The general capture row takes one argument (the tag) and goes through the 1-argument helper:
        // arity is part of the contract and QC resolves an overload by argument count. Its handler is the
        // same one the request verb calls, it validates the tag with the same predicate, and it renders
        // through ScreenShot.Capture only, so the row cannot photograph anything through a different path.
        AddOneArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.capture", "alterum.test.capture <tag>",
            "Gated proof: one-frame capture of the current game camera view through ScreenShot.Capture " +
            "(same camera pose and settings, render state restored), then report the PNG artifact, its " +
            "byte count and its SHA-256 (no image assertion)",
            tag => RunNativeTestVerb(host, "alterum.test.capture", tag));

        // The capture verb is a gated mutation like the selection row above, and it takes two arguments
        // of its own (clientId, tag). It goes through the shared helper, so its conversion is
        // independent of every other row: a refused signature is one line, never a half-installed entry
        // and never a claim in `status` that the row exists. The handler re-resolves the remote and its
        // body, so the row cannot capture anything the request transport would refuse.
        AddTwoArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.capture-remote", "alterum.test.capture-remote <clientId> <tag>",
            "Gated proof: capture one known remote with a live body through the remote shot path, then " +
            "report the PNG artifact and its SHA-256 (no image assertion)",
            (clientId, tag) => RunNativeTestVerb(host, "alterum.test.capture-remote", clientId + " " + tag));

        // The pose report's row takes one argument (clientId), so it goes through the 1-argument helper:
        // arity is part of the contract and QC resolves an overload by argument count. Its handler is the
        // same one the request verb calls, and it re-resolves the remote, its live body and the shared
        // shot view, so a row that outlived the gate cannot report a pose either.
        AddOneArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.camera-remote", "alterum.test.camera-remote <clientId>",
            "Gated proof: report where the remote shot path would put the game's camera and what it " +
            "would aim at for one known remote with a live body (applies nothing, writes no artifact)",
            clientId => RunNativeTestVerb(host, "alterum.test.camera-remote", clientId));

        // The observation pair: both take two arguments (actor, slug) and are READ-ONLY. Each is
        // converted on its own, so a bridge that refuses one signature cannot take the rows above
        // down with it, and the aligned lists only ever gain a row the bridge actually converted.
        AddTwoArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.assert-model", "alterum.test.assert-model <clientId|local> <slug>",
            "Read-only: structured pass/fail of an actor's wanted/advertised/canonical/applied/body/" +
            "HUD/wardrobe/readiness against <slug> ('native' = the game's own row)",
            (actor, slug) => RunNativeTestVerb(host, "alterum.test.assert-model", actor + " " + slug));

        AddTwoArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.await-model", "alterum.test.await-model <clientId|local> <slug>",
            "Read-only: wait (frame tick, time/frame bounded) until an actor's identity is <slug>; " +
            "PASS/TIMEOUT is reported after this row returns",
            (actor, slug) => RunNativeTestVerb(host, "alterum.test.await-model", actor + " " + slug));

        // The image proof: READ-ONLY, like the rows above, and converted on its own. The handler
        // re-applies the whole path policy (relative under the screens dir, .png, existing plain file),
        // so a row that answered after the gate closed still cannot read an arbitrary file.
        AddTwoArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.assert-image", "alterum.test.assert-image <path> <maxMagentaPixels>",
            "Read-only: decode one PNG under the screens dir with PngDecoder, report SHA-256, dimensions, " +
            "pixel/magenta counts and PASS/FAIL against <maxMagentaPixels> (nothing is written)",
            (path, max) => RunNativeTestVerb(host, "alterum.test.assert-image", path + " " + max));

        // The asset diagnostics: zero arguments, READ-ONLY, converted one row at a time for the same
        // reason as the pair above (a refused signature must not take other rows down with it).
        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.assets", "alterum.test.assets",
            "Read-only: shared model asset caches (entries, users, idle/leased, retired state) and the " +
            "live body each entry backs",
            () => RunNativeTestVerb(host, "alterum.test.assets", null));

        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.assert-assets", "alterum.test.assert-assets",
            "Read-only: PASS/FAIL of the asset cache invariants (no destroyed leased asset, no duplicate " +
            "ownership, idle-only eviction, retired caches held until release, healthy live bodies)",
            () => RunNativeTestVerb(host, "alterum.test.assert-assets", null));

        // The runtime snapshot: READ-ONLY, and its syntax carries an OPTIONAL tag, so it is registered
        // twice - 0 and 1 arguments - exactly like `alterum.test.identity` above. Each arity is converted
        // on its own, so a refused `Action<string>` bridge cannot take the 0-argument row (or any other
        // row) down with it. The handler validates the tag with the same predicate that names a capture
        // artifact and writes its one manifest under the screens dir, so the row cannot write elsewhere.
        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.snapshot", "alterum.test.snapshot",
            "Read-only: write one structured runtime manifest under the screens dir (process/hot/scene/" +
            "pose/points/identity/remotes/assets/camera/report state) and report its path + SHA-256",
            () => RunNativeTestVerb(host, "alterum.test.snapshot", null));

        AddOneArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.snapshot", "alterum.test.snapshot <tag>",
            "Read-only: the same runtime manifest, named by <tag> (A-Z a-z 0-9 '-' '_', at most " +
            RemoteModels.MaxCaptureTag + " chars)",
            tag => RunNativeTestVerb(host, "alterum.test.snapshot", tag));

        // Named points: the table is READ-ONLY (0 arguments). Applying a point is a gated setup
        // mutation, so its 1-argument row is converted on its own like the other rows; the handler
        // re-checks the gate and server authority before anything moves.
        AddZeroArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.points", "alterum.test.points",
            "Read-only: the named test points (scene, standing coordinate, yaw, intent) and the live " +
            "preconditions an apply needs",
            () => RunNativeTestVerb(host, "alterum.test.points", null));

        AddOneArgTestRow(host, names, labels, descriptions, arities, handlers,
            "alterum.test.point", "alterum.test.point <name>",
            "Gated setup (server/host only): move the local player onto a named point through the game's " +
            "own PlayerTeleportator.TeleportOnServer; refuses when scene/player/authority is unavailable",
            name => RunNativeTestVerb(host, "alterum.test.point", name));


        _testRowHost = host;
        var rows = new List<object>(names.Count);
        var added = new List<string>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            try
            {
                object row = rowCtor.Invoke(new object[] { handlers[i], names[i], descriptions[i] });
                object accepted = add.Invoke(null, new object[] { row });
                if (accepted is bool ok && ok) { rows.Add(row); added.Add(labels[i]); continue; }
                host.LogWarning($"[qc:test] native row '{labels[i]}': TryAddCommand returned {Describe(accepted)} " +
                                "- that row is not in the console's table (other rows are unaffected)");
            }
            catch (Exception e)
            {
                host.LogWarning($"[qc:test] native row '{labels[i]}': {Describe(e)} " +
                                "(other rows and the qc.request transport are unaffected)");
            }
        }

        if (rows.Count == 0) { _testRowsWhy = "no row accepted"; return; }
        _testRows = rows.ToArray();
        _testRowsWhy = rows.Count < names.Count ? $"partial {rows.Count}/{names.Count}" : null;
        host.LogInfo($"[qc:test] native rows: {rows.Count}/{names.Count} added [{string.Join(", ", added)}] " +
                     $"LoadedCommandCount {before}->{LoadedCount()} (QcGate owns execution)");
        ReportTableRows(host, names, arities);
    }

    // One 2-argument read-only row: convert first, and append to the aligned lists only when the
    // bridge accepted the signature. A refused conversion is one line naming the row, never a
    // half-installed entry (names/labels/descriptions/arities/handlers stay index-aligned).
    private static void AddTwoArgTestRow(IHostContext host, List<string> names, List<string> labels,
        List<string> descriptions, List<int> arities, List<object> handlers,
        string name, string label, string description, Action<string, string> action)
    {
        object handler;
        try { handler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string, string>>(action); }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] native row '{label}': delegate bridge refused ({Describe(e)}); " +
                            "that row stays on the qc.request transport only");
            return;
        }
        handlers.Add(handler);
        names.Add(name);
        labels.Add(label);
        descriptions.Add(description);
        arities.Add(2);
    }

    // One 0-argument row, converted on its own: same contract as AddTwoArgTestRow (a refused bridge
    // is one line naming the row, and the aligned lists only gain a row that was converted). Both the
    // read-only diagnostics and the gated wardrobe close action use this shape.
    private static void AddZeroArgTestRow(IHostContext host, List<string> names, List<string> labels,
        List<string> descriptions, List<int> arities, List<object> handlers,
        string name, string label, string description, Action action)
    {
        object handler;
        try { handler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(action); }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] native row '{label}': delegate bridge refused ({Describe(e)}); " +
                            "that row stays on the qc.request transport only");
            return;
        }
        handlers.Add(handler);
        names.Add(name);
        labels.Add(label);
        descriptions.Add(description);
        arities.Add(0);
    }

    // One 1-argument row, converted on its own for the same reason: arity is part of the contract, so
    // a bridge that refuses Action<string> must not disturb the 0- or 2-argument rows (or the request
    // transport). `alterum.test.point <name>` is the row this exists for.
    private static void AddOneArgTestRow(IHostContext host, List<string> names, List<string> labels,
        List<string> descriptions, List<int> arities, List<object> handlers,
        string name, string label, string description, Action<string> action)
    {
        object handler;
        try { handler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(action); }
        catch (Exception e)
        {
            host.LogWarning($"[qc:test] native row '{label}': delegate bridge refused ({Describe(e)}); " +
                            "that row stays on the qc.request transport only");
            return;
        }
        handlers.Add(handler);
        names.Add(name);
        labels.Add(label);
        descriptions.Add(description);
        arities.Add(1);
    }

    // Post-install proof, not a wish: enumerate the processor's own table (the same read `sigs` does)
    // and say which expected (name, argument count) rows it now holds. TryAddCommand's bool reports what
    // the processor decided; this reports what the table contains - together they answer "are these
    // commands genuinely registered" without trusting either alone.
    private static void ReportTableRows(IHostContext host, List<string> names, List<int> arities)
    {
        var want = new List<string>(names.Count);
        for (int i = 0; i < names.Count; i++) want.Add(names[i] + "/" + arities[i]);
        var found = new List<string>(names.Count);
        try
        {
            object all = FindMethod(_proc, "GetAllCommands", 0, includeNonPublic: false)?.Invoke(null, null);
            var en = all?.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(all, null);
            var moveNext = en?.GetType().GetMethod("MoveNext", Type.EmptyTypes);
            var current = en?.GetType().GetProperty("Current");
            if (en == null || moveNext == null || current == null)
            {
                host.LogWarning("[qc:test] native rows: cannot enumerate the processor's table (GetAllCommands)");
                return;
            }
            // Il2Cpp generics are not System.Collections.IEnumerable, so the enumerator is driven
            // reflectively - exactly as `sigs` reads the same table.
            while (true)
            {
                object more;
                try { more = moveNext.Invoke(en, null); }
                catch (Exception e) { host.LogWarning($"[qc:test] native rows: table MoveNext threw {Describe(e)}"); break; }
                if (!(more is bool) || !(bool)more) break;
                object cmd;
                try { cmd = current.GetValue(en); } catch { continue; }
                if (cmd == null) continue;
                string name = Str(cmd, "CommandName");
                // The test layer's own namespaces: `alterum.test.*` (canonical rows: probes, observation
                // pair, asset diagnostics, points table/action, selection verb, wardrobe open/close and
                // the capture verb) and `test.*` (the five compatibility aliases). Both are wanted rows,
                // and each is checked at its own (name, argument count), so an alias that failed to
                // convert or register is reported missing by name instead of hiding behind its canonical
                // twin.
                if (name == null || !(name.StartsWith("alterum.test.") || name.StartsWith("test."))) continue;
                string key = name + "/" + Str(cmd, "ParamCount");
                if (want.Contains(key) && !found.Contains(key)) found.Add(key);
            }
        }
        catch (Exception e) { host.LogWarning($"[qc:test] native rows: table read threw {Describe(e)}"); return; }

        var missing = new List<string>(want.Count);
        for (int i = 0; i < want.Count; i++) if (!found.Contains(want[i])) missing.Add(want[i]);
        if (missing.Count == 0)
        {
            host.LogInfo($"[qc:test] native rows in the table: {found.Count}/{want.Count} [{string.Join(", ", found)}]");
            return;
        }
        _testRowsWhy = $"table shows {found.Count}/{want.Count}";
        host.LogWarning($"[qc:test] native rows: the processor's table shows {found.Count}/{want.Count}; " +
                        $"missing {string.Join(", ", missing)} (the qc.request transport still answers)");
    }

    // GenerateCommandTable(forceReload: true) replaces the table wholesale, rows included. Drop the
    // bookkeeping without TryRemoveCommand (those rows are already gone) so the next sync re-adds them.
    internal static void TestRowsForget()
    {
        _testRows = null;
        _testRowsDone = false;
        _testRowsWhy = "table rebuild";
    }

    // Called from QcGate.Dispose (hot reload, `gateoff`), before this assembly is unloaded. Returns the
    // number of rows the processor agreed to drop; 0 means there was nothing to remove (or the build
    // has no TryRemoveCommand), which is a log line at the call site, never a throw on teardown.
    internal static int TestRowsTeardown()
    {
        _testRowsDone = false;
        _testRowsWhy = "generation teardown";
        var rows = _testRows;
        _testRows = null;
        if (rows == null || rows.Length == 0) return 0;
        var remove = FindMethod(_proc, "TryRemoveCommand", 1, includeNonPublic: false);
        if (remove == null)
        {
            QcAccess.Note($"test rows: no TryRemoveCommand(CommandData) in this build; " +
                          $"{rows.Length} row(s) stay in the console's table");
            return 0;
        }
        int removed = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            try { if (remove.Invoke(null, new object[] { rows[i] }) is bool ok && ok) removed++; }
            catch { }
        }
        if (removed < rows.Length)
            QcAccess.Note($"test rows: {rows.Length - removed} of {rows.Length} row(s) stayed in the " +
                          "console's table (TryRemoveCommand refused)");
        return removed;
    }

    // The body of a native row: the SAME methods the request verbs call, through a sink that forwards
    // every line to the log and collects it for the console panel, so the two transports cannot
    // disagree. Never throws into il2cpp's reflection call.
    private static void RunNativeTestVerb(IHostContext host, string verb, string arg)
    {
        // Defence in depth: rows are only installed while unlocked, but a row that outlived the gate
        // (a teardown that could not remove it) must still not run while the console is locked.
        if (!QcAccess.IsUnlocked)
        {
            try { host?.LogInfo("[qc:test] native row refused: console is locked (type AEZAKMI in the pause menu)"); } catch { }
            return;
        }
        // Compatibility aliases resolve to their canonical verb HERE, before the switch: a legacy row
        // and its canonical row then execute the same case arm - one handler, one gate, one read. The
        // gate above is deliberately before this mapping, so both spellings are refused identically.
        verb = CanonicalTestVerb(verb);
        try
        {
            var sink = new ConsoleSink(host);
            switch (verb)
            {
                case "alterum.test.pose": TestPose(sink); break;
                case "alterum.test.remote-list": TestRemoteList(sink); break;
                case "alterum.test.identity": TestIdentity(sink, arg ?? ""); break;
                case "alterum.test.wardrobe-list": TestWardrobeList(sink); break;
                // The observation pair: same handlers the request verbs call. `await-model` returns
                // after registering the wait; its PASS/TIMEOUT lines are emitted by the frame tick.
                case "alterum.test.assert-model": TestAssertModel(sink, arg ?? ""); break;
                case "alterum.test.await-model": TestAwaitModel(sink, arg ?? ""); break;
                // The asset diagnostics: the same read-only handlers the request verbs call (they take
                // no argument and mutate nothing).
                case "alterum.test.assets": TestAssets(sink); break;
                case "alterum.test.assert-assets": TestAssertAssets(sink); break;
                // The image proof: the same read-only handler the request verb calls. Its own path
                // policy is what keeps it inside the screens dir, on both transports.
                case "alterum.test.assert-image": TestAssertImage(sink, arg ?? ""); break;
                // The runtime snapshot: the same read-only handler the request verb calls. Its own tag
                // validation and screens-dir confinement keep the single artifact inside the screens
                // dir, so a row that outlived the gate still cannot write anywhere else.
                case "alterum.test.snapshot": TestSnapshot(sink, arg ?? ""); break;
                // Named points: the table is read-only; applying one is the gated setup handler, and
                // the gate was checked above. Its own server-authority/scene/player checks still run,
                // so a row that outlived the gate cannot move anything the request path would refuse.
                case "alterum.test.points": TestPoints(sink); break;
                case "alterum.test.point": TestPoint(sink, arg ?? ""); break;
                // The gate was checked above; these are the same handlers the request verbs call, so
                // the rows cannot open or close anything the request transport would refuse. The open
                // row's compatibility alias `test.open-wardrobe` was already mapped to this verb, so
                // one arm covers both spellings.
                case "alterum.test.open-wardrobe": TestOpenWardrobe(sink); break;
                case "alterum.test.close-wardrobe": TestCloseWardrobe(sink); break;
                // The selection row: the gate was checked above, so a row that outlived the gate still
                // cannot select anything while locked. `<slug> <ui|protocol>` arrives as one argument
                // (the two delegate parameters are joined here) and is parsed by the same handler the
                // request verb calls.
                case "alterum.test.select-model": TestSelectModel(sink, arg ?? ""); break;
                // The general capture row: the same handler the request verb calls, so the row cannot render
                // or write anything the request transport would refuse. The whole render path is
                // ScreenShot.Capture (one frame, current pose, targetTexture/RT restored in a finally).
                case "alterum.test.capture": TestCapture(sink, arg ?? ""); break;
                // The capture row: the gate was checked above, and the handler re-resolves the client id,
                // the known remote and its live body, so a row that outlived the gate cannot capture
                // anything the request transport would refuse either.
                case "alterum.test.capture-remote": TestCaptureRemote(sink, arg ?? ""); break;
                // The proof family's pose report: the same handler the request verb calls. It re-resolves
                // the client id, the known remote, its live body and the shared shot view, so the row
                // cannot report a pose the request transport would refuse.
                case "alterum.test.camera-remote": TestCameraRemote(sink, arg ?? ""); break;

                default: sink.LogWarning($"[qc:test] native row: unknown verb '{verb}'"); break;
            }
            PrintToConsole(host, sink.Lines);
        }
        catch (Exception e)
        {
            try { host?.LogWarning($"[qc:test] native '{verb}' threw {Describe(e)} (the console stays usable)"); } catch { }
        }
    }

    // The layer's ONE alias table: a legacy `test.*` spelling -> its canonical `alterum.test.*` verb.
    // Anything else (already canonical, or unrelated) is returned unchanged. The native rows resolve
    // through this (RunNativeTestVerb, before its switch) and the request dispatcher encodes the same
    // mapping as stacked case labels, which keeps the typed spelling for its own refusal line - either
    // way a legacy name can never reach a different handler, gate or arity than its canonical form.
    // There is no reverse mapping and no second table.
    private static string CanonicalTestVerb(string verb) => verb switch
    {
        "test.pose" => "alterum.test.pose",
        "test.identity" => "alterum.test.identity",
        "test.remote-list" => "alterum.test.remote-list",
        "test.wardrobe-list" => "alterum.test.wardrobe-list",
        "test.open-wardrobe" => "alterum.test.open-wardrobe",
        _ => verb,
    };

    // The console's own panel, not a second renderer: one LogToConsole per collected line. The log
    // already has them (the sink forwards), so a missing/dead instance degrades to the log only.
    private static void PrintToConsole(IHostContext host, List<string> lines)
    {
        if (lines == null || lines.Count == 0) return;
        var c = LiveInstance();
        var print = c == null ? null : FindMethod(_qc, "LogToConsole", 2, includeNonPublic: false);
        if (print == null)
        {
            host?.LogInfo($"[qc:test] native: {lines.Count} line(s) in the log only (no live console instance to print to)");
            return;
        }
        int printed = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            try { print.Invoke(c, new object[] { lines[i], true }); printed++; }
            catch (Exception e)
            {
                host?.LogWarning($"[qc:test] native: LogToConsole threw {Describe(e)} after {printed} line(s)");
                return;
            }
        }
        host?.LogInfo($"[qc:test] native: {printed} line(s) printed in the console");
    }

    private static ConstructorInfo FindCtor(Type t, params Type[] signature)
    {
        if (t == null) return null;
        try
        {
            return t.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, signature, null);
        }
        catch { return null; }
    }

    // Forwards everything to the host the mod already logs through, and keeps the same lines for the
    // console panel. Pure pass-through: the diagnostics read config/net/scene state exactly as they do
    // on the request path.
    private sealed class ConsoleSink : IHostContext
    {
        private readonly IHostContext _host;
        private readonly List<string> _lines = new(32);

        internal ConsoleSink(IHostContext host) { _host = host; }

        internal List<string> Lines => _lines;

        public ConfigSnapshot Config => _host.Config;
        public string ConfigPath => _host.ConfigPath;
        public string ScreensDir => _host.ScreensDir;
        public string InstanceName => _host.InstanceName;
        public bool ReloadConfig() => _host.ReloadConfig();
        public INetBridge Net => _host.Net;

        public void LogInfo(string message) => Relay(message, 0);
        public void LogWarning(string message) => Relay(message, 1);
        public void LogError(string message) => Relay(message, 2);

        private void Relay(string message, int level)
        {
            if (level == 0) _host?.LogInfo(message);
            else if (level == 1) _host?.LogWarning(message);
            else _host?.LogError(message);
            if (message != null) _lines.Add(message);
        }
    }

    // The local player, resolved exactly the way the mirror/body modules resolve it (provider first,
    // then the Netcode-owned Player), so `alterum.test.pose` names the same object they drive. The
    // two guards
    // below are part of that contract, not decoration: a prefab asset has an EMPTY scene name and an
    // owned-but-deactivated Player (scene teardown, a leaving actor) is not the live actor, and
    // ReelLogic.FindLocalPlayerEntity - the resolution every body module uses - rejects both. Without
    // them this verb would report a dead/off-scene object as the player the test is looking at.
    private static Gameplay.PlayerSystem.Player FindLocalPlayer(IHostContext host, out string via)
    {
        via = "none";
        try
        {
            var provs = SceneCache.Scan(Il2CppType.Of<Gameplay.PlayerSystem.CurrentPlayerProvider>());
            for (int i = 0; provs != null && i < provs.Length; i++)
            {
                var p = provs[i].TryCast<Gameplay.PlayerSystem.CurrentPlayerProvider>();
                var player = p?.CurrentPlayer;
                if (player == null || player.gameObject == null) continue;
                if (string.IsNullOrEmpty(player.gameObject.scene.name)) continue;
                via = "CurrentPlayerProvider";
                return player;
            }
        }
        catch (Exception e) { host.LogWarning($"[qc:test] CurrentPlayerProvider failed: {e.Message}"); }

        try
        {
            var players = SceneCache.Scan(Il2CppType.Of<Gameplay.PlayerSystem.Player>());
            for (int i = 0; players != null && i < players.Length; i++)
            {
                var p = players[i].TryCast<Gameplay.PlayerSystem.Player>();
                if (p == null || !p.IsOwner || p.gameObject == null) continue;
                if (string.IsNullOrEmpty(p.gameObject.scene.name)) continue;
                if (!p.gameObject.activeInHierarchy) continue;
                via = "IsOwner";
                return p;
            }
        }
        catch { }
        return null;
    }

    // Is this client id the one the bridge registered for THIS instance? Only a registered bridge can
    // answer: before registration `LocalClientId` is 0, which is also the server's id, and treating that
    // as "this is me" would mislabel a real client 0 (the host, from a peer's point of view).
    private static bool IsLocalClientId(IHostContext host, ulong clientId)
    {
        try
        {
            var net = host.Net;
            if (net == null) return false;
            var st = net.Status;
            return st.State == NetState.Registered && st.LocalClientId == clientId;
        }
        catch { return false; }
    }

    private static string SafeOwner(Gameplay.PlayerSystem.Player p)
    {
        try { return p.OwnerClientId.ToString(); } catch { return "?"; }
    }

    private static bool SafeActiveGo(GameObject go)
    {
        try { return go != null && go.activeInHierarchy; } catch { return false; }
    }

    private static bool SafeActiveSelf(Component c)
    {
        try { return c.gameObject.activeSelf; } catch { return false; }
    }

    private static object CallPrivate(object target, string method, int argc)
    {
        if (target == null) return "<null target>";
        var m = FindMethod(target.GetType(), method, argc);
        if (m == null) return "<no method>";
        try { return m.Invoke(target, null); }
        catch (Exception e) { return "<threw " + Describe(e) + ">"; }
    }

    // The command table only exists as one generated string in this QC version
    // (QuantumConsoleProcessor.GenerateCommandList is private static), so the dump goes to a file
    // and the log carries the counts plus any give/quest-looking lines.
    private static void Dump(IHostContext host)
    {
        Resolve(host);
        string text = null;
        try
        {
            var m = FindMethod(_proc, "GenerateCommandList", 0) ??
                    FindMethod(_proc, "GetCommandList", 0);
            if (m != null) text = m.Invoke(null, null) as string;
        }
        catch (Exception e) { host.LogWarning($"[qc] command list threw: {Describe(e)}"); }

        int count = LoadedCount();
        host.LogInfo($"[qc] command table: LoadedCommandCount={count} TableGenerated={GetStatic(_proc, "TableGenerated")}");
        if (string.IsNullOrEmpty(text)) { host.LogWarning("[qc] command list is empty"); return; }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        host.LogInfo($"[qc] command list: {lines.Length} lines, {text.Length} chars");
        try
        {
            string outPath = Path.Combine(host.ScreensDir, "qc-commands-live.txt");
            File.WriteAllText(outPath, text);
            host.LogInfo($"[qc] wrote {outPath}");
        }
        catch (Exception e) { host.LogWarning($"[qc] could not write command list: {e.Message}"); }

        int shown = 0;
        for (int i = 0; i < lines.Length && shown < 200; i++)
        {
            string l = lines[i].Trim();
            if (l.Length == 0) continue;
            if (Interesting(l)) { host.LogInfo($"[qc] cmd {l}"); shown++; }
        }
    }

    // Why `enabled = true` reads back false: write it twice, on the typed wrapper and through the
    // GameObject, and compare with a sibling component that nothing is expected to touch.
    private static void Probe(IHostContext host)
    {
        if (RefuseLocked("probe")) return;
        Resolve(host);
        var consoles = FindConsoles();
        if (consoles.Count == 0) { host.LogWarning("[qc] probe: no console"); return; }
        var c = consoles[0];
        var b = c.TryCast<Behaviour>();
        host.LogInfo($"[qc] probe: wrapper={c.GetType().FullName} isBehaviour={b != null}");
        if (b == null) return;
        host.LogInfo($"[qc] probe: enabled[0]={b.enabled}");
        b.enabled = true;
        host.LogInfo($"[qc] probe: enabled[1]={b.enabled}");
        b.enabled = true;
        host.LogInfo($"[qc] probe: enabled[2]={b.enabled} Instance={Describe(GetStatic(_qc, "Instance"))}");
        // Control: a sibling behaviour that nothing disables.
        try
        {
            var sib = c.gameObject.GetComponent(Il2CppType.Of<UnityEngine.UI.GraphicRaycaster>())?.TryCast<Behaviour>();
            if (sib != null)
            {
                host.LogInfo($"[qc] probe: control GraphicRaycaster enabled[0]={sib.enabled}");
                sib.enabled = false;
                host.LogInfo($"[qc] probe: control after false={sib.enabled}");
                sib.enabled = true;
                host.LogInfo($"[qc] probe: control after true={sib.enabled}");
            }
        }
        catch (Exception e) { host.LogWarning($"[qc] probe: control threw {Describe(e)}"); }
        host.LogInfo($"[qc] probe: calling Activate()");
        Call(c, "Activate", 0);
        host.LogInfo($"[qc] probe: IsActive={GetProperty(c, "IsActive")} enabled={b.enabled} " +
                     $"Instance={Describe(GetStatic(_qc, "Instance"))}");
        ProcStatus(host);
    }

    // `man <command>` is a console built-in and the console never initializes, so the manual comes
    // from the processor's own GenerateCommandManual(string) (private static in this build).
    private static void Sig(IHostContext host, string command)
    {
        Resolve(host);
        if (command.Length == 0) { host.LogWarning("[qc] sig: no command name"); return; }
        var m = FindMethod(_proc, "GenerateCommandManual", 1);
        if (m == null) { host.LogWarning("[qc] GenerateCommandManual(string) not found"); return; }
        try { host.LogInfo($"[qc] sig {command}: {Describe(m.Invoke(null, new object[] { command }))}"); }
        catch (Exception e) { host.LogWarning($"[qc] sig '{command}' threw: {Describe(e)}"); }
    }

    // CommandData.CommandSignature for every give/quest/goto-ish command, straight out of the
    // processor's own table (GetAllCommands is public static).
    private static void Sigs(IHostContext host)
    {
        Resolve(host);
        var m = FindMethod(_proc, "GetAllCommands", 0);
        if (m == null) { host.LogWarning("[qc] GetAllCommands() not found"); return; }
        object all;
        try { all = m.Invoke(null, null); }
        catch (Exception e) { host.LogWarning($"[qc] GetAllCommands threw: {Describe(e)}"); return; }
        if (all == null) { host.LogWarning("[qc] GetAllCommands returned null"); return; }

        // The Il2Cpp generic IEnumerable is not a System.Collections.IEnumerable, so the
        // enumerator is driven reflectively: GetEnumerator -> MoveNext -> Current.
        int n = 0;
        try
        {
            var en = all.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(all, null);
            if (en == null) { host.LogWarning("[qc] sigs: no GetEnumerator"); return; }
            var moveNext = en.GetType().GetMethod("MoveNext", Type.EmptyTypes);
            var current = en.GetType().GetProperty("Current");
            if (moveNext == null || current == null) { host.LogWarning("[qc] sigs: enumerator has no MoveNext/Current"); return; }
            while (true)
            {
                object more;
                try { more = moveNext.Invoke(en, null); } catch (Exception e) { host.LogWarning($"[qc] sigs MoveNext threw: {Describe(e)}"); break; }
                if (!(more is bool) || !(bool)more) break;
                object cmd;
                try { cmd = current.GetValue(en); } catch { continue; }
                if (cmd == null) continue;
                string name = Str(cmd, "CommandName");
                string sig = Str(cmd, "CommandSignature");
                if (name == null) continue;
                if (!Interesting(name)) continue;
                host.LogInfo($"[qc] cmd {name}{sig}");
                if (++n >= 60) break;
            }
        }
        catch (Exception e) { host.LogWarning($"[qc] enumerate threw: {Describe(e)}"); }
        host.LogInfo($"[qc] sigs listed={n}");
    }

    private static string Str(object o, string member)
    {
        var v = GetProperty(o, member);
        return v == null ? "" : v.ToString();
    }

    // Explicit local developer transport: exec <request-id> <QC command>. Filesystem access to
    // this instance is the trust boundary; no network endpoint, keystroke, pause menu, or persistent
    // unlock. Always use QC's real parser/processor, with the same native-row invocation bridge.
    private static void ExecuteDeveloperCommand(IHostContext host, string arg)
    {
        int space = arg.IndexOf(' ');
        if (space <= 0 || !Guid.TryParseExact(arg.Substring(0, space), "N", out var requestId))
            throw new ArgumentException("exec requires a 32-hex request id followed by a console command");
        string id = requestId.ToString("N");
        string command = arg.Substring(space + 1).Trim();
        host.LogInfo($"[qc:direct] id={id} state=begin");
        try
        {
            if (command.Length == 0) throw new ArgumentException("empty console command");
            QcAccess.WithDeveloperCommand(() =>
            {
                Resolve(host);
                TestRowsSync(host);
                if (LoadedCount() <= 0) throw new InvalidOperationException("QC command table is not ready; retry after scene initialization");
                var invoke = FindMethod(_proc, "InvokeCommand", 1);
                if (invoke == null) throw new MissingMethodException("QuantumConsoleProcessor.InvokeCommand(string)");
                object result = invoke.Invoke(null, new object[] { command });
                string output = result is Il2CppSystem.Object native &&
                                native.GetIl2CppType()?.FullName == "System.String"
                    ? IL2CPP.Il2CppStringToManaged(native.Pointer)
                    : result?.ToString() ?? "null";
                host.LogInfo($"[qc:direct] id={id} result={output}");
            });
            // Dispatch complete is not an assertion that an asynchronous game action succeeded.
            host.LogInfo($"[qc:direct] id={id} state=dispatched playerAccess={QcAccess.Summary}");
        }
        catch (Exception e)
        {
            host.LogWarning($"[qc:direct] id={id} state=error error={Describe(e)} playerAccess={QcAccess.Summary}");
        }
    }

    private static void Invoke(IHostContext host, string command)
    {
        if (RefuseLocked("invoke")) return;
        Resolve(host);
        object instance = GetStatic(_qc, "Instance");
        if (instance == null)
        {
            var sm = FindMethod(_proc, "InvokeCommand", 1);
            if (sm == null) { host.LogWarning("[qc] invoke: Instance is null and no static InvokeCommand"); return; }
            try
            {
                object r = sm.Invoke(null, new object[] { command });
                host.LogInfo($"[qc] invoke '{command}' (static processor) -> {Describe(r)}");
            }
            catch (Exception e) { host.LogWarning($"[qc] invoke '{command}' threw: {Describe(e)}"); }
            return;
        }
        object result = Call(instance, "InvokeCommand", 1, command);
        host.LogInfo($"[qc] invoke '{command}' -> {Describe(result)}");
    }

    // The one place that knows how to get a *typed* QuantumConsole proxy. Everything that touches
    // the console - the ensure path, Tilde and the request verbs - goes through here, and what
    // comes back is either a proxy whose managed type IS QFSW.QC.QuantumConsole or null. There is
    // no untyped fallback: an untyped proxy cannot be assigned to Instance and silently has no
    // members to reflect over.
    internal static Component TypedConsole(IHostContext host, out string diag)
    {
        diag = "";
        Resolve(host);
        if (_qc == null) { diag = "no QFSW.QC.QuantumConsole type"; return null; }
        return PickConsole(host, "typed", false, out diag);
    }

    // ------------------------------------------------------------------ typed acquisition
    //
    // The trap this file has to stay out of: GetComponents<Component>() AND GetComponent(Il2CppType)
    // can both hand back a bare UnityEngine.Component wrapper even when the il2cpp object underneath
    // is a QuantumConsole (measured: on the very first attempt after a fresh launch the same
    // GetComponent(Il2CppType) call returns UnityEngine.Component, and on the next generation, with
    // nothing else changed, it returns QFSW.QC.QuantumConsole). Such a proxy has no `enabled`,
    // no `Initialize`, no `KeyConfig`, and assigning it to Instance throws
    //   ArgumentException: Object of type 'UnityEngine.Component' cannot be converted to type
    //   'QFSW.QC.QuantumConsole'
    // which leaves Instance=null and the command table empty. AsTyped() below removes the
    // dependency on the interop layer having warmed up: it rebuilds the proxy from the il2cpp
    // object pointer with the class the QFSW.QC interop assembly defines.

    private struct ConsoleCandidate
    {
        internal string Name, Path, RawType, How;
        internal bool SceneValid, GoActiveSelf, ActiveInHierarchy, Enabled;
        internal Component Typed;
    }

    private static Component AsTyped(Component c, out string how)
    {
        how = "null";
        if (c == null) return null;
        string raw;
        try { raw = c.GetType().FullName; } catch { raw = "?"; }
        if (raw == QcTypeName) { how = "already-typed"; return c; }
        if (_qc == null) { how = $"no type for untyped({raw})"; return null; }
        IntPtr ptr = IntPtr.Zero;
        try { ptr = c.Pointer; } catch { }

        // 1) A new proxy of the real class over the same il2cpp object: `new QuantumConsole(ptr)`,
        //    which is the typed reference the game's own members accept.
        if (ptr != IntPtr.Zero)
        {
            try
            {
                object made = Activator.CreateInstance(_qc, new object[] { ptr });
                var mc = made as Component;
                if (mc != null && mc.GetType().FullName == QcTypeName) { how = $"new {_qc.Name}(ptr) from untyped({raw})"; return mc; }
                how = $"new {_qc.Name}(ptr) gave {(made == null ? "null" : made.GetType().FullName)}";
            }
            catch (Exception e) { how = $"new {_qc.Name}(ptr) threw {e.GetType().Name}: {e.Message}"; }
        }

        // 2) Il2CppObjectBase.TryCast<T>() with the resolved class as the type argument.
        try
        {
            MethodInfo best = null;
            var bt = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase);
            foreach (var m in bt.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { best = m; break; }
            if (best != null)
            {
                var typed = best.MakeGenericMethod(_qc).Invoke(c, null) as Component;
                if (typed != null && typed.GetType().FullName == QcTypeName) { how = $"TryCast<{_qc.Name}>() from untyped({raw})"; return typed; }
                how += "; TryCast gave " + (typed == null ? "null" : typed.GetType().FullName);
            }
            else how += "; no TryCast<T>";
        }
        catch (Exception e) { how += $"; TryCast threw {e.GetType().Name}"; }
        return null;
    }

    private static List<ConsoleCandidate> ScanConsoles(out int transforms)
    {
        var list = new List<ConsoleCandidate>(8);
        transforms = 0;
        if (_qc == null) return list;
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Transform>());
            if (all == null) return list;
            transforms = all.Length;
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i].TryCast<Transform>();
                if (t == null) continue;
                GameObject go;
                try { go = t.gameObject; } catch { continue; }
                if (go == null) continue;
                string n;
                try { n = go.name; } catch { continue; }
                if (n == null || !n.StartsWith("Quantum Console")) continue;

                Component raw = null;
                string rawType = null;
                try
                {
                    var comps = go.GetComponents<Component>();
                    for (int k = 0; comps != null && k < comps.Length; k++)
                    {
                        var comp = comps[k];
                        if (comp == null) continue;
                        string full = null;
                        // The il2cpp class, not GetType(): the wrapper may be an untyped Component.
                        try { full = comp.GetIl2CppType()?.FullName; } catch { }
                        if (full != QcTypeName) continue;
                        raw = comp;
                        try { rawType = comp.GetType().FullName; } catch { rawType = "?"; }
                        break;
                    }
                }
                catch { }
                if (raw == null) continue;

                var cand = new ConsoleCandidate { Name = n, Path = PathOf(raw), RawType = rawType };
                try { cand.SceneValid = go.scene.IsValid(); } catch { }
                try { cand.GoActiveSelf = go.activeSelf; } catch { }
                try { cand.ActiveInHierarchy = go.activeInHierarchy; } catch { }
                try { var b = raw.TryCast<Behaviour>(); cand.Enabled = b != null && b.enabled; } catch { }
                string how;
                cand.Typed = AsTyped(raw, out how);
                cand.How = how;
                list.Add(cand);
            }
        }
        catch { }
        return list;
    }

    // The only selection rule: an ACTIVE console in a loaded scene. A console object that is not
    // active in its hierarchy never ran Awake, so its Instance cannot be assigned and its UI cannot
    // be shown - the menu's dormant 'Quantum Console (SRP)' root is reported by the trace and left
    // untouched. `trace` prints every candidate with the type its wrapper actually has, which is
    // what distinguishes "not present yet" from "present but untyped".
    private static Component PickConsole(IHostContext host, string why, bool trace, out string diag)
    {
        var cands = ScanConsoles(out int transforms);
        diag = $"transforms={transforms} candidates={cands.Count} scene='{ActiveSceneName()}'";
        Component pick = null;
        string pickWhy = "none";
        for (int i = 0; i < cands.Count && pick == null; i++)
            if (cands[i].Typed != null && cands[i].ActiveInHierarchy && cands[i].SceneValid)
            { pick = cands[i].Typed; pickWhy = $"active+scene '{cands[i].Name}' via {cands[i].How}"; }
        for (int i = 0; i < cands.Count && pick == null; i++)
            if (cands[i].Typed != null && cands[i].ActiveInHierarchy)
            { pick = cands[i].Typed; pickWhy = $"active '{cands[i].Name}' via {cands[i].How}"; }

        if (trace)
        {
            host.LogInfo($"[qc] {why}: scan at {Stamp()} {diag}");
            for (int i = 0; i < cands.Count; i++)
            {
                var cc = cands[i];
                host.LogInfo($"[qc] {why}:   [{i}] '{cc.Name}' path='{cc.Path}' scene={cc.SceneValid} goActive={cc.GoActiveSelf} activeInHierarchy={cc.ActiveInHierarchy} " +
                             $"enabled={cc.Enabled} wrapper={cc.RawType} typed={(cc.Typed == null ? "NO" : "yes")} via={cc.How}");
            }
        }
        if (pick == null) { diag += " no-active-typed-console"; return null; }
        diag += $" pick={pickWhy} wrapper={pick.GetType().FullName}";
        return pick;
    }

    private static string ActiveSceneName()
    {
        try
        {
            var s = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return $"{s.name}#{s.handle} loaded={s.isLoaded}";
        }
        catch { return "?"; }
    }

    // Wall clock plus the same clock the frame loop uses: "which scene, when" is the whole
    // cold-start question, and the instance log's own timestamp alone cannot order a scene load
    // against a frame.
    private static string Stamp()
    {
        string t = "?", f = "?";
        try { t = Time.realtimeSinceStartup.ToString("0.000"); f = Time.frameCount.ToString(); } catch { }
        return $"t={t}s f={f} now={DateTime.Now:HH:mm:ss.fff}";
    }

    private static Component ReferenceFrom(string typeName, string member)
    {
        try
        {
            var t = FindType(typeName);
            if (t == null) return null;
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.From(t));
            if (all == null) return null;
            for (int i = 0; i < all.Length; i++)
            {
                var obj = all[i];
                if (obj == null) continue;
                var prop = t.GetProperty(member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                var val = prop != null ? prop.GetValue(obj) : null;
                var comp = val as Component;
                if (comp != null && comp.GetType().FullName == QcTypeName) return comp;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ QC surface

    private static void Resolve(IHostContext host)
    {
        if (_qc == null) _qc = FindType(QcTypeName);
        if (_proc == null) _proc = FindType(ProcTypeName);
        if (_qc == null) host?.LogWarning($"[qc] type {QcTypeName} not found in any loaded assembly");
        if (_proc == null) host?.LogWarning($"[qc] type {ProcTypeName} not found in any loaded assembly");
    }

    private static int LoadedCount()
    {
        try { return Convert.ToInt32(GetStatic(_proc, "LoadedCommandCount")); }
        catch { return -1; }
    }

    private static void ProcStatus(IHostContext host)
    {
        host.LogInfo($"[qc] processor: LoadedCommandCount={GetStatic(_proc, "LoadedCommandCount")} " +
                     $"TableGenerated={GetStatic(_proc, "TableGenerated")}");
    }

    // Name-first scan: the console object is called "Quantum Console (SRP) Canvas" in the lobby
    // scenes and "Quantum Console (SRP)" in the menu, so a Transform sweep finds both regardless of
    // whether the interop layer hands back a typed proxy for the MonoBehaviour.
    private static List<Component> FindConsoles()
    {
        var list = new List<Component>(8);
        if (_qc == null) return list;
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Transform>());
            if (all == null) { Diag("[qc] diag: no transforms"); return list; }
            Diag($"[qc] diag: transforms={all.Length}");
            int named = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i].TryCast<Transform>();
                if (t == null) continue;
                GameObject go;
                try { go = t.gameObject; } catch { continue; }
                if (go == null) continue;
                string n;
                try { n = go.name; } catch { continue; }
                if (n == null || !n.StartsWith("Quantum Console")) continue;
                named++;
                var c = FindQuantumConsoleOn(go);
                if (c != null) list.Add(c);
            }
            Diag($"[qc] diag: named={named} matched={list.Count}");
        }
        catch (Exception e)
        {
            Diag($"[qc] diag: scan threw {e.GetType().Name}: {e.Message}");
        }
        return list;
    }

    // The wrapper matters more than the match. `GetComponents<Component>()` hands back proxies whose
    // managed type is UnityEngine.Component even when the il2cpp type underneath is QuantumConsole
    // (GetIl2CppType() reports the real one, GetType() does not) - and a `Component` proxy has no
    // `enabled` and no Awake, which is exactly why the earlier "enabled = true" attempt read back
    // false with no error: the setter went nowhere. Asking the GameObject for the component BY the
    // resolved il2cpp type returns a properly typed QuantumConsole proxy.
    private static Component FindQuantumConsoleOn(GameObject go)
    {
        try
        {
            var comps = go.GetComponents<Component>();
            if (comps == null) return null;
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                string full = null;
                try { full = c.GetIl2CppType()?.FullName; } catch { }
                if (full == null) { try { full = c.GetType().FullName; } catch { } }
                if (full != QcTypeName) continue;

                try
                {
                    var typed = AsTyped(c, out string how);
                    if (typed != null)
                    {
                        Diag($"[qc] diag: typed wrapper '{typed.GetType().FullName}' ({how})");
                        return typed;
                    }
                    Diag($"[qc] diag: no typed wrapper for the console ({how})");
                }
                catch (Exception e) { Diag($"[qc] diag: typed get threw {e.GetType().Name}: {e.Message}"); }

                // TryCast<Behaviour> is a *Behaviour*, not a QuantumConsole: it still cannot be
                // assigned to Instance, so it is not a fallback here.
                Diag("[qc] diag: only an untyped Component proxy was available - skipped");
                return null;
            }
        }
        catch (Exception e) { Diag($"[qc] diag: components threw {e.GetType().Name}: {e.Message}"); }
        return null;
    }

    private static void Diag(string msg)
    {
        try { Debug.Log(msg); } catch { }
    }

    private static string PathOf(Component c)
    {
        try
        {
            var sb = new StringBuilder(c.gameObject.name);
            var t = c.transform;
            int guard = 0;
            while (t != null && t.parent != null && guard++ < 24)
            {
                t = t.parent;
                sb.Insert(0, t.gameObject.name + "/");
            }
            return sb.ToString();
        }
        catch { return "?"; }
    }

    private static bool SafeEnabled(Component c)
    {
        try { return c.TryCast<Behaviour>().enabled; } catch { return false; }
    }

    private static bool SafeActive(Component c)
    {
        try { return c.gameObject.activeInHierarchy; } catch { return false; }
    }

    // MonoBehaviour.enabled, through the Behaviour base the interop actually exposes (Component
    // itself has no `enabled` member in this binding).
    private static void SetEnabled(Component c, bool on)
    {
        try
        {
            var b = c.TryCast<Behaviour>();
            if (b != null) b.enabled = on;
        }
        catch (Exception e)
        {
            try { Debug.LogWarning($"[qc] set enabled threw: {Describe(e)}"); } catch { }
        }
    }

    // Which command names are worth echoing into the log from the full list dump.
    private static bool Interesting(string line)
    {
        string l = line.ToLowerInvariant();
        if (l.StartsWith("list of all commands")) return false;
        return l.Contains("give") || l.Contains("item") || l.Contains("quest") || l.Contains("task") ||
               l.Contains("complete") || l.Contains("spawn") || l.Contains("add") || l.Contains("location") ||
               l.Contains("teleport") || l.Contains("god") || l.Contains("money") || l.Contains("xp");
    }

    private static object Call(object target, string method, int argc, params object[] args)
    {
        if (target == null) return null;
        var m = FindMethod(target.GetType(), method, argc);
        if (m == null) return $"<no method {method}/{argc}>";
        try
        {
            return m.Invoke(target, args);
        }
        catch (Exception e)
        {
            try { Debug.LogWarning($"[qc] {method} threw: {Describe(e)}"); } catch { }
            return "<threw " + Describe(e) + ">";
        }
    }

    private static void InvokeOn(object target, string method, int argc)
    {
        if (target == null) return;
        var m = FindMethod(target.GetType(), method, argc, includeNonPublic: true);
        if (m == null)
        {
            try { Debug.LogWarning($"[qc] {method}/{argc} not found on {target.GetType().FullName}"); } catch { }
            return;
        }
        try { m.Invoke(target, null); }
        catch (Exception e)
        {
            try { Debug.LogWarning($"[qc] {method} threw: {Describe(e)}"); } catch { }
        }
    }

    private static MethodInfo FindMethod(Type t, string name, int argc, bool includeNonPublic = true)
    {
        if (t == null) return null;
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                    (includeNonPublic ? BindingFlags.NonPublic : 0);
        MethodInfo[] ms;
        try { ms = t.GetMethods(flags); } catch { return null; }
        for (int i = 0; i < ms.Length; i++)
        {
            if (ms[i].Name != name) continue;
            if (ms[i].GetParameters().Length == argc) return ms[i];
        }
        return null;
    }

    private static object GetStatic(Type t, string member)
    {
        if (t == null) return null;
        var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic;
        try
        {
            var p = t.GetProperty(member, flags);
            if (p != null) return p.GetValue(null);
            var f = t.GetField(member, flags);
            if (f != null) return f.GetValue(null);
        }
        catch { }
        return null;
    }

    private static object GetProperty(object target, string member)
    {
        if (target == null) return null;
        var t = target.GetType();
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var p = t.GetProperty(member, flags);
            if (p != null) return p.GetValue(target);
            var f = t.GetField(member, flags);
            if (f != null) return f.GetValue(target);
        }
        catch (Exception e) { return "<threw " + Describe(e) + ">"; }
        return null;
    }

    private static Type FindType(string fullName)
    {
        try
        {
            var direct = Type.GetType(fullName + ", QFSW.QC", false);
            if (direct != null) return direct;
        }
        catch { }
        try
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    var t = asms[i].GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string Describe(object o)
    {
        if (o == null) return "null";
        var s = o as string;
        if (s != null) return s.Length > 400 ? s.Substring(0, 400) + "..." : s;
        return o.ToString();
    }

    private static string Describe(Exception e)
    {
        var ex = e;
        while (ex is TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
        return $"{ex.GetType().Name}: {ex.Message}";
    }

}

// ---------------------------------------------------------------------------------------------
// AEZAKMI: the pause-menu code that unlocks the console.
//
// Why the text-input event and not a key poll: the code is letters, and a key poll would have to
// name seven keys, assume the layout, and would not see what the player actually typed into a
// composed input. `Keyboard.onTextInput` is the Input System's own text event - the one IME and
// character composition feed - and it costs nothing when no menu is up because no listener is
// subscribed until this generation's first tick.
//
// Deliberately NOT used: legacy `Input.inputString` (per-frame string, legacy backend only here),
// a hidden input field (would take focus and be visible/driveable), synthesized key events, and any
// consumption of Esc. Esc keeps closing the menu exactly as before; a menu close resets the buffer,
// so a partial code cannot straddle two menus.
//
// Everything a reload replaces lives here: the listener, the buffer, the recognition counters. The
// thing a reload must NOT reset - "already unlocked" - is not here (see QcAccess).
internal static class PauseCode
{
    private const string Code = "aezakmi";
    private const int CodeLength = 7;
    private const float OwnerSeconds = 0.25f;

    private static readonly char[] Buf = new char[CodeLength];
    private static int _n;
    private static bool _armed;
    private static UnityEngine.InputSystem.Keyboard _kb;
    private static Il2CppSystem.Action<char> _onText;
    private static bool _pending;
    private static IHostContext _log;
    private static int _sceneHandle = int.MinValue;
    private static bool _menuOpen;
    private static bool _focused = true;
    private static float _ownerAt;
    private static bool _ownerBusy;
    private static float _menuAt;
    private static bool _menuFallback;
    private static bool _ownerSeen;
    private static int _matches;
    private static int _resets;

    internal static string Summary =>
        $"capture={(_armed ? "armed" : "off")} chars={_n}/{CodeLength} pending={(_pending ? 1 : 0)} " +
        $"matches={_matches} resets={_resets}";

    // Per frame, from GameConsole.Tick (the normal game tick). The text callback below only records
    // a character and, on a match, raises a flag; the unlock itself, the confirmation sound and the
    // open all happen here.
    internal static void Tick(IHostContext host, float now)
    {
        _log = host;
        Arm(host);

        bool focused = FocusOk();
        if (!focused && _focused) Reset("focus-loss");
        _focused = focused;

        int handle = SceneHandle();
        if (handle != _sceneHandle)
        {
            if (_sceneHandle != int.MinValue) Reset("scene");
            _sceneHandle = handle;
        }

        bool open = MenuOpen();
        if (!open && _menuOpen) Reset("menu-close");
        _menuOpen = open;

        // A text field / chat / rebind row taking the keyboard is a reset too, not just a character
        // arriving while it holds it: a partial code typed before the field is focused must not
        // survive and complete after it closes.
        if (open)
        {
            bool busy = OwnerBusy();
            if (busy && !_ownerSeen) Reset("text-input");
            _ownerSeen = busy;
        }
        else _ownerSeen = false;

        if (!_pending) return;
        _pending = false;
        Drain(host);
    }

    private static void Drain(IHostContext host)
    {
        if (QcAccess.IsUnlocked) return;
        // The match was raised inside a device callback; between that character and this tick the
        // keyboard can have changed hands. Re-read the ownership before acting on it, so a code that
        // straddled a focus change unlocks nothing (the tick's Reset covers the observed edges, this
        // covers the window between them).
        if (!CanCapture())
        {
            _log?.LogWarning("[qc:access] unlock refused: the keyboard changed hands between the code's last " +
                             "character and this tick (the capture reset line names why)");
            Reset("scope");
            return;
        }
        // QcAccess.Unlock logs the unlock, or names why it could not be recorded (fail locked).
        if (!QcAccess.Unlock("pause-code")) return;
        string result = ReelAudio.Confirm(host);
        host.LogInfo($"[qc:access] confirmation cue=settle result={result}");
        GameConsole.OpenFromCheat(host);
        // The console's native test.* rows exist only behind the gate: this is the moment they become
        // addable (the table itself was built by the scene's ensure, long before).
        try { GameConsole.TestRowsSync(host); } catch { }
    }

    // ---- capture ----------------------------------------------------------------------------

    private static void Arm(IHostContext host)
    {
        var kb = CurrentKeyboard();
        IntPtr want = Ptr(kb);
        if (_armed && _onText != null && want != IntPtr.Zero && want == Ptr(_kb)) return;
        Disarm();
        if (kb == null || want == IntPtr.Zero) return;
        try
        {
            var del = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<char>>(new Action<char>(OnText));
            kb.add_onTextInput(del);
            _onText = del;
            _kb = kb;
            _armed = true;
            _n = 0;
            host?.LogInfo("[qc:access] capture armed via Keyboard.onTextInput");
        }
        catch (Exception e)
        {
            _onText = null;
            _kb = null;
            _armed = false;
            host?.LogWarning($"[qc:access] capture arm failed: {e.GetType().Name}: {e.Message}");
        }
    }

    internal static void Disarm()
    {
        var kb = _kb;
        var del = _onText;
        _armed = false;
        _kb = null;
        _onText = null;
        _n = 0;
        _pending = false;                 // a match waiting for the tick dies with the capture
        if (del == null) return;
        try { if (kb != null) kb.remove_onTextInput(del); } catch { }
    }

    // The Input System's text event. Never throws into the engine: an exception here would land in
    // a device callback and could take the input update down with it.
    private static void OnText(char c)
    {
        try { Accept(c); } catch { }
    }

    private static void Accept(char c)
    {
        if (!CanCapture()) { Reset("scope"); return; }

        var kb = CurrentKeyboard();
        if (kb != null)
        {
            try
            {
                // A modifier is held: this is a shortcut (Ctrl+C and friends), not typing. Some
                // platforms deliver the letter anyway, so the check is on the modifiers, not on
                // the character being a control code.
                if (kb.ctrlKey.isPressed || kb.altKey.isPressed) return;
                // IME composition owns the character stream; its intermediate characters are not
                // what the player typed.
                if (kb.imeSelected != null && kb.imeSelected.isPressed) { Reset("ime"); return; }
            }
            catch { }
        }

        if (char.IsControl(c)) return;
        char lower = char.ToLowerInvariant(c);
        if (lower < 'a' || lower > 'z') { Reset("non-letter"); return; }

        if (_n < CodeLength) Buf[_n++] = lower;
        else
        {
            Array.Copy(Buf, 1, Buf, 0, CodeLength - 1);
            Buf[CodeLength - 1] = lower;
        }
        if (_n == CodeLength) Check();
    }

    private static void Check()
    {
        for (int i = 0; i < CodeLength; i++)
            if (Buf[i] != Code[i]) return;

        _n = 0;
        _matches++;
        if (QcAccess.IsUnlocked)
        {
            _log?.LogInfo("[qc:access] code matched source=pause-code ignored=already-unlocked");
            return;
        }
        _log?.LogInfo($"[qc:access] code matched source=pause-code scene='{SceneName()}'");
        _pending = true;
    }

    private static void Reset(string why)
    {
        // A PENDING MATCH is part of the code state: `Check` clears the counter the moment it raises
        // `_pending`, so returning early on `_n == 0` would let a code straddle a focus/scene change
        // and still unlock on the next tick.
        bool hadCode = _n > 0;
        bool hadPending = _pending;
        _n = 0;
        _pending = false;
        if (!hadCode && !hadPending) return;
        if (hadPending) _log?.LogInfo($"[qc:access] matched code dropped why={why}: it arrived in a window " +
                                      "the capture no longer owns");
        if (!hadCode) return;
        _resets++;
        if (why == "non-letter" || why == "scope") return;   // per-character, not worth a line
        _log?.LogInfo($"[qc:access] code reset why={why}");
    }

    // ---- scope ------------------------------------------------------------------------------

    // The full condition for taking a character. Every clause is a reason the pause menu does not
    // own the keyboard right now: no menu, no focus, our own console is up, a text field or a rebind
    // row is up (this is also what covers "editing a settings value" - those are input fields too).
    //
    // OWNERSHIP IS RE-READ HERE, uncached: this runs once per typed character, and the 0.25 s cache
    // the frame tick uses is exactly wide enough for a field to take focus between a partial code and
    // its last letter. A cache hit would accept that letter and complete a code the player typed into
    // somebody else's field.
    private static bool CanCapture()
    {
        if (QcAccess.IsUnlocked) return false;    // nothing left to unlock: no re-sound on a retype
        if (!MenuOpen()) return false;
        if (!FocusOk()) return false;
        try { if (GameConsole.CapturesInput) return false; } catch { }
        if (ScanOwnerBusy()) return false;
        return true;
    }

    // MenuScope.IsOpen is the normal answer (the game's own PauseStateChanged plus a 4 Hz IsPaused
    // read on a held reference). The held Pauser is a fallback for the frames before the event has
    // landed; it is memoized, so a closed menu does not cost a property read per frame either.
    private static bool MenuOpen()
    {
        try { if (MenuScope.IsOpen) return true; } catch { }
        float now;
        try { now = Time.unscaledTime; } catch { return false; }
        if (now < _menuAt) return _menuFallback;
        _menuAt = now + OwnerSeconds;
        _menuFallback = false;
        try
        {
            var p = MenuScope.Pauser;
            if (p != null && !p.Equals(null)) _menuFallback = p.IsPaused;
        }
        catch { }
        return _menuFallback;
    }

    private static bool FocusOk()
    {
        try { return Application.isFocused; } catch { return false; }
    }

    // Anything that owns the keyboard while the pause menu is up: a focused text field (chat, a
    // rename box, a settings value, a rebind filter) or a row mid-rebind. Cached per 0.25 s for the
    // PER-FRAME path, the same order of cost as ModBindings.Blocked; a reader that decides about a
    // single character must call ScanOwnerBusy directly instead of trusting this cache.
    private static bool OwnerBusy()
    {
        float now;
        try { now = Time.unscaledTime; } catch { now = 0f; }
        if (now < _ownerAt) return _ownerBusy;
        _ownerAt = now + OwnerSeconds;
        _ownerBusy = ScanOwnerBusy();
        return _ownerBusy;
    }

    // The uncached scan. Two scene-cache reads, no allocation of ours; it runs once per captured
    // character and once per 0.25 s tick, never per frame.
    private static bool ScanOwnerBusy()
    {
        try
        {
            var fields = SceneCache.Scan(Il2CppType.Of<TMPro.TMP_InputField>());
            for (int i = 0; fields != null && i < fields.Length; i++)
            {
                var f = fields[i].TryCast<TMPro.TMP_InputField>();
                if (f == null) continue;
                var go = f.gameObject;
                if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
                if (f.isFocused) return true;
            }
            var rows = SceneCache.Scan(Il2CppType.Of<RebindActionUI>());
            for (int i = 0; rows != null && i < rows.Length; i++)
            {
                var r = rows[i].TryCast<RebindActionUI>();
                if (r == null) continue;
                var go = r.gameObject;
                if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
                if (r.ongoingRebind != null) return true;
            }
        }
        catch { }
        return false;
    }

    private static UnityEngine.InputSystem.Keyboard CurrentKeyboard()
    {
        try { return UnityEngine.InputSystem.Keyboard.current; } catch { return null; }
    }

    private static IntPtr Ptr(UnityEngine.InputSystem.Keyboard kb)
    {
        try { return kb == null ? IntPtr.Zero : kb.Pointer; } catch { return IntPtr.Zero; }
    }

    private static int SceneHandle()
    {
        try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { return int.MinValue; }
    }

    private static string SceneName()
    {
        try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { return "?"; }
    }
}
