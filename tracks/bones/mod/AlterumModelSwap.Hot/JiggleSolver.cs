using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MagicaCloth2;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Our own breast jiggle: a self-contained Verlet spring for the two chest leaf bones.
//
// Why not the shipped cloth: MagicaCloth2 never steps a team in this build (notes/cloth-lifecycle.md
// and cloth-cleanup-evidence.log - TeamManager.SimulationStepTeamUpdate is invoked 0 times/s,
// SimulationStepCount stays 0 across 11 hot reloads and a scene change, and with the animator
// frozen an AddForce impulse is 0.0000 deg over 25 and 50 frames). Nothing managed revives it, so
// this replaces it with the same class of solution the reference mod (UnityJigglePhysics) uses:
// Verlet integration, parent-relative motion, bone position + rotation write-back, no colliders.
//
// Model
//   * Particles live in world space; the spring target is the ANIMATED pose converted to world
//     (parent.TransformPoint(animatedLocalPosition)). The only thing a particle is pulled toward is
//     where the animation currently puts that breast, so body motion, steps and turns move the
//     target and the particle lags behind it: parent-relative motion with no world drift, because
//     the target is attached to the skeleton, not to the world.
//   * Integration is position-Verlet in fixedDeltaTime units (Time.fixedDeltaTime, clamped, at most
//     MaxSubsteps per frame): gravity, an angular spring toward the animated pose, velocity damping,
//     a soft pendulum length constraint and a soft positional pull to rest. Length and angle
//     corrections are Lerps, as in the reference mod. Effective values are logged.
//   * The write phase runs in LateUpdate, i.e. after the game's animation has written the skeleton,
//     and is strictly additive: it reads the animated localPosition/localRotation and writes
//     animated + offset. Nothing else in the process moves a frozen skeleton, so a non-zero
//     deviation there is proof that this solver ran and that the write survived.
//   * Each write is remembered. A frame later the live value is compared against it: identical
//     means nothing re-wrote the bone, so the offset is still ours and is subtracted before the
//     value is used as the animated pose; different means the animation overwrote it and the fresh
//     value is used. Without that, a frozen animator would fold our own offset into the anchor.
//
// Bones: chest_02_l / chest_02_r (the leaves Proportions scales 1.45x). chest_01_* is only the
// pendulum pivot and is never written. Attach waits until Proportions has registered the rig, so
// the offset is always applied on top of the established scaling.
//
// Requests (persistent file "<ScreensDir>/solver.request", polled at 1 Hz):
//   enabled=0|1        opt-out (default 1). 0 strips every write and leaves no offset behind.
//   tag=name           CSV name: <ScreensDir>/jsolve_trace_<tag>.csv
//   trace=1            per-frame trace lines (trace=N: every Nth frame, 0 = off)
//   impulse=<m/s>      one-shot world velocity impulse on every simulated leaf
//   impulseat=<s>      delay that impulse until <s> seconds after arming (0 = immediately)
//   reset=1            one-shot: strip our writes and re-record the live pose as the original
//   dump=1             one-shot: per-rig state (bands, seeded, holds/overwrites, clamps)
//   gravity/friction/angleelasticity/blend/airdrag/lengthelasticity/soften/rotgain
//                      /maxmm/maxdeg  parameter overrides (logged with the effective values)
internal static class JiggleSolver
{
    internal const string RequestName = "solver.request";

    // --- reference-mod seed (UnityJigglePhysics baked breast settings) ---------------------
    // The reference's numbers are unitless and its integrator is not ours, so each one is mapped
    // to a rate here; the mapping is what the [jsolve:params] line prints.
    private const float RefGravity = 0.1f;           // -> 9.81 * 0.1 = 0.98 m/s²
    private const float RefFriction = 0.4f;          // -> 0.4 * 27 = 10.8 1/s velocity damping
    private const float RefAngleElasticity = 0.6f;   // -> ω² = 0.6 * 540 = 324 (18 rad/s, 2.9 Hz)
    private const float RefBlend = 1.0f;             // write-back gain
    private const float RefAirDrag = 0.01f;          // -> 0.01 * 30 = 0.3 1/s extra damping
    private const float RefLengthElasticity = 0.1f;  // -> 0.1 * 300 1/s pendulum-length correction
    private const float RefSoften = 0.1f;            // -> 0.1 * 20 1/s pull of the offset to zero

    private static float _gravity = RefGravity;
    private static float _friction = RefFriction;
    private static float _angleElasticity = RefAngleElasticity;
    private static float _blend = RefBlend;
    private static float _airDrag = RefAirDrag;
    private static float _lengthElasticity = RefLengthElasticity;
    private static float _soften = RefSoften;
    private static float _rotGain = 0.5f;

    // Unit conversions (constant, part of the documented mapping above).
    private const float GravityMps2 = 9.81f;
    // The two scale constants that turn the reference's unitless numbers into rates. Tuned for a
    // ~2 Hz wobble (7.5 frames per cycle at the game's 15 fps cap, so the motion survives sampling)
    // with zeta ~0.32 (one visible overshoot, settles in about a second).
    private const float AngleStiffScale = 260f;   // ω² = 0.6 * 260 = 156 (12.5 rad/s, 2.0 Hz)
    private const float FrictionScale = 20f;      // c  = 0.4 * 20  = 8.0 1/s
    private const float AirDragScale = 30f;
    private const float LengthScale = 300f;
    private const float SoftenScale = 1f;     // soften 0.1 -> a 0.2%/substep pull; the ring comes from the angle spring, not from this

    // Bounds that keep a bad frame, a teleport or a scene change from producing a visible artefact.
    private static float _maxOffset = 0.045f;        // m, clamp on the particle offset
    private static float _maxAngleDeg = 32f;         // deg, clamp on the applied rotation
    private const float MaxFrameDelta = 0.1f;        // s, one frame is never longer than this
    private const int MaxSubsteps = 4;               // covers 4 * 16.7 ms = 67 ms per frame

