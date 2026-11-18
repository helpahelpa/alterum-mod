using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// The tank top's chest weights: the seam that capped the chest.
//
// The garment does not follow the chest. On Diana the tank top (Jane_Undershirt, 2232 verts) is
// weighted to spine_04 (847 mass), clavicle_l/r (597), spine_03 (300), pelvis (217), spine_02
// (148) and only 109 to chest_02_l/r - and 0 to chest_01_l/r. The skin under it over the bust is
// spine_04 + chest_02 (and clavicle at the strap). So when the chest is scaled the skin moves and
// the fabric over it lags, and a skin facet pushes out between two fabric vertices at the upper
// chest. No bone scale can close that (pass 3/4): the fix is on the mesh.
//
// For the chest band only, each garment vertex takes the weight mix of the skin under it (nearest
// body-mesh vertices, inverse-distance weighted), mixed in by a smooth falloff so the transition
// has no step. The neckline/armhole rims (the mesh's own welded boundary loops) and the straps
// stay exactly as authored - those are the clavicle-pinned parts that already look right.
//
// Which mesh supplies that skin is decided from the RIG'S STRUCTURE, not from a name: a source has
// to carry the chest chain and to be carried by the bust leaves inside the chest band (see
// SelectSkinSource). The old rule looked for a renderer whose name contains 'body', which found
// nothing on Sarah's third-person rig (Sarah_Belt / Chain / Pants / Shirt / Hands / Head /
// Hairstyle - no skin renderer at all in the name sense) and left her garment weights authored.
//
// The mesh is cloned per renderer (never the shared asset) and the clone is destroyed on teardown,
// like every other asset this mod owns.
internal static class ChestWeights
{
    // --- region -------------------------------------------------------------------------
    // Vertical band, relative to the mid-point of chest_01_l/chest_01_r in the garment's bind
    // pose. Covers the bust and the upper chest below the neckline; the fades keep the band's
    // edges from becoming a new seam.
    internal const float BandBelow = 0.075f;      // m below the chest_01 head
    internal const float BandAbove = 0.095f;      // m above it
    internal const float BandFade = 0.030f;       // m of smooth ramp at either end
    internal const float BandHalfWidth = 0.160f;  // m either side of the sternum
    internal const float WidthFade = 0.020f;
    // Rim clearance: the garment's open rims (neckline, armholes, bottom hem) are found from the
    // mesh's welded boundary loops and left alone; the blend ramps in over this distance inside.
    internal const float RimInner = 0.000f;
    internal const float RimOuter = 0.012f;
    // Skin sampling (world-bind space, i.e. the mesh's bind pose placed by the renderer transform).
    internal const int SampleK = 6;
    internal const float SampleRadius = 0.060f;
    internal const float Peak = 1.0f;
    // Skin-source selection (see SelectSkinSource): a candidate needs this many vertices inside the
    // chest band, and the band may hold at most MaxBandShare of its vertices - a whole body passes
    // through the chest (0-3 % of itself there), a pouch/chain/vest is concentrated on it (29-61 %).
    internal const int MinBandVertices = 40;
    internal const float MaxBandShare = 0.10f;
    internal const float MinBandAlpha = 0.25f;

    private const int MaxInfluences = 4;

    internal sealed class Fix
    {
        public SkinnedMeshRenderer R;
        public Mesh Original;
        public Mesh Clone;
        public string Mesh = "";
        public int Candidates;
        public int Changed;
        public int NoSkin;
        public float MeanShift;
        public float MaxShift;
        public string Section = "";
    }

