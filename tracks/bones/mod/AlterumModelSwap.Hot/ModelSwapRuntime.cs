using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using Gameplay.PlayerSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// Reloadable half of the mod. Nothing here may call ClassInjector: injected types live in
// the host assembly and cannot be re-registered after a reload.
public class ModelSwapRuntime
{
    // Placement chosen in game and baked in. World space, Lobby scene.
    private const bool UseFixedLobbyPose = true;
    private const int FrameCap = 15;
    private float _nextFpsCheck;
    private static readonly Vector3 FixedLobbyPos = new Vector3(-0.328f, 1.153f, -12.273f);
    private static readonly Vector3 FixedLobbyEuler = new Vector3(0f, 41.06f, 0f);

    private readonly IHostContext _h;
    private ConfigSnapshot _cfg;

    private readonly bool _firstActivation;

    public ModelSwapRuntime(IHostContext host, bool firstActivation)
    {
        _h = host;
        _firstActivation = firstActivation;
        _cfg = host.Config;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private float _t;
    private bool _inited;
    private IntPtr _myHwnd = IntPtr.Zero;
    private IntPtr _prevFg = IntPtr.Zero;
    private float _nextAssert;
    private bool _wasFullscreenLogged;
    private bool _windowLogged;
    private int _lastLeft = int.MinValue, _lastTop = int.MinValue;

    private bool _dumpedModels;
    private int _shots;
    private string _lastScene = "";
    private readonly HashSet<string> _seenInstances = new();

    private GameObject _mirror;
    private MirrorRuntime _mirrorRt;
    private Proportions _proportions;
    private float _nextMirrorValidation;
    private bool _suppressAuto;
    private DateTime _cfgMtime = DateTime.MinValue;
    private float _nextCfgCheck;
    private string _overlayInfo = "";
    private Vector3 _camAnchor;
    private float _camStill;
    private Camera _gameCam;
    private float _nextAutoMirror;
    private bool _rendered;
    private bool _abortSequence;
    private string[] _clicks;
    private int _clickIndex;
    private float _nextClickAt = -1f;

    public void Update(float dt)
    {
        try
        {
            _t += dt;
            var previousConfig = _cfg;
            _cfg = _h.Config;
            if (previousConfig.MirrorShowPlayer != _cfg.MirrorShowPlayer ||
                previousConfig.MirrorWidth != _cfg.MirrorWidth || previousConfig.MirrorHeight != _cfg.MirrorHeight ||
                previousConfig.MirrorFrame != _cfg.MirrorFrame || previousConfig.MirrorLobbyOnly != _cfg.MirrorLobbyOnly)
            {
                DestroyMirror();
                _h.LogInfo("[mirror] configuration changed; restoring body and recreating fixed viewpoint");
            }
            var log = _h;

            if (!_inited) { _inited = true; Init(); }
            KeepQuiet();
            EnforceFrameCap();

            string scene = SceneManager.GetActiveScene().name;
            if (scene != _lastScene)
            {
                _lastScene = scene;
                _camStill = 0f; _camAnchor = Vector3.zero;
                _suppressAuto = false;
                DestroyMirror();                      // scene unload destroys the GameObject, not the assets
                log.LogInfo($"[scene] -> '{scene}' at t={_t:F1}");
                LogLiveInstances();
            }

            if (!_dumpedModels && _t > 3f) _dumpedModels = DumpModelTable();

            // Global MagicaCloth loop recovery. Per frame, not per sweep: it is readiness-gated
            // and manager-scoped, and a re-created manager (scene change) must get a fresh
            // attempt without waiting for the next 1 Hz sweep.
            ClothSim.Tick(_h);
            // Diagnostics for that intervention: step-callback counters and the cost window.
            ClothMetrics.Tick(_h, dt);

            // Bone pass: enlarge breasts (female rigs) / hips (all rigs) and keep the shipped
            // breast cloth reported. Its own SetupModel postfix pulls the sweep forward.
            if (_proportions == null) _proportions = new Proportions(_h);
            _proportions.Tick(_t);
            // Before/after proportion captures (fixed ortho camera, frozen pose); request-driven only.
            ProportionShots.Tick(_h, _proportions);
            // Our own Verlet jiggle for the two chest leaves. Request/rescan only; the write phase
            // runs in LateUpdate, after the game's animation has written the skeleton.
            JiggleSolver.Tick(_h);
            BonesProbe.Tick(_h, MirrorCamera, dt);
            JiggleProbe.Tick(_h, dt);

            // The mirror is part of the lobby: place it (or re-place it after a scene reload)
            // as soon as a local player exists in the Lobby scene.
            bool lobbyScene = !_cfg.MirrorLobbyOnly ||
                              (scene != null && scene.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0);
            // Only place once the view has stopped moving: the lobby camera starts somewhere else
            // and flies to the player, and Camera.main can briefly resolve to another camera.
            Camera cam = null;
            if (_cfg.AutoMirror && !_suppressAuto && lobbyScene && _mirror == null)
            {
                cam = FindGameCamera();
                if (cam != null)
                {
                    // settle on total displacement from a fixed anchor, not per-frame delta
                    Vector3 p0 = cam.transform.position;
                    if ((p0 - _camAnchor).sqrMagnitude > 0.0025f) { _camAnchor = p0; _camStill = 0f; }
                    else _camStill += Time.unscaledDeltaTime;
                }

                if (cam != null && _camStill > 1.0f &&
                    _t > _cfg.AutoMirrorDelay && _t > _nextAutoMirror)
                {
                    _nextAutoMirror = _t + 1.5f;
                    _gameCam = cam;
                    PlaceMirror();
                }
            }

            _mirrorRt?.Visibility?.Update();
            if (_mirrorRt != null && _t >= _nextMirrorValidation)
            {
                _nextMirrorValidation = _t + 1f;
                if (MirrorFactory.BodyLayerConflicts(_mirrorRt.BodyLayer))
                {
                    log.LogWarning("[mirror:body-layer-conflict] screen camera now includes isolated body bit; restoring and recreating");
                    DestroyMirror();
                }
            }

            RunClicks();

            if (!_rendered && _t > _cfg.RenderDelay) { _rendered = true; RenderWardrobeSlots(); }

            if (_cfg.TakeScreenshots &&
                _t > _cfg.ScreenshotInterval * (_shots + 1) && _shots < 40)
            {
                _shots++;
                Shot($"shot{_shots:00}");
            }
        }
        catch (Exception e) { _h.LogError($"Update threw: {e}"); }
    }

    private void Init()
    {
        var log = _h;
        Application.runInBackground = true;
        EnforceFrameCap();
        try
        {
            log.LogInfo($"[build] game '{Application.version}' on Unity {Application.unityVersion} " +
                        $"(plugin built against this game's interop)");
        }
        catch { }
        try
        {
            _myHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            var fg = @"C:\Windows\Temp\alterum\fg.json";
            if (System.IO.File.Exists(fg))
            {
                var m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText(fg), "\"hwnd\"\\s*:\\s*(\\d+)");
                if (m.Success) _prevFg = new IntPtr(long.Parse(m.Groups[1].Value));
            }
            log.LogInfo($"[quiet] init hwnd={_myHwnd} previousForeground={_prevFg}");
        }
        catch (Exception e) { log.LogWarning($"[quiet] init failed: {e.Message}"); }

        string seq = _firstActivation ? _cfg.ClickSequence : null;
        if (!string.IsNullOrEmpty(seq))
        {
            _clicks = seq.Split('|');
            _nextClickAt = _cfg.ClickFirstDelay;
            log.LogInfo($"[auto] click sequence: {seq}");
        }
    }