    private const float PollSeconds = 1f;
    private const float RescanSeconds = 1f;
    private const int TraceCap = 400;                // trace lines per request session

    private sealed class Leaf
    {
        public Transform T, P;
        public long TPtr, PPtr;
        public string Name = "";
        // world-space Verlet particle
        public Vector3 PosW, PrevW;
        public bool Seeded;
        // animated pose of this frame (ours stripped) and what we added to it
        public Vector3 AnimPos;
        public Quaternion AnimRot = Quaternion.identity;
        public Vector3 OffLocal;                     // what we add to localPosition (parent space)
        public Quaternion AddRot = Quaternion.identity;
        // the exact values we wrote, so the next frame can tell "still ours" from "re-animated"
        public bool Wrote;
        public Vector3 WrittenPos;
        public Quaternion WrittenRot = Quaternion.identity;
        // evidence
        public Vector3 OrigPos;
        public Quaternion OrigRot = Quaternion.identity;
        public bool Orig;
        public int Frames, Holds, Overwrites, Clamps;
        public float MaxDevDeg, MaxDevMm, MaxOffMm;
        public float LastClampLog;
        public Vector3 ParentW;
        // anchor of the current frame (parent world position, animated pose in world, pendulum length)
        public Vector3 RestWOfThisFrame;
        public float LengthOfThisFrame;
        public bool HeldThisFrame;
        public float MisMm, MisDeg;                  // how far the live value was from our write
        public Vector3 PendingImpulse;               // m/s, applied when the particle is next seeded
    }

    // The shipped breast BoneCloth never simulates, but it still writes the pose it captured at
    // build time back onto the leaves every frame - after LateUpdate, which silently erased the
    // first working version of this solver (the trace showed a 200 mm offset while the rendered
    // frames were pixel-identical). Its write-back is the only thing in the process that can fight
    // the jiggle, so it is switched off with SetSkipWriting(true) while the solver is armed and
    // restored from the captured value when the solver lets go. The component keeps running, so
    // the bone pass still reports breastJiggleCloth=1 running=1.
    private sealed class Writer
    {
        public MagicaCloth Mc;
        public string Path = "";
        public bool Prior, PriorCaptured;
        public int Reasserts;
    }

    private sealed class Rig
    {
        public int Key;
        public string Name = "";
        public ThirdViewPlayerModel M;
        public readonly List<Leaf> Leaves = new();
        public readonly List<Writer> Writers = new();
        public bool Dead;
    }

    private static readonly Dictionary<int, Rig> _rigs = new();
    private static readonly List<int> _stale = new();
    private static IHostContext _log;
    private static bool _enabled = true;
    private static bool _trace;
    private static int _traceEvery = 1;
    private static string _tag = "run";
    private static int _frame, _traceLines;
    private static bool _traceCapLogged;
    private static double _startedAt;
    private static float _accum, _nextPoll, _nextRescan, _nextRateLog;
    private static float _pendingImpulse, _pendingTeleport, _teleportApplied;
    private static double _impulseDueAt;
    private static bool _impulseArmed;
    private static double _lastImpulseAt = -1d;
    private static float _lastImpulseMps;
    private static DateTime _reqMtime = DateTime.MinValue;
    private static readonly StringBuilder _csv = new();
    private static bool _shutdown;

    // =====================================================================================
    // per-frame entry points (ModelSwapRuntime.Update / .LateUpdate)
    // =====================================================================================
    internal static void Tick(IHostContext log)
    {
        _log = log;
        if (_shutdown) return;
        float now = Time.unscaledTime;
        try
        {
            if (now >= _nextPoll) { _nextPoll = now + PollSeconds; PollRequest(log); }
            if (_enabled && now >= _nextRescan) { _nextRescan = now + RescanSeconds; Rescan(log); }
            if (_impulseArmed && _enabled && Time.realtimeSinceStartupAsDouble >= _impulseDueAt)
            {
                _impulseArmed = false;
                ApplyImpulse(log, _pendingImpulse);
            }
            if (now >= _nextRateLog) { _nextRateLog = now + 30f; RateSummary(log); }
        }
        catch (Exception e) { try { log.LogWarning($"[jsolve] tick failed: {e.GetType().Name}: {e.Message}"); } catch { } }
    }

    internal static void LateTick(IHostContext log)
    {
        if (_shutdown || !_enabled) return;
        _log = log;
        try { SolveFrame(log); }
        catch (Exception e) { try { log.LogWarning($"[jsolve] solve failed: {e.GetType().Name}: {e.Message}"); } catch { } }
    }

    // =====================================================================================
    // discovery
    // =====================================================================================
    // 1 Hz. Rigs are keyed by the ThirdViewPlayerModel pointer, exactly like Proportions does, and
    // attach only when Proportions already owns the rig: that is the ordering guarantee, an offset
    // is written on top of the established 1.45x leaf scale, never before it.
    private static void Rescan(IHostContext log)
    {
        var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
        _stale.Clear();
        var seen = new HashSet<int>();
        for (int i = 0; i < arr.Length; i++)
        {
            var m = arr[i].TryCast<ThirdViewPlayerModel>();
            if (m == null) continue;
            var go = m.gameObject;
            if (go == null) continue;
            if (go.scene.name == null || go.scene.name.Length == 0) continue;   // prefab asset
            int key = Ptr(m);
            if (!seen.Add(key)) continue;
            if (!Writable(m, go, out string why))
            {
                if (_rigs.ContainsKey(key)) { _stale.Add(key); log.LogInfo($"[jsolve:gate] rig='{go.name}' no longer writable ({why}); detaching"); }
                continue;
            }
            if (_rigs.TryGetValue(key, out var known)) { HoldWriters(log, known, "reassert"); continue; }
            var prop = Proportions.Current;
            if (prop == null || !prop.KnowsRig(key))
            {
                // Proportions applies on its own sweep; attaching before the scale exists would put
                // the offset under the 1.45 and then move the anchor when the scale lands.
                continue;
            }
            Attach(log, m, key);
        }
        foreach (var kv in _rigs) if (!seen.Contains(kv.Key) || kv.Value.Dead) _stale.Add(kv.Key);
        for (int i = 0; i < _stale.Count; i++) if (_rigs.TryGetValue(_stale[i], out var r)) Detach(log, r, "model gone / unwritable");
    }

