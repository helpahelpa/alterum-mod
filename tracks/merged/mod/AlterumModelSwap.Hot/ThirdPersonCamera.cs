using System;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using KinematicCharacterController;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Events;

namespace AlterumModelSwap.Hot;

// A real third-person camera for the local player in the Lobby.
//
// The game has none for the owner: `PovToggle` switches the game's view *mode* (which body it draws,
// which proxies it hides), it does not move the lens. `Lobby Global/Main Camera` is driven by
// Cinemachine from the player's own first-person view state, so in third person it sits inside the
// head - measured eye (1.11,1.87,-8.10) against head centre (1.13,1.80,-8.06), the inside of four of
// the body's own renderer bounds. Drawing the body through that lens is a face-interior view.
//
// This rig moves that one camera, once per frame, AFTER the game has written it:
//   * preferred hook: CinemachineCore.CameraUpdatedEvent. CinemachineBrain invokes it at the very end
//     of its own LateUpdate, right after it pushed its state into the Unity camera - so the write
//     order is "game writes, then we write, then the frame renders". Nothing can fight it and it
//     cannot jitter from an ordering race.
//   * fallback hook: this module's own LateUpdate (PumpBehaviour.LateUpdate), used only when the
//     Cinemachine event is unavailable or stops firing. A survival check at the next Update proves
//     whether the pose written there lived to the frame boundary; if it did not, the offset is not
//     trusted and the body bit is withdrawn instead of showing the inside of the head.
//
// Pose: one of a small ordered set of candidate offsets (see CandidatePoses), aimed at the body's own
// bound centre with the pitch fitted to the camera's own vertical field of view, so the body is
// centred and the head stays below the frame top. "Behind" is tried first and wins whenever it has
// room; the elevated and over-shoulder candidates exist for spots where directly behind is walled in
// (the Lobby spawn). The projection is never touched: fov, near and far stay the game's.
//
// Collision: one raycast per candidate, from the eye; the chosen distance is the first hit behind it
// minus a margin (the Lobby tent is small, so this engages most of the time).
//
// Never ship "inside your own head": if no candidate fits, or the resulting pose is inside any body
// renderer bound, the camera is left exactly as the game wrote it and the body bit is withdrawn. The
// local cosmetic bit follows that same answer (MirrorPlayerVisibility.LocalBodyOnScreen), so a
// withdrawn body never leaves its hat floating.

internal sealed class ThirdPersonCameraRig : IDisposable
{
    // The natural offset: behind the eye and above it. 2.30 m back keeps the whole body in a ~60 deg
    // vertical fov at 640x360; 0.42 m up puts the lens above the hat brim so the head does not fill
    // the top. Also the base of the `rays` diagnostic, so the numbers stay comparable.
    private const float BackDistance = 2.30f;
    private const float UpOffset = 0.42f;
    // The collision ray may never be overridden: a shortened distance is a hard maximum, and if the
    // room left over is smaller than this there is no third-person view at that spot at all (the
    // fallback in Apply then keeps the game's own pose and takes the body bit away).
    private const float MinDistance = 0.90f;
    private const float RoomHysteresis = 0.30f;
    private const float DistanceSmoothing = 12f;
    private const float RayMargin = 0.20f;
    private const float InsideMargin = 0.04f;
    // The sight line from the lens to the body is traced short of the body by this much, so the body's
    // own surface cannot make its own visibility test fail.
    private const float SightMargin = 0.15f;
    // Aim: keep this much of the vertical half-fov free above the head and below the feet.
    private const float FrameMarginDegrees = 5f;

    private const float HookSilenceSeconds = 0.5f;
    private const float PollSeconds = 0.4f;
    private const float HookRetrySeconds = 1f;
    private const int WindowFrames = 60;

    private const string RequestFileName = "cam.request";

    // ---------------------------------------------------------------- candidate poses
    // Behind is the natural third-person view and is always tried first, so the fallbacks never
    // replace it: they only win where the behind ray has no usable room. The Lobby spawn is that
    // place - the tent's back wall (`Palatka_Low/Colliders/Collider (3)`) sits ~0.34 m behind the
    // eye and its top is ~0.59 m above it, so a behind ray needs more than ~60 deg of elevation to
    // clear it - while the open side of the same tent has ~1.4 m. Each candidate is one straight ray
    // from the eye (back + up in the view's yaw frame, then yawed around world up), its length is a
    // hard maximum, and the full ray is traced before it can be chosen.
    private readonly struct Candidate
    {
        internal readonly string Name;
        internal readonly float Back, Up, Yaw;
        // The shortest distance at which this direction still gives an outside-the-body, readable
        // frame. The behind views need 0.90 m (the old floor); a high-and-close view does not, which is
        // the whole point of it (the Lobby spawn has 0.34 m behind, 1.06 m up and no more).
        internal readonly float MinDist;
        internal Candidate(string name, float back, float up, float yaw, float minDist)
        {
            Name = name; Back = back; Up = up; Yaw = yaw; MinDist = minDist;
        }
        internal float Distance => Mathf.Sqrt(Back * Back + Up * Up);
        internal float Elevation => Mathf.Atan2(Up, Back) * Mathf.Rad2Deg;
    }

    private static readonly Candidate[] CandidatePoses =
    {
        new Candidate("behind",             2.30f, 0.42f,   0f, MinDistance),   // today's view: ray 2.338 m, ~10 deg up
        new Candidate("behind-elevated",    2.00f, 1.15f,   0f, MinDistance),   // ~1.15 m up, ray 2.307 m, ~30 deg up
        new Candidate("overshoulder-left",  1.90f, 0.55f,  35f, MinDistance),   // 35 deg off the view axis, ray 2.044 m
        new Candidate("overshoulder-right", 1.90f, 0.55f, -35f, MinDistance),
        // High and close: 0.30 m back, 0.95 m up (72 deg), 0.996 m ray. The only shape that fits under
        // the Lobby spawn's ceiling (2.80 m, i.e. 1.06 m above the eye) while staying inside the room.
        new Candidate("elevated-close",     0.30f, 0.95f,   0f, 0.55f),
        // Beside: 72 deg off the view axis. Last, and only for spots where even a high-close lens has no
        // room - the tent's open side is the one direction a walled-in corner still has.
        new Candidate("beside-left",        1.45f, 0.30f,  72f, MinDistance),
        new Candidate("beside-right",       1.45f, 0.30f, -72f, MinDistance),
    };

    // One pick: the first candidate with usable room, its ray result, and the distance actually
    // allowed along it (the collision margin is a maximum, never a suggestion).
    private struct Pick
    {
        internal bool Fits;
        internal int Index;
        internal Candidate Pose;
        internal string Name;
        internal Vector3 Direction;
        internal float Desired, Hit, Room, Target;
        internal string HitName, HitPath;
    }

    private readonly IHostContext _log;
    private readonly MirrorPlayerVisibility _visibility;
    private readonly string _id = "tp-cam." + Guid.NewGuid().ToString("N");

