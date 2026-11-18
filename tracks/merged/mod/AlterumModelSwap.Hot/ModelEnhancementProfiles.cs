using System;
using System.Collections.Generic;

namespace AlterumModelSwap.Hot;

// Per-model chest / hip enhancement profiles, keyed by wardrobe slug.
//
// This lives next to the models, not in the roster file: the roster is the selection list and must
// stay a data table stream `I` owns, while how a body can be enlarged is a property of that body's
// bake. Every entry states which mechanism the model supports and the numbers that mechanism uses:
//
//   RiggedBones  the model's own skeleton is still live (SahurRig keeps its 71-bone Mixamo rig), so
//                scaling the chest / hip bones inflates exactly the regions their skin weights cover.
//                The bone names are the MODEL side of the roster's BoneMap, not the game's.
//   RestDeform   no usable bone (rigid region bakes: one bone per piece, no breast/hip control at
//                all), so the geometry is deformed in REST space: a bounded radial expansion inside
//                a height band of the model, from the immutable original vertices.
//
// Anything with no entry falls back to Fallback(), which is RestDeform with anthropometric bands -
// so a model added to the roster without a profile still responds and still reports which mechanism
// it actually used. The capability matrix (`[enh:matrix]`) is generated from this table plus the
// measured result, never hand-written.
internal enum EnhanceMode { None, RiggedBones, RestDeform }

internal sealed class EnhancementProfile
{
    public string Slug;
    public EnhanceMode Chest = EnhanceMode.RestDeform;
    public EnhanceMode Hips = EnhanceMode.RestDeform;

    // RiggedBones: model-side bone names. A name that is missing at runtime drops that toggle to
    // RestDeform for this body rather than doing nothing silently.
    public string[] ChestBones;
    public string[] HipBones;

    // Bounded multipliers. Chest is a radial expansion of the ribcage/bust band, hips of the pelvis
    // band; both are capped so a profile cannot produce a body the garments/cloth cannot follow.
    public float ChestScale = 1.30f;
    public float HipScale = 1.18f;

    // RestDeform bands, as a fraction of the model's own height (0 = feet, 1 = top of head), plus
    // how wide the smooth falloff is. The default values are the measured anatomy of the shipped
    // bakes (chest band at 68..80 % of height on a 1.80 m body, hips at 44..54 %).
    public float ChestCenterFrac = 0.74f;
    public float ChestHalfFrac = 0.070f;
    public float HipCenterFrac = 0.49f;
    public float HipHalfFrac = 0.055f;

    public string Note = "";
}

internal static class ModelEnhancementProfiles
{
    // Hard caps applied on top of any profile, so a typo cannot ship a monster.
    internal const float MaxChestScale = 1.60f;
    internal const float MaxHipScale = 1.45f;

    private static readonly Dictionary<string, EnhancementProfile> _bySlug =
        new(StringComparer.OrdinalIgnoreCase);

    static ModelEnhancementProfiles()
    {
        Add(new EnhancementProfile
        {
            Slug = "sahur-roblox", Chest = EnhanceMode.RiggedBones, Hips = EnhanceMode.RiggedBones,
            // Mixamo naming after the bake's prune: the spine chain is Spine/Spine1/Spine2 and the
            // pelvis is Hips. Chest_02 has no counterpart, so the ribcage band is Spine2 and the
            // bust volume follows its children (Neck/LeftShoulder stay counter-scaled in the bake).
            ChestBones = new[] { "Spine2" },
            HipBones = new[] { "Hips" },
            ChestScale = 1.28f, HipScale = 1.15f,
            Note = "71-bone Mixamo rig, spine2/hips",
        });
        Add(new EnhancementProfile
        {
            Slug = "capuchino", Chest = EnhanceMode.RiggedBones, Hips = EnhanceMode.RiggedBones,
            // 56-bone pruned rig; the chest control is the upper spine, the pelvis is DEF-spine.
            ChestBones = new[] { "spine.003", "spine.002", "Spine2", "spine2" },
            HipBones = new[] { "DEF-spine", "pelvis", "Hips" },
            ChestScale = 1.26f, HipScale = 1.16f,
            Note = "56-bone rig, spine.003/DEF-spine",
        });
        Add(new EnhancementProfile
        {
            Slug = "sahur-classic", Chest = EnhanceMode.RestDeform, Hips = EnhanceMode.RestDeform,
            // A wooden statue: the chest band is the barrel's belly, the hips its hem.
            ChestScale = 1.18f, HipScale = 1.12f,
            ChestCenterFrac = 0.66f, ChestHalfFrac = 0.090f,
            HipCenterFrac = 0.36f, HipHalfFrac = 0.070f,
            Note = "8 rigid pieces, rest-space band",
        });
        Add(new EnhancementProfile
        {
            Slug = "golubiro", Chest = EnhanceMode.RestDeform, Hips = EnhanceMode.RestDeform,
            ChestScale = 1.22f, HipScale = 1.14f,
            ChestCenterFrac = 0.70f, ChestHalfFrac = 0.080f,
            HipCenterFrac = 0.46f, HipHalfFrac = 0.060f,
            Note = "10 rigid pieces, rest-space band",
        });
    }