    // ------------------------------------------------------------------ apply
    // Returns the list of fixes applied (possibly empty). Never throws: a failure is logged and
    // already-applied clones in this call are undone, so the caller's transaction stays clean.
    internal static List<Fix> Apply(IHostContext log, ThirdViewPlayerModel model, bool female)
    {
        var fixes = new List<Fix>();
        if (model == null || model.gameObject == null) return fixes;
        if (!female) return fixes;   // no chest bones, no chest seam
        try
        {
            var comps = model.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            if (comps == null || comps.Length == 0) return fixes;
            var skins = new List<SkinnedMeshRenderer>();
            for (int i = 0; i < comps.Length; i++)
            {
                var r = comps[i]?.TryCast<SkinnedMeshRenderer>();
                if (r == null || r.sharedMesh == null || r.gameObject == null) continue;
                skins.Add(r);
            }

            var pick = SelectSkinSource(skins);
            log.LogInfo($"[chest:pick] model='{model.gameObject.name}' {pick.Reason} census=[{pick.Census}]");
            if (pick.R == null)
            {
                log.LogWarning("[chest:skip] no skin surface in the chest band on this rig (see [chest:pick]); " +
                               "the chest region cannot be sampled, garment weights left as authored");
                return fixes;
            }

            var bodySkin = new SkinData(log, pick.R);
            if (!bodySkin.Ok)
            {
                log.LogWarning($"[chest:skip] skin source '{pick.R.gameObject.name}' not readable ({bodySkin.Error}); " +
                               "garment weights left as authored");
                return fixes;
            }

            foreach (var r in skins)
            {
                if (ReferenceEquals(r, pick.R)) continue;
                string nm = r.gameObject.name ?? "";
                if (!IsChestGarment(nm))
                {
                    log.LogInfo($"[chest:skip] mesh='{nm}' reason=not-a-torso-garment " +
                                "(name is not a shirt/vest/tank/top; accessories and the skin are left alone)");
                    continue;
                }
                var fix = FixOne(log, r, bodySkin);
                if (fix == null) continue;
                if (fix.Changed <= 0)
                {
                    log.LogInfo($"[chest:skip] mesh='{nm}' reason=no-verts-changed " +
                                $"(candidates={fix.Candidates}, noSkin={fix.NoSkin})");
                    DestroyClone(fix);
                    continue;
                }
                fixes.Add(fix);
            }
            if (fixes.Count == 0)
                log.LogInfo($"[chest:apply] model='{model.gameObject.name}' source='{pick.R.gameObject.name}' " +
                            "no garment mesh needed a chest reweight");
            return fixes;
        }
        catch (Exception e)
        {
            log.LogError($"[chest:apply-failed] {e.GetType().Name}: {e.Message} - undoing {fixes.Count} mesh fix(es)");
            Restore(log, fixes);
            return new List<Fix>();
        }
    }

    // The chest garment, by name. Deliberately narrow: the pouch/strap, belts, boots, skirts and
    // the hair/bearers are worn OVER or beside the shirt and must keep their authored weights.
    private static bool IsChestGarment(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        if (n.Contains("hair") || n.Contains("head") || n.Contains("body")) return false;
        return n.Contains("shirt") || n.Contains("vest") || n.Contains("tank") ||
               n.Contains("blouse") || n.Contains("tee");
    }

    // ---- skin source ------------------------------------------------------------------

    // Structural skin selection: the source is found by what it is, not by what it is called.
    //
    // Why it cannot be a name rule: Sarah's third-person rig carries no renderer whose name says
    // 'body' (Sarah_Belt / Chain / Pants / Shirt / Hands / Head / Hairstyle), so the old rule logged
    // '[chest:skip] no body/skin SkinnedMeshRenderer found' and her garment kept its authored
    // weights while the chest cage moved at 1.90.
    //
    // A candidate must
    //   (a) carry the chest chain in its own bone array (chest_01_l/r + chest_02_l/r) - the band
    //       frame is built from those bones in that renderer's bind pose, so a renderer without
    //       them cannot describe the region at all;
    //   (b) have >= MinBandVertices vertices inside the chest band (it has to exist at the chest);
    //   (c) be a surface that merely PASSES THROUGH the band, not one that IS the band:
    //       bandVerts <= MaxBandShare * verts. This is the skin/accessory split, and it is what the
    //       real rigs measure (see the census in every [chest:pick] line): a whole body puts 0-3 %
    //       of itself in the chest band (Diana 601/20836 = 2.9 %, Kiona 466/21338 = 2.2 %, Emma
    //       30/4570 = 0.7 %), while everything strapped or worn on the chest is concentrated there
    //       (Diana_Pouch 1448/4915 = 29 %, Medallion 2338/5739 = 41 %, Scarf 800/1313 = 61 %,
    //       Suspenders 1316/4557 = 29 %, and the tank tops 881/2232 = 39 %). Nothing sits between
    //       3 % and 29 % on any rig checked, so 10 % separates skin from worn kit by a wide margin;
    //   (d) not be one of the chest garments being fixed: a garment sampled against itself returns
    //       its own weights, so it can never be the reference.
    // Among the candidates that pass, the one with the most vertices inside the band wins - the
    // densest surface actually present at the chest. No candidate: skip, authored weights stay.
    //
    // Sarah is the case this is built for and the case it must refuse: her only chest surface is
    // Sarah_Shirt (1345 band verts of 7171 = 19 %, and a garment), and no other renderer has any
    // vertex in the band, so there is no skin to sample and her authored weights stay. That is the
    // honest answer for a rig whose torso IS the shirt.
    private sealed class SkinPick
    {
        public SkinnedMeshRenderer R;
        public int BandVertices;
        public float LeafBand;
        public string Census = "";
        public string Reason = "";
    }

    private struct BandProbe
    {
        public int N, Band;
        public float Leaf;
        public bool ChestChain;
    }