    private static bool Writable(ThirdViewPlayerModel m, GameObject go, out string why)
    {
        why = "";
        try
        {
            if (!go.activeInHierarchy) { why = "model inactive"; return false; }
            var all = m.Bones?._allBones;
            if (all == null || all.Count == 0) { why = "bones not initialised"; return false; }
        }
        catch (Exception e) { why = e.GetType().Name; return false; }
        return true;
    }

    private static void Attach(IHostContext log, ThirdViewPlayerModel m, int key)
    {
        var rig = new Rig { Key = key, Name = m.gameObject.name, M = m };
        try
        {
            var all = m.Bones._allBones;
            for (int i = 0; i < all.Count; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.name ?? "";
                bool leafL = n.Equals("chest_02_l", StringComparison.OrdinalIgnoreCase);
                bool leafR = n.Equals("chest_02_r", StringComparison.OrdinalIgnoreCase);
                if (!leafL && !leafR) continue;
                var p = t.parent;
                if (p == null) continue;
                rig.Leaves.Add(new Leaf
                {
                    T = t, P = p, TPtr = Ptr(t), PPtr = Ptr(p),
                    Name = leafL ? "chest_02_l" : "chest_02_r",
                });
            }
        }
        catch (Exception e) { log.LogWarning($"[jsolve:attach] rig='{rig.Name}' scan failed: {e.Message}"); return; }
        if (rig.Leaves.Count == 0) return;
        // The dead breast cloth's write-back is what fights the offset; find it first.
        try
        {
            var comps = m.GetComponentsInChildren(Il2CppType.Of<MagicaCloth>(), true);
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var mc = comps[i]?.TryCast<MagicaCloth>();
                if (mc == null) continue;
                if (!BreastCloth(mc)) continue;
                rig.Writers.Add(new Writer { Mc = mc, Path = TPath(mc.transform) });
            }
        }
        catch (Exception e) { log.LogWarning($"[jsolve:writer] rig='{rig.Name}' cloth scan failed: {e.Message}"); }