    private static void Add(EnhancementProfile p)
    {
        if (p == null || string.IsNullOrEmpty(p.Slug)) return;
        p.ChestScale = Math.Min(p.ChestScale, MaxChestScale);
        p.HipScale = Math.Min(p.HipScale, MaxHipScale);
        _bySlug[p.Slug] = p;
    }

    // The profile for a roster entry, derived when the table has no explicit row: a rigged model's
    // OWN bone map already states which of its bones is the chest and which is the pelvis (the game
    // side of each pair is the rig bone the roster maps onto), so the profile is read off that map
    // instead of being invented. Rigid models have no such map and fall back to the bands.
    internal static EnhancementProfile ForSpec(ModelSpec spec)
    {
        if (spec == null) return Fallback("?");
        if (_bySlug.TryGetValue(spec.Slug ?? "", out var known)) return known;
        if (spec.Kind == ModelKind.Rigged && spec.BoneMap != null && spec.BoneMap.Length > 0)
        {
            var chest = new List<string>();
            var hips = new List<string>();
            foreach (var pair in spec.BoneMap)
            {
                if (pair == null || pair.Length < 2) continue;
                string game = pair[1] ?? "";
                if (game.StartsWith("chest_0", StringComparison.OrdinalIgnoreCase) ||
                    game.StartsWith("spine_04", StringComparison.OrdinalIgnoreCase) ||
                    game.StartsWith("spine_03", StringComparison.OrdinalIgnoreCase))
                    chest.Add(pair[0]);
                else if (game.StartsWith("pelvis", StringComparison.OrdinalIgnoreCase) ||
                         game.StartsWith("thigh_", StringComparison.OrdinalIgnoreCase))
                    hips.Add(pair[0]);
            }
            if (chest.Count > 0 || hips.Count > 0)
            {
                var derived = new EnhancementProfile
                {
                    Slug = spec.Slug,
                    Chest = chest.Count > 0 ? EnhanceMode.RiggedBones : EnhanceMode.RestDeform,
                    Hips = hips.Count > 0 ? EnhanceMode.RiggedBones : EnhanceMode.RestDeform,
                    ChestBones = chest.Count > 0 ? chest.ToArray() : null,
                    HipBones = hips.Count > 0 ? hips.ToArray() : null,
                    ChestScale = 1.24f, HipScale = 1.14f,
                    Note = "derived from the roster BoneMap",
                };
                Add(derived);
                return derived;
            }
        }
        return Fallback(spec.Slug);
    }

    internal static EnhancementProfile For(string slug)
    {
        if (!string.IsNullOrEmpty(slug) && _bySlug.TryGetValue(slug, out var p)) return p;
        return Fallback(slug);
    }

    // A model added without a profile: still responds, with the generic rest-space bands. Logged as
    // "fallback" by the matrix so a missing profile is visible instead of silent.
    private static EnhancementProfile Fallback(string slug)
    {
        return new EnhancementProfile
        {
            Slug = slug ?? "?",
            Chest = EnhanceMode.RestDeform, Hips = EnhanceMode.RestDeform,
            ChestScale = 1.20f, HipScale = 1.12f,
            Note = "fallback (no profile entry)",
        };
    }

    internal static IEnumerable<EnhancementProfile> All => _bySlug.Values;

    internal static bool HasProfile(string slug) =>
        !string.IsNullOrEmpty(slug) && _bySlug.ContainsKey(slug);
}