    // The live generation. A retired generation's listener (hot reload) must never move the camera.
    private static ThirdPersonCameraRig _current;
    private static int _generations;
    private readonly int _generation = ++_generations;

    private UnityAction<CinemachineBrain> _listener;
    private bool _hooked, _disposed;
    private string _hookState = "not-installed";
    private float _lastHookAt = float.NegativeInfinity, _nextHookTry, _nextPoll;
    private int _hookCalls;
    private string _lastHookCamera = "none";

    private bool _fallbackMode, _fallbackLogged, _hookErrorLogged;
    private bool _engaged, _proven, _insideBody, _veto, _appliedPrev, _havePrev, _noRoom;
    private float _smoothedDist;
    private string _picked = "none";
    private readonly int[] _pickFrames = new int[CandidatePoses.Length];
    private readonly float[] _candRoom = new float[CandidatePoses.Length];
    private readonly float[] _candHit = new float[CandidatePoses.Length];
    private readonly string[] _candHitName = new string[CandidatePoses.Length];
    private readonly string[] _candSight = new string[CandidatePoses.Length];
    private string _lastPickLine = "";
    private string _lastPickKey = "";
    private int _pickLogs;
    private int _noRoomFrames, _noRoomLogged;
    private bool _overwriteLogged, _surviveLogged, _rayFailLogged;
    private int _insideLogged;
    private Camera _camRef;
    private Vector3 _originalPos, _basePos, _appliedPos, _prevAppliedPos;
    private Quaternion _originalRot, _baseRot, _appliedRot, _prevAppliedRot;

    // Stats for the report: how often the collision shortened the distance, and 60-frame stability.
    private int _frames, _appliedFrames, _shortenedFrames, _windowFrames, _engages, _insideFrames;
    private float _minDist = float.MaxValue, _lastDist, _sumDist, _lastHit, _lastHeadDist;
    private float _windowMaxPos, _windowMaxRot;
    private float _lastAim;
    private string _lastHitName = "none", _lastShortenNote = "none";

    internal ThirdPersonCameraRig(IHostContext log, MirrorPlayerVisibility visibility)
    {
        _log = log;
        _visibility = visibility;
        _current = this;
        _log.LogInfo($"[tp-cam:init] generation={_generation} candidates={CandidatePoses.Length} " +
                     $"behind={F(CandidatePoses[0].Back)}m/{F(CandidatePoses[0].Up)}m " +
                     $"minDist={F(MinDistance)}m margin={F(RayMargin)}m hook=CinemachineCore.CameraUpdatedEvent" + CandidateTable());
        TryHook();
    }

    // ---------------------------------------------------------------- frame hooks
    // Update: hook health, the survival check, the body-bit veto, the request file.
    internal void Tick()
    {
        if (_disposed) return;
        if (!ReferenceEquals(_current, this)) return;
        _frames++;
        if (!_hooked && Time.unscaledTime >= _nextHookTry) TryHook();
        if (_appliedPrev) { _appliedPrev = false; VerifySurvival(); }
        ApplyVeto();
        PollRequest();
    }

    // LateUpdate: only used when the Cinemachine event is not delivering (see the class comment).
    internal void LateTick()
    {
        if (_disposed || !ReferenceEquals(_current, this)) return;
        if (_hooked && Time.unscaledTime - _lastHookAt < HookSilenceSeconds) return;
        if (!_visibility.WantsThirdPersonCamera) { Disengage("lateupdate:not-third"); return; }
        var cam = SafeScreenCamera();
        if (cam == null) return;
        if (!_fallbackMode)
        {
            _fallbackMode = true;
            if (!_fallbackLogged)
            {
                _fallbackLogged = true;
                _log.LogWarning($"[tp-cam:fallback] Cinemachine event not delivering (state={_hookState} calls={_hookCalls} " +
                                $"age={F(Time.unscaledTime - _lastHookAt)}s); applying from LateUpdate and verifying survival instead");
            }
        }
        Apply(cam, "lateupdate");
        ApplyVeto();
    }

    private void TryHook()
    {
        _nextHookTry = Time.unscaledTime + HookRetrySeconds;
        try
        {
            var ev = CinemachineCore.CameraUpdatedEvent;
            if (ev == null) { _hookState = "event-null"; return; }
            _listener = DelegateSupport.ConvertDelegate<UnityAction<CinemachineBrain>>(
                new Action<CinemachineBrain>(OnCameraUpdated));
            ev.AddListener(_listener);
            _hooked = true;
            _hookState = "installed";
            _log.LogInfo($"[tp-cam:hook] installed on CinemachineCore.CameraUpdatedEvent " +
                         "(fires at the end of CinemachineBrain.LateUpdate, after the game's own camera write)");
        }
        catch (Exception e)
        {
            _hookState = "failed:" + e.GetType().Name;
            if (!_hookErrorLogged) { _hookErrorLogged = true; _log.LogWarning($"[tp-cam:hook] install failed: {e}"); }
        }
    }

    private void OnCameraUpdated(CinemachineBrain brain)
    {
        if (_disposed || !ReferenceEquals(_current, this)) return;
        _lastHookAt = Time.unscaledTime;
        if (_hookCalls++ == 0) _log.LogInfo("[tp-cam:hook] first Cinemachine callback received");
        try
        {
            if (!_visibility.WantsThirdPersonCamera) { Disengage("hook:not-third"); return; }
            var cam = brain != null ? brain.OutputCamera : null;
            if (cam == null) { _lastHookCamera = "no-output-camera"; return; }
            _lastHookCamera = MirrorPlayerVisibility.Hierarchy(cam.transform);
            if (!_visibility.IsScreenCamera(cam)) return;
            Apply(cam, "cinemachine");
            ApplyVeto();
        }
        catch (Exception e) { _log.LogWarning($"[tp-cam:apply] hook failed (camera left as the game wrote it): {e.Message}"); }
    }