    private static SkinPick SelectSkinSource(List<SkinnedMeshRenderer> skins)
    {
        var pick = new SkinPick();
        var census = new StringBuilder();
        var rejected = new StringBuilder();
        int withChain = 0;
        for (int i = 0; i < skins.Count; i++)
        {
            var r = skins[i];
            string nm = r.gameObject != null ? (r.gameObject.name ?? "?") : "?";
            var p = Probe(r);
            float share = p.N > 0 ? p.Band / (float)p.N : 0f;
            if (census.Length > 0) census.Append(' ');
            census.Append(nm).Append(':').Append(p.Band).Append('b').Append('/').Append(p.N).Append('v')
                  .Append(" share=").Append(share.ToString("P1", CultureInfo.InvariantCulture))
                  .Append(" leaf=").Append(p.Leaf.ToString("F2", CultureInfo.InvariantCulture));
            if (!p.ChestChain) { census.Append(" (no-chest-chain)"); continue; }
            withChain++;
            if (p.Band < MinBandVertices)
            {
                census.Append(" (band<" + MinBandVertices + ")");
                Reject(rejected, nm + " band=" + p.Band);
                continue;
            }
            if (share > MaxBandShare)
            {
                census.Append(" (worn-on-chest)");
                Reject(rejected, nm + " share=" + share.ToString("P0", CultureInfo.InvariantCulture));
                continue;
            }
            if (IsChestGarment(nm))
            {
                census.Append(" (garment)");
                Reject(rejected, nm + " is a chest garment");
                continue;
            }
            census.Append(" [candidate]");
            if (pick.R == null || p.Band > pick.BandVertices)
            {
                pick.R = r;
                pick.BandVertices = p.Band;
                pick.LeafBand = p.Leaf;
            }
        }
        pick.Census = census.ToString();
        pick.Reason = pick.R != null
            ? $"selected '{pick.R.gameObject.name}' by structure (bandVerts={pick.BandVertices}, " +
              $"leafBand={pick.LeafBand.ToString("F2", CultureInfo.InvariantCulture)}, chestChain={withChain}/{skins.Count})"
            : $"no skin surface in the chest band among {skins.Count} renderer(s) (chestChain={withChain}, " +
              $"rejected=[{rejected}]; needs >= {MinBandVertices} verts in the band and <= " +
              $"{MaxBandShare.ToString("P0", CultureInfo.InvariantCulture)} of the mesh inside it)";
        return pick;
    }

    private static void Reject(StringBuilder sb, string what)
    {
        if (sb.Length > 0) sb.Append("; ");
        sb.Append(what);
    }

