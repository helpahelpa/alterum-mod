using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using Animancer;
using Animations.ExtendedAnimancer.PersonAnimancer;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Honest census of the player model's own animation source: every Animancer layer, every state
// reachable from it (mixers included, recursively), and every clip the graph/transition library
// can produce. Written as one line per item so the whole set is auditable from LogOutput.log and
// can be diffed between runs.
internal static class NativeCensus
{
    private static readonly string[] PersonLayerNames = { "BaseLayer", "LeftHand", "RightHand", "BothHands", "Legs" };

    internal static string LayerName(int index) =>
        index >= 0 && index < PersonLayerNames.Length ? PersonLayerNames[index] : $"Added{index}";

    // One census pass. Returns the ordered, de-duplicated clip list (by name) that the graph can
    // produce, so the caller can reuse exactly the set that was logged.
    internal static List<AnimationClip> Dump(IHostContext log, PersonAnimancerComponent person, Animator animator)
    {
        var clips = new List<AnimationClip>();
        if (person == null || animator == null)
        {
            log.LogWarning("[emote:census] no person animancer bound; nothing to enumerate");
            return clips;
        }

        string avatar = "null";
        try
        {
            var av = animator.avatar;
            avatar = av != null ? $"'{av.name}' isHuman={av.isHuman} isValid={av.isValid}" : "null";
        }
        catch (Exception e) { avatar = "unreadable(" + e.Message + ")"; }

        int layerCount = -1;
        try { layerCount = person.Layers.Count; } catch (Exception e) { log.LogWarning($"[emote:census] layer count failed: {e.Message}"); }
        log.LogInfo($"[emote:census] BEGIN animator='{animator.name}' avatar={avatar} layers={layerCount} " +
                    $"gameLayers={PersonLayerNames.Length}");

        // --- every layer, its mode, and every state reachable from it -------------------------
        for (int li = 0; li < layerCount; li++)
        {
            AnimancerLayer layer = null;
            try { layer = person.Layers[li]; } catch (Exception e) { log.LogWarning($"[emote:census] layer {li} unreadable: {e.Message}"); }
            if (layer == null) continue;

            bool additive = false;
            string mask = "none";
            float weight = -1f;
            int stateCount = -1;
            try { additive = layer.IsAdditive; } catch { }
            try { var m = layer.Mask; mask = m != null ? m.name : "none"; } catch { }
            try { weight = layer.Weight; } catch { }
            try { stateCount = layer.States.Count; } catch { }
            log.LogInfo($"[emote:census] LAYER {li} name='{LayerName(li)}' additive={additive} mask='{mask}' " +
                        $"weight={weight:F3} states={stateCount} parts=[{MaskParts(layer)}]");

            // Additive/upper-body variants are only meaningful with their mask, so the mask content
            // is part of the census: which humanoid body parts and which rig transforms it enables.
            AvatarMask am = null;
            try { am = layer.Mask; } catch { }
            if (am != null)
            {
                int tc = -1;
                try { tc = am.transformCount; } catch { }
                var sb = new StringBuilder();
                for (int ti = 0; ti < tc && ti < 64; ti++)
                {
                    string tp = "?"; float tw = -1f; bool ta = false;
                    try { tp = am.GetTransformPath(ti); } catch { }
                    try { tw = am.GetTransformWeight(ti); } catch { }
                    try { ta = am.GetTransformActive(ti); } catch { }
                    if (!ta && tw <= 0.0001f) continue;
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(tp).Append('=').Append(tw.ToString("F2"));
                }
                log.LogInfo($"[emote:census] MASK layer={li} name='{am.name}' transforms={tc} active=[{sb}]");
            }

            try
            {
                var states = layer.States;
                for (int si = 0; si < states.Count; si++) DumpState(log, li, additive, states[si], 1);
            }
            catch (Exception e) { log.LogWarning($"[emote:census] layer {li} state walk failed: {e.Message}"); }
        }

        // --- what the graph can produce (includes transitions not yet instantiated as states) --
        var gathered = new Il2CppSystem.Collections.Generic.List<AnimationClip>();
        bool gatheredOk = false;
        try
        {
            // GatherAnimationClips wants ICollection<T>; the interop List is the same native
            // object under another interface, so re-wrap it rather than allocate a second list.
            var coll = gathered.TryCast<Il2CppSystem.Collections.Generic.ICollection<AnimationClip>>();
            person.GatherAnimationClips(coll);
            gatheredOk = true;
        }
        catch (Exception e) { log.LogWarning($"[emote:census] GatherAnimationClips failed: {e.Message}"); }
        int gatheredCount = -1;
        try { gatheredCount = gathered.Count; } catch { }
        log.LogInfo($"[emote:census] gather source=AnimancerComponent.GatherAnimationClips ok={gatheredOk} count={gatheredCount}");

        var list = new Il2CppSystem.Collections.Generic.List<AnimationClip>();
        bool listOk = false;
        try { person.GetAnimationClips(list); listOk = true; }
        catch (Exception e) { log.LogWarning($"[emote:census] GetAnimationClips failed: {e.Message}"); }
        int listCount = -1;
        try { listCount = list.Count; } catch { }
        log.LogInfo($"[emote:census] gather source=AnimancerComponent.GetAnimationClips ok={listOk} count={listCount}");

        // --- the clip set, de-duplicated by name, with the origins that produced it ------------
        var seen = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
        var origins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(AnimationClip c, string origin)
        {
            if (c == null) return;
            string n = c.name;
            if (string.IsNullOrEmpty(n)) n = "<unnamed>";
            if (!seen.ContainsKey(n)) { seen[n] = c; origins[n] = new List<string>(); }
            if (!origins[n].Contains(origin)) origins[n].Add(origin);
        }

        if (gatheredOk)
            for (int i = 0; i < gathered.Count; i++) { try { Add(gathered[i], "graph"); } catch { } }
        if (listOk)
            for (int i = 0; i < list.Count; i++) { try { Add(list[i], "transitions"); } catch { } }

        var names = new List<string>(seen.Keys);
        names.Sort(StringComparer.Ordinal);
        log.LogInfo($"[emote:census] CLIPS unique={names.Count}");
        for (int i = 0; i < names.Count; i++)
        {
            var c = seen[names[i]];
            var o = origins[names[i]];
            var sb = new StringBuilder();
            sb.Append($"[emote:census] CLIP {i:000} '").Append(names[i]).Append("'");
            sb.Append(" len=").Append(Fmt(c.length));
            sb.Append(" loop=").Append(Try(() => c.isLooping) ? "1" : "0");
            sb.Append(" humanMotion=").Append(Try(() => c.humanMotion) ? "1" : "0");
            sb.Append(" empty=").Append(Try(() => c.empty) ? "1" : "0");
            sb.Append(" legacy=").Append(Try(() => c.legacy) ? "1" : "0");
            sb.Append(" frameRate=").Append(Fmt(c.frameRate));
            sb.Append(" origins=").Append(string.Join("+", o.ToArray()));
            log.LogInfo(sb.ToString());
            clips.Add(c);
        }

        // --- every AnimationClip loaded anywhere in the process (reference census) -------------
        // Not part of the player graph: this is what else the game has loaded, so a clip that is
        // visible in memory but unreachable from these layers is documented, not silently dropped.
        var loaded = SceneCache.Scan(Il2CppType.Of<AnimationClip>());
        var uniq = new List<AnimationClip>();
        var loadedNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < loaded.Length; i++)
        {
            AnimationClip c = null;
            try { c = loaded[i].TryCast<AnimationClip>(); } catch { }
            if (c == null) continue;
            string n = c.name;
            if (string.IsNullOrEmpty(n) || !loadedNames.Add(n)) continue;
            uniq.Add(c);
        }
        log.LogInfo($"[emote:census] LOADED total={uniq.Count} (all AnimationClip objects in memory, de-duplicated by name)");
        uniq.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        for (int i = 0; i < uniq.Count; i++)
        {
            var c = uniq[i];
            log.LogInfo($"[emote:census] LOADEDCLIP {i:000} '{c.name}' len={Fmt(c.length)} loop={(Try(() => c.isLooping) ? 1 : 0)} " +
                        $"humanMotion={(Try(() => c.humanMotion) ? 1 : 0)} inGraph={(seen.ContainsKey(c.name) ? 1 : 0)}");
        }