    // ---------------------------------------------------------------- pose
    private void Apply(Camera cam, string why)
    {
        Transform t;
        Vector3 basePos;
        Quaternion baseRot;
        try
        {
            t = cam.transform;
            if (t == null) return;
            basePos = t.position;
            baseRot = t.rotation;
        }
        catch (Exception e) { _log.LogWarning($"[tp-cam:apply] camera unreadable: {e.Message}"); return; }

        if (!_engaged)
        {
            _engaged = true;
            _engages++;
            // The pose the game itself wrote, before this rig touched anything: the exact transform
            // that must be back on first person / dispose.
            _originalPos = basePos;
            _originalRot = baseRot;
            _havePrev = false;
            _smoothedDist = 0f;
            _windowMaxPos = 0f;
            _windowMaxRot = 0f;
            _windowFrames = 0;
            _log.LogInfo($"[tp-cam:engage] why={why} camera='{MirrorPlayerVisibility.Hierarchy(t)}' " +
                         $"base={V(basePos)} euler={V(baseRot.eulerAngles)} fov={F(cam.fieldOfView)} " +
                         $"pixels={cam.pixelWidth}x{cam.pixelHeight}");
        }
        _camRef = cam;
        _basePos = basePos;
        _baseRot = baseRot;

        // 1. Candidate pose: behind first, then the elevated/over-shoulder fallbacks. Each candidate's
        //    own ray is traced from the eye and its length is a hard maximum (see TryPick).
        if (!TryPick(basePos, baseRot, out var pick))
        {
            // Nothing has room at this eye pose: keep the game's own pose and take the body bit away,
            // rather than putting the lens inside geometry or inside the head.
            _noRoom = true;
            _noRoomFrames++;
            _insideBody = false;
            _picked = "none";
            if (_noRoomLogged++ < 3)
                _log.LogWarning($"[tp-cam:no-room] why={why} no candidate of {CandidatePoses.Length} fits " +
                                $"({BestRoomNote()}; floors {F(MinFloor)}-{F(MaxFloor)}m, +{F(RoomHysteresis)}m after a refusal): " +
                                "no third-person pose with a clear sight line to the body exists here; " +
                                "camera left at the game's own pose and the body bit withdrawn");
            RestoreBase(basePos, baseRot, t);
            ApplyVeto();
            return;
        }
        _noRoom = false;
        bool samePose = pick.Name == _picked;
        if (!samePose) _smoothedDist = 0f;
        _picked = pick.Name;
        _pickFrames[pick.Index]++;
        _lastHit = pick.Hit;
        _lastHitName = pick.HitName;
        _lastHitPath = pick.HitPath;
        bool shortened = pick.Target < pick.Desired;
        _lastShortenNote = shortened ? $"shortened-to-{F(pick.Target)}" : "full";
        float target = pick.Target;
        // Approaching an obstacle is immediate, pulling back out is smoothed, so the distance can
        // never lag past the collision result and the view cannot snap when the player turns.
        _smoothedDist = _havePrev && samePose ? Mathf.Min(target, Mathf.Lerp(_smoothedDist, target, 1f - Mathf.Exp(-DistanceSmoothing * Time.unscaledDeltaTime))) : target;
        float dist = Mathf.Clamp(_smoothedDist, 0f, target);
        Vector3 pos = basePos + pick.Direction * dist;

        // 2. Aim: at the body's own bound centre, pitch fitted to the camera's own vertical fov so
        //    the head stays inside the frame (below the top edge) at any distance.
        if (!_visibility.TryGetBodyBounds(out var body)) body = new Bounds(basePos + Vector3.down * 0.9f, new Vector3(0.6f, 1.8f, 0.6f));
        Vector3 aim = body.center;
        Vector3 flatTo = new Vector3(aim.x - pos.x, 0f, aim.z - pos.z);
        float flatDist = flatTo.magnitude;
        float yaw;
        if (flatDist > 0.15f) yaw = Mathf.Atan2(flatTo.x, flatTo.z) * Mathf.Rad2Deg;
        else yaw = baseRot.eulerAngles.y;
        float halfFov = cam.orthographic ? 30f : cam.fieldOfView * 0.5f;
        float d = Mathf.Max(0.10f, flatDist);
        // Elevation of the body's extremes as seen from the lens (negative = below it).
        float aTop = Mathf.Atan2(body.max.y - pos.y, d) * Mathf.Rad2Deg;
        float aFeet = Mathf.Atan2(body.min.y - pos.y, d) * Mathf.Rad2Deg;
        float limit = Mathf.Max(1f, halfFov - FrameMarginDegrees);
        float elev = (aTop + aFeet) * 0.5f;                 // body centred vertically
        float minElev = aTop - limit;                       // head must stay below the frame top
        float maxElev = aFeet + limit;                      // feet must stay above the frame bottom
        // A body taller than the frame cannot satisfy both: keep the head in (it is the identity).
        if (minElev > maxElev) elev = minElev;
        else elev = Mathf.Clamp(elev, minElev, maxElev);
        elev = Mathf.Clamp(elev, -80f, 80f);
        // Unity pitches DOWN for a positive Euler.x, so an elevation above the horizon is negative x.
        Quaternion rot = Quaternion.Euler(-elev, yaw, 0f);
        _lastAim = elev;

        // 3. Fallback guard: never render the inside of the owner's own head.
        if (_visibility.BodyContains(pos, InsideMargin, out var insideNames))
        {
            _insideBody = true;
            _insideFrames++;
            if (_insideLogged++ < 3)
                _log.LogWarning($"[tp-cam:inside-body] why={why} pos={V(pos)} dist={F(dist)} in=[{insideNames}] " +
                                "-> camera left at the game's own pose and the body bit withdrawn (no face-interior view)");
            RestoreBase(basePos, baseRot, t);
            ApplyVeto();
            return;
        }
        _insideBody = false;

        try { t.SetPositionAndRotation(pos, rot); }
        catch (Exception e) { _log.LogWarning($"[tp-cam:apply] write failed: {e.Message}"); return; }

        _appliedPos = pos;
        _appliedRot = rot;
        _proven = true;
        _appliedPrev = true;
        _appliedFrames++;
        if (shortened) _shortenedFrames++;
        _lastDist = dist;
        _sumDist += dist;
        if (dist < _minDist) _minDist = dist;
        if (_visibility.TryGetHeadCenter(out var head)) _lastHeadDist = Vector3.Distance(pos, head);
        // Where the head's top edge falls relative to the frame's top edge: >0 = inside the frame.
        LogPick(pick, why, dist, -(elev), (elev + limit) - aTop);

        // 60-frame stability: how much the applied pose moves while the player is not.
        if (_havePrev)
        {
            float dp = Vector3.Distance(pos, _prevAppliedPos);
            float dr = Quaternion.Angle(rot, _prevAppliedRot);
            if (dp > _windowMaxPos) _windowMaxPos = dp;
            if (dr > _windowMaxRot) _windowMaxRot = dr;
        }
        _prevAppliedPos = pos;
        _prevAppliedRot = rot;
        _havePrev = true;
        if (++_windowFrames >= WindowFrames) { _windowFrames = 0; LogStats("window"); }
    }

