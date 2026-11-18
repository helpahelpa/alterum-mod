using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using MagicaCloth2;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Read-only introspection of the shipped MagicaCloth2 runtime.
//
// The managed interop assembly exposes the *internal* manager surface (MagicaManager, TeamManager,
// TeamData, ClothProcess) even though the simulation itself is native IL2CPP code. Every member
// used here is a plain getter or value field on a proxy type, so it can be called from a plugin.
//
// What we need to separate, per team:
//   * is the per-team simulation actually stepping?  TeamData.updateCount / nowUpdateTime
//   * if not, which gate stopped it?                TeamData/ClothProcess culling + skip flags
// Everything is guarded: any member that the runtime refuses throws and is reported as such, it
// never takes the probe down.
internal static class ClothInternals
{
    private static float _nextCensus;
    private sealed class Prev
    {
        public int Upd; public bool Seen;
        public readonly Dictionary<int, Quaternion> Rot = new();
        public float Motion = -1f; public int Bones;
    }
    private static readonly Dictionary<string, Prev> _prev = new();
    internal static string LastTeamError = "";

    internal static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        for (int i = 0; p != null && i < 12; i++) { sb.Insert(0, p.name + "/"); p = p.parent; }
        string s = sb.ToString();
        return s.Length <= 96 ? s : "..." + s.Substring(s.Length - 93);
    }

    private static string T(Func<string> f)
    {
        try { return f(); } catch (Exception e) { return $"<{e.GetType().Name}: {e.Message}>"; }
    }

    // ---- manager-wide ---------------------------------------------------------------------

    internal static string Global()
    {
        var sb = new StringBuilder();
        sb.Append(T(() =>
        {
            var t = MagicaManager.Time;
            return t == null
                ? " time=null"
                : $" simDt={t.SimulationDeltaTime:F4} maxDt={t.MaxDeltaTime:F3} gts={t.GlobalTimeScale:F2}" +
                  $" freq={t.simulationFrequency} maxSteps={t.maxSimulationCountPerFrame} loc={(int)t.updateLocation}" +
                  $" fixed={t.FixedUpdateCount} tValid={t.isValid}";
        }));
        sb.Append(T(() =>
        {
            var s = MagicaManager.Simulation;
            return s == null ? " sim=null" : $" steps={s.SimulationStepCount} particles={s.ParticleCount}";
        }));
        sb.Append(T(() => $" playing={MagicaManager.isPlaying}"));
        sb.Append(T(() => $" focused={Application.isFocused} runBg={Application.runInBackground} screen={Screen.width}x{Screen.height}"));
        sb.Append(T(() => $" timeScale={Time.timeScale:F2} dt={Time.deltaTime:F4} udt={Time.unscaledDeltaTime:F4} t={Time.time:F2} ut={Time.unscaledTime:F2} frame={Time.frameCount} fixed={Time.fixedDeltaTime:F4} capture={Time.captureDeltaTime:F4}"));
        return sb.ToString();
    }

    // Manager health + the visibility dictionaries camera culling actually decides from.
    internal static string Managers()
    {
        var sb = new StringBuilder();
        sb.Append(T(() => $" time={MagicaManager.Time != null && MagicaManager.Time.isValid}"));
        sb.Append(T(() => $" team={MagicaManager.Team.IsValid()} tc={MagicaManager.Team.TeamCount}/{MagicaManager.Team.TrueTeamCount}/{MagicaManager.Team.ActiveTeamCount} updMax={MagicaManager.Team.TeamMaxUpdateCount}"));
        sb.Append(T(() => $" cloth={MagicaManager.Cloth.isValid} boneCloths={MagicaManager.Cloth.boneClothSet?.Count} meshCloths={MagicaManager.Cloth.meshClothSet?.Count}"));
        sb.Append(T(() => $" simSteps={MagicaManager.Simulation.SimulationStepCount} particles={MagicaManager.Simulation.ParticleCount}"));
        sb.Append(T(() => $" render={MagicaManager.Render.IsValid()} bone={MagicaManager.Bone.IsValid()} vmesh={MagicaManager.VMesh.IsValid()}" +
                           $" sim={MagicaManager.Simulation.IsValid()} coll={MagicaManager.Collider.IsValid()} wind={MagicaManager.Wind.IsValid()} pre={MagicaManager.PreBuild.IsValid()}"));
        sb.Append(T(() =>
        {
            var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            bool reg = MagicaManager.CheckRegist(ref loop);
            return $" loopReg={reg}";
        }));
        sb.Append(T(() =>
        {
            var a = MagicaManager.Cloth.animatorVisibleDict;
            if (a == null) return " aVis=null";
            var b = new StringBuilder($" aVis={a.Count}[");
            int n = 0;
            foreach (var kv in a) { if (n++ >= 6) break; b.Append($"{kv.Key}={(kv.Value ? 1 : 0)},"); }
            b.Append(']');
            return b.ToString();
        }));
        sb.Append(T(() =>
        {
            var a = MagicaManager.Cloth.rendererVisibleDict;
            if (a == null) return " rVis=null";
            var b = new StringBuilder($" rVis={a.Count}[");
            int n = 0;
            foreach (var kv in a) { if (n++ >= 6) break; b.Append($"{kv.Key}={(kv.Value ? 1 : 0)},"); }
            b.Append(']');
            return b.ToString();
        }));
        return sb.ToString();
    }

    // ---- one cloth ------------------------------------------------------------------------

    internal static string Proc(MagicaCloth mc)
    {
        if (mc == null) return " proc=null";
        var sb = new StringBuilder();
        var p = T(() => { var q = mc.process; return q == null ? "null" : "ok"; });
        if (p != "ok") return $" proc=<{p}>";
        ClothProcess cp;
        try { cp = mc.process; } catch (Exception e) { return $" proc=<{e.GetType().Name}>"; }

        sb.Append(T(() => $" team={cp.TeamId}"));
        sb.Append(T(() => { var r = cp.result; return $" result={(int)r.Result}"; }));
        sb.Append(T(() => $" clothType={(int)cp.clothType}"));
        sb.Append(T(() => $" run={cp.IsRunning()}"));
        sb.Append(T(() => $" valid={cp.IsValid()}"));
        sb.Append(T(() => $" enable={cp.IsEnable}"));
        sb.Append(T(() => $" camInvis={cp.IsCameraCullingInvisible()}"));
        sb.Append(T(() => $" camKeep={cp.IsCameraCullingKeep()}"));
        sb.Append(T(() => $" distInvis={cp.IsDistanceCullingInvisible()}"));
        sb.Append(T(() => $" skipWrite={cp.IsSkipWriting()}"));
        sb.Append(T(() => $" camMode={(int)cp.cameraCullingMode} camOldInvis={cp.cameraCullingOldInvisible}"));

        // Which objects did MagicaCloth decide to test for camera visibility?
        sb.Append(T(() =>
        {
            var a = cp.cameraCullingAnimator;
            if (a == null) return " camAnim=none";
            return $" camAnim='{PathOf(a.transform)}' en={a.enabled}/{a.gameObject.activeInHierarchy} cull={a.cullingMode}";
        }));
        sb.Append(T(() =>
        {
            var rs = cp.cameraCullingRenderers;
            if (rs == null) return " camRends=null";
            var b = new StringBuilder($" camRends={rs.Count}[");
            for (int i = 0; i < rs.Count && i < 6; i++)
            {
                var r = rs[i];
                if (r == null) { b.Append("null,"); continue; }
                bool vis = false;
                try { vis = r.isVisible; } catch { }
                b.Append($"{r.name}:en={r.enabled}/{r.gameObject.activeInHierarchy}:vis={vis},");
            }
            if (rs.Count > 6) b.Append("...");
            b.Append(']');
            return b.ToString();
        }));

        // The runtime "animation properties": these override the serialized parameters every frame.
        // If a weight is 0 here the cloth can step all it likes and still write the rest pose back.
        sb.Append(T(() => $" bwP={mc.blendWeightProperty:F3}/{mc._blendWeightProperty:F3}" +
                           $" gP={mc.gravityProperty:F3}/{mc._gravityProperty:F3}" +
                           $" dP={mc.dampingProperty:F3}/{mc._dampingProperty:F3}" +
                           $" aP={mc.animationPoseRatioProperty:F3}/{mc._animationPoseRatioProperty:F3}" +
                           $" wI={mc.worldInertiaProperty:F3}/{mc._worldInertiaProperty:F3}" +
                           $" lI={mc.localInertiaProperty:F3}/{mc._localInertiaProperty:F3}" +
                           $" windP={mc.windInfluenceProperty:F3}/{mc._windInfluenceProperty:F3}"));

        // Per-team simulation counters. This is the ground truth for "is it stepping".
        sb.Append(Team(cp));
        return sb.ToString();
    }

    // The interop exposes TeamManager.GetTeamDataRef(int) as a ref return, but the generated proxy
    // returns `ref *(TeamData*)ptr`: a reference to the *native* struct, not to a managed wrapper.
    // Reading its fields directly dereferences the wrong thing (ObjectCollectedException), so copy
    // the struct bytes into a freshly boxed wrapper and read that. TeamData is pure blittable data.
    internal static TeamManager.TeamData TeamCopy(int teamId)
    {
        ref TeamManager.TeamData src = ref MagicaManager.Team.GetTeamDataRef(teamId);
        var cls = Il2CppClassPointerStore<TeamManager.TeamData>.NativeClassPtr;
        uint align = 0;
        int size = IL2CPP.il2cpp_class_value_size(cls, ref align);
        var box = new TeamManager.TeamData();
        IntPtr dst = IL2CPP.Il2CppObjectBaseToPtrNotNull(box);
        unsafe
        {
            void* sp = Unsafe.AsPointer(ref src);
            Buffer.MemoryCopy(sp, (void*)dst, size, size);
        }
        return box;
    }

    internal static string Team(ClothProcess cp)
    {
        return T(() =>
        {
            int id = cp.TeamId;
            var tm = MagicaManager.Team;
            if (tm == null) return " teamMgr=null";
            TeamManager.TeamData td = TeamCopy(id);
            if (System.Object.ReferenceEquals(td, null)) return " td=null";
            return $" upd={td.updateCount} skip={td.skipCount} now={td.nowUpdateTime:F2} dt={td.frameDeltaTime:F4}" +
                   $" ts={td.timeScale:F2} nowTs={td.nowTimeScale:F2} t={td.time:F2}/{td.oldTime:F2} ft={td.frameUpdateTime:F2}" +
                   $" en={td.IsEnable} val={td.IsValid}" +
                   $" run={td.IsRunning} step={td.IsStepRunning} cull={td.IsCullingInvisible}" +
                   $" camInvis={td.IsCameraCullingInvisible} camKeep={td.IsCameraCullingKeep}" +
                   $" distInvis={td.IsDistanceCullingInvisible} reset={td.IsReset} proc={td.IsProcess}" +
                   $" umode={(int)td.updateMode}/{(int)td.originalUpdateMode} fixedUpd={td.IsFixedUpdate} unscaled={td.IsUnscaled}" +
                   $" wI={td.velocityWeight:F3} wD={td.distanceWeight:F3} wB={td.blendWeight:F3} poseR={td.animationPoseRatio:F3}" +
                   $" gRatio={td.gravityRatio:F3} gDot={td.gravityDot:F3} force={(int)td.forceMode}" +
                   $" pc={td.particleChunk.dataLength} proxy={td.proxyCommonChunk.dataLength} baseLine={td.baseLineChunk.dataLength}";
        });
    }

    internal static int UpdateCount(MagicaCloth mc)
    {
        try
        {
            var cp = mc.process;
            if (cp == null) return int.MinValue;
            TeamManager.TeamData td = TeamCopy(cp.TeamId);
            return td == null ? int.MinValue : td.updateCount;
        }
        catch (Exception e) { LastTeamError = $"{e.GetType().Name}: {e.Message}"; return int.MinValue; }
    }

    // Mean per-bone local-rotation delta (degrees) over the cloth's own used transforms since the
    // previous call. Independent of any counter: a cloth that is simulating moves its bones, a
    // restored/frozen one does not. Bones the Animator animates also count, which is why the
    // interesting cases are looked at with the Animator disabled and in the census detail.
    private static float Motion(MagicaCloth mc, Prev prev, out int bones)
    {
        bones = 0;
        var set = mc.GetUsedTransform();
        if (set == null) return -1f;
        double sum = 0; int n = 0;
        foreach (var t in set)
        {
            if (t == null) continue;
            int key;
            try { key = t.GetInstanceID(); } catch { continue; }
            var q = t.localRotation;
            if (prev.Rot.TryGetValue(key, out var old)) { sum += Quaternion.Angle(old, q); n++; }
            prev.Rot[key] = q;
            bones++;
        }
        return n == 0 ? -1f : (float)(sum / n);
    }

    // ---- scene census ---------------------------------------------------------------------

    // Every MagicaCloth in memory, with its per-team update counter and the delta since the last
    // census. A team whose counter grows is being simulated; one that stays put is gated.
    internal static void Census(IHostContext log, string tag)
    {
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object> arr;
        try { arr = SceneCache.Scan(Il2CppType.Of<MagicaCloth>()); }
        catch (Exception e) { log.LogWarning($"[census] '{tag}' scan failed: {e.Message}"); return; }

        int total = 0, running = 0, stepping = 0;
        var detail = new StringBuilder();
        for (int i = 0; i < arr.Length; i++)
        {
            MagicaCloth mc;
            try { mc = arr[i].TryCast<MagicaCloth>(); } catch { continue; }
            if (mc == null) continue;
            total++;
            try
            {
                var go = mc.gameObject;
                if (go == null) continue;
                var cp = mc.process;
                string path = PathOf(mc.transform);
                int upd = UpdateCount(mc);
                if (!_prev.TryGetValue(path, out var prev)) { prev = new Prev(); _prev[path] = prev; }
                int delta = prev.Seen ? upd - prev.Upd : int.MinValue;
                prev.Upd = upd; prev.Seen = true;
                float mot = Motion(mc, prev, out int motionBones);
                prev.Motion = mot; prev.Bones = motionBones;

                bool run = false; int mode = -1, rends = -1, ctype = -1; bool camInvis = false, skip = false;
                try { run = cp != null && cp.IsRunning(); } catch { }
                try { ctype = cp != null ? (int)cp.clothType : -1; } catch { }
                try { mode = cp != null ? (int)cp.cameraCullingMode : -1; } catch { }
                try { camInvis = cp != null && cp.IsCameraCullingInvisible(); } catch { }
                try { skip = cp != null && cp.IsSkipWriting(); } catch { }
                try { rends = cp?.cameraCullingRenderers?.Count ?? -1; } catch { }
                if (run) running++;
                if (delta > 0) stepping++;
                bool active = false;
                try { active = go.activeInHierarchy; } catch { }
                string rv = "";
                try
                {
                    var rs = cp?.cameraCullingRenderers;
                    var dict = MagicaManager.Cloth.rendererVisibleDict;
                    if (rs != null && dict != null && rs.Count > 0)
                    {
                        var b = new StringBuilder(" rvis=[");
                        for (int k = 0; k < rs.Count && k < 4; k++)
                        {
                            var r = rs[k];
                            if (r == null) { b.Append("null,"); continue; }
                            bool has = dict.TryGetValue(r.GetInstanceID(), out bool v);
                            b.Append(has ? (v ? "1" : "0") : "?").Append(',');
                        }
                        b.Append(']');
                        rv = b.ToString();
                    }
                }
                catch { }
                detail.Append($"\n  #{i} ctype={ctype} upd={upd} d={delta} mot={mot:F4} bones={motionBones} run={run} " +
                              $"camInvis={camInvis} camMode={mode} skip={skip} rends={rends} active={active}{rv} '{path}'");
            }
            catch (Exception e) { log.LogWarning($"[census] item {i} failed: {e.Message}"); }
        }
        log.LogInfo($"[census] '{tag}' cloths={total} running={running} stepping={stepping} teamErr='{LastTeamError}'{detail}");
    }

    // Force every cloth in the scene, once. Used as the control: if no cloth's bones move after
    // this, no cloth in this build is simulating, whatever the gate is.
    internal static void KickAll(IHostContext log)
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<MagicaCloth>());
            int n = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                var mc = arr[i].TryCast<MagicaCloth>();
                if (mc == null) continue;
                try
                {
                    var cp = mc.process;
                    if (cp == null || !cp.IsRunning()) continue;
                    var t = mc.transform;
                    var dir = t != null ? t.right : Vector3.right;
                    mc.AddForce(dir, 30f, ClothForceMode.VelocityChange);
                    n++;
                }
                catch { }
            }
            if (Time.unscaledTime - _lastKickLog > 2f) { _lastKickLog = Time.unscaledTime; log.LogInfo($"[jiggle:kick] AddForce sent to {n} cloth(s)"); }
        }
        catch (Exception e) { log.LogWarning($"[jiggle:kick] failed: {e.Message}"); }
    }

    private static float _lastKickLog;

    internal static void TickCensus(IHostContext log, bool wanted, string tag)
    {
        if (!wanted) return;
        if (Time.unscaledTime < _nextCensus) return;
        _nextCensus = Time.unscaledTime + 1.0f;
        Census(log, tag);
    }
}