    // Cap the frame rate so two simultaneous instances do not fight over the machine.
    // The game re-applies its own display settings a few seconds into startup, which resets
    // vSync/targetFrameRate too, so re-assert periodically. vSync must stay 0 or Unity
    // ignores targetFrameRate entirely.
    private void EnforceFrameCap()
    {
        try
        {
            if (_t < _nextFpsCheck) return;
            _nextFpsCheck = _t + 2f;
            if (QualitySettings.vSyncCount == 0 && Application.targetFrameRate == FrameCap) return;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = FrameCap;
            _h.LogInfo($"[fps] cap applied {FrameCap} (vSync off)");
        }
        catch (Exception e) { _h.LogWarning($"[fps] enforce failed: {e.Message}"); }
    }

    // Enforce windowed + off-screen every frame; the game re-applies its own saved
    // display settings a few seconds into startup, which would otherwise go fullscreen.
    private void KeepQuiet()
    {
        if (!_cfg.ForceWindowed && !_cfg.Quiet) return;
        var log = _h;

        if (_myHwnd == IntPtr.Zero) _myHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        if (Screen.fullScreen)
        {
            Screen.fullScreen = false;
            Screen.SetResolution(_cfg.QuietWidth, _cfg.QuietHeight, false);
            if (!_wasFullscreenLogged) { _wasFullscreenLogged = true; log.LogInfo($"[window] enforcing windowed {_cfg.QuietWidth}x{_cfg.QuietHeight} at t={_t:F2}"); }
            _nextAssert = 0f;
        }

        if (_t >= _nextAssert)
        {
            _nextAssert = _t + 0.5f;
            const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
            if (_myHwnd != IntPtr.Zero)
            {
                // Quiet parks it off-screen; otherwise place it at the configured spot.
                // Size comes from the -screen-width/-screen-height launch args; moving the
                // window only, and only when it is in the wrong place, avoids fighting Unity
                // (which recentres the window whenever the resolution changes).
                int x = _cfg.Quiet ? -6000 : _cfg.WindowX;
                int y = _cfg.Quiet ? 0 : _cfg.WindowY;
                RECT r;
                bool moved = false;
                if (GetWindowRect(_myHwnd, out r))
                {
                    if (r.Left != _lastLeft || r.Top != _lastTop)
                    {
                        // Log only when the window actually changed position, so an unstable
                        // window is obvious in the log instead of a one-off line.
                        if (_lastLeft != int.MinValue)
                            log.LogInfo($"[window] moved ({_lastLeft},{_lastTop}) -> ({r.Left},{r.Top})");
                        _lastLeft = r.Left; _lastTop = r.Top;
                    }
                    if (Math.Abs(r.Left - x) > 2 || Math.Abs(r.Top - y) > 2)
                        moved = SetWindowPos(_myHwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
                if (!_windowLogged)
                {
                    _windowLogged = true;
                    log.LogInfo($"[window] {Screen.width}x{Screen.height} windowed at ({x},{y}) quiet={_cfg.Quiet} moved={moved}");
                }
            }
        }

        // If the game grabbed focus, hand it back to whatever the user was using.
        if (_cfg.Quiet && _prevFg != IntPtr.Zero && GetForegroundWindow() == _myHwnd)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            ShowWindow(_prevFg, 9);
            SetForegroundWindow(_prevFg);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
        }
    }

    private void RunClicks()
    {
        if (_clicks == null || _clickIndex >= _clicks.Length) return;
        if (_abortSequence) return;
        if (_t < _nextClickAt) return;

        string name = _clicks[_clickIndex].Trim();

        if (name.StartsWith("@"))
        {
            _clickIndex++;
            _nextClickAt = _t + _cfg.ClickStepDelay;
            if (name.Equals("@LobbyPrivate", StringComparison.OrdinalIgnoreCase)) SetLobbyTypePrivate();
            else if (name.Equals("@Mirror", StringComparison.OrdinalIgnoreCase)) PlaceMirror();
            else if (name.Equals("@MirrorToMe", StringComparison.OrdinalIgnoreCase)) TeleportMirrorToPlayer();
            else if (name.StartsWith("@JoinLobby=", StringComparison.OrdinalIgnoreCase)) JoinLobby(name.Substring("@JoinLobby=".Length).Trim());
            else if (name.Equals("@LobbyCode", StringComparison.OrdinalIgnoreCase)) LogLobbyCode();
            else if (name.Equals("@Peers", StringComparison.OrdinalIgnoreCase)) LogPeers();
            else _h.LogWarning($"[auto] unknown directive '{name}'");
            return;
        }

        // Wait for the element instead of trusting timers: the UI comes up at very
        // different speeds (Steam emulation is markedly slower than a normal launch).
        int hits = ActivateOrClick(name);
        if (hits == 0)
        {
            _clickWait += 0.5f;
            if (_clickWait > 30f)
            {
                _h.LogWarning($"[auto] gave up waiting for '{name}' after {_clickWait:F0}s");
                _abortSequence = true;
                DumpUi();
                return;
            }
            _nextClickAt = _t + 0.5f;
            return;
        }

        _clickWait = 0f;
        _clickIndex++;
        _nextClickAt = _t + _cfg.ClickStepDelay;
    }
    private float _clickWait;

    // Fill the join-code field and press the confirm button (multiplayer testing).
    private void JoinLobby(string code)
    {
        try
        {
            var fields = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMPro.TMP_InputField>());
            TMPro.TMP_InputField field = null;
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i].TryCast<TMPro.TMP_InputField>();
                if (f == null) continue;
                var go = f.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                field = f; break;
            }
            if (field == null) { _h.LogWarning("[join] no active input field"); DumpUi(); return; }