    // Keep the game's own pose on the camera and stop claiming the body may be drawn. The engage is
    // NOT reset: the transform captured at engage time is still the pose first person and dispose
    // have to restore, and re-capturing it every frame would make a refused pose unobservable.
    private void RestoreBase(Vector3 basePos, Quaternion baseRot, Transform t)
    {
        try { if (t != null) t.SetPositionAndRotation(basePos, baseRot); } catch { }
        _proven = false;
        _havePrev = false;
        _appliedPrev = false;
        _smoothedDist = 0f;
    }
    // The game writes the camera every frame, so returning to first person needs no write: the frame
    // that stops applying already carries the game's own transform. This measures that claim and, if
    // anything did leave the lens elsewhere (fallback hook, hot reload mid-frame), puts the pose this
    // rig captured at engage time back.
    private void Disengage(string why)
    {
        if (!_engaged) { _proven = false; return; }
        _engaged = false;
        _proven = false;
        _appliedPrev = false;
        _havePrev = false;
        var cam = _camRef;
        float dp = 0f, dr = 0f;
        bool wrote = false;
        try
        {
            if (cam != null && cam.transform != null)
            {
                dp = Vector3.Distance(cam.transform.position, _originalPos);
                dr = Quaternion.Angle(cam.transform.rotation, _originalRot);
                if (dp > 1e-4f || dr > 1e-3f)
                {
                    cam.transform.SetPositionAndRotation(_originalPos, _originalRot);
                    wrote = true;
                }
            }
        }
        catch { }
        _log.LogInfo($"[tp-cam:restore] why={why} posDelta={F(dp)}m rotDelta={F(dr)}deg wroteBack={wrote} " +
                     $"target={V(_originalPos)} (0/0 = the game restored its own pose exactly)");
        LogStats("restore");
        _insideBody = false;
    }