        log.LogInfo($"[emote:census] END layers={layerCount} clips={clips.Count} loaded={uniq.Count} " +
                    "(bone-drive counts are emitted by nativeprobe/nativesweep, which sample the clip)");
        return clips;
    }


    private static readonly AvatarMaskBodyPart[] AllParts =
    {
        AvatarMaskBodyPart.Root, AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head,
        AvatarMaskBodyPart.LeftLeg, AvatarMaskBodyPart.RightLeg, AvatarMaskBodyPart.LeftArm,
        AvatarMaskBodyPart.RightArm, AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers,
        AvatarMaskBodyPart.LeftFootIK, AvatarMaskBodyPart.RightFootIK, AvatarMaskBodyPart.LeftHandIK,
        AvatarMaskBodyPart.RightHandIK,
    };

    // Humanoid body parts a layer's AvatarMask enables. This is the mask that decides how an
    // upper-body clip must be layered onto the body.
    private static string MaskParts(AnimancerLayer layer)
    {
        try
        {
            var m = layer.Mask;
            if (m == null) return "no-mask(full body)";
            var sb = new StringBuilder();
            foreach (var part in AllParts)
            {
                bool on = false;
                try { on = m.GetHumanoidBodyPartActive(part); } catch { }
                if (!on) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(part.ToString());
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }
        catch (Exception e) { return "mask-unreadable:" + e.Message; }
    }

    // Recursive so blend-tree/mixer children are counted too: those are the locomotion states the
    // layers actually hold, and a layer's top-level state may be nothing but a mixer.
    private static void DumpState(IHostContext log, int layerIndex, bool layerAdditive, AnimancerState state, int depth)
    {
        if (state == null || depth > 6) return;
        string type = "?", clipName = "", path = "";
        float len = -1f, weight = -1f;
        bool loop = false, playing = false;
        int children = 0;
        try { type = state.GetIl2CppType().Name; } catch { }
        try { var c = state.Clip; clipName = c != null ? c.name : ""; } catch { }
        try { len = state.Length; } catch { }
        try { loop = state.IsLooping; } catch { }
        try { weight = state.Weight; } catch { }
        try { playing = state.IsPlaying; } catch { }
        try { path = state.GetPath(); } catch { }
        try { children = state.ChildCount; } catch { }

        log.LogInfo($"[emote:census] STATE layer={layerIndex}({LayerName(layerIndex)}) additive={layerAdditive} " +
                    $"depth={depth} type='{type}' clip='{clipName}' len={Fmt(len)} loop={(loop ? 1 : 0)} " +
                    $"playing={(playing ? 1 : 0)} weight={weight:F3} children={children} node='{path}'");

        for (int i = 0; i < children; i++)
        {
            AnimancerState child = null;
            try { child = state.GetChild(i); } catch (Exception e) { log.LogWarning($"[emote:census] child {i} of '{path}' unreadable: {e.Message}"); }
            DumpState(log, layerIndex, layerAdditive, child, depth + 1);
        }
    }

    private static string Fmt(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    // Property reads on a dead/odd asset must never abort the census.
    private static bool Try(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }
}