    // One renderer measured against the chest band, in its own bind pose. Same frame and same
    // falloff as FixOne, so the source is judged on exactly the region that will be rewritten.
    private static BandProbe Probe(SkinnedMeshRenderer r)
    {
        var p = new BandProbe();
        try
        {
            var mesh = r.sharedMesh;
            if (mesh == null) return p;
            var verts = mesh.vertices;
            var bw = mesh.boneWeights;
            var bones = r.bones;
            var binds = mesh.bindposes;
            if (verts == null || bw == null || bones == null || binds == null) return p;
            if (bw.Length != verts.Length || binds.Length != bones.Length) return p;
            p.N = verts.Length;
            var boneIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int b = 0; b < bones.Length; b++)
            {
                string n = bones[b] != null ? bones[b].name : null;
                if (!string.IsNullOrEmpty(n) && !boneIndex.ContainsKey(n)) boneIndex[n] = b;
            }
            if (!boneIndex.ContainsKey("chest_01_l") || !boneIndex.ContainsKey("chest_01_r") ||
                !boneIndex.ContainsKey("chest_02_l") || !boneIndex.ContainsKey("chest_02_r")) return p;
            p.ChestChain = true;
            if (!TryFrame(r, boneIndex, out var pivot, out var up, out var side)) return p;
            int c02l = boneIndex["chest_02_l"], c02r = boneIndex["chest_02_r"];
            var m = r.transform.localToWorldMatrix;
            float leaf = 0f;
            int inBand = 0;
            for (int v = 0; v < verts.Length; v++)
            {
                if (BandAlpha(pivot, up, side, m.MultiplyPoint3x4(verts[v])) <= MinBandAlpha) continue;
                inBand++;
                var w = bw[v];
                leaf += LeafWeight(w, c02l) + LeafWeight(w, c02r);
            }
            p.Band = inBand;
            p.Leaf = inBand > 0 ? leaf / inBand : 0f;
            return p;
        }
        catch { return p; }
    }

    private static float LeafWeight(BoneWeight w, int bone)
    {
        if (bone < 0) return 0f;
        float s = 0f;
        if (w.boneIndex0 == bone) s += w.weight0;
        if (w.boneIndex1 == bone) s += w.weight1;
        if (w.boneIndex2 == bone) s += w.weight2;
        if (w.boneIndex3 == bone) s += w.weight3;
        return s;
    }

    // The band frame: pivot = mid chest_01, up = pelvis -> pivot, side = chest_01_l -> chest_01_r.
    // Skeleton-relative on purpose, so the wardrobe clones (at x~25.7, not at the origin) get the
    // same band as a clone standing at the origin.
    private static bool TryFrame(SkinnedMeshRenderer r, Dictionary<string, int> boneIndex,
                                 out Vector3 pivot, out Vector3 up, out Vector3 side)
    {
        pivot = up = side = Vector3.zero;
        if (!boneIndex.TryGetValue("chest_01_l", out int c01l) ||
            !boneIndex.TryGetValue("chest_01_r", out int c01r)) return false;
        var binds = r.sharedMesh.bindposes;
        var bones = r.bones;
        if (binds == null || bones == null || binds.Length != bones.Length) return false;
        var m = r.transform.localToWorldMatrix;
        var left = m.MultiplyPoint3x4(binds[c01l].inverse.MultiplyPoint3x4(Vector3.zero));
        var right = m.MultiplyPoint3x4(binds[c01r].inverse.MultiplyPoint3x4(Vector3.zero));
        pivot = (left + right) * 0.5f;
        int pelvis = boneIndex.TryGetValue("pelvis", out int pIdx) ? pIdx : -1;
        up = pelvis >= 0
            ? (pivot - m.MultiplyPoint3x4(binds[pelvis].inverse.MultiplyPoint3x4(Vector3.zero)))
            : Vector3.up;
        if (up.sqrMagnitude < 1e-8f) up = Vector3.up;
        up = up.normalized;
        side = right - left;
        side -= Vector3.Dot(side, up) * up;          // orthonormal, in the skeleton's plane
        if (side.sqrMagnitude < 1e-8f) side = Vector3.Cross(up, Vector3.forward);
        side = side.normalized;
        return true;
    }

    private static Fix FixOne(IHostContext log, SkinnedMeshRenderer r, SkinData body)
    {
        var fix = new Fix { R = r, Mesh = r.gameObject.name ?? "?" };
        var mesh = r.sharedMesh;
        if (mesh == null) return null;
        var bw = mesh.boneWeights;
        var verts = mesh.vertices;
        var binds = mesh.bindposes;
        var bones = r.bones;
        if (bw == null || verts == null || binds == null || bones == null || bw.Length != verts.Length ||
            binds.Length != bones.Length)
        {
            log.LogInfo($"[chest:skip] mesh='{fix.Mesh}' reason=not-readable (boneWeights/vertices/bindposes missing or mismatched)");
            return null;
        }
        if (bw.Length != 0 && !mesh.isReadable) { log.LogWarning($"[chest:skip] '{fix.Mesh}' mesh not readable"); return null; }
        if (bones.Length == 0 || verts.Length == 0) return null;

        // The garment needs chest bones for this to mean anything.
        var boneIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int b = 0; b < bones.Length; b++)
        {
            string n = bones[b] != null ? bones[b].name : null;
            if (!string.IsNullOrEmpty(n) && !boneIndex.ContainsKey(n)) boneIndex[n] = b;
        }
        int c01l, c01r, c02l, c02r;
        if (!boneIndex.TryGetValue("chest_01_l", out c01l) || !boneIndex.TryGetValue("chest_01_r", out c01r) ||
            !boneIndex.TryGetValue("chest_02_l", out c02l) || !boneIndex.TryGetValue("chest_02_r", out c02r))
        {
            log.LogInfo($"[chest:skip] mesh='{fix.Mesh}' reason=no-chest-bones (bone array has no chest_01/02 pair)");
            return null;
        }

        // Region frame: the model's own up axis (pelvis -> mid chest_01) and the axis between the
        // two chest_01 bones. Skeleton-relative, so a rotated model (the wardrobe, the player
        // facing any direction) gets the same band as one standing at the origin.
        if (!TryFrame(r, boneIndex, out var pivot, out var up, out var side))
        {
            log.LogInfo($"[chest:skip] mesh='{fix.Mesh}' reason=no-band-frame (chest_01_l/r have no usable " +
                        "bind pose or bone array)");
            return null;
        }

        // world-bind position of every garment vertex
        var m = r.transform.localToWorldMatrix;
        var world = new Vector3[verts.Length];
        for (int v = 0; v < verts.Length; v++) world[v] = m.MultiplyPoint3x4(verts[v]);

        // candidate vertices: inside the chest band, not on one of the mesh's open rims
        var rims = RimPositions(mesh, world);
        var cand = new List<int>();
        var alpha = new List<float>();
        for (int v = 0; v < verts.Length; v++)
        {
            float a = BandAlpha(pivot, up, side, world[v]);
            if (a <= 1e-3f) continue;
            float dRim = Nearest(rims, world[v]);
            a *= Ramp(dRim, RimInner, RimOuter);
            if (a <= 1e-3f) continue;
            cand.Add(v); alpha.Add(a);
        }
        fix.Candidates = cand.Count;
        fix.Section = string.Format(CultureInfo.InvariantCulture,
            "band=[-{0:F3},+{1:F3}] fade={2:F3} halfWidth={3:F3} rim=0..{4:F3} rims={5}",
            BandBelow, BandAbove, BandFade, BandHalfWidth, RimOuter, rims.Length);
        if (cand.Count == 0) return fix;

        // rewrite
        var outW = new BoneWeight[bw.Length];
        for (int v = 0; v < bw.Length; v++) outW[v] = bw[v];
        float sumShift = 0f, maxShift = 0f;
        int changed = 0;
        var target = new float[bones.Length];
        var pairs = new List<KeyValuePair<int, float>>(8);
        for (int c = 0; c < cand.Count; c++)
        {
            int v = cand[c];
            float a = alpha[c];
            // target = the skin's weight mix under this vertex
            if (!body.Sample(world[v], target, boneIndex)) { fix.NoSkin++; continue; }

            // mix, keep the 4 largest, normalise
            pairs.Clear();
            var w = bw[v];
            Acc(pairs, w.boneIndex0, w.weight0 * (1f - a));
            Acc(pairs, w.boneIndex1, w.weight1 * (1f - a));
            Acc(pairs, w.boneIndex2, w.weight2 * (1f - a));
            Acc(pairs, w.boneIndex3, w.weight3 * (1f - a));
            for (int b = 0; b < target.Length; b++)
                if (target[b] > 0f) Acc(pairs, b, target[b] * a);
            if (!Normalise(pairs, out var nw)) continue;
            float shift = 0f;
            shift += Mathf.Abs(nw.weight0 - w.weight0) + Mathf.Abs(nw.weight1 - w.weight1) +
                     Mathf.Abs(nw.weight2 - w.weight2) + Mathf.Abs(nw.weight3 - w.weight3);
            shift *= 0.5f;
            if (shift < 1e-4f) continue;
            outW[v] = nw;
            changed++;
            sumShift += shift;
            if (shift > maxShift) maxShift = shift;
        }
        fix.Changed = changed;
        if (changed == 0) return fix;

        // invariant check on the data that is about to be written: every changed vertex sums to 1
        // and carries at most 4 influences (BoneWeight has 4 slots; this proves none is dropped).
        int badSum = 0, badInfluences = 0;
        float worstSum = 0f;
        for (int c = 0; c < cand.Count; c++)
        {
            int v = cand[c];
            var w = outW[v];
            float sum = w.weight0 + w.weight1 + w.weight2 + w.weight3;
            if (Mathf.Abs(sum - 1f) > 1e-3f) { badSum++; if (Mathf.Abs(sum - 1f) > worstSum) worstSum = Mathf.Abs(sum - 1f); }
            int n = (w.weight0 > 0f ? 1 : 0) + (w.weight1 > 0f ? 1 : 0) + (w.weight2 > 0f ? 1 : 0) + (w.weight3 > 0f ? 1 : 0);
            if (n > MaxInfluences) badInfluences++;
        }
        if (badSum > 0) log.LogError($"[chest:invariant] '{fix.Mesh}': {badSum} vertex weight sums off by up to {worstSum:F5} (normalisation failed)");
        if (badInfluences > 0) log.LogError($"[chest:invariant] '{fix.Mesh}': {badInfluences} vertices have more than {MaxInfluences} influences");

        // clone + write + assign
        Mesh clone = null;
        try
        {
            var obj = UnityEngine.Object.Instantiate(mesh);
            clone = obj != null ? obj.TryCast<Mesh>() : null;
            if (clone == null) throw new InvalidOperationException("Instantiate(mesh) returned no Mesh");
            var arr = new Il2CppStructArray<BoneWeight>(outW.Length);
            for (int i = 0; i < outW.Length; i++) arr[i] = outW[i];
            clone.boneWeights = arr;
            var check = clone.boneWeights;
            if (check == null || check.Length != outW.Length)
                throw new InvalidOperationException($"boneWeights write did not take (len={(check == null ? -1 : check.Length)})");
            fix.Original = mesh;
            fix.Clone = clone;
            r.sharedMesh = clone;
        }
        catch (Exception e)
        {
            log.LogError($"[chest:write-failed] '{fix.Mesh}': {e.GetType().Name}: {e.Message}; " +
                         "boneWeights is not writable on this mesh - leaving the authored weights in place");
            if (clone != null) { try { UnityEngine.Object.Destroy(clone); } catch { } }
            return null;
        }
        fix.MeanShift = sumShift / changed;
        fix.MaxShift = maxShift;
        log.LogInfo($"[chest:apply] mesh='{fix.Mesh}' verts={verts.Length} {fix.Section} " +
                    $"candidates={fix.Candidates} changed={fix.Changed} noSkin={fix.NoSkin} " +
                    $"maxShift={fix.MaxShift:F3} meanShift={fix.MeanShift:F3} " +
                    $"normalised={(badSum == 0 && badInfluences == 0 ? "ok(sum=1,<=4)" : "FAILED")} " +
                    $"mesh={mesh.name}->{fix.Clone.name} (clone)");
        return fix;
    }

    // The chest band falloff: 1 in the middle, 0 at both ends and at the sides. Measured along the
    // skeleton's own axes (up = pelvis -> chest pivot, side = chest_01_l -> chest_01_r), so the
    // band is the same on a model at the origin and on one standing somewhere rotated.
    private static float BandAlpha(Vector3 pivot, Vector3 up, Vector3 side, Vector3 p)
    {
        var d = p - pivot;
        float y = Vector3.Dot(d, up);
        float x = Vector3.Dot(d, side);
        float a = Ramp(y, -BandBelow, -BandBelow + BandFade) * (1f - Ramp(y, BandAbove - BandFade, BandAbove));
        a *= 1f - Ramp(Mathf.Abs(x), BandHalfWidth - WidthFade, BandHalfWidth);
        if (a > 1f) a = 1f;
        return a * Peak;
    }

    private static float Ramp(float v, float lo, float hi)
    {
        if (hi <= lo) return v >= hi ? 1f : 0f;
        float t = (v - lo) / (hi - lo);
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }

    private static float Nearest(Vector3[] pts, Vector3 p)
    {
        if (pts == null || pts.Length == 0) return float.MaxValue;
        float best = float.MaxValue;
        for (int i = 0; i < pts.Length; i++)
        {
            float d = (pts[i] - p).sqrMagnitude;
            if (d < best) best = d;
        }
        return Mathf.Sqrt(best);
    }

    // Open rims of the mesh: edges used by exactly one triangle after welding coincident vertices.
    // On the tank top that is the bottom hem, the neckline and both armholes (the UV seams are
    // welded away, so they do not count as edges of the garment).
    private static Vector3[] RimPositions(Mesh mesh, Vector3[] world)
    {
        var welded = new int[world.Length];
        var map = new Dictionary<long, int>(world.Length * 2);
        const float q = 10000f;   // 0.1 mm
        int next = 0;
        for (int i = 0; i < world.Length; i++)
        {
            var p = world[i];
            long key = (long)Mathf.RoundToInt(p.x * q) * 73856093L ^
                       (long)Mathf.RoundToInt(p.y * q) * 19349663L ^
                       (long)Mathf.RoundToInt(p.z * q) * 83492791L;
            if (!map.TryGetValue(key, out int id))
            {
                id = next++;
                map[key] = id;
            }
            welded[i] = id;
        }
        var edgeCount = new Dictionary<long, int>(world.Length * 3);
        int subs = mesh.subMeshCount;
        for (int s = 0; s < subs; s++)
        {
            var tris = mesh.GetTriangles(s);
            if (tris == null) continue;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                Edge(edgeCount, welded[tris[t]], welded[tris[t + 1]]);
                Edge(edgeCount, welded[tris[t + 1]], welded[tris[t + 2]]);
                Edge(edgeCount, welded[tris[t + 2]], welded[tris[t]]);
            }
        }
        var rimIds = new HashSet<int>();
        foreach (var kv in edgeCount)
            if (kv.Value == 1) { rimIds.Add((int)(kv.Key >> 32)); rimIds.Add((int)(kv.Key & 0xffffffffL)); }
        var rim = new List<Vector3>();
        for (int i = 0; i < welded.Length; i++)
            if (rimIds.Contains(welded[i])) rim.Add(world[i]);
        return rim.ToArray();
    }

    private static void Edge(Dictionary<long, int> edges, int a, int b)
    {
        if (a == b) return;
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        edges.TryGetValue(key, out int n);
        edges[key] = n + 1;
    }

    private static void Acc(List<KeyValuePair<int, float>> list, int bone, float w)
    {
        if (w <= 0f || bone < 0) return;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Key == bone)
            {
                list[i] = new KeyValuePair<int, float>(bone, list[i].Value + w);
                return;
            }
        list.Add(new KeyValuePair<int, float>(bone, w));
    }

    private static bool Normalise(List<KeyValuePair<int, float>> pairs, out BoneWeight result)
    {
        result = new BoneWeight();
        if (pairs.Count == 0) return false;
        pairs.Sort((x, y) => y.Value.CompareTo(x.Value));
        if (pairs.Count > MaxInfluences) pairs.RemoveRange(MaxInfluences, pairs.Count - MaxInfluences);
        float sum = 0f;
        for (int i = 0; i < pairs.Count; i++) sum += pairs[i].Value;
        if (sum <= 1e-6f) return false;
        var bw = new BoneWeight();
        for (int i = 0; i < pairs.Count; i++)
        {
            float w = pairs[i].Value / sum;
            switch (i)
            {
                case 0: bw.boneIndex0 = pairs[i].Key; bw.weight0 = w; break;
                case 1: bw.boneIndex1 = pairs[i].Key; bw.weight1 = w; break;
                case 2: bw.boneIndex2 = pairs[i].Key; bw.weight2 = w; break;
                default: bw.boneIndex3 = pairs[i].Key; bw.weight3 = w; break;
            }
        }
        result = bw;
        return true;
    }

    // ------------------------------------------------------------------ skin sampling
    private sealed class SkinData
    {
        public bool Ok;
        public string Error = "";
        private Vector3[] _pos;                 // world-bind
        private BoneWeight[] _bw;
        private string[] _bone;                 // bone name by index
        private readonly Dictionary<long, List<int>> _grid = new();
        private const float Cell = 0.02f;

        public SkinData(IHostContext log, SkinnedMeshRenderer r)
        {
            try
            {
                var mesh = r.sharedMesh;
                var verts = mesh.vertices;
                var bw = mesh.boneWeights;
                var bones = r.bones;
                if (verts == null || bw == null || bones == null || bw.Length != verts.Length)
                {
                    Error = "vertices/boneWeights/bones missing or mismatched";
                    return;
                }
                var m = r.transform.localToWorldMatrix;
                _pos = new Vector3[verts.Length];
                _bw = new BoneWeight[bw.Length];
                for (int v = 0; v < verts.Length; v++)
                {
                    _pos[v] = m.MultiplyPoint3x4(verts[v]);
                    _bw[v] = bw[v];
                }
                _bone = new string[bones.Length];
                for (int b = 0; b < bones.Length; b++) _bone[b] = bones[b] != null ? bones[b].name : null;
                for (int v = 0; v < _pos.Length; v++)
                {
                    long k = Key(_pos[v]);
                    if (!_grid.TryGetValue(k, out var list)) { list = new List<int>(); _grid[k] = list; }
                    list.Add(v);
                }
                Ok = true;
            }
            catch (Exception e) { Error = e.GetType().Name + ": " + e.Message; }
        }

        private static long Key(Vector3 p) =>
            ((long)Mathf.FloorToInt(p.x / Cell) << 40) ^
            ((long)Mathf.FloorToInt(p.y / Cell) << 20) ^
            (long)Mathf.FloorToInt(p.z / Cell);

        // Fills `target` (indexed by the garment's bone indices) with the inverse-distance weighted
        // weight mix of the nearest skin vertices; bones the garment does not have are dropped.
        public bool Sample(Vector3 p, float[] target, Dictionary<string, int> garmentBone)
        {
            Array.Clear(target, 0, target.Length);
            int cx = Mathf.FloorToInt(p.x / Cell), cy = Mathf.FloorToInt(p.y / Cell), cz = Mathf.FloorToInt(p.z / Cell);
            int r = Mathf.CeilToInt(SampleRadius / Cell);
            float bestSq = SampleRadius * SampleRadius;
            // collect the k nearest
            var best = new List<(float d, int v)>(SampleK + 4);
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        long k = ((long)(cx + dx) << 40) ^ ((long)(cy + dy) << 20) ^ (long)(cz + dz);
                        if (!_grid.TryGetValue(k, out var list)) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            float d = (_pos[list[i]] - p).sqrMagnitude;
                            if (d > bestSq) continue;
                            best.Add((d, list[i]));
                        }
                    }
            if (best.Count == 0) return false;
            best.Sort((a, b) => a.d.CompareTo(b.d));
            if (best.Count > SampleK) best.RemoveRange(SampleK, best.Count - SampleK);
            float total = 0f;
            for (int i = 0; i < best.Count; i++)
            {
                float iw = 1f / Mathf.Max(best[i].d, 1e-8f);
                total += iw;
                var w = _bw[best[i].v];
                Add(w.boneIndex0, w.weight0, iw);
                Add(w.boneIndex1, w.weight1, iw);
                Add(w.boneIndex2, w.weight2, iw);
                Add(w.boneIndex3, w.weight3, iw);
            }
            if (total <= 0f) return false;
            float sum = 0f;
            for (int b = 0; b < target.Length; b++) sum += target[b];
            if (sum <= 1e-6f) return false;
            for (int b = 0; b < target.Length; b++) target[b] /= sum;
            return true;

            void Add(int bone, float w, float iw)
            {
                if (w <= 0f || bone < 0 || bone >= _bone.Length) return;
                string n = _bone[bone];
                if (string.IsNullOrEmpty(n) || !garmentBone.TryGetValue(n, out int gi)) return;
                target[gi] += w * iw;
            }
        }
    }

    // ------------------------------------------------------------------ teardown
    internal static void Restore(IHostContext log, List<Fix> fixes)
    {
        if (fixes == null) return;
        foreach (var f in fixes)
        {
            if (f == null) continue;
            try
            {
                if (f.R != null && f.Original != null) f.R.sharedMesh = f.Original;
            }
            catch (Exception e) { log.LogWarning($"[chest:restore] '{f.Mesh}' sharedMesh restore failed: {e.Message}"); }
            DestroyClone(f);
            log.LogInfo($"[chest:restore] mesh='{f.Mesh}' original sharedMesh restored, clone destroyed " +
                        $"(changed={f.Changed})");
        }
        fixes.Clear();
    }

    private static void DestroyClone(Fix f)
    {
        if (f?.Clone == null) return;
        try { UnityEngine.Object.Destroy(f.Clone); } catch { }
        f.Clone = null;
    }

    // ------------------------------------------------------------------ dump (probe)
    // One CSV per matching renderer: bind-space vertex positions, weights, bone bind-pose heads
    // and the triangle list. This is how the region rule above was chosen - from the real mesh.
    internal static string Dump(IHostContext log, ThirdViewPlayerModel model, string match)
    {
        if (model == null) return null;
        var comps = model.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
        int written = 0;
        string lastName = null;
        for (int i = 0; comps != null && i < comps.Length; i++)
        {
            var r = comps[i]?.TryCast<SkinnedMeshRenderer>();
            if (r == null) continue;
            string name = r.gameObject != null ? r.gameObject.name : "?";
            if (match.Length > 0 && match != "*" && name.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var mesh = r.sharedMesh;
            if (mesh == null) continue;
            var bw = mesh.boneWeights;
            var verts = mesh.vertices;
            var binds = mesh.bindposes;
            var bones = r.bones;
            if (bw == null || verts == null || binds == null || bones == null) continue;
            if (bw.Length != verts.Length || binds.Length != bones.Length) continue;

            var sb = new StringBuilder();
            sb.AppendLine($"mesh={name} verts={verts.Length} bones={bones.Length} " +
                          $"tr={r.transform.name} lossyScale={Fmt(r.transform.lossyScale)} " +
                          $"localScale={Fmt(r.transform.localScale)}");
            var m = r.transform.localToWorldMatrix;
            for (int b = 0; b < bones.Length; b++)
            {
                var head = m.MultiplyPoint3x4(binds[b].inverse.MultiplyPoint3x4(Vector3.zero));
                string bn = bones[b] != null ? bones[b].name : "<null>";
                sb.AppendLine($"b,{b},{bn},{Fmt(head)}");
            }
            for (int v = 0; v < verts.Length; v++)
            {
                var w = bw[v];
                Vector3 p = m.MultiplyPoint3x4(verts[v]);
                sb.AppendLine($"v,{v},{Fmt(p)}," +
                              $"{w.boneIndex0}:{w.weight0.ToString("F4", CultureInfo.InvariantCulture)}," +
                              $"{w.boneIndex1}:{w.weight1.ToString("F4", CultureInfo.InvariantCulture)}," +
                              $"{w.boneIndex2}:{w.weight2.ToString("F4", CultureInfo.InvariantCulture)}," +
                              $"{w.boneIndex3}:{w.weight3.ToString("F4", CultureInfo.InvariantCulture)}");
            }
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    sb.AppendLine($"t,{tris[t]},{tris[t + 1]},{tris[t + 2]}");
            }
            string safe = (name + "_" + match).Replace(' ', '_');
            foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
            string path = Path.Combine(log.ScreensDir, "chestdump_" + safe + ".csv");
            try
            {
                File.WriteAllText(path, sb.ToString());
                log.LogInfo($"[chest:dump] mesh='{name}' verts={verts.Length} bones={bones.Length} -> {path}");
                written++;
                lastName = path;
            }
            catch (Exception e) { log.LogWarning($"[chest:dump] write '{name}' failed: {e.Message}"); }
        }
        if (written == 0) log.LogWarning($"[chest:dump] no readable SkinnedMeshRenderer matched '{match}'");
        return lastName;
    }

    private static string Fmt(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "{0:F5},{1:F5},{2:F5}", v.x, v.y, v.z);
}