    // Nothing may write the camera between the Cinemachine push and the next Update: if the pose is
    // still exactly what this rig left there, the ordering is proven rather than assumed.
    private void VerifySurvival()
    {
        var cam = _camRef;
        if (cam == null) return;
        try
        {
            if (cam.transform == null) return;
            float dp = Vector3.Distance(cam.transform.position, _appliedPos);
            float dr = Quaternion.Angle(cam.transform.rotation, _appliedRot);
            bool survived = dp < 1e-3f && dr < 0.05f;
            if (!_surviveLogged)
            {
                _surviveLogged = true;
                _log.LogInfo($"[tp-cam:order] applied pose survived to the next Update: {survived} " +
                             $"(posDelta={F(dp)}m rotDelta={F(dr)}deg, hook={_hookState} fallback={_fallbackMode})");
            }
            if (!survived && !_overwriteLogged)
            {
                _overwriteLogged = true;
                _log.LogWarning($"[tp-cam:overwritten] the pose written last frame is gone " +
                                $"(posDelta={F(dp)}m rotDelta={F(dr)}deg): something writes the camera after this hook. " +
                                "Body bit withdrawn rather than trusting the offset.");
                _insideBody = true;   // same guard as the bounds check: withdraw, never show the face
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- body-bit veto
    // The body may only be drawn when this rig has demonstrably moved the camera out of it: the mask
    // admission is one frame behind the hook, so "not proven yet" has to veto, or the engage frame
    // would render the inside of the head.
    private void ApplyVeto()
    {
        if (_disposed) return;
        if (!_visibility.WantsThirdPersonCamera) { _visibility.SetBodyVeto(false, null); return; }
        // Measured once in Update (before the admission decision) and re-measured after the pose is
        // written, so the mask and the lens can never disagree about whether there is room.
        bool noRoom = !TryPickFromEye(out _);
        bool veto = !_proven || _insideBody || noRoom;
        _visibility.SetBodyVeto(veto,
            _insideBody ? "camera-inside-body" : noRoom ? "no-room" : (!_proven ? "camera-not-moved" : null));
    }

    // ---------------------------------------------------------------- candidate picking
    // The floor a candidate is held to right now: the behind views' 0.90 m minimum, plus the hysteresis
    // while the rig is refusing. The high-close candidate carries its own shorter floor.
    private float FloorFor(Candidate c) => c.MinDist + (_noRoom ? RoomHysteresis : 0f);
    private static float MinFloor => MinOf(c => c.MinDist);
    private static float MaxFloor => MaxOf(c => c.MinDist);
    private static float MinOf(Func<Candidate, float> f) { float m = float.MaxValue; for (int i = 0; i < CandidatePoses.Length; i++) m = Mathf.Min(m, f(CandidatePoses[i])); return m; }
    private static float MaxOf(Func<Candidate, float> f) { float m = 0f; for (int i = 0; i < CandidatePoses.Length; i++) m = Mathf.Max(m, f(CandidatePoses[i])); return m; }

    // The candidate's direction: behind the eye in the view's yaw frame, raised by the candidate's
    // own up/back ratio (so a yawed candidate keeps its elevation), then yawed around world up.
    private static Vector3 Direction(Quaternion baseRot, Candidate c)
    {
        Vector3 fwd = baseRot * Vector3.forward;
        Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
        if (flat.sqrMagnitude < 1e-6f) flat = new Vector3(0f, 0f, -1f);   // looking straight up/down
        flat.Normalize();
        Vector3 v = flat * -c.Back + Vector3.up * c.Up;
        if (c.Yaw != 0f) v = Quaternion.Euler(0f, c.Yaw, 0f) * v;
        return v / c.Distance;   // |v| == the candidate's own ray length
    }

    // First candidate, in table order, whose full ray from the eye leaves usable room. "Usable" is
    // the distance floor the behind view always had (MinDistance, plus the hysteresis while the rig
    // has been refusing), so a shortened-but-usable behind view still wins over any fallback: the
    // elevated poses exist to make walled-in spots usable, not to replace the natural view.
    // The result is a hard maximum - Target = min(ray length, room) - and every candidate's ray is
    // recorded for the diagnostic, including when none fits.
    private bool TryPick(Vector3 eye, Quaternion baseRot, out Pick pick)
    {
        pick = default;
        pick.Name = "none";
        for (int i = 0; i < CandidatePoses.Length; i++)
        {
            var c = CandidatePoses[i];
            Vector3 dir = Direction(baseRot, c);
            float hit = NearestHit(eye, dir, c.Distance, out var hitName, out var hitPath);
            float room = hit > 0f ? hit - RayMargin : float.PositiveInfinity;
            _candRoom[i] = room;
            _candHit[i] = hit;
            _candHitName[i] = hitName ?? "none";
            _candSight[i] = "not-tested";
            if (room < FloorFor(c)) continue;
            float target = Mathf.Min(c.Distance, room);
            // A clear ray to the pose is not enough: the view must actually reach the body. A lens that
            // cleared the tent wall by passing *above its roof* has no line to the body at all, and
            // admitting it would draw a roof, not a player. Trace the sight line before choosing.
            Vector3 pos = eye + dir * target;
            if (!SightClear(pos, out var blocker)) { _candSight[i] = "blocked:" + blocker; continue; }
            _candSight[i] = "clear";
            pick.Fits = true;
            pick.Index = i;
            pick.Pose = c;
            pick.Name = c.Name;
            pick.Direction = dir;
            pick.Desired = c.Distance;
            pick.Hit = hit;
            pick.HitName = hitName ?? "none";
            pick.HitPath = hitPath ?? "none";
            pick.Room = room;
            pick.Target = target;
            return true;
        }
        return false;
    }

    // Is the body actually visible from this pose? Both the bounds centre and the head must have a
    // line that no foreign collider interrupts (the body's own hitboxes are skipped by NearestHit).
    private bool SightClear(Vector3 pos, out string blocker)
    {
        blocker = "none";
        if (!_visibility.TryGetBodyBounds(out var body)) return true;   // nothing to prove against yet
        if (!SightRay(pos, body.center, out blocker)) return false;
        Vector3 head;
        Vector3 aim = _visibility.TryGetHeadCenter(out head) ? head : body.center + Vector3.up * (body.extents.y * 0.5f);
        return SightRay(pos, aim, out blocker);
    }

    private bool SightRay(Vector3 from, Vector3 to, out string blocker)
    {
        blocker = "none";
        Vector3 d = to - from;
        float dist = d.magnitude;
        if (dist < 1e-4f) return true;
        d /= dist;
        float hit = NearestHit(from, d, Mathf.Max(0.01f, dist - SightMargin), out var name);
        if (hit <= 0f) return true;
        blocker = $"{name}@{F(hit)}";
        return false;
    }

    // Uses the pose the game last wrote (or the pose the game wrote this frame when the rig is
    // engaged), so Update can refuse the body bit before any camera work happens, instead of
    // admitting it and withdrawing it later in the same frame.
    private bool TryPickFromEye(out Pick pick)
    {
        pick = default;
        pick.Name = "none";
        var cam = SafeScreenCamera();
        if (cam == null) return false;
        Vector3 eye;
        Quaternion rot;
        try
        {
            eye = _engaged ? _basePos : cam.transform.position;
            rot = _engaged ? _baseRot : cam.transform.rotation;
        }
        catch { return false; }
        return TryPick(eye, rot, out pick);
    }

    // The failing case in one clause: the natural behind ray plus whichever candidate had the most
    // room, so a refused spot is diagnosable without another request.
    private string BestRoomNote()
    {
        int best = -1;
        float bestRoom = float.NegativeInfinity;
        for (int i = 0; i < CandidatePoses.Length; i++)
            if (!float.IsNaN(_candRoom[i]) && _candRoom[i] > bestRoom) { bestRoom = _candRoom[i]; best = i; }
        string behind = $"behind room={RoomText(0)}m (hit '{_candHitName[0]}'@{( _candHit[0] > 0f ? F(_candHit[0]) : "none")})";
        if (best < 0 || best == 0) return behind;
        return behind + $", max={CandidatePoses[best].Name} room={RoomText(best)}m sight={_candSight[best] ?? "not-tested"}";
    }

    private string RoomText(int i)
        => float.IsPositiveInfinity(_candRoom[i]) ? "inf" : F(Mathf.Max(0f, _candRoom[i]));

    // Log which candidate won, once per distinct answer (and always on a dump), with the numbers the
    // choice was made from: distance along the ray, pitch down, and head margin inside the frame.
    private void LogPick(Pick pick, string why, float dist, float pitch, float headBelowTop)
    {
        string line = $"[tp-cam:pick] candidate={pick.Name} dist={F(pick.Target)} {(pick.Target < pick.Desired ? "shortened" : "full")} " +
                      $"ray={F(pick.Desired)} pitch={F(pitch)} elevCand={F(pick.Pose.Elevation)} yaw={F(pick.Pose.Yaw)} " +
                      $"hit='{pick.HitName}'@{(pick.Hit > 0f ? F(pick.Hit) : "none")} room={RoomText(pick.Index)} " +
                      $"headBelowTop={F(headBelowTop)}deg applied={F(dist)}";
        // One line per distinct answer, not per frame: the pitch jitters by hundredths while the idle
        // animation runs, and 15 lines a second of the same candidate is not evidence.
        string key = $"{pick.Name}|{pick.Target:F2}|{Mathf.Round(pitch):F0}|{Mathf.Round(headBelowTop):F0}|{pick.HitName}";
        _lastPickLine = line;
        if (key == _lastPickKey) return;
        _lastPickKey = key;
        if (_pickLogs++ >= 60) return;
        _log.LogInfo($"[tp-cam:pick] why={why} " + line.Substring("[tp-cam:pick] ".Length));
    }

    private string CandidateTable()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < CandidatePoses.Length; i++)
        {
            var c = CandidatePoses[i];
            sb.Append($"\n    [tp-cam:candidate] {i} {c.Name} back={F(c.Back)} up={F(c.Up)} yaw={F(c.Yaw)} " +
                      $"ray={F(c.Distance)} elev={F(c.Elevation)} min={F(c.MinDist)}" +
                      $" room={(i < _candHitName.Length && _candHitName[i] != null ? RoomText(i) : "-")}" +
                      $" hit='{(_candHitName[i] ?? "-")}'@{(i < _candHit.Length && _candHit[i] > 0f ? F(_candHit[i]) : "-")}" +
                      $" sight={_candSight[i] ?? "-"}");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- reading the scene
    private float NearestHit(Vector3 origin, Vector3 dir, float maxDistance, out string name)
        => NearestHit(origin, dir, maxDistance, out name, out _);

    private string _lastHitPath = "none";

    private float NearestHit(Vector3 origin, Vector3 dir, float maxDistance, out string name, out string path)
    {
        name = null;
        path = null;
        float best = -1f;
        try
        {
            var hits = Physics.RaycastAll(origin, dir, maxDistance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                var col = h.collider;
                if (col == null) continue;
                var tr = col.transform;
                if (tr == null) continue;
                // The body's own bone hitboxes (15 colliders on layer 14) are around the lens, not
                // geometry behind it.
                if (_visibility.IsOwnTransform(tr)) continue;
                if (best < 0f || h.distance < best)
                {
                    best = h.distance;
                    name = col.name;
                    path = $"{MirrorPlayerVisibility.Hierarchy(tr)} layer={col.gameObject.layer}" +
                           $" ('{LayerMask.LayerToName(col.gameObject.layer)}') {col.GetIl2CppType().Name}";
                }
            }
        }
        catch (Exception e)
        {
            if (!_rayFailLogged) { _rayFailLogged = true; _log.LogWarning($"[tp-cam:ray] failed (full distance used): {e.Message}"); }
        }
        return best;
    }

    private Camera SafeScreenCamera()
    {
        try { return _visibility.ScreenCamera(); } catch { return null; }
    }

    // ---------------------------------------------------------------- diagnostics
    private void PollRequest()
    {
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;
        string path;
        try { path = Path.Combine(_log.ScreensDir, RequestFileName); } catch { return; }
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text = "";
        try { text = File.ReadAllText(path).Trim(); } catch { }
        try { File.Delete(path); } catch { }
        string cmd = "dump";
        {
            var first = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0].Trim();
            int sp = first.IndexOfAny(new[] { ' ', '\t', '=' });
            cmd = (sp > 0 ? first.Substring(0, sp) : first).ToLowerInvariant();
            if (cmd.Length == 0) cmd = "dump";
        }
        switch (cmd)
        {
            case "dump": Dump(text); break;
            case "stab": _windowFrames = 0; _windowMaxPos = 0f; _windowMaxRot = 0f; _log.LogInfo("[tp-cam:stab] 60-frame stability window started"); break;
            case "reset": _appliedFrames = _shortenedFrames = _engages = _insideFrames = 0; _minDist = float.MaxValue; _sumDist = 0f; _log.LogInfo("[tp-cam:stats] counters reset"); break;
            case "rays": DumpRays(); break;
            case "probe": DumpProbe(); break;
            // Harness only: put the local player somewhere else (the Lobby spawn has its back 0.39 m
            // from the tent wall, so there is no room for any third-person pose there). Logged with
            // the old and new position so a capture can never be mistaken for the spawn pose.
            case "playerpos": MovePlayer(text); break;
            case "playermove": MovePlayerRelative(text); break;
            case "teleport": TeleportPlayer(text); break;
            default: _log.LogWarning($"[tp-cam:request] '{cmd}' is not dump/stab/reset/rays/probe"); break;
        }
    }

    private void Dump(string text)
    {
        var cam = SafeScreenCamera();
        if (cam == null) { _log.LogWarning("[tp-cam:dump] no screen camera right now (menu/loading?)"); return; }
        int mask = 0;
        try { mask = cam.cullingMask; } catch { }
        _log.LogInfo($"[tp-cam:dump] generation={_generation} camera='{MirrorPlayerVisibility.Hierarchy(cam.transform)}' " +
                     $"mask=0x{mask:X8} fov={F(cam.fieldOfView)} near={F(cam.nearClipPlane)} far={F(cam.farClipPlane)} " +
                     $"pixels={cam.pixelWidth}x{cam.pixelHeight} depth={F(cam.depth)} pose={V(cam.transform.position)} " +
                     $"euler={V(cam.transform.eulerAngles)}");
        _log.LogInfo($"[tp-cam:dump] thirdPersonWant={_visibility.WantsThirdPersonCamera} armed={_visibility.IsArmed} " +
                     $"hookState={_hookState} hookCalls={_hookCalls} hookAge={F(Time.unscaledTime - _lastHookAt)}s " +
                     $"hookCamera='{_lastHookCamera}' fallback={_fallbackMode}");
        _log.LogInfo($"[tp-cam:dump] engaged={_engaged} proven={_proven} veto={_veto} insideBody={_insideBody} " +
                     $"applied={V(_appliedPos)} euler={V(_appliedRot.eulerAngles)} aimElevation={F(_lastAim)} " +
                     $"original={V(_originalPos)} base={V(_basePos)} " +
                     $"dist={F(_lastDist)} headDist={F(_lastHeadDist)} hit='{_lastHitName}'@{F(_lastHit)} {_lastShortenNote} " +
                     $"hitPath={_lastHitPath}");
        if (_visibility.TryGetBodyBounds(out var body))
            _log.LogInfo($"[tp-cam:dump] bodyBounds center={V(body.center)} min={V(body.min)} max={V(body.max)} " +
                         $"eyeInsideBody={_visibility.BodyContains(_basePos, 0f, out var n)} eyeIn=[{n}]");
        _log.LogInfo($"[tp-cam:dump] frames={_frames} appliedFrames={_appliedFrames} shortened={_shortenedFrames} " +
                     $"noRoomFrames={_noRoomFrames} engages={_engages} insideBodyFrames={_insideFrames} " +
                     $"minDist={F(_minDist == float.MaxValue ? 0f : _minDist)} " +
                     $"avgDist={F(_appliedFrames == 0 ? 0f : _sumDist / _appliedFrames)}");
        // Refresh the table from the eye pose of this moment: in first person the rig does not pick, and
        // stale numbers would read like the geometry had changed between two dumps.
        TryPickFromEye(out _);
        _log.LogInfo($"[tp-cam:dump] picked={_picked} picks={PickCounts()} lastPick=[{_lastPickLine}]{CandidateTable()}");
        DumpBrains(cam);
        DumpMovers();
        if (text.IndexOf("rays", StringComparison.OrdinalIgnoreCase) >= 0) DumpRays();
        if (text.IndexOf("probe", StringComparison.OrdinalIgnoreCase) >= 0) DumpProbe();
    }

    private string PickCounts()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < CandidatePoses.Length; i++)
            if (_pickFrames[i] > 0) sb.Append(CandidatePoses[i].Name).Append(':').Append(_pickFrames[i]).Append(' ');
        return sb.Length == 0 ? "none" : sb.ToString().TrimEnd();
    }

