using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// The wardrobe's model roster, as DATA.
//
// Every model the wardrobe offers is one `ModelSpec` here: which baked blob and texture to embed,
// which portraits to draw in the tab/HUD slot, which of the two existing renderer paths to drive,
// and — the part that used to be branching code — the table that turns a bone/piece id into one of
// the game skeleton's bones:
//
//   ModelKind.Rigid   -> SahurModel. One rigid piece per bone region, 1.0 weights, host body hidden
//                        and restored on switch. The table is `RegionBones`: region id (the bake's
//                        per-vertex byte) -> game bone name, in the order the bake numbered them.
//   ModelKind.Rigged  -> SahurRig. The model keeps its own skeleton and is delta-retargeted from the
//                        game rig every LateUpdate. The table is `BoneMap`: { model bone, game bone }.
//
// Adding a model is therefore a spec + its embedded resources, no new renderer mechanism and no
// new branch in the modules. `SahurModel`/`SahurRig` are singleton modules (one live body of each
// kind at a time, which is what "one wardrobe selection" means); each keeps one parsed asset per
// spec key, so switching between two rigid models tears the old body down and builds the new one.

internal enum ModelKind { Rigid, Rigged }

internal sealed class ModelSpec
{
    public string Slug;                  // asset cache key and log tag
    public string Label;                 // wardrobe tab label, also the PlayerModelData name (visible text:
                                         // no demo marker -- DemoOnly/DemoReason carry the licence facts)
    public string[] AdoptNames;          // names this entry may adopt on a hot reload: the previous
                                         // generation's labels FIRST, then the current one, so a live
                                         // entry is renamed rather than duplicated when Label changes
    public ModelKind Kind;

    public string BlobSuffix;            // embedded .mset (rigid) / .rset (rigged)
    // One albedo (+ optional normal) per submesh MATERIAL index, in the bake's material order.
    // Rigid bakes have a single material; Capuchino's rigged bake has two (Head 0, Body 1).
    public string[] AlbedoSuffixes;
    public string[] NormalSuffixes;      // same length, entries may be null
    public string AlbedoSuffix => AlbedoSuffixes != null && AlbedoSuffixes.Length > 0 ? AlbedoSuffixes[0] : null;
    public int Materials => AlbedoSuffixes == null || AlbedoSuffixes.Length == 0 ? 1 : AlbedoSuffixes.Length;
    public string PortraitSuffix;        // full-body portrait (tab row, tall slot)
    public string BustSuffix;            // head+shoulders (HUD square slot)

    // Rigid only. `true` = the bake is still in the source's handedness and needs the runtime's
    // 180 deg yaw (decided per bake in its manifest.json: mirror_x false -> 180). The variants2
    // bakes are mirrored to the game's handedness at bake time, so they run with no yaw.
    public bool Yaw180;
    // Rigid only, metres: lift the mesh so its feet land on the model root's floor (y=0). The
    // bake's origin is wherever the source's was - the pigeon's stood on it (min y -0.5 cm) - and a
    // bake whose source hung below its own origin would otherwise sit half a body underground.
    // Taken from the bake manifest's bbox, not guessed at runtime.
    public float YOffset;
    public string[] RegionBones;         // region id -> game bone, in bake order
    public bool Bat;                     // build the classic bat prop (Sahur only, it is the joke)

    // Rigged only. { model bone, game bone } pairs. Anatomy, not names: the game side is what the
    // shipped rig uses (UE-style), the model side is whatever the bake emitted.
    public string[][] BoneMap;

    // Hat/cosmetic anchor for this model (measured; see HatAnchors below). Optional: when null the
    // measured default for the slug is used, and when BOTH are absent the fit falls back to the
    // native head measurement (which is wrong for these bodies: it measures the hidden game head).
    // Set this to override the measured default for one entry.
    public HatAnchor AnchorOverride;

    // Object names in the scene are `Tag + "_Body" | "_PreviewBody" | "_Skeleton" | "_Bat"`. The
    // "Sahur" prefix is the mod's own-object marker: MirrorPlayerVisibility / CosmeticMarkers /
    // SahurPreview skip anything starting with it, so imported models keep the prefix too.
    public string Tag;

    // Licence. DemoOnly models are integrated for the local demo only and must never ship: this
    // flag is asserted in the log, marked in the csproj next to the resources, and listed in
    // notes/wardrobe-variants2.md. DemoReason is why, verbatim from the bake's manifest.json
    // (`demo_reason`) - it is logged with the warning so the reason is not a second, drifting copy.
    public bool DemoOnly;
    public string DemoReason;
    public string Credit;