        _rigs[key] = rig;
        if (_startedAt <= 0d) _startedAt = Time.realtimeSinceStartupAsDouble;
        HoldWriters(log, rig, "attach");
        // Record the pose we start from (restore proof) and seed on the first solve frame.
        foreach (var leaf in rig.Leaves)
        {
            try { leaf.OrigPos = leaf.T.localPosition; leaf.OrigRot = leaf.T.localRotation; leaf.Orig = true; }
            catch { }
        }
        LogParams(log, $"attach rig='{rig.Name}' leaves={rig.Leaves.Count}");
        log.LogInfo($"[jsolve:attach] rig='{rig.Name}' model=0x{key:X} bones={(rig.Leaves.Count == 2 ? "chest_02_l,chest_02_r" : "1 of 2")} " +
                    $"chest_01_untouched=1 proportionsApplied=1 scale={ScaleOf(rig)}");
    }

    private static string ScaleOf(Rig rig)
    {
        var sb = new StringBuilder();
        foreach (var l in rig.Leaves)
        {
            try { sb.Append($"{(sb.Length > 0 ? "," : "")}{l.Name}:{l.T.localScale.x:F2}"); } catch { }
        }
        return sb.ToString();
    }

    private static void Detach(IHostContext log, Rig rig, string why)
    {
        if (rig == null) return;
        Strip(log, rig, why);
        ReleaseWriters(log, rig, why);
        _rigs.Remove(rig.Key);
        log.LogInfo($"[jsolve:detach] rig='{rig.Name}' reason='{why}' (no further writes)");
    }

    // =====================================================================================
    // solve + write
    // =====================================================================================
    private static void SolveFrame(IHostContext log)
    {
        if (_rigs.Count == 0) return;
        float frame = Time.deltaTime;
        if (frame <= 0f) frame = Time.unscaledDeltaTime;
        if (frame > MaxFrameDelta) frame = MaxFrameDelta;

        float fs = Time.fixedDeltaTime;
        if (fs <= 0f) fs = 0.02f;
        if (fs < 1f / 240f) fs = 1f / 240f;
        if (fs > 1f / 30f) fs = 1f / 30f;

        _accum += frame;
        int steps = 0;
        while (_accum >= fs && steps < MaxSubsteps) { _accum -= fs; steps++; }
        if (_accum > fs) _accum = fs;   // dropped substeps: never let the accumulator grow

        // Teleport check: shift every rig root for exactly one solve, then put it back. The anchor
        // jumps metres while the particle stays in world space, which is the worst case the clamp
        // and the strip logic have to survive.
        if (Math.Abs(_pendingTeleport) > 0f)
        {
            _teleportApplied = _pendingTeleport;
            _pendingTeleport = 0f;
            float shift = _teleportApplied;
            foreach (var kv in _rigs)
            {
                try { kv.Value.M.transform.position = kv.Value.M.transform.position + kv.Value.M.transform.right * shift; } catch { }
            }
            log.LogInfo($"[jsolve:teleport] rig root(s) jumped {shift:0.###}m for one frame (anchor moves, particles do not)");
        }

        foreach (var kv in _rigs)
        {
            var rig = kv.Value;
            if (rig.Dead) continue;
            if (!StillWritable(rig, out string why))
            {
                rig.Dead = true;
                log.LogInfo($"[jsolve:gate] rig='{rig.Name}' disabled: {why}; no writes, this rig is detached on the next rescan");
                Strip(log, rig, why);
                continue;
            }
            foreach (var leaf in rig.Leaves)
            {
                if (!ReadAnimated(leaf)) { rig.Dead = true; break; }
                if (!leaf.Seeded) { leaf.PosW = leaf.PrevW = leaf.RestWOfThisFrame; leaf.Seeded = true; }
                if (steps > 0) Integrate(leaf, steps, fs);
                Write(log, rig, leaf, steps, fs);
            }
        }

        if (Math.Abs(_teleportApplied) > 0f)
        {
            float back = -_teleportApplied;
            _teleportApplied = 0f;
            foreach (var kv in _rigs)
            {
                try { kv.Value.M.transform.position = kv.Value.M.transform.position + kv.Value.M.transform.right * back; } catch { }
            }
        }

        _frame++;
        if (_frame % 120 == 0) FlushCsv();
    }

    private static bool StillWritable(Rig rig, out string why)
    {
        why = "";
        try
        {
            var go = rig.M?.gameObject;
            if (go == null) { why = "model destroyed"; return false; }
            if (!go.activeInHierarchy) { why = "model inactive (despawn/dead)"; return false; }
            for (int i = 0; i < rig.Leaves.Count; i++)
            {
                var l = rig.Leaves[i];
                if (l.T == null) { why = $"{l.Name} destroyed"; return false; }
                if (l.P == null) { why = $"{l.Name} parent destroyed"; return false; }
            }
        }
        catch (Exception e) { why = e.GetType().Name; return false; }
        return true;
    }

    // Reads the animated pose. Our previous write is still in the transform unless something else
    // re-wrote the bone, so it is detected and removed here - the anchor must never include it.
    private static bool ReadAnimated(Leaf leaf)
    {
        Vector3 pos;
        Quaternion rot;
        try { pos = leaf.T.localPosition; rot = leaf.T.localRotation; }
        catch { return false; }

        var p = leaf.T.parent;
        if (p == null) return false;
        if (Ptr(p) != leaf.PPtr) { leaf.P = p; leaf.PPtr = Ptr(p); leaf.Seeded = false; }

        leaf.HeldThisFrame = false;
        if (leaf.Wrote)
        {
            // Tolerance, not exact equality: a Transform stores localPosition/localRotation as a
            // matrix decomposition, so a write/read round trip is not bit-exact (~1.3 um measured).
            bool same = (pos - leaf.WrittenPos).sqrMagnitude <= 4e-10f &&
                        Math.Abs(Quaternion.Dot(rot, leaf.WrittenRot)) >= 0.9999998f;
            leaf.HeldThisFrame = same;
            if (!same)
            {
                leaf.MisMm = (pos - leaf.WrittenPos).magnitude * 1000f;
                leaf.MisDeg = Quaternion.Angle(rot, leaf.WrittenRot);
            }
            else { leaf.MisMm = 0f; leaf.MisDeg = 0f; }
            if (same)
            {
                pos -= leaf.OffLocal;
                rot = Quaternion.Inverse(leaf.AddRot) * rot;
                leaf.Holds++;
            }
            else leaf.Overwrites++;
        }

        leaf.AnimPos = pos;
        leaf.AnimRot = rot;
        try
        {
            leaf.ParentW = leaf.P.position;
            leaf.RestWOfThisFrame = leaf.P.TransformPoint(pos);
            leaf.LengthOfThisFrame = (leaf.RestWOfThisFrame - leaf.ParentW).magnitude;
        }
        catch { return false; }
        return true;
    }

    private static void Integrate(Leaf leaf, int steps, float dt)
    {
        // spring toward the animated pose (the "angle" constraint), straight out of the reference
        // parameters: ω² = angleElasticity * AngleStiffScale
        float omega2 = Mathf.Max(0f, _angleElasticity) * AngleStiffScale;
        Vector3 acc = new Vector3(0f, -GravityMps2 * _gravity, 0f) + (leaf.RestWOfThisFrame - leaf.PosW) * omega2;
        float damp = Mathf.Clamp01((_friction * FrictionScale + _airDrag * AirDragScale) * dt);
        float keep = 1f - damp;
        float dt2 = dt * dt;
        float lenK = 1f - Mathf.Exp(-Mathf.Max(0f, _lengthElasticity) * LengthScale * dt);
        float softK = 1f - Mathf.Exp(-Mathf.Max(0f, _soften) * SoftenScale * dt);

        if (leaf.PendingImpulse.sqrMagnitude > 0f)
        {
            // position-Verlet velocity is (pos - prev)/dt, so a velocity impulse belongs on prev
            leaf.PrevW -= leaf.PendingImpulse * dt;
            leaf.PendingImpulse = Vector3.zero;
        }

        for (int i = 0; i < steps; i++)
        {
            Vector3 disp = (leaf.PosW - leaf.PrevW) * keep;
            Vector3 next = leaf.PosW + disp + acc * dt2;
            leaf.PrevW = leaf.PosW;
            leaf.PosW = next;

            // pendulum length (Lerp)
            Vector3 d = leaf.PosW - leaf.ParentW;
            float len = d.magnitude;
            if (len > 1e-5f && leaf.LengthOfThisFrame > 1e-5f)
            {
                Vector3 want = leaf.ParentW + d * (leaf.LengthOfThisFrame / len);
                leaf.PosW = Vector3.Lerp(leaf.PosW, want, lenK);
            }
            // soften: a small positional pull toward the animated pose (Lerp)
            leaf.PosW = Vector3.Lerp(leaf.PosW, leaf.RestWOfThisFrame, softK);
        }
    }

    private static void Write(IHostContext log, Rig rig, Leaf leaf, int steps, float dt)
    {
        // clamp before anything is written: a bad frame can never produce a visible artefact
        Vector3 offW = leaf.PosW - leaf.RestWOfThisFrame;
        float mag = offW.magnitude;
        bool clamped = false;
        if (mag > _maxOffset)
        {
            leaf.PosW = leaf.RestWOfThisFrame + offW * (_maxOffset / mag);
            leaf.Clamps++;
            clamped = true;
            float now = Time.unscaledTime;
            if (now - leaf.LastClampLog > 1f)
            {
                leaf.LastClampLog = now;
                log.LogWarning($"[jsolve:clamp] rig='{rig.Name}' bone={leaf.Name} offset {mag * 1000f:F1}mm -> {_maxOffset * 1000f:F1}mm " +
                               $"(clamp # {leaf.Clamps}); amplitude stays bounded (teleport / hitch / scene change?)");
            }
        }

        Vector3 offLocal;
        Quaternion add = Quaternion.identity;
        try
        {
            Vector3 curLocal = leaf.P.InverseTransformPoint(leaf.PosW);
            offLocal = (curLocal - leaf.AnimPos) * Mathf.Clamp01(_blend);

            // rotation write-back: swing the bone from the animated direction toward the particle
            Vector3 restDir = leaf.AnimPos;
            if (restDir.sqrMagnitude > 1e-10f && curLocal.sqrMagnitude > 1e-10f)
            {
                Vector3 a = restDir.normalized, b = curLocal.normalized;
                if (Vector3.Angle(a, b) > 0.05f)
                {
                    add = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(a, b), Mathf.Clamp01(_rotGain));
                    float ang = Quaternion.Angle(Quaternion.identity, add);
                    float cap = _maxAngleDeg;
                    if (ang > cap) add = Quaternion.Slerp(Quaternion.identity, add, cap / ang);
                }
            }

            leaf.T.localPosition = leaf.AnimPos + offLocal;
            leaf.T.localRotation = add * leaf.AnimRot;
            leaf.WrittenPos = leaf.T.localPosition;
            leaf.WrittenRot = leaf.T.localRotation;
            leaf.Wrote = true;
            leaf.OffLocal = offLocal;
            leaf.AddRot = add;
        }
        catch (Exception e)
        {
            log.LogWarning($"[jsolve:write] rig='{rig.Name}' bone={leaf.Name} failed: {e.Message}; detaching");
            rig.Dead = true;
            return;
        }

        leaf.Frames++;
        float offMm = offLocal.magnitude * 1000f;
        if (offMm > leaf.MaxOffMm) leaf.MaxOffMm = offMm;

        float devDeg = 0f, devMm = 0f;
        try
        {
            if (!leaf.Orig) { leaf.OrigPos = leaf.AnimPos; leaf.OrigRot = leaf.AnimRot; leaf.Orig = true; }
            devDeg = Quaternion.Angle(leaf.OrigRot, leaf.T.localRotation);
            devMm = (leaf.T.localPosition - leaf.OrigPos).magnitude * 1000f;
        }
        catch { }
        if (devDeg > leaf.MaxDevDeg) leaf.MaxDevDeg = devDeg;
        if (devMm > leaf.MaxDevMm) leaf.MaxDevMm = devMm;
        float angDeg = Quaternion.Angle(Quaternion.identity, add);

        if (_trace && TraceAllowed())
        {
            log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[jsolve:tr] f={0} t={1:0.000}s rig='{2}' bone={3} dev={4:F3}deg/{5:F2}mm off={6:F2}mm ang={7:F3}deg " +
                "offv=({8:F2},{9:F2},{10:F2})mm held={11} mis={12:F4}mm/{13:F4}deg sub={14} clamp={15}",
                _frame, Time.realtimeSinceStartupAsDouble - _startedAt, rig.Name, leaf.Name, devDeg, devMm, offMm, angDeg,
                offLocal.x * 1000f, offLocal.y * 1000f, offLocal.z * 1000f,
                (leaf.HeldThisFrame ? 1 : 0), leaf.MisMm, leaf.MisDeg, steps, clamped ? 1 : 0));
            _traceLines++;
        }

        if (leaf.Frames % 2 == 1)
        {
            _csv.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:0.000},{2},{3:F4},{4:F4},{5:F4},{6:F3},{7:F4},{8},{9},{10},{11}",
                _frame, Time.realtimeSinceStartupAsDouble - _startedAt, leaf.Name,
                Quaternion.Angle(leaf.OrigRot, leaf.AnimRot), (leaf.T.localPosition - leaf.OffLocal - leaf.OrigPos).magnitude * 1000f,
                offMm, Quaternion.Angle(Quaternion.identity, add), leaf.PosW.y, leaf.Holds, leaf.Overwrites,
                leaf.Clamps, clamped ? 1 : 0));
        }
    }

    private static bool TraceAllowed()
    {
        if (_traceLines >= TraceCap)
        {
            if (!_traceCapLogged)
            {
                _traceCapLogged = true;
                try { _log?.LogInfo($"[jsolve] trace cap {TraceCap} lines reached; per-frame trace off (dump/rate still live)"); } catch { }
            }
            return false;
        }
        if (_traceEvery <= 1) return true;
        return _frame % _traceEvery == 0;
    }

    // =====================================================================================
    // impulses / reset / strip
    // =====================================================================================
    private static void ApplyImpulse(IHostContext log, float mps)
    {
        if (Math.Abs(mps) < 1e-6f) return;
        float fs = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
        int n = 0;
        Vector3 dirLogged = Vector3.zero;
        foreach (var kv in _rigs)
        {
            if (kv.Value.Dead) continue;
            foreach (var leaf in kv.Value.Leaves)
            {
                try
                {
                    Vector3 dir = leaf.P.forward;
                    if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
                    dir.Normalize();
                    Vector3 v = dir * mps;
                    if (leaf.Seeded) leaf.PrevW -= v * fs;   // position-Verlet: velocity lives on prev
                    else leaf.PendingImpulse += v;           // seeded on the next frame
                    if (n == 0) dirLogged = dir;
                    n++;
                }
                catch { }
            }
        }
        _lastImpulseAt = Time.realtimeSinceStartupAsDouble;
        _lastImpulseMps = mps;
        log.LogInfo($"[jsolve:impulse] {mps:0.###} m/s to {n} leaf particle(s) dir=({dirLogged.x:F2},{dirLogged.y:F2},{dirLogged.z:F2}) " +
                    $"substep={fs:F4}s (world velocity; the anchor is the animated pose, so this is the only force in a frozen rig)");
    }

    private static void Reset(IHostContext log)
    {
        foreach (var kv in _rigs) Strip(log, kv.Value, "reset");
        foreach (var kv in _rigs)
            foreach (var leaf in kv.Value.Leaves)
            {
                try
                {
                    leaf.OrigPos = leaf.T.localPosition;
                    leaf.OrigRot = leaf.T.localRotation;
                    leaf.Orig = true;
                    leaf.MaxDevDeg = leaf.MaxDevMm = leaf.MaxOffMm = 0f;
                    leaf.Holds = leaf.Overwrites = leaf.Clamps = leaf.Frames = 0;
                    leaf.Seeded = false;
                }
                catch { }
            }
        log.LogInfo($"[jsolve:reset] {_rigs.Count} rig(s): writes stripped, live pose recorded as the original, particles re-seeded");
    }

    // Removes our offset from every leaf. The values used are exactly the animated values we read
    // before writing, so with nothing else touching the skeleton this is bit-exact.
    private static void Strip(IHostContext log, Rig rig, string why)
    {
        if (rig == null) return;
        int n = 0, exact = 0;
        float worstDeg = 0f, worstMm = 0f;
        foreach (var leaf in rig.Leaves)
        {
            if (!leaf.Wrote) continue;
            try
            {
                leaf.T.localPosition = leaf.AnimPos;
                leaf.T.localRotation = leaf.AnimRot;
                leaf.Wrote = false;
                leaf.Seeded = false;
                n++;
                var p = leaf.T.localPosition;
                var q = leaf.T.localRotation;
                float dMm = (p - leaf.AnimPos).magnitude * 1000f;
                float dDeg = Quaternion.Angle(q, leaf.AnimRot);
                if (dMm > worstMm) worstMm = dMm;
                if (dDeg > worstDeg) worstDeg = dDeg;
                if (dMm <= 1e-4f && dDeg <= 1e-3f) exact++;
            }
            catch { }
        }
        if (n > 0)
        {
            string orig = "";
            foreach (var leaf in rig.Leaves)
            {
                try
                {
                    if (!leaf.Orig) continue;
                    float dMm = (leaf.T.localPosition - leaf.OrigPos).magnitude * 1000f;
                    float dDeg = Quaternion.Angle(leaf.T.localRotation, leaf.OrigRot);
                    orig += $" {leaf.Name}:dOrig={dMm:F6}mm/{dDeg:F6}deg";
                }
                catch { }
            }
            log.LogInfo($"[jsolve:restore] rig='{rig.Name}' reason='{why}' restored={n} exact={exact}/{n} " +
                        $"verify={worstMm:F6}mm/{worstDeg:F6}deg{orig}");
        }
    }

    private static void StripAll(IHostContext log, string why)
    {
        foreach (var kv in _rigs) Strip(log, kv.Value, why);
    }

    // =====================================================================================
    // the dead cloth's write-back
    // =====================================================================================
    // SetSkipWriting(true) is forwarded to the cloth process, so a rebuild resets it: the 1 Hz
    // rescan re-asserts it. The prior value is captured once and restored on release, so disabling
    // the solver leaves the cloth exactly as the game built it.
    private static void HoldWriters(IHostContext log, Rig rig, string why)
    {
        int changed = 0;
        foreach (var w in rig.Writers)
        {
            try
            {
                var p = w.Mc.process;
                if (p == null) continue;
                bool now = p.IsSkipWriting();
                if (!w.PriorCaptured) { w.Prior = now; w.PriorCaptured = true; }
                if (now) continue;
                w.Mc.SetSkipWriting(true);
                changed++;
                if (why == "reassert") w.Reasserts++;
            }
            catch { }
        }
        if (changed > 0)
            log.LogInfo($"[jsolve:writer] rig='{rig.Name}' {changed} breast cloth write-back(s) -> skipWriting=1 ({why}); " +
                        $"the cloth is inert here and only rewrites the build pose over the jiggle " +
                        $"cloth='{(rig.Writers.Count > 0 ? rig.Writers[0].Path : "")}'");
    }

    private static void ReleaseWriters(IHostContext log, Rig rig, string why)
    {
        int restored = 0;
        foreach (var w in rig.Writers)
        {
            if (!w.PriorCaptured) continue;
            try
            {
                var p = w.Mc.process;
                if (p == null) continue;
                if (p.IsSkipWriting() != w.Prior) { w.Mc.SetSkipWriting(w.Prior); restored++; }
            }
            catch { }
        }
        if (restored > 0)
            log.LogInfo($"[jsolve:writer] rig='{rig.Name}' {restored} breast cloth write-back(s) restored to the captured value ({why})");
    }

    private static bool BreastCloth(MagicaCloth mc)
    {
        try
        {
            var rb = mc.serializeData?.rootBones;
            for (int i = 0; rb != null && i < rb.Count; i++)
            {
                var t = rb[i];
                if (t == null) continue;
                string n = t.name ?? "";
                if (n.IndexOf("chest_0", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    private static string TPath(Transform t)
    {
        if (t == null) return "null";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        for (int i = 0; p != null && i < 12; i++) { sb.Insert(0, p.name + "/"); p = p.parent; }
        string s = sb.ToString();
        return s.Length <= 140 ? s : "..." + s.Substring(s.Length - 137);
    }

    // =====================================================================================
    // request
    // =====================================================================================
    private static void PollRequest(IHostContext log)
    {
        string path;
        try { path = Path.Combine(log.ScreensDir, RequestName); } catch { return; }
        try
        {
            if (!File.Exists(path))
            {
                if (!_enabled) { _enabled = true; _startedAt = Time.realtimeSinceStartupAsDouble; _nextRescan = 0f; log.LogInfo("[jsolve] opt-out file removed: enabled=1"); }
                return;
            }
            var mt = File.GetLastWriteTimeUtc(path);
            if (mt == _reqMtime) return;
            _reqMtime = mt;

            bool? enabled = null;
            bool dump = false, reset = false;
            string tag = _tag;
            bool trace = _trace;
            int every = _traceEvery;
            float impulse = 0f, impulseAt = 0f;
            bool haveImpulse = false;
            float gravity = _gravity, friction = _friction, angle = _angleElasticity, blend = _blend,
                  air = _airDrag, length = _lengthElasticity, soften = _soften, rot = _rotGain,
                  maxmm = _maxOffset * 1000f, maxdeg = _maxAngleDeg;

            foreach (var raw in File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                bool on = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase);
                switch (k)
                {
                    case "enabled": case "sim": enabled = on; break;
                    case "tag": tag = Sanitize(v); break;
                    case "trace":
                        every = Mathf.Clamp(I(v, on ? 1 : 0), 0, 60);
                        trace = every > 0;
                        if (every == 0) every = 1;
                        break;
                    case "impulse": impulse = F(v, 0f); haveImpulse = true; break;
                    case "impulseat": impulseAt = Mathf.Max(0f, F(v, 0f)); break;
                    case "teleport": _pendingTeleport = F(v, 0f); log.LogInfo($"[jsolve:teleport] armed {_pendingTeleport:0.###}m root jump for the next solve frame (instability check)"); break;
                    case "reset": reset = on; break;
                    case "dump": dump = on; break;
                    case "gravity": gravity = F(v, gravity); break;
                    case "friction": friction = F(v, friction); break;
                    case "angleelasticity": angle = F(v, angle); break;
                    case "blend": blend = F(v, blend); break;
                    case "airdrag": air = F(v, air); break;
                    case "lengthelasticity": length = F(v, length); break;
                    case "soften": soften = F(v, soften); break;
                    case "rotgain": rot = F(v, rot); break;
                    case "maxmm": maxmm = F(v, maxmm); break;
                    case "maxdeg": maxdeg = F(v, maxdeg); break;
                    default: log.LogWarning($"[jsolve:request] key '{k}' unknown; ignored"); break;
                }
            }

            if (tag != _tag) { FlushCsv(); _tag = tag; }
            _traceLines = 0;             // every request-file version is a new trace session
            _traceCapLogged = false;
            _trace = trace; _traceEvery = Mathf.Clamp(every, 1, 60);
            _gravity = gravity; _friction = friction; _angleElasticity = angle; _blend = blend;
            _airDrag = air; _lengthElasticity = length; _soften = soften; _rotGain = Mathf.Clamp01(rot);
            _maxOffset = Mathf.Clamp(maxmm, 1f, 500f) / 1000f;
            _maxAngleDeg = Mathf.Clamp(maxdeg, 1f, 89f);
            LogParams(log, "request");

            if (enabled.HasValue && enabled.Value != _enabled)
            {
                _enabled = enabled.Value;
                if (!_enabled)
                {
                    StripAll(log, "enabled=0");
                    foreach (var kv in _rigs) ReleaseWriters(log, kv.Value, "enabled=0");
                    log.LogInfo("[jsolve] opt-out enabled=0: every offset removed, no writes until enabled=1");
                }
                else
                {
                    _startedAt = Time.realtimeSinceStartupAsDouble;
                    _frame = 0; _accum = 0f; _traceLines = 0;
                    _nextRescan = 0f;
                    log.LogInfo("[jsolve] opt-out enabled=1: re-attaching on the next rescan");
                }
            }
            if (dump) Dump(log);
            if (reset) Reset(log);
            if (haveImpulse && Math.Abs(impulse) > 1e-6f)
            {
                if (impulseAt > 0.01f)
                {
                    _pendingImpulse = impulse; _impulseDueAt = Time.realtimeSinceStartupAsDouble + impulseAt; _impulseArmed = true;
                    _startedAt = Time.realtimeSinceStartupAsDouble;
                    log.LogInfo($"[jsolve:impulse] armed {impulse:0.###} m/s at +{impulseAt:0.##}s");
                }
                else ApplyImpulse(log, impulse);
            }
        }
        catch (Exception e) { try { log.LogWarning($"[jsolve:request] read failed: {e.Message}"); } catch { } }
    }

    private static void LogParams(IHostContext log, string why)
    {
        float omega2 = _angleElasticity * AngleStiffScale;
        float hz = omega2 > 0f ? Mathf.Sqrt(omega2) / (2f * Mathf.PI) : 0f;
        float c = _friction * FrictionScale + _airDrag * AirDragScale;
        float zeta = omega2 > 0f ? c / (2f * Mathf.Sqrt(omega2)) : 0f;
        log.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[jsolve:params] {0}: gravity={1:F3} ({2:F2} m/s2) friction={3:F3} ({4:F2} 1/s) angleElasticity={5:F3} " +
            "(omega2={6:F1}, f={7:F2}Hz) airDrag={8:F3} ({9:F2} 1/s) lengthElasticity={10:F3} soften={11:F3} " +
            "blend={12:F2} rotGain={13:F2} zeta={14:F2} substep={15:F4}s maxSub={16} clamp={17:F1}mm/{18:F1}deg trace={19}/{20}",
            why, _gravity, GravityMps2 * _gravity, _friction, _friction * FrictionScale, _angleElasticity,
            omega2, hz, _airDrag, _airDrag * AirDragScale, _lengthElasticity, _soften, _blend, _rotGain, zeta,
            Time.fixedDeltaTime, MaxSubsteps, _maxOffset * 1000f, _maxAngleDeg, _trace ? 1 : 0, _traceEvery));
    }

    private static void Dump(IHostContext log)
    {
        log.LogInfo($"[jsolve:dump] enabled={(_enabled ? 1 : 0)} rigs={_rigs.Count} frames={_frame} " +
                    $"traceLines={_traceLines} tag='{_tag}' fixedDelta={Time.fixedDeltaTime:F4}s");
        foreach (var kv in _rigs)
        {
            var rig = kv.Value;
            log.LogInfo($"[jsolve:dump] rig='{rig.Name}' model=0x{rig.Key:X} dead={(rig.Dead ? 1 : 0)} leaves={rig.Leaves.Count}");
            foreach (var l in rig.Leaves)
            {
                try
                {
                    log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "[jsolve:dump]   {0} frames={1} holds={2} overwrites={3} clamps={4} maxDev={5:F3}deg/{6:F3}mm " +
                        "maxOff={7:F2}mm now=(pos {8:F4},{9:F4},{10:F4} offv {11:F3},{12:F3},{13:F3}mm) orig=(pos {14:F4},{15:F4},{16:F4})",
                        l.Name, l.Frames, l.Holds, l.Overwrites, l.Clamps, l.MaxDevDeg, l.MaxDevMm, l.MaxOffMm,
                        l.T.localPosition.x, l.T.localPosition.y, l.T.localPosition.z,
                        l.OffLocal.x * 1000f, l.OffLocal.y * 1000f, l.OffLocal.z * 1000f,
                        l.OrigPos.x, l.OrigPos.y, l.OrigPos.z));
                }
                catch (Exception e) { log.LogInfo($"[jsolve:dump]   {l.Name} <{e.Message}>"); }
            }
        }
    }

    private static void RateSummary(IHostContext log)
    {
        if (_rigs.Count == 0) return;
        int leaves = 0, holds = 0, over = 0, clamps = 0;
        float maxDev = 0f, maxOff = 0f;
        foreach (var kv in _rigs)
        {
            if (kv.Value.Dead) continue;
            foreach (var l in kv.Value.Leaves)
            {
                leaves++; holds += l.Holds; over += l.Overwrites; clamps += l.Clamps;
                if (l.MaxDevDeg > maxDev) maxDev = l.MaxDevDeg;
                if (l.MaxOffMm > maxOff) maxOff = l.MaxOffMm;
            }
        }
        log.LogInfo($"[jsolve:rate] enabled={(_enabled ? 1 : 0)} rigs={_rigs.Count} leaves={leaves} frames={_frame} " +
                    $"held={holds} overwritten={over} clamps={clamps} peakDev={maxDev:F3}deg peakOff={maxOff:F2}mm " +
                    $"lastImpulse={( _lastImpulseAt >= 0d ? _lastImpulseMps.ToString("0.###", CultureInfo.InvariantCulture) + "m/s" : "none" )} " +
                    $"fixedDelta={Time.fixedDeltaTime:F4}s fwdSubsteps<= {MaxSubsteps}");
    }

    // =====================================================================================
    // teardown
    // =====================================================================================
    internal static void Shutdown(IHostContext log)
    {
        if (_shutdown) return;
        _shutdown = true;
        try { StripAll(log, "teardown"); } catch { }
        try { foreach (var kv in _rigs) ReleaseWriters(log, kv.Value, "teardown"); } catch { }
        try
        {
            int leaves = 0; float maxDev = 0f, maxOff = 0f;
            foreach (var kv in _rigs)
                foreach (var l in kv.Value.Leaves)
                {
                    leaves++; if (l.MaxDevDeg > maxDev) maxDev = l.MaxDevDeg; if (l.MaxOffMm > maxOff) maxOff = l.MaxOffMm;
                }
            log.LogInfo($"[jsolve] shutdown: rigs={_rigs.Count} leaves={leaves} frames={_frame} " +
                        $"peakDev={maxDev:F3}deg peakOff={maxOff:F2}mm (all offsets removed, bones left exactly as the animation has them)");
        }
        catch { }
        try { FlushCsv(); } catch { }
        _rigs.Clear();
        // Cleared again: this only guards re-entrancy during teardown. A hot reload gets fresh
        // statics from a new assembly load, and if the host ever re-activates this instance the
        // solver must be able to re-attach rather than stay dead.
        _shutdown = false;
    }

    private static void FlushCsv()
    {
        if (_csv.Length == 0 || _log == null) return;
        try
        {
            string path = Path.Combine(_log.ScreensDir, "jsolve_trace_" + Sanitize(_tag) + ".csv");
            File.WriteAllText(path, "frame,t,bone,devOrigDeg,devOrigMm,offMm,addRotDeg,posWy,holds,overwrites,clamps,clamped\n" + _csv);
        }
        catch { }
    }

    // =====================================================================================
    // helpers
    // =====================================================================================
    private static int Ptr(Il2CppObjectBase o)
    {
        try { return o == null ? 0 : (int)o.Pointer.ToInt64(); } catch { return 0; }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "run";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        return sb.Length == 0 ? "run" : sb.ToString();
    }

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int I(string s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