            _h.LogInfo($"[join] field '{PathOf(field.transform)}' <- '{code}'");
            field.text = code;
            try { field.onValueChanged.Invoke(code); } catch { }
            try { field.onEndEdit.Invoke(code); } catch { }
            DumpUi();
        }
        catch (Exception e) { _h.LogError($"[join] failed: {e}"); }
    }

    // Count everything that reveals whether another player is present.
    private void LogPeers()
    {
        try
        {
            var players = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Player>());
            var names = new List<string>();
            for (int i = 0; i < players.Length; i++)
            {
                var pl = players[i].TryCast<Player>();
                if (pl == null) continue;
                var t = pl.transform;
                if (t == null) continue;
                names.Add(PathOf(t) + " connected=" + pl.IsConnected);
            }
            _h.LogInfo($"[peers] Player objects={names.Count}");
            foreach (var n in names) _h.LogInfo($"[peers]   {n}");

            var nmObj = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Unity.Netcode.NetworkManager>());
            for (int i = 0; i < nmObj.Length; i++)
            {
                var nm = nmObj[i].TryCast<Unity.Netcode.NetworkManager>();
                if (nm == null) continue;
                if (!nm.IsListening) continue;
                _h.LogInfo($"[peers] NetworkManager listening server={nm.IsServer} client={nm.IsClient}");
            }
        }
        catch (Exception e) { _h.LogError($"[peers] failed: {e.Message}"); }
    }

    // Report the current lobby's join code so parallel instances can be pointed at it.
    private void LogLobbyCode()
    {
        try
        {
            if (!Steam.SteamLobbyManager.IsInLobby) { _h.LogInfo("[lobby] not in a lobby"); return; }
            var lobby = Steam.SteamLobbyManager.CurrentLobby;
            ulong id = lobby.Id;
            if (id == 0) { _h.LogInfo("[lobby] lobby id unavailable"); return; }
            string code = Steam.LobbyCode.Encode(id);
            _h.LogInfo($"[lobby] raw={id} code={code}");
        }
        catch (Exception e) { _h.LogError($"[lobby] code lookup failed: {e.Message}"); }
    }

    private int ActivateOrClick(string pattern)
    {
        var log = _h;
        int hits = 0;
        var mbs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MonoBehaviour>());
        for (int i = 0; i < mbs.Length; i++)
        {
            var mb = mbs[i].TryCast<MonoBehaviour>();
            if (mb == null) continue;
            var go = mb.gameObject;
            if (go == null || !go.activeInHierarchy) continue;
            string path;
            try { path = PathOf(go.transform); } catch { continue; }
            if (path.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (mb.GetIl2CppType().Name == "Button")
            {
                var b = mb.TryCast<UI.Elements.Button>();
                if (b == null) continue;
                try
                {
                    bool interactable = false, canClick = false;
                    try { interactable = b.IsInteractable; } catch { }
                    try { canClick = b.CanClick(); } catch { }
                    var safe = b.ClickedSafe;
                    var safeRef = b.ClickedSafeReferenced;
                    log.LogInfo($"[ui] click '{path}' interactable={interactable} canClick={canClick} " +
                                $"ClickedSafe={(safe == null ? "null" : "set")} " +
                                $"ClickedSafeReferenced={(safeRef == null ? "null" : "set")}");

                    var es = UnityEngine.EventSystems.EventSystem.current;
                    UnityEngine.EventSystems.PointerEventData ped = null;
                    if (es != null) ped = new UnityEngine.EventSystems.PointerEventData(es);
                    try { b.Click(ped); } catch (Exception e) { log.LogWarning($"[ui] Click() threw: {e.Message}"); }

                    if (safe != null) { try { safe.Invoke(); } catch (Exception e) { log.LogWarning($"[ui] ClickedSafe threw: {e.Message}"); } }
                    if (safeRef != null) { try { safeRef.Invoke(b); } catch (Exception e) { log.LogWarning($"[ui] ClickedSafeReferenced threw: {e.Message}"); } }
                    b.ClickedUnsafe.Invoke();
                    hits++;
                }
                catch (Exception e) { log.LogError($"[ui] click failed: {e}"); }
                continue;
            }

            var tab = mb.TryCast<UI.Elements.Tab>();
            if (tab != null)
            {
                try
                {
                    log.LogInfo($"[ui] Activate tab '{path}'");
                    tab.Activate();
                    hits++;
                }
                catch (Exception e) { log.LogError($"[ui] Activate failed: {e}"); }
            }
        }
        return hits;
    }

    // Force the "Create a lobby" panel to Private so nothing is ever published publicly.
    private void SetLobbyTypePrivate()
    {
        var log = _h;
        try
        {
            var mbs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MonoBehaviour>());
            for (int i = 0; i < mbs.Length; i++)
            {
                var mb = mbs[i].TryCast<MonoBehaviour>();
                if (mb == null) continue;
                if (mb.GetIl2CppType().Name != "LobbyCreatorView") continue;

                var view = mb.TryCast<Pause.UI.Controllers.LobbyCreatorView>();
                if (view == null) continue;
                var dd = view._lobbyTypeDropdown;
                if (dd == null) { log.LogError("[auto] lobby type dropdown is null"); _abortSequence = true; return; }

                // Never assume the enum order: walk the options and pick the one labelled Private.
                int found = -1;
                var labels = new StringBuilder();
                for (int idx = 0; idx < 6; idx++)
                {
                    try { dd.SelectOption(idx, true); }
                    catch { break; }
                    string label = "<unknown>";
                    try { var tmp = dd._selectedOptionTMP; if (tmp != null) label = tmp.text; } catch { }
                    labels.Append($"[{idx}]='{label}' ");
                    if (label != null && label.IndexOf("Private", StringComparison.OrdinalIgnoreCase) >= 0) { found = idx; break; }
                }
                log.LogInfo($"[auto] lobby type options: {labels}");

                if (found < 0)
                {
                    log.LogError("[auto] could not select a Private lobby type - NOT creating a lobby");
                    _abortSequence = true;
                    return;
                }

                string final = "<unknown>";
                try { var tmp2 = dd._selectedOptionTMP; if (tmp2 != null) final = tmp2.text; } catch { }
                log.LogInfo($"[auto] lobby type = index {found} ('{final}') - safe to create");
                return;
            }
            log.LogError("[auto] LobbyCreatorView not found");
            _abortSequence = true;
        }
        catch (Exception e) { log.LogError($"[auto] SetLobbyTypePrivate failed: {e}"); _abortSequence = true; }
    }

    // Alterum can have several enabled cameras tagged MainCamera (menu + lobby). Pick the one
    // actually showing the game view rather than trusting Camera.main.
    private Camera FindGameCamera()
    {
        Camera best = null; int bestScore = -1;
        try
        {
            var cams = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i].TryCast<Camera>();
                if (c == null) continue;
                var go = c.gameObject;
                if (go == null || !go.activeInHierarchy || !c.enabled) continue;
                if (c.targetTexture != null) continue;
                if (go.scene.name == null || go.scene.name.Length == 0) continue;

                int score = 0;
                string path = PathOf(go.transform);
                if (path.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0) score += 20;
                if (path.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0) score -= 20;
                if (go.name.IndexOf("Main Camera", StringComparison.OrdinalIgnoreCase) >= 0) score += 5;
                score += (int)c.depth;
                if (score > bestScore) { bestScore = score; best = c; }
            }
        }
        catch { }
        return best;
    }

    private Transform FindLocalPlayer()
    {
        try
        {
            var provs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Gameplay.PlayerSystem.CurrentPlayerProvider>());
            for (int i = 0; i < provs.Length; i++)
            {
                var prov = provs[i].TryCast<Gameplay.PlayerSystem.CurrentPlayerProvider>();
                if (prov == null) continue;
                var player = prov.CurrentPlayer;
                if (player == null) continue;
                var tr = player.transform;
                if (tr != null) { _h.LogInfo($"[mirror] local player from CurrentPlayerProvider: '{PathOf(tr)}'"); return tr; }
            }
        }
        catch (Exception e) { _h.LogWarning($"[mirror] CurrentPlayerProvider failed: {e.Message}"); }

        try
        {
            var players = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Gameplay.PlayerSystem.Player>());
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i].TryCast<Gameplay.PlayerSystem.Player>();
                if (player == null || !player.IsOwner || string.IsNullOrEmpty(player.gameObject.scene.name)) continue;
                _h.LogInfo($"[mirror] local player by Netcode IsOwner: '{PathOf(player.transform)}'");
                return player.transform;
            }
        }
        catch { }
        return null;
    }

    private void PlaceMirror()
    {
        var log = _h;
        try
        {
            DestroyMirror();
            string scene = SceneManager.GetActiveScene().name;
            if (_cfg.MirrorLobbyOnly && (scene == null || scene.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) < 0))
            {
                log.LogInfo($"[mirror] fixed Lobby mirror not placed in '{scene}'");
                return;
            }

            var cam = FindGameCamera();
            if (cam == null) { log.LogWarning("[mirror] no game camera"); return; }
            log.LogInfo($"[mirror] using camera '{PathOf(cam.transform)}' at {cam.transform.position}");
            var player = FindLocalPlayer();
            if (player == null) { log.LogWarning("[mirror] local player not found"); return; }

            _mirrorRt = MirrorFactory.Create(log, player, cam,
                _cfg.MirrorWidth,
                _cfg.MirrorHeight,
                _cfg.MirrorOffset,
                _cfg.MirrorCenterHeight,
                _cfg.MirrorFrame,
                _cfg.MirrorShowPlayer,
                _cfg.MirrorTilt,
                _cfg.MirrorBottomOnFloor,
                _cfg.MirrorSnapToWall,
                _cfg.MirrorEyeHeight,
                UseFixedLobbyPose, FixedLobbyPos,
                Quaternion.Euler(FixedLobbyEuler));

            if (_mirrorRt == null) { log.LogError("[mirror] creation failed"); return; }
            _mirror = _mirrorRt.Root;
        }
        catch (Exception e) { log.LogError($"[mirror] PlaceMirror failed: {e}"); }
    }

    public void LateUpdate()
    {
        // Re-assert the pinned chest-cage children after the animator wrote the skeleton: the
        // counter-scale must survive animated local positions, not only the frozen capture pose.
        try { _proportions?.LateTick(); } catch (Exception e) { _h.LogWarning($"[bounds:late] {e.Message}"); }
        // Jiggle next: the mirror renders in this same LateUpdate, so the offset has to be on the
        // skeleton before its camera draws a frame.
        JiggleSolver.LateTick(_h);
        if (_mirrorRt != null) { try { _mirrorRt.Tick(); } catch (Exception e) { _h.LogError($"[mirror] tick failed: {e}"); } }
        JiggleProbe.LateTick(_h);
    }

    // Single teardown path: removal, re-placement, config re-apply, scene change and reload.
    private void DestroyMirror()
    {
        var rt = _mirrorRt; _mirrorRt = null;
        if (rt != null) { try { rt.Dispose(); } catch (Exception e) { _h.LogWarning($"[mirror] dispose failed: {e.Message}"); } }
        var go = _mirror; _mirror = null;
        if (go != null) { try { UnityEngine.Object.Destroy(go); } catch { } }
    }

    // Retiring this generation must not leave a stale mirror or scaled skeletons in the scene.
    public void Dispose()
    {
        try { JiggleProbe.Abort(_h); } catch { }
        // Strip the jiggle offset before the bone pass restores its own recorded transforms.
        try { JiggleSolver.Shutdown(_h); } catch (Exception e) { _h.LogWarning($"[jsolve] shutdown failed: {e.Message}"); }
        try { ClothMetrics.Shutdown(_h); } catch (Exception e) { _h.LogWarning($"[bounds:cost] shutdown failed: {e.Message}"); }
        try { _proportions?.Dispose(); } catch (Exception e) { _h.LogWarning($"[bounds] dispose failed: {e.Message}"); }
        _proportions = null;
        try { ClothSim.OnDispose(_h); } catch { }
        DestroyMirror();
    }

    // Only the mirror camera draws the local player's body; the screen camera deliberately does not.
    private Camera MirrorCamera()
    {
        try { return _mirrorRt?.reflectCam; } catch { return null; }
    }

    public void OnGUI()
    {
        if (!_cfg.MirrorOverlay) return;
        try
        {
            if (GUI.Button(new Rect(18f, 18f, 250f, 32f), "Mirror to me   (F7)")) TeleportMirrorToPlayer();
            if (GUI.Button(new Rect(18f, 56f, 250f, 32f), "Remove mirror   (F9)")) RemoveMirror();
            if (!string.IsNullOrEmpty(_overlayInfo))
                GUI.Label(new Rect(18f, 96f, 560f, 48f), _overlayInfo);
        }
        catch { }
    }

    // Fixed Lobby fixture: relocation would move the owned viewpoint too.
    private void TeleportMirrorToPlayer()
    {
        _overlayInfo = "Fixed Lobby mirror: relocation disabled; @Mirror recreates it.";
        _h.LogInfo("[mirror] @MirrorToMe/overlay relocation disabled: fixed Lobby viewpoint must not follow the player");
    }

    private void RemoveMirror()
    {
        var log = _h;
        try
        {
            DestroyMirror();
            _suppressAuto = true;
            _overlayInfo = "mirror removed";
            log.LogInfo("[mirror] removed");
        }
        catch (Exception e) { log.LogError($"[mirror] remove failed: {e}"); }
    }

    private bool CameraPose(Camera cam, out Vector3 eye, out Vector3 look)
    {
        eye = cam.transform.position;
        look = cam.transform.forward;
        look.y = 0f;
        if (look.sqrMagnitude < 1e-4f) { look = cam.transform.forward; if (look.sqrMagnitude < 1e-4f) look = Vector3.forward; }
        look = look.normalized;
        return true;
    }

    // Floor height under a world point; falls back to a fixed eye height.
    private float GroundY(Vector3 eye)
    {
        float eyeHeight = _cfg.MirrorEyeHeight;
        float floorY = eye.y - eyeHeight;
        float best = float.MaxValue;
        try
        {
            var hits = Physics.RaycastAll(eye, Vector3.down, eyeHeight + 4f);
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i].collider;
                if (c == null) continue;
                if (hits[i].distance < 0.4f) continue;
                if (IsPlayerPart(c.transform)) continue;
                if (hits[i].distance < best) best = hits[i].distance;
            }
        }
        catch (Exception e) { _h.LogWarning($"[mirror] floor raycast failed: {e.Message}"); }
        return best < float.MaxValue ? eye.y - best : floorY;
    }

    private static bool IsPlayerPart(Transform t)
    {
        int guard = 0;
        while (t != null && guard++ < 12)
        {
            if (t.name != null && t.name.IndexOf("Owner: 0", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            t = t.parent;
        }
        return false;
    }

    private void DumpUi()
    {
        var log = _h;
        try
        {
            var sb = new StringBuilder();
            var mbs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MonoBehaviour>());
            var seen = new HashSet<string>();
            for (int i = 0; i < mbs.Length; i++)
            {
                var mb = mbs[i].TryCast<MonoBehaviour>();
                if (mb == null) continue;
                var go = mb.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                string tn = mb.GetIl2CppType().Name;
                if (tn != "Button" && tn != "ButtonedTab" && tn != "Tab") continue;
                string path = PathOf(go.transform);
                string key = tn + path;
                if (!seen.Add(key)) continue;
                sb.AppendLine($"  [{tn}] '{path}'");
            }
            log.LogInfo("[ui] ACTIVE game UI elements:\n" + sb);
        }
        catch (Exception e) { log.LogWarning($"[ui] dump failed: {e.Message}"); }
    }

    // The Lobby keeps every player model instantiated under "Lobby Global/Wardrobe/Wardrobe Scene/Models".
    // Rendering one of those slots with our own camera gives a clean before/after picture of the
    // model data swap without needing to play the game.
    private void RenderWardrobeSlots()
    {
        var log = _h;
        try
        {
            string slots = _cfg.RenderSlots;
            if (string.IsNullOrEmpty(slots)) return;

            var models = new List<ThirdViewPlayerModel>();
            Transform parent = null;
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null) continue;
                var go = m.gameObject;
                if (go == null || go.scene.name == null || go.scene.name.Length == 0) continue;
                string path = PathOf(go.transform);
                if (path.IndexOf("Wardrobe Scene/Models/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                models.Add(m);
                parent = go.transform.parent;
            }
            if (models.Count == 0) { log.LogWarning("[render] no wardrobe models found"); return; }

            models.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
            var names = new StringBuilder();
            for (int i = 0; i < models.Count; i++)
            {
                var g = models[i].gameObject;
                names.AppendLine($"    slot {i}: '{g.name}' activeSelf={g.activeSelf} activeInHierarchy={g.activeInHierarchy} " +
                                 $"pos={g.transform.position}");
            }
            log.LogInfo($"[render] wardrobe slots ({models.Count}), parent='{(parent == null ? "?" : PathOf(parent))}':\n{names}");

            // diagnostic: what cameras exist
            var cams = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());
            var csb = new StringBuilder();
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i].TryCast<Camera>();
                if (c == null || c.gameObject == null) continue;
                var g = c.gameObject;
                if (g.scene.name == null || g.scene.name.Length == 0) continue;
                csb.AppendLine($"    cam '{PathOf(g.transform)}' active={g.activeInHierarchy} enabled={c.enabled} pos={g.transform.position} rt={(c.targetTexture == null ? "no" : "yes")}");
            }
            log.LogInfo($"[render] cameras ({cams.Length}):\n{csb}");

            foreach (var tok in slots.Split(','))
            {
                if (!int.TryParse(tok.Trim(), out int idx)) continue;
                if (idx < 0 || idx >= models.Count) { log.LogWarning($"[render] slot {idx} out of range"); continue; }
                var target = models[idx];
                log.LogInfo($"[render] rendering slot {idx} = '{target.gameObject.name}'");
                RenderToPng(target.gameObject, $"slot{idx}_{target.gameObject.name}");
            }
        }
        catch (Exception e) { log.LogError($"[render] failed: {e}"); }
    }

    private void RenderToPng(GameObject target, string fileName)
    {
        var log = _h;
        GameObject camGo = null, lightGo = null;
        RenderTexture rt = null;
        Transform oldParent = null;
        Vector3 oldPos = Vector3.zero;
        Quaternion oldRot = Quaternion.identity;

        try
        {
            const int W = 800, H = 1000;

            // The wardrobe keeps these instances disabled until it is opened; detach + enable
            // temporarily so the model can be rendered on its own.
            oldParent = target.transform.parent;
            oldPos = target.transform.position;
            oldRot = target.transform.rotation;

            target.transform.SetParent(null, true);
            target.SetActive(true);

            Vector3 center;
            float radius;
            if (!ComputeBounds(target, out center, out radius))
            {
                log.LogWarning($"[render] '{target.name}' has no usable renderer bounds");
                return;
            }
            log.LogInfo($"[render] '{target.name}' bounds center={center} radius={radius}");

            Vector3 dir = new Vector3(0f, 0.10f, 1f).normalized;
            float dist = Mathf.Max(radius * 2.4f, 2.0f);
            Vector3 camPos = center + dir * dist;

            camGo = new GameObject("ModShotCam");
            var cam = camGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) { log.LogWarning("[render] could not create camera"); return; }
            cam.transform.position = camPos;
            cam.transform.LookAt(center);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.065f, 0.08f, 1f);
            cam.enabled = false;

            lightGo = new GameObject("ModShotLight");
            lightGo.transform.position = center + dir * (dist * 0.55f) + Vector3.up * (radius * 0.6f);
            var light = lightGo.AddComponent(Il2CppType.Of<Light>()).TryCast<Light>();
            if (light != null)
            {
                light.type = LightType.Point;
                light.range = Mathf.Max(radius * 6f, 6f);
                light.intensity = 3.0f;
                light.color = Color.white;
            }

            rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;

            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0f, 0f, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;

            var png = ImageConversion.EncodeToPNG(tex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];

            string path = System.IO.Path.Combine(log.ScreensDir, fileName + ".png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllBytes(path, bytes);
            log.LogInfo($"[render] wrote {path} ({bytes.Length} bytes)");
        }
        catch (Exception e) { log.LogError($"[render] '{fileName}' failed: {e}"); }
        finally
        {
            if (rt != null) rt.Release();
            if (camGo != null) UnityEngine.Object.Destroy(camGo);
            if (lightGo != null) UnityEngine.Object.Destroy(lightGo);
            try
            {
                target.SetActive(false);
                if (oldParent != null) target.transform.SetParent(oldParent, true);
                target.transform.SetPositionAndRotation(oldPos, oldRot);
            }
            catch { }
        }
    }

    // Union of every Renderer under the model (skinned meshes report local bounds while inactive).
    private static bool ComputeBounds(GameObject target, out Vector3 center, out float radius)
    {
        center = Vector3.zero; radius = 0f;
        var comps = target.GetComponentsInChildren(Il2CppType.Of<Renderer>());
        bool any = false;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        for (int i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            if (c == null) continue;
            var r = c.TryCast<Renderer>();
            if (r == null) continue;
            if (!r.enabled) continue;
            Bounds b = r.bounds;
            if (b.size.x <= 0f && b.size.y <= 0f && b.size.z <= 0f) continue;
            if (!any) { min = b.min; max = b.max; any = true; }
            else { min = Vector3.Min(min, b.min); max = Vector3.Max(max, b.max); }
        }
        if (!any) return false;
        center = (min + max) * 0.5f;
        radius = (max - min).magnitude * 0.5f;
        if (radius <= 0.01f || float.IsNaN(radius)) return false;
        return true;
    }

    private void Shot(string name)
    {
        string path = System.IO.Path.Combine(_h.ScreensDir, name + ".png");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            ScreenCapture.CaptureScreenshot(path);
            _h.LogInfo($"[shot] {name}.png");
        }
        catch (Exception e) { _h.LogWarning($"[shot] failed: {e.Message}"); }
    }

    private static string PathOf(Transform t)
    {
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        int guard = 0;
        while (p != null && guard++ < 12) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    private void LogLiveInstances()
    {
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null) continue;
                var go = m.gameObject;
                if (go == null || go.scene.name == null || go.scene.name.Length == 0) continue;
                string key = go.name + "@" + go.scene.name;
                if (_seenInstances.Add(key))
                    _h.LogInfo($"[model] live '{PathOf(go.transform)}' scene='{go.scene.name}'");
            }
        }
        catch { }
    }

    // Diagnostics only: dumps every PlayerModelData asset it can see. The model swap that used
    // to hang off this dump was removed; nothing here mutates game state.
    private bool DumpModelTable()
    {
        var log = _h;

        var datas = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerModelData>());
        log.LogInfo($"PlayerModelData instances: {datas.Length}");
        if (datas.Length == 0) { log.LogInfo("not loaded yet, will retry"); return false; }

        var all = new List<PlayerModelData>();
        for (int i = 0; i < datas.Length; i++)
        {
            var d = datas[i].TryCast<PlayerModelData>();
            if (d != null) all.Add(d);
        }

        var sb = new StringBuilder();
        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            string third = "<null>", first = "<null>", idx = "?", gender = "?";
            try { var t = d._thirdViewPlayerModel; third = t == null ? "<null>" : t.gameObject.name; } catch { }
            try { var f = d._firstViewPlayerModel; first = f == null ? "<null>" : f.gameObject.name; } catch { }
            try { idx = d.Index.ToString(); } catch { }
            try { gender = d.Gender.ToString(); } catch { }
            sb.AppendLine($"  [{i}] '{d.name}' index={idx} gender={gender} third='{third}' first='{first}'");
        }
        log.LogInfo("=== PlayerModelData ===\n" + sb);
        return true;
    }
}