    internal bool IsRigged => Kind == ModelKind.Rigged;
    internal string BodyName => Tag + (IsRigged ? "_Body" : "_Body");
    internal string PreviewBodyName => Tag + "_PreviewBody";
    internal string MeshName => Tag + "_Mesh";
}

// One cosmetic anchor: the transform applied on top of the game `head` bone, in that bone's local
// frame. Every cosmetic (hats today, chest/hip/arm later) is parented as `head bone x HatAnchor`,
// so one measured entry per model corrects all of them; the cosmetic's own baked local offsets stay
// on top of it.
internal sealed class HatAnchor
{
    public Vector3 Offset;      // head-local position of the cosmetic root (metres)
    public Quaternion Rotation; // head-local rotation of the cosmetic root (source-head frame)
    public float Scale;         // uniform scale: model head width / source head width

    // The rotation from the cosmetic pack's source-head frame into the game head bone's local
    // frame: inverse(native head bind rotation) * source-head rotation. Both factors are constants
    // (the pack's source head and the native skeleton's rest head), so every entry shares it; it is
    // stored per anchor anyway so a model that needs a different hat frame can override it.
    public static readonly Quaternion DefaultRotation =
        new Quaternion(0.48857f, -0.47689f, 0.52209f, 0.51116f);
}

internal static class ModelRoster
{
    // ---------------------------------------------------------------- measured hat anchors
    //
    // Measured, not hand-tuned: for each baked asset the vertices whose dominant bone is the head
    // (rigid: the `head` region piece; rigged: weight >= 0.5 on the model's own head bone) were
    // taken in model-root space at bind, moved into the game head bone's local frame with the
    // native head bind transform, and reduced to the same four numbers the native fit measures
    // (width, top, centre x/z). Offset/Scale below are that box expressed as the cosmetic root's
    // placement: Offset = R * (A_head - A_source * Scale) with R = DefaultRotation, A_source the
    // pack's source head anchor. Scale = head width / source head width.
    //
    // Tool + per-model table: .temp/hatanchor/measure.py (anchors.json in the same folder). The
    // live cross-check is the `hatanchor.request` probe in CosmeticsRuntime.
    private struct Entry
    {
        public string Slug;
        public Vector3 Offset;
        public float Scale;
        public Entry(string slug, Vector3 offset, float scale) { Slug = slug; Offset = offset; Scale = scale; }
    }

    private static readonly Entry[] MeasuredHatAnchors =
    {
        // rigged: Sahur (Roblox) ~2.25x a native head wide, so the hat scales with it
        new Entry("sahur-roblox", new Vector3(0.2918f, -0.0046f, -0.0040f), 0.5300f),
        // rigid: classic Sahur, oversized wooden head
        new Entry("sahur-classic", new Vector3(0.2459f, -0.0194f, 0.0012f), 0.4656f),
        // rigid pigeon, head+hat region
        new Entry("golubiro", new Vector3(0.3478f, 0.0074f, 0.0049f), 0.6148f),
        // rigged Capuchino: the coffee-cup head is the widest in the roster
        new Entry("capuchino", new Vector3(0.4777f, 0.0111f, 0.0564f), 0.7590f),
        // the eleven Nordbo bakes, all normalised to 1.8 m from taller/shorter sources
        new Entry("tc2-scout", new Vector3(0.0717f, -0.0274f, -0.0135f), 0.2537f),
        new Entry("tc2-soldier", new Vector3(0.0888f, 0.0044f, -0.0027f), 0.2760f),
        new Entry("tc2-pyro", new Vector3(0.0550f, -0.0415f, 0.0108f), 0.2305f),
        new Entry("tc2-demoman", new Vector3(0.0253f, -0.0235f, 0.0030f), 0.1935f),
        new Entry("tc2-heavy", new Vector3(0.0521f, -0.0170f, 0.0032f), 0.2282f),
        new Entry("tc2-engineer", new Vector3(0.0884f, 0.0289f, -0.0010f), 0.2763f),
        new Entry("tc2-medic", new Vector3(0.0213f, -0.0125f, 0.0020f), 0.1874f),
        new Entry("tc2-sniper", new Vector3(0.1403f, 0.0426f, 0.0146f), 0.2570f),
        new Entry("tc2-spy", new Vector3(0.0332f, 0.0152f, 0.0007f), 0.2028f),
        new Entry("tc2-saxton", new Vector3(0.1787f, 0.0518f, -0.0041f), 0.3956f),
        new Entry("peter-griffin", new Vector3(0.1979f, 0.0208f, 0.0022f), 0.4191f),
    };