    // What can move the local player at all: the teleport/hierarchy seams used by
    // `teleport=` above. Evidence only, one line per type found.
    private void DumpMovers()
    {
        var owner = _visibility.Owner;
        _log.LogInfo($"[tp-cam:movers] owner='{(owner == null ? "null" : MirrorPlayerVisibility.Hierarchy(owner))}'");
        Scan<KinematicCharacterMotor>("KinematicCharacterMotor");
        Scan<PlayerTeleportator>("PlayerTeleportator");
        Scan<CharacterController>("CharacterController");
        void Scan<T>(string label) where T : UnityEngine.Object
        {
            try
            {
                var arr = SceneCache.Scan(Il2CppType.Of<T>());
                int shown = 0, under = 0;
                var sb = new StringBuilder();
                for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i].TryCast<Component>();
                    if (c == null || c.transform == null) continue;
                    bool mine = owner != null && MirrorPlayerVisibility.Under(c.transform, owner);
                    if (mine) under++;
                    if (shown++ >= 4) continue;
                    sb.Append($"\n    [tp-cam:movers] {label} under={mine} path='{MirrorPlayerVisibility.Hierarchy(c.transform)}' pos={V(c.transform.position)}");
                }
                _log.LogInfo($"[tp-cam:movers] {label}: total={arr.Length} underOwner={under}{sb}");
            }
            catch (Exception e) { _log.LogWarning($"[tp-cam:movers] {label} scan failed: {e.Message}"); }
        }
    }

    private void DumpBrains(Camera screen)
    {
        try
        {
            var brains = SceneCache.Scan(Il2CppType.Of<CinemachineBrain>());
            _log.LogInfo($"[tp-cam:dump] brains={brains.Length}");
            for (int i = 0; i < brains.Length; i++)
            {
                var b = brains[i].TryCast<CinemachineBrain>();
                if (b == null) continue;
                Camera oc = null;
                try { oc = b.OutputCamera; } catch { }
                bool mine = false;
                try { mine = oc != null && screen != null && oc.GetInstanceID() == screen.GetInstanceID(); } catch { }
                string vcam = "unreadable", blend = "unreadable";
                try { vcam = b.ActiveVirtualCamera == null ? "null" : b.ActiveVirtualCamera.Name; } catch { }
                try { blend = b.IsBlending ? $"{b.ActiveBlend.CamA?.Name}->{b.ActiveBlend.CamB?.Name}" : "none"; } catch { }
                _log.LogInfo($"[tp-cam:dump] brain[{i}] path='{MirrorPlayerVisibility.Hierarchy(b.transform)}' " +
                             $"output='{(oc == null ? "null" : MirrorPlayerVisibility.Hierarchy(oc.transform))}' isScreen={mine} " +
                             $"update={b.UpdateMethod} blendMethod={b.BlendUpdateMethod} vcam='{vcam}' blend='{blend}'");
            }
        }
        catch (Exception e) { _log.LogWarning($"[tp-cam:dump] brain scan failed: {e.Message}"); }
    }

    // Harness only: translate the local player along its own view axis (a normal walk the lobby
    // grants nobody from a request file). Used to reach a spot whose back wall is far enough for a
    // third-person pose; every move is logged with old -> new so a capture cannot be mistaken for
    // the spawn pose.
    // Harness only: move the local player with the game's own teleport (PlayerTeleportator, the API
    // the game uses for exile/return). A raw transform write is reverted by the replica; this is the
    // sanctioned path and it moves the KCC motor as well. Used to reach a standing spot whose back
    // wall is far enough for a third-person pose - the Lobby spawn has 0.39 m of room behind it.
    private void TeleportPlayer(string text)
    {
        var owner = _visibility.Owner;
        if (owner == null) { _log.LogWarning("[tp-cam:teleport] no local player"); return; }
        string dir = "fwd";
        float metres = 2.0f;
        Vector2? absolute = null;
        int eq = text.IndexOf('=');
        if (eq > 0)
        {
            var v = text.Substring(eq + 1).Trim().Split(':');
            if (v.Length == 2)
            {
                dir = v[0].Trim().ToLowerInvariant();
                float.TryParse(v[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out metres);
            }
            // Absolute variant, harness only: teleport=to:x,z (the feet keep the floor they are on).
            // Needed to put the player back on the spawn pose after a capture run moved it.
            if (v.Length == 2 && v[0].Trim().Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                var parts = v[1].Split(',');
                if (parts.Length == 2 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var tx) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tz))
                {
                    absolute = new Vector2(tx, tz);
                    dir = "to";
                }
            }
        }
        var cam = SafeScreenCamera();
        if (cam == null) return;
        if (!_visibility.TryGetBodyBounds(out var body)) { _log.LogWarning("[tp-cam:teleport] body bounds unavailable"); return; }
        Vector3 fwd = _engaged ? _baseRot * Vector3.forward : cam.transform.rotation * Vector3.forward;
        Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
        if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
        flat.Normalize();
        Vector3 delta = dir switch
        {
            "back" => -flat * metres,
            "left" => new Vector3(-flat.z, 0f, flat.x) * metres,
            "right" => new Vector3(flat.z, 0f, -flat.x) * metres,
            _ => flat * metres
        };
        Vector3 target = absolute.HasValue
            ? new Vector3(absolute.Value.x, body.min.y + 0.01f, absolute.Value.y)
            : new Vector3(body.center.x, body.min.y + 0.01f, body.center.z) + delta;
        string label = absolute.HasValue ? $"to={F(target.x)},{F(target.z)}" : $"{dir}={F(metres)}m";
        try
        {
            PlayerTeleportator teleportator = null;
            var all = SceneCache.Scan(Il2CppType.Of<PlayerTeleportator>());
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i].TryCast<PlayerTeleportator>();
                if (t == null || t.transform == null) continue;
                if (!MirrorPlayerVisibility.Under(t.transform, owner)) continue;
                teleportator = t;
                break;
            }
            if (teleportator == null)
            {
                // The motor is the authoritative mover; the teleportator is optional sugar around it.
                var motors = SceneCache.Scan(Il2CppType.Of<KinematicCharacterMotor>());
                for (int i = 0; i < motors.Length; i++)
                {
                    var m = motors[i].TryCast<KinematicCharacterMotor>();
                    if (m == null || m.transform == null) continue;
                    if (!MirrorPlayerVisibility.Under(m.transform, owner)) continue;
                    Quaternion r2 = Quaternion.Euler(0f, (_engaged ? _baseRot : cam.transform.rotation).eulerAngles.y, 0f);
                    m.SetPositionAndRotation(target, r2, true);
                    _log.LogInfo($"[tp-cam:teleport] harness {label} via KinematicCharacterMotor.SetPositionAndRotation -> " +
                                 $"{V(target)} yaw={F(r2.eulerAngles.y)} motor='{MirrorPlayerVisibility.Hierarchy(m.transform)}' (this is not the spawn pose)");
                    return;
                }
                _log.LogWarning("[tp-cam:teleport] no PlayerTeleportator and no KinematicCharacterMotor under the local owner");
                return;
            }
            Quaternion rot = _engaged ? _baseRot : cam.transform.rotation;
            rot = Quaternion.Euler(0f, rot.eulerAngles.y, 0f);
            teleportator.TeleportOnServer(target, rot, true);
            _log.LogInfo($"[tp-cam:teleport] harness {label} via PlayerTeleportator.TeleportOnServer -> " +
                         $"{V(target)} yaw={F(rot.eulerAngles.y)} (this is not the spawn pose)");
        }
        catch (Exception e) { _log.LogWarning($"[tp-cam:teleport] failed: {e.Message}"); }
    }

    private void MovePlayerRelative(string text)
    {
        var owner = _visibility.Owner;
        if (owner == null) { _log.LogWarning("[tp-cam:playermove] no local player"); return; }
        var cam = SafeScreenCamera();
        if (cam == null) return;
        string dir = "fwd";
        float metres = 1.5f;
        int eq = text.IndexOf('=');
        if (eq > 0)
        {
            var v = text.Substring(eq + 1).Trim().Split(':');
            if (v.Length == 2)
            {
                dir = v[0].Trim().ToLowerInvariant();
                float.TryParse(v[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out metres);
            }
        }
        Vector3 fwd = _engaged ? _baseRot * Vector3.forward : cam.transform.rotation * Vector3.forward;
        Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
        if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
        flat.Normalize();
        Vector3 delta = dir switch
        {
            "back" => -flat * metres,
            "left" => new Vector3(-flat.z, 0f, flat.x) * metres,
            "right" => new Vector3(flat.z, 0f, -flat.x) * metres,
            _ => flat * metres
        };
        try
        {
            Vector3 from = owner.position;
            owner.position = from + delta;
            _log.LogInfo($"[tp-cam:playermove] harness {dir}={F(metres)}m '{MirrorPlayerVisibility.Hierarchy(owner)}' " +
                         $"{V(from)} -> {V(owner.position)} (this is not the spawn pose)");
        }
        catch (Exception e) { _log.LogWarning($"[tp-cam:playermove] failed: {e.Message}"); }
    }

    private void MovePlayer(string text)
    {
        var owner = _visibility.Owner;
        if (owner == null) { _log.LogWarning("[tp-cam:playerpos] no local player"); return; }
        Vector3? to = null;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0 || !line.Substring(0, eq).Trim().Equals("playerpos", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Substring(eq + 1).Split(',');
            if (parts.Length != 3) continue;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
            to = new Vector3(x, y, z);
        }
        if (to == null) { _log.LogWarning("[tp-cam:playerpos] expected playerpos=x,y,z"); return; }
        Vector3 from;
        try { from = owner.position; } catch { return; }
        try
        {
            owner.position = to.Value;
            _log.LogInfo($"[tp-cam:playerpos] harness move '{MirrorPlayerVisibility.Hierarchy(owner)}' " +
                         $"{V(from)} -> {V(to.Value)} (this is not the spawn pose; logged so the capture cannot be mistaken for it)");
        }
        catch (Exception e) { _log.LogWarning($"[tp-cam:playerpos] failed: {e.Message}"); }
    }

    private void DumpRays()
    {
        var cam = SafeScreenCamera();
        if (cam == null) return;
        // From the pose the GAME wrote, not from the offset pose this rig wrote: the geometry that
        // decides the distance is relative to the eye, and that is where the collision ray starts.
        Vector3 eye = _engaged ? _basePos : cam.transform.position;
        Quaternion baseRot = _engaged ? _baseRot : cam.transform.rotation;
        Vector3 fwd = baseRot * Vector3.forward;
        Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
        if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
        flat.Normalize();
        var sb = new StringBuilder();
        foreach (float up in new[] { UpOffset, 0f })
            foreach (float yaw in new[] { 0f, 90f, 180f })
            {
                Vector3 d = Quaternion.Euler(0f, yaw, 0f) * (-flat * BackDistance + Vector3.up * up);
                float len = d.magnitude;
                d /= len;
                sb.Append($"\n  [tp-cam:ray] up={F(up)} yaw={F(yaw)} len={F(len)}");
                try
                {
                    var hits = Physics.RaycastAll(eye, d, len + 1.5f, ~0, QueryTriggerInteraction.Ignore);
                    int shown = 0;
                    var arr = new System.Collections.Generic.List<(float dist, string text)>();
                    for (int i = 0; i < hits.Length; i++)
                    {
                        var h = hits[i];
                        var col = h.collider;
                        if (col == null || col.transform == null) continue;
                        bool own = _visibility.IsOwnTransform(col.transform);
                        var b = col.bounds;
                        string size = $"size=({F(b.size.x)},{F(b.size.y)},{F(b.size.z)})";
                        bool contains = b.Contains(eye);
                        arr.Add((h.distance, $"\n    [tp-cam:hit] @{F(h.distance)} '{col.name}' own={own} {size} " +
                                              $"centre={V(b.center)} containsEye={contains} " +
                                              $"path='{MirrorPlayerVisibility.Hierarchy(col.transform)}' layer={col.gameObject.layer}"));
                    }
                    arr.Sort((a, b2) => a.dist.CompareTo(b2.dist));
                    foreach (var e in arr) { if (shown++ >= 4) break; sb.Append(e.text); }
                    if (arr.Count == 0) sb.Append("\n    [tp-cam:hit] none");
                }
                catch (Exception e) { sb.Append($"\n    [tp-cam:hit] ray failed {e.Message}"); }
            }
        _log.LogInfo($"[tp-cam:rays] eye={V(eye)} forward={V(flat)} applied={V(_appliedPos)} dist={F(_lastDist)}{sb}");
    }

    // A fan of ray lengths from the eye, in the view's yaw frame: the geometry that decides which
    // candidate can be chosen. Diagnostic only ("cam.request: probe"); every ray starts exactly where
    // the offset rays start, so the numbers are directly comparable with a pick.
    private void DumpProbe()
    {
        var cam = SafeScreenCamera();
        if (cam == null) { _log.LogWarning("[tp-cam:probe] no screen camera right now"); return; }
        Vector3 eye = _engaged ? _basePos : cam.transform.position;
        Quaternion baseRot = _engaged ? _baseRot : cam.transform.rotation;
        Vector3 fwd = baseRot * Vector3.forward;
        Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
        if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
        flat.Normalize();
        var sb = new StringBuilder();
        sb.Append($"\n  [tp-cam:probe] eye={V(eye)} viewYaw={F(baseRot.eulerAngles.y)} forward={V(flat)} " +
                  $"floor={F(MinDistance)}m+{F(RoomHysteresis)}m margin={F(RayMargin)}m; yaw is degrees off the behind direction (0 = the" +
                  $" 'behind' candidate, +35 = overshoulder-left, 180 = straight ahead)");
        Vector3 back = -flat;
        foreach (float elev in new[] { 0f, 15f, 30f, 45f, 55f, 60f, 65f, 70f, 80f })
            foreach (float yaw in new[] { 0f, 20f, 35f, 50f, 65f, 80f, 90f, -35f, -65f, -90f, 180f })
            {
                Vector3 h = Quaternion.Euler(0f, yaw, 0f) * back;
                float e = elev * Mathf.Deg2Rad;
                Vector3 d = h * Mathf.Cos(e) + Vector3.up * Mathf.Sin(e);
                float hit = NearestHit(eye, d, 3.0f, out var name);
                string room = hit > 0f ? F(hit - RayMargin) : "inf";
                sb.Append($"\n    [tp-cam:probe] elev={F(elev)} yaw={F(yaw)} room={room} " +
                          $"hit='{(name ?? "none")}'@{(hit > 0f ? F(hit) : "clear-to-3.0")}");
            }
        _log.LogInfo("[tp-cam:probe] from the eye (the same origin every candidate ray uses)" + sb);
    }

    private void LogStats(string why)
    {
        if (_appliedFrames == 0 && _engages == 0) return;
        _log.LogInfo($"[tp-cam:stats] why={why} frames={_frames} applied={_appliedFrames} engages={_engages} " +
                     $"collisionShortened={_shortenedFrames} ({( _appliedFrames == 0 ? 0 : 100f * _shortenedFrames / _appliedFrames):F1}%) " +
                     $"minDist={F(_minDist == float.MaxValue ? 0f : _minDist)} avgDist={F(_appliedFrames == 0 ? 0f : _sumDist / _appliedFrames)} " +
                     $"lastDist={F(_lastDist)} headDist={F(_lastHeadDist)} noRoom={_noRoomFrames} insideBody={_insideFrames} " +
                     $"picked={_picked} picks={PickCounts()} window{dp(_windowMaxPos, _windowMaxRot)}");
    }

    private static string dp(float pos, float rot) => $"MaxDeltaPos={F(pos)}m/MaxDeltaRot={F(rot)}deg";

    // ---------------------------------------------------------------- teardown
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_hooked && _listener != null)
            {
                var ev = CinemachineCore.CameraUpdatedEvent;
                if (ev != null) ev.RemoveListener(_listener);
            }
        }
        catch (Exception e) { _log.LogWarning($"[tp-cam:dispose] listener removal failed: {e.Message}"); }
        _listener = null;
        _hooked = false;
        LogStats("dispose");
        Disengage("dispose");
        try { _visibility.SetBodyVeto(false, null); } catch { }
        if (ReferenceEquals(_current, this)) _current = null;
        _log.LogInfo($"[tp-cam:disposed] generation={_generation} hookState={_hookState} hookCalls={_hookCalls} " +
                     $"camera left at={V(_originalPos)}");
    }

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})", v.x, v.y, v.z);
}