    // Measured anchor of the entry with this slug, plus the spec's own override when it has one.
    internal static HatAnchor AnchorFor(ModelSpec spec)
    {
        if (spec == null) return null;
        if (spec.AnchorOverride != null) return spec.AnchorOverride;
        for (int i = 0; i < MeasuredHatAnchors.Length; i++)
        {
            if (!string.Equals(MeasuredHatAnchors[i].Slug, spec.Slug, StringComparison.Ordinal)) continue;
            return new HatAnchor
            {
                Offset = MeasuredHatAnchors[i].Offset,
                Rotation = HatAnchor.DefaultRotation,
                Scale = MeasuredHatAnchors[i].Scale,
            };
        }
        return null;
    }

    // The roster entry currently worn, recognised by its own body object under the local player
    // model root (`Tag + "_Body"`). Null for a native body.
    internal static ModelSpec WornSpec(Transform modelRoot)
    {
        if (modelRoot == null) return null;
        try
        {
            var all = modelRoot.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var t = all[i].TryCast<Transform>();
                if (t == null) continue;
                string n = t.gameObject.name ?? "";
                if (n.Length == 0) continue;
                for (int k = 0; k < All.Length; k++)
                {
                    var spec = All[k];
                    if (!string.Equals(n, spec.BodyName, StringComparison.Ordinal)) continue;
                    if (t.gameObject.activeInHierarchy) return spec;
                }
            }
        }
        catch { }
        return null;
    }

    // The Sahur-roblox bone table. Kept here (not in the retarget code) now that there is more than
    // one rigged model: game bone (UE-style, what the shipped rig uses) -> our Mixamo bone. The game
    // carries five spine joints (spine_01..spine_05) where the model has three (Spine/Spine1/Spine2)
    // that are also the parents of the shoulders and neck, so the five are distributed over the three
    // by height: 1st, 3rd and 5th; spine_02/spine_04 are skipped and reported as unmapped. The deltas
    // are accumulated world deltas, so each mapped bone still ends up at the game bone's own world
    // orientation - the skip only thins the interpolation.
    private static readonly string[][] SahurRobloxMap =
    {
        new[] { "Hips",        "pelvis" },
        new[] { "Spine",       "spine_01" },
        new[] { "Spine1",      "spine_03" },
        new[] { "Spine2",      "spine_05" },
        new[] { "Neck",        "neck_01" },
        new[] { "Head",        "head" },
        new[] { "LeftShoulder",  "clavicle_l" },
        new[] { "LeftArm",       "upperarm_l" },
        new[] { "LeftForeArm",   "lowerarm_l" },
        new[] { "LeftHand",      "hand_l" },
        new[] { "RightShoulder", "clavicle_r" },
        new[] { "RightArm",      "upperarm_r" },
        new[] { "RightForeArm",  "lowerarm_r" },
        new[] { "RightHand",     "hand_r" },
        new[] { "LeftUpLeg",   "thigh_l" },
        new[] { "LeftLeg",     "calf_l" },
        new[] { "LeftFoot",    "foot_l" },
        new[] { "RightUpLeg",  "thigh_r" },
        new[] { "RightLeg",    "calf_r" },
        new[] { "RightFoot",   "foot_r" },
    };

    // Region ids every variants2 rigid bake uses (their manifest's piece_table): torso, head, arm_l,
    // arm_r, leg_l, leg_r. Identical to the classic Sahur bake, so the table is shared; a bake that
    // ever numbers its regions differently only needs its own array here.
    private static readonly string[] RigidRegions =
    {
        "spine_01", "head", "upperarm_l", "upperarm_r", "thigh_l", "thigh_r",
    };

    // ============================================================== the entries, in wardrobe order
    //
    // Existing indices must not move: the game persists the selected index, and both Sahur entries
    // have been at 10/11 since they shipped. New models are appended as their bakes land.
    // Balerina Capuchino: a real Rigify rig, pruned 301 -> 56 bones and re-parented under one root
    // (`DEF-spine`) at bake time, so the existing delta retarget runs unchanged - it only needs this
    // table. Its 23 pairs, measured from bone positions in the source (manifest.json -> bone_map),
    // including the toe (`ball_*`) and the two extra spine links the game has but the Sahur Mixamo
    // rig did not. The bake's canonical space is metres (bones included), so no load-time scale.
    private static readonly string[][] CapuchinoMap =
    {
        new[] { "DEF-spine",        "pelvis" },
        new[] { "DEF-spine.001",    "spine_01" },
        new[] { "DEF-spine.002",    "spine_02" },
        new[] { "DEF-spine.003",    "spine_03" },
        new[] { "DEF-spine.004",    "spine_04" },
        new[] { "DEF-spine.005",    "neck_01" },
        new[] { "DEF-spine.006",    "head" },
        new[] { "DEF-shoulder.L",   "clavicle_l" },
        new[] { "DEF-upper_arm.L",  "upperarm_l" },
        new[] { "DEF-forearm.L",    "lowerarm_l" },
        new[] { "DEF-hand.L",       "hand_l" },
        new[] { "DEF-shoulder.R",   "clavicle_r" },
        new[] { "DEF-upper_arm.R",  "upperarm_r" },
        new[] { "DEF-forearm.R",    "lowerarm_r" },
        new[] { "DEF-hand.R",       "hand_r" },
        new[] { "DEF-thigh.L",      "thigh_l" },
        new[] { "DEF-shin.L",       "calf_l" },
        new[] { "DEF-foot.L",       "foot_l" },
        new[] { "DEF-toe.L",        "ball_l" },
        new[] { "DEF-thigh.R",      "thigh_r" },
        new[] { "DEF-shin.R",       "calf_r" },
        new[] { "DEF-foot.R",       "foot_r" },
        new[] { "DEF-toe.R",        "ball_r" },
    };

    // The four entries that shipped before the Nordbo set, at fixed indices 10..13. The Nordbo ones
    // (index 14 onwards) live in the generated NordboRoster.g.cs and are appended after these, so
    // their order and indices are unchanged by adding models to the end of the roster.
    private static readonly ModelSpec[] Base =
    {
        new ModelSpec
        {
            Slug = "sahur-roblox", Tag = "SahurRigged",
            Label = "Sahur", AdoptNames = new[] { "Sahur" }, Kind = ModelKind.Rigged,
            BlobSuffix = "sahur_roblox.rset",
            AlbedoSuffixes = new[] { "sahur_roblox_albedo.png" },
            NormalSuffixes = new[] { "sahur_roblox_normal.png" },
            PortraitSuffix = "sahur_roblox_portrait.png", BustSuffix = "sahur_roblox_bust.png",
            BoneMap = SahurRobloxMap,
            Credit = "'Tung Sahur Game 3D model (Roblox)' by alex.cgwarrior, CC BY 4.0 " +
                     "(modified: 4K maps downscaled, roughness/AO folded into constants, mesh mirrored " +
                     "in X, weights reduced to 4 influences)",
        },
        new ModelSpec
        {
            Slug = "sahur-classic", Tag = "Sahur",
            Label = "Sahur (Classic)", AdoptNames = new[] { "Sahur (Classic)", "Sahur" }, Kind = ModelKind.Rigid,
            BlobSuffix = "sahur.mset", AlbedoSuffixes = new[] { "sahur_albedo.png" },
            PortraitSuffix = "sahur_portrait.png", BustSuffix = "sahur_bust.png",
            Yaw180 = true, YOffset = 0f, RegionBones = RigidRegions, Bat = true,
            Credit = "'Tung Tung Tung Sahur' by @BakaFoxArt, CC0 1.0",
        },
        // "Shpioniro Golubiro" by alex.cgwarrior, CC BY 4.0
        // https://sketchfab.com/3d-models/shpioniro-golubiro-game-3d-model-free-download-f503749e40a44321bee4c46c00c759d9
        // Baked as a rigid region split (no rig in the source at all): 10 pieces, regions 0..5 as
        // above, plus the pigeon's FED-2 camera prop hung on spine_01 and 3 sunglasses pieces on the
        // head. Mirror_x applied at bake time, so no yaw. 1.80 m, arm span 2.14 m (A-pose).
        new ModelSpec
        {
            Slug = "golubiro", Tag = "Sahur_Golubiro",
            Label = "Golubiro", AdoptNames = new[] { "Golubiro" }, Kind = ModelKind.Rigid,
            BlobSuffix = "shpioniro-golubiro.mset", AlbedoSuffixes = new[] { "shpioniro-golubiro_albedo.png" },
            PortraitSuffix = "shpioniro-golubiro_portrait.png", BustSuffix = "shpioniro-golubiro_bust.png",
            Yaw180 = false, YOffset = 0.0054f, RegionBones = RigidRegions, Bat = false,
            Credit = "'Shpioniro Golubiro' by alex.cgwarrior, CC BY 4.0 " +
                     "(modified: mesh mirrored in X, scaled to 180 cm, maps downscaled to one 1024^2 " +
                     "unlit albedo, normal/roughness/AO folded into constants)",
        },
        // ---- Capuchino: live again after the canonical-space re-bake (2026-11-13) ----
        //
        // The bake now converts once (cm -> 1.80 m canonical metres, X mirrored) at the cluster
        // `TransformLink` binds, re-derives every bone local from frozen canonical worlds under one
        // root (`DEF-spine`, mapped to `pelvis`), and validates itself: mesh height, skeleton span
        // ratio, byte weights summing to 255, winding after the reflection, decoded joint binds vs
        // the frozen worlds, frame-zero identity deltas and CPU-skinned articulation fixtures. The
        // 105.9055 load-time rescale is gone with the defect that needed it.
        //
        // Appended at the end on purpose: the game persists the selected model as an index and the
        // two Sahur entries must keep 10/11.
        new ModelSpec
        {
            Slug = "capuchino", Tag = "Sahur_Capuchino",
            Label = "Capuchino", AdoptNames = new[] { "Capuchino" }, Kind = ModelKind.Rigged,
            BlobSuffix = "balerina-capuchino.rset",
            AlbedoSuffixes = new[] { "balerina-capuchino_albedo_0.png", "balerina-capuchino_albedo_1.png" },
            NormalSuffixes = new[] { "balerina-capuchino_normal_0.png", "balerina-capuchino_normal_1.png" },
            PortraitSuffix = "balerina-capuchino_portrait.png", BustSuffix = "balerina-capuchino_bust.png",
            BoneMap = CapuchinoMap,
            Credit = "'Balerina Capuchino (rigged, game ready)' by alex.cgwarrior, CC BY 4.0 " +
                     "(modified: bones pruned 301->56, weights 8->4 influences, maps downscaled, " +
                     "AO/roughness dropped, mesh mirrored in X, scaled to 1.80 m)",
        },
    };

    // Base first, then whatever the generator staged. Registration order is wardrobe order and the
    // game persists the selected index, so new models are only ever appended.
    internal static readonly ModelSpec[] All = Append(Base, NordboRoster.Specs);

    private static ModelSpec[] Append(ModelSpec[] head, ModelSpec[] tail)
    {
        if (tail == null || tail.Length == 0) return head;
        var all = new ModelSpec[head.Length + tail.Length];
        Array.Copy(head, all, head.Length);
        Array.Copy(tail, 0, all, head.Length, tail.Length);
        return all;
    }

    // Labels a previous generation registered that are no longer in All. "Labubu" is the one
    // withdrawal: a CC BY-NC bundle with a no-redistribution clause over third-party character IP,
    // which was never distributable and is now out of the mod entirely. The data object outlives a
    // hot reload, so a withdrawn entry is dropped from PlayerModelDatabase.Models by name instead of
    // lingering as a row that renders the donor body.
    internal static readonly string[] Retired = { "Labubu" };

    internal static bool IsRetired(string label)
    {
        foreach (var r in Retired) if (string.Equals(r, label, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static ModelSpec BySlug(string slug)
    {
        foreach (var s in All) if (string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    // The classic entry: what the F3/F2 request file drives when nothing else is selected.
    internal static ModelSpec Classic => BySlug("sahur-classic");

    // The mod's own scene objects: every one is named `Sahur...` and one of the suffixes below.
    // One predicate for all of them, because "is this ours" is asked from the body hide/restore
    // snapshot, the mirror sweep, and the preview's live-rig count.
    internal static bool IsOurObjectName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        if (!n.StartsWith("Sahur", StringComparison.Ordinal)) return false;
        return n.EndsWith("_Body", StringComparison.Ordinal)
            || n.EndsWith("_PreviewBody", StringComparison.Ordinal)
            || n.EndsWith("_Skeleton", StringComparison.Ordinal)
            || n.EndsWith("_Bat", StringComparison.Ordinal)
            || n.EndsWith("_ScaleHuman", StringComparison.Ordinal);
    }

    internal static int IndexOf(ModelSpec spec)
    {
        for (int i = 0; i < All.Length; i++) if (ReferenceEquals(All[i], spec)) return i;
        return -1;
    }
}
