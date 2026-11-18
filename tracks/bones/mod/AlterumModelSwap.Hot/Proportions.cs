using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MagicaCloth2;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Enlarged breasts (female rigs only) and hips (all 3P rigs) by scaling bones, plus a report of
// the shipped MagicaCloth2 breast rig that supplies the jiggle.
//
// Ground truth this is built on (tracks/bones/notes/notes.md, static analysis of the shipped
// prefabs - not guesses):
//   * the 5 female 3P rigs have chest_01_l/r -> chest_02_l/r; the bone name case differs between
//     rig generations (Chest_01_l vs chest_01_l) so every lookup here is case-insensitive;
//   * no male rig has any chest/breast bone, and no rig has a hip/butt bone at all - the only
//     hip controls are pelvis and thigh_l/r;
//   * every female 3P prefab already ships a MagicaCloth2 BoneCloth on <Model>/Magica Cloth/Breast
//     whose rootBones are chest_01_l/r. Scaling the *leaf* chest_02_* leaves both cloth root
//     bones exactly where the virtual mesh sampled them, so the rest lengths stay valid and the
//     shipped cloth keeps jiggling the (now larger) breast. Scaling chest_01_* instead would move
//     the simulated points and require a rebuild - that is why the leaf is used.
//   * pelvis is the root of the body under "root", so its localScale multiplies every child's
//     offset and every child's scale. It is therefore handled as: scale pelvis, counter-scale its
//     direct children to 1/s (restoring their subtrees to their natural size) and divide their
//     local Y by s (keeping feet on the floor and the spine at its height). X and Z are left
//     scaled, which is the actual widening: the hip mass inflates and the leg roots move apart.
//   * the leaf alone cannot grow past ~1.6: it scales the SKIN bust while the tank top ignores it.
//     Measured skin weights (pass 3, `propcap weights=*`) show which bone carries the fabric:
//       Diana/Kiona/Sarah  Jane_Undershirt mass  spine_04 847, clavicles 597, spine_03 300,
//                          pelvis 217, spine_02 148, chest_02 109, chest_01 0
//       Emma/Freya         Shirt (vest) band     Chest_02 1141, clavicles 1070, neck 72,
//                          spine_03 53
//     So the garment follows the PARENT of chest_01 (spine_04 there, spine_02 on the older
//     Emma/Freya rig), not chest_01 itself (Diana's shirt has no chest_01 weight at all). That
//     parent - the "cage" - is scaled by ChestCageScale, which moves the shirt and the ribcage
//     together with the whole bust chain underneath it. Its non-chest children (spine_05 ->
//     clavicles/neck/head on Diana, spine_03 -> clavicles/neck/head on Freya, plus the accessory
//     holders) are counter-scaled to 1/s and their local positions divided by s, so the shoulders,
//     arms, neck and total height are bit-identical to the unscaled rig. The leaf keeps its own
//     relative scale: total bust enlargement = BreastLeafScale * ChestCageScale.
//
// Applied on every PlayerModelSetuper.SetupModel (Harmony postfix -> a sweep on the next frame,
// because the model is not finished initialising inside the postfix), so respawns and wardrobe
// changes are covered, and restored on teardown.
internal sealed class Proportions : IDisposable
{
    // --- tunables ------------------------------------------------------------------------
    // Breasts: uniform scale on the two chest_02 leaves. 1.80 (the pass-2 first request) put the
    // bare skin through the tank top near the strap; 1.62 shows the first single-pixel poke and
    // 1.55 is clean with margin on every female outfit checked (Diana/Freya/Emma/Sarah). This is
    // the honest ceiling of leaf-only scaling: the shirt is weighted mostly to chest_01, so the
    // skin outgrows it long before the clothed silhouette reaches 1.8x. Request keys
    // breastscale/hipscale re-apply a different value live for A/B captures (see below).
    private const float BreastLeafScaleDefault = 1.55f;
    // Chest cage: uniform scale on the parent of the two chest_01 bones (the bone the tank top is
    // actually weighted to - see the header). 1.00 is the pass-2 behaviour (leaf only). The value
    // is the lever that makes the fabric follow: at 1.097 the chest is 1.55*1.097 = 1.70x its
    // natural size and the tank top grows with the skin instead of being overrun by it. The
    // counter-scaled children keep shoulder width / arm size / height exactly as they were.
    //
    // History: 1.161 (1.80x total) was shipped in pass 3 and 1.097 (1.70x) in pass 4, because past
    // ~1.70x a skin facet pushed out between two fabric vertices at the upper chest - a tan patch
    // on the tank top that the vertex-based penetration count cannot see (2232 garment vertices
    // against 20836 skin ones). Pass 5 fixed the cause instead: the garment's chest weights now
    // follow the skin under them (ChestWeights.cs), so the fabric deforms with the chest. With
    // that, the visible skin-through-garment pixels at the strap junction are 0 at 1.70, 1.80 and
    // 1.90 (and at 1.95/2.00/2.10), against 98 / 238 / 311 px before the weight fix. 1.90 is the
    // largest swept value and is shipped here; see notes/proportions-pass5.md.
    private const float ChestCageScaleDefault = 1.22581f;
    // Hips: pelvis uniform scale. 1.50 moves each leg root 50% of its pelvis-relative X outwards
    // (measured 0.243 m -> 0.299 m thigh-root separation) while the torso and legs keep their
    // size and height because of the child compensation (derived from this value, not stale).
    // 1.55/1.60 were also clean in the renders; 1.50 is the requested first-pass value and reads
    // as a deliberate exaggeration on the male rigs too, which every pelvis scaling touches.
    private const float HipScaleDefault = 1.50f;

    private static float _breastScale = BreastLeafScaleDefault;
    private static float _hipScale = HipScaleDefault;
    private static float _cageScale = ChestCageScaleDefault;

    internal static float BreastLeafScale => _breastScale;
    internal static float HipScale => _hipScale;
    internal static float ChestCageScale => _cageScale;
    // Experiment: grow the clavicles/neck with the cage instead of holding them at 1x, and pin the
    // arm/head attachment points one level deeper (upperarm_l/r, neck_02/head). Same invariance,
    // but the tank top's hem/straps (clavicle + neck weighted) grow with the skin they sit on.
    private static bool _clavGrow = ClavicleGrowDefault;
    private const bool ClavicleGrowDefault = false;
    internal static bool ClavicleGrow => _clavGrow;
    // Total bust enlargement relative to the natural rig (what the pass-2 sweep values meant).
    internal static float ChestTotalScale => _breastScale * _cageScale;

    private const float SweepInterval = 1.0f;
    private static Proportions _current;

    // The bone pass owns the scaling; JiggleSolver attaches only to a rig this instance has already
    // applied to, which is the ordering guarantee for "offset on top of the established scale".
    internal static Proportions Current => _current;
    internal bool KnowsRig(int modelKey) => _rigs.ContainsKey(modelKey);

    private sealed class Node
    {
        public Transform T;
        public Vector3 Scale, Pos;
        public Vector3 Offset;      // natural world offset from the pelvis (pelvis children only)
        public Vector3 TargetPos;   // localPosition we applied (pelvis / cage children)
        public int LatePins;        // how often LateTick had to undo an animation write (cage children)
        public string Role;
    }

    private sealed class Rig
    {
        public int Model;
        public ThirdViewPlayerModel ModelRef;
        public string Name = "";
        public bool Female;
        public readonly List<Node> Nodes = new();
        public int BreastCloths, ClothCount, BreastClothsRunning;
        public bool ClothInitTried;
        public float HipHalfWidth;
        public float NextVerify;
        public int Verifies;
        public List<ChestWeights.Fix> ChestFixes;
    }

    private readonly IHostContext _log;
    private readonly string _id = "com.user0.alterum.proportions." + Guid.NewGuid().ToString("N");
    private readonly Dictionary<int, Rig> _rigs = new();
    private Harmony _harmony;
    private float _nextSweep, _nextAudit;
    private bool _disposed, _auditDone, _patchWarned;
    private int _setups;

    // Test/diagnostic hooks (proportions.request, one-shot keys, polled at 1 Hz). The failure
    // injection exists to prove the apply transaction: the request arms one throw, the apply
    // rolls back, and the next sweep must record the true (unscaled) originals.
    private const string RequestName = "proportions.request";
    private DateTime _reqMtime = DateTime.MinValue;
    private float _nextReqPoll;
    private string _failPoint = "";

    internal Proportions(IHostContext log) => _log = log;

    // Re-assert every recorded node right now (same writes as the periodic verifier). The capture
    // probe calls this before each render because the animation system overwrites the pelvis-child
    // local positions, which would otherwise show a different leg-root offset in every view.
    internal void ReapplyAll(string why)
    {
        int n = 0;
        foreach (var kv in _rigs)
            foreach (var node in kv.Value.Nodes)
                try { SetNode(kv.Value, node); n++; } catch { }
        if (n > 0) _log.LogInfo($"[bounds:reapply] {_rigs.Count} rig(s), {n} node(s) re-asserted ({why})");
    }

    // Per-frame pass, called from ModelSwapRuntime.LateUpdate after the animation has written the
    // skeleton: the animator is free to rewrite a bone's local POSITION (it does for spine_01 /
    // thigh_l / thigh_r, see implementation.md), which would undo the cage-child pin and move the
    // shoulders/neck by (s-1)*offset. Re-asserting just those few positions costs a handful of
    // transform writes per rig and keeps the invariance exact on animated rigs, not only on frozen
    // capture clones. Scales are left to the periodic verifier (nothing animates scale).
    internal void LateTick()
    {
        if (_disposed || Mathf.Abs(_cageScale - 1f) < 1e-5f) return;
        foreach (var kv in _rigs)
        {
            var rig = kv.Value;
            foreach (var node in rig.Nodes)
            {
                if (node.Role != "cage-comp" && node.Role != "cage-trunk") continue;
                try
                {
                    if (node.T == null || node.TargetPos == Vector3.zero) continue;
                    if ((node.T.localPosition - node.TargetPos).sqrMagnitude > 1e-10f)
                    {
                        node.T.localPosition = node.TargetPos;
                        node.LatePins++;
                    }
                }
                catch { }
            }
        }
    }

    // Runtime scale override used by the proportions-pass2 capture/tuning loop (ProportionShots).
    // Restores every rig from its recorded originals and re-applies immediately, so the new
    // counter-scale and offsets are computed from the true unit values, never from a ratio
    // derived for the old scale. The shipped values above stay the defaults.
    internal void SetScales(float breast, float hip, string why) => SetScales(breast, hip, _cageScale, why);

    // Runtime toggle for the garment chest-weight fix (A/B captures). Off restores the authored
    // sharedMesh and destroys the clones; on re-applies to every registered rig.
    private bool _chestFix = true;
    internal bool ChestFixEnabled => _chestFix;

    internal void SetChestFix(bool on, string why)
    {
        if (_disposed || on == _chestFix) return;
        _chestFix = on;
        if (!on)
        {
            int n = 0;
            foreach (var kv in _rigs)
            {
                if (kv.Value.ChestFixes == null) continue;
                n += kv.Value.ChestFixes.Count;
                ChestWeights.Restore(_log, kv.Value.ChestFixes);
                kv.Value.ChestFixes = null;
            }
            _log.LogWarning($"[chest:off] {n} garment mesh fix(es) reverted to the authored weights ({why})");
            return;
        }
        int applied = 0, changed = 0;
        foreach (var kv in _rigs)
        {
            var rig = kv.Value;
            try
            {
                if (rig.ModelRef == null) continue;
                rig.ChestFixes = ChestWeights.Apply(_log, rig.ModelRef, rig.Female);
                applied += rig.ChestFixes.Count;
                foreach (var f in rig.ChestFixes) changed += f.Changed;
            }
            catch (Exception e) { _log.LogWarning($"[chest:on-failed] rig='{rig.Name}': {e.Message}"); }
        }
        _log.LogWarning($"[chest:on] chest weight fix re-applied ({why}): {applied} mesh(es), {changed} vert(s)");
    }

    internal void SetClavicleGrow(bool grow, string why)
    {
        if (_disposed || grow == _clavGrow) return;
        _clavGrow = grow;
        _log.LogInfo($"[bounds:chest-mode] clavicleGrow={(_clavGrow ? 1 : 0)} ({why}): re-deriving every rig from recorded originals");
        foreach (var kv in _rigs)
            foreach (var node in kv.Value.Nodes)
                try { SetNode(kv.Value, node); } catch { }
    }

    internal void SetScales(float breast, float hip, float cage, string why)
    {
        if (_disposed) return;
        breast = Mathf.Clamp(breast, 1f, 3f);
        hip = Mathf.Clamp(hip, 1f, 3f);
        cage = Mathf.Clamp(cage, 0.5f, 3f);
        if (Mathf.Abs(breast - _breastScale) < 1e-4f && Mathf.Abs(hip - _hipScale) < 1e-4f &&
            Mathf.Abs(cage - _cageScale) < 1e-4f) return;
        string was = string.Format(CultureInfo.InvariantCulture, "{0:F2}/{1:F2}/{2:F3}", _breastScale, _hipScale, _cageScale);
        _breastScale = breast; _hipScale = hip; _cageScale = cage;
        // Re-derive every write from the recorded unit originals and natural offsets. No
        // restore/resweep: the originals must stay the ones recorded from the true pose, or a
        // scale change would re-base them from whatever frame the animation happened to be on.
        int nodes = 0;
        foreach (var kv in _rigs)
            foreach (var node in kv.Value.Nodes)
                try { SetNode(kv.Value, node); nodes++; } catch { }
        _log.LogInfo($"[bounds:scale] breast/hip/cage {was} -> " +
                     string.Format(CultureInfo.InvariantCulture, "{0:F2}/{1:F2}/{2:F3}", _breastScale, _hipScale, _cageScale) +
                     $" (total chest {ChestTotalScale:F2}) ({why}): {_rigs.Count} rig(s), {nodes} node(s) re-derived from recorded originals " +
                     $"(hip counter-scale 1/{_hipScale:F2} = {1f / _hipScale:F3}, cage counter-scale 1/{_cageScale:F3} = {1f / _cageScale:F3})");
    }

    private static int Ptr(Il2CppObjectBase o) => o == null ? 0 : (int)o.Pointer.ToInt64();

    internal void Tick(float t)
    {
        if (_disposed) return;
        if (_failPoint.Length == 0) PollRequest();
        if (_harmony == null && !_patchWarned) InstallPatches();
        if (!_auditDone && t >= _nextAudit) { _nextAudit = t + 2f; _auditDone = Audit(); }
        if (t >= _nextSweep) { _nextSweep = t + SweepInterval; Sweep(); }
        VerifyDue(t);
    }

    // ---------------------------------------------------------------- hook
    private void InstallPatches()
    {
        try
        {
            _harmony = new Harmony(_id);
            _harmony.Patch(
                AccessTools.DeclaredMethod(typeof(PlayerModelSetuper), "SetupModel", new[] { typeof(PlayerModelData) }),
                postfix: new HarmonyMethod(typeof(Proportions), nameof(SetupModelPostfix)));
            _current = this;
            _log.LogInfo($"[bounds:patch-installed] generation={_id} SetupModel postfix -> sweep");
        }
        catch (Exception e)
        {
            _patchWarned = true;
            _log.LogError($"[bounds:patch-failed] generation={_id}: {e}");
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
            // Without the hook the periodic sweep still catches every new model, just later.
        }
    }

    // Runs inside the game's SetupModel: the model exists but is not finished initialising, so
    // only pull the sweep forward.
    private static void SetupModelPostfix()
    {
        var self = _current;
        if (self == null || self._disposed) return;
        self._nextSweep = float.NegativeInfinity;
        if (++self._setups <= 6)
            self._log.LogInfo($"[bounds:setup] SetupModel#{self._setups} -> sweep on next update (respawn / wardrobe change covered)");
    }

    // ---------------------------------------------------------------- audit (once)
    // Read-only description of every ThirdViewPlayerModel that is loaded, including the model
    // assets (no scene) that no player is currently using. This is what proves "all 5 female rigs
    // have the two-bone breast chain + cloth, no male rig has any".
    private bool Audit()
    {
        try
        {
            var datas = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerModelData>());
            if (datas.Length == 0) return false;
            var sb = new StringBuilder();
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            var seen = new HashSet<int>();
            int rows = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || !seen.Add(Ptr(m))) continue;
                var go = m.gameObject;
                if (go == null) continue;
                sb.AppendLine(Describe(m, go.scene.name == null || go.scene.name.Length == 0 ? "asset" : go.scene.name));
                rows++;
            }
            _log.LogInfo($"[bounds:audit] {rows} ThirdViewPlayerModel object(s):\n{sb}");
            return rows > 0;
        }
        catch (Exception e)
        {
            _log.LogWarning($"[bounds:audit] failed: {e.Message}");
            return false;
        }
    }

    private string Describe(ThirdViewPlayerModel m, string where)
    {
        var bones = m.Bones;
        var all = bones?._allBones;
        int count = all?.Count ?? 0;
        var chest = new List<string>();
        var pelvis = "<none>";
        var thighs = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var tr = all[i];
            if (tr == null) continue;
            string n = tr.name ?? "";
            if (n.Equals("chest_02_l", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("chest_02_r", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("chest_01_l", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("chest_01_r", StringComparison.OrdinalIgnoreCase))
                chest.Add(n);
            else if (n.Equals("pelvis", StringComparison.OrdinalIgnoreCase)) pelvis = n;
            else if (n.Equals("thigh_l", StringComparison.OrdinalIgnoreCase) || n.Equals("thigh_r", StringComparison.OrdinalIgnoreCase))
                thighs.Add(n);
        }
        var cloth = ScanCloth(m, null, allowInit: false);
        return $"  '{m.gameObject.name}' where={where} bones={count} chestBones=[{string.Join(",", chest)}] " +
               $"pelvis='{pelvis}' thighs=[{string.Join(",", thighs)}] cloths={cloth}";
    }

    // ---------------------------------------------------------------- apply
    private void Sweep()
    {
        if (_disposed) return;
        // ClothSim.Tick() is driven per frame from ModelSwapRuntime.Update: registration must be
        // readiness-gated and manager-scoped, which a 1 Hz sweep cannot see.
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            var live = new HashSet<int>();
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null) continue;
                var go = m.gameObject;
                if (go == null) continue;
                // Model assets (prefab templates) and the wardrobe's shared templates have no
                // scene; mutating a prefab asset would bake the scale into every later instance.
                if (go.scene.name == null || go.scene.name.Length == 0) continue;
                int key = Ptr(m);
                live.Add(key);
                if (m.Bones?._allBones == null || m.Bones._allBones.Count == 0) continue;  // not initialised yet
                if (_rigs.TryGetValue(key, out var known))
                {
                    // Already applied: only check that nothing (animation, ragdoll switch) reset it.
                    if (Time.unscaledTime >= known.NextVerify) Verify(known, m);
                    continue;
                }
                Apply(m, key);
            }
            // Models that went away: restore what still exists, forget the rest.
            if (_rigs.Count > 0)
            {
                List<int> gone = null;
                foreach (var kv in _rigs)
                    if (!live.Contains(kv.Key)) (gone ??= new List<int>()).Add(kv.Key);
                if (gone != null)
                    foreach (var key in gone)
                    {
                        Restore(_rigs[key], "model gone");
                        _rigs.Remove(key);
                    }
            }
        }
        catch (Exception e) { _log.LogError($"[bounds:sweep-failed] {e}"); }
    }

    private void Apply(ThirdViewPlayerModel model, int key)
    {
        var all = model.Bones._allBones;
        Transform pelvis = null, chestL = null, chestR = null, clavL = null, clavR = null, neck = null;
        for (int i = 0; i < all.Count; i++)
        {
            var t = all[i];
            if (t == null) continue;
            string n = t.name ?? "";
            if (pelvis == null && n.Equals("pelvis", StringComparison.OrdinalIgnoreCase)) pelvis = t;
            else if (chestL == null && n.Equals("chest_02_l", StringComparison.OrdinalIgnoreCase)) chestL = t;
            else if (chestR == null && n.Equals("chest_02_r", StringComparison.OrdinalIgnoreCase)) chestR = t;
            else if (clavL == null && n.Equals("clavicle_l", StringComparison.OrdinalIgnoreCase)) clavL = t;
            else if (clavR == null && n.Equals("clavicle_r", StringComparison.OrdinalIgnoreCase)) clavR = t;
            else if (neck == null && n.Equals("neck_01", StringComparison.OrdinalIgnoreCase)) neck = t;
        }

        var rig = new Rig
        {
            Model = key,
            ModelRef = model,
            Name = model.gameObject.name,
            Female = chestL != null && chestR != null,
            NextVerify = Time.unscaledTime + 2f,
        };

        // Register the rig BEFORE the first write. Node originals are recorded by Touch() before
        // each individual write, so from this point on teardown and the periodic verifier can see
        // exactly what this apply touched. Without this, a throw halfway through left the already
        // scaled bones outside every registry; the next sweep treated the rig as new, recorded
        // the scaled values as originals and compounded the modification.
        _rigs[key] = rig;
        var notes = new List<string>();
        try
        {
            if (rig.Female)
            {
                ScaleLeaf(rig, chestL, "chest_02_l");
                Inject("afterBreast");
                ScaleLeaf(rig, chestR, "chest_02_r");
                notes.Add($"breast={BreastLeafScale}x on '{chestL.name}','{chestR.name}'");
                // Cage = the bone the tank top is actually weighted to = the parent of chest_01_l/r
                // (one level above the leaves). Both chest_01 bones must share it.
                var pivotL = ParentOf(chestL);
                var pivotR = ParentOf(chestR);
                var cage = ParentOf(pivotL);
                if (cage != null && pivotR != null && ParentOf(pivotR) != null && Ptr(ParentOf(pivotR)) == Ptr(cage))
                    ScaleCage(rig, cage, clavL, clavR, neck, notes);
                else notes.Add("cage=not-applicable (chest_01_l/r have no common parent)");
            }
            else notes.Add("breast=not-applicable (no chest_02_l/r in this rig)");

            if (pelvis == null) notes.Add("hips=not-applicable (no pelvis bone)");
            else ScaleHips(rig, pelvis, notes);

            // The garment's chest weights: the fabric over the bust takes the weight mix of the
            // skin under it (ChestWeights.cs). Runs after the bones so the sampling sees the same
            // rig, and is registered on the rig so teardown restores the authored mesh.
            rig.ChestFixes = _chestFix ? ChestWeights.Apply(_log, model, rig.Female) : new List<ChestWeights.Fix>();
            if (rig.ChestFixes.Count > 0)
            {
                int changed = 0, cand = 0;
                var names = new List<string>();
                foreach (var f in rig.ChestFixes) { changed += f.Changed; cand += f.Candidates; names.Add(f.Mesh); }
                notes.Add($"chestWeights={changed}/{cand} vert(s) on [{string.Join(",", names)}]");
            }
            else notes.Add("chestWeights=none");

            string cloth = ScanCloth(model, rig, allowInit: true);

            _log.LogInfo($"[bounds:apply] rig='{rig.Name}' model=0x{key:X} bones={all.Count} female={rig.Female} " +
                         $"scaled={rig.Nodes.Count} breastScale={(rig.Female ? BreastLeafScale : 1f)} " +
                         $"hipScale={(pelvis != null ? HipScale : 1f)} {string.Join(" ", notes)} cloth={cloth}");
        }
        catch (Exception e)
        {
            // Roll the transaction back from the recorded originals and forget the rig, so the
            // next sweep retries from a clean slate instead of applying 1.45 on top of 1.45.
            Restore(rig, "apply failed");
            _rigs.Remove(key);
            _log.LogError($"[bounds:apply-failed] rig='{rig.Name}' model=0x{key:X} " +
                          $"({e.GetType().Name}: {e.Message}) rolled back {rig.Nodes.Count} node(s); " +
                          $"rig forgotten, next sweep retries from the true originals");
        }
    }

    // Throws once when the request armed this exact point, then disarms. Test-only.
    private void Inject(string point)
    {
        if (_failPoint != point) return;
        _failPoint = "";
        throw new InvalidOperationException("injected failure at '" + point + "'");
    }

    private void ScaleLeaf(Rig rig, Transform leaf, string role)
    {
        var node = Touch(rig, leaf, role);
        leaf.localScale = node.Scale * BreastLeafScale;
    }

    private static Transform ParentOf(Transform t)
    {
        try { return t != null ? t.parent : null; } catch { return null; }
    }

    private static bool IsChestChain(string name) =>
        (name ?? "").StartsWith("chest_01", StringComparison.OrdinalIgnoreCase);

    // The bone that actually carries the tank top: the parent of chest_01_l/r (spine_04 on the
    // Diana/Kiona/Sarah rigs, spine_02 on Emma/Freya). Scaling it moves the fabric AND the whole
    // bust chain. What must NOT change is only the outer body: shoulders/arms and the head.
    //
    // Pass-3 first attempt counter-scaled the whole non-chest subtree and the tank top's collar
    // (weighted to spine_05, which was shrunk to 1/s) collapsed into a scoop neckline - so the
    // compensation is applied exactly one level later, on the trunks that lead to the arms/head:
    //
    //   cage                       scale * s        (the garment and the chest grow)
    //   cage-trunk (spine_05/03)   position / s     (scale stays s: its own skin is chest, grows)
    //   clavicle_l/r, neck_01      scale,position / s   (world size + position exactly unchanged)
    //   holders/hitboxes           scale,position / s   (accessories must not inflate)
    //
    // The upperarm/head subtrees inherit the compensated parent, so shoulder width, arm size and
    // total height are bit-identical. Local positions are recorded once, so re-applying is
    // idempotent, and LateTick re-asserts the pinned positions after the animator each frame.
    private void ScaleCage(Rig rig, Transform cage, Transform clavL, Transform clavR, Transform neck, List<string> notes)
    {
        var node = Touch(rig, cage, "cage");
        float s = ChestCageScale;

        // The trunk is the parent of the clavicles (spine_05 on Diana, spine_03 on Freya). If the
        // clavicles are missing or do not share a parent, fall back to counter-scaling everything
        // except the bust chain (old behaviour, keeps the rig's dimensions but flattens collars).
        Transform trunk = ParentOf(clavL);
        if (trunk != null && (ParentOf(clavR) == null || Ptr(ParentOf(clavR)) != Ptr(trunk) || Ptr(trunk) == Ptr(cage)))
            trunk = null;

        var pinned = new List<(Node node, Transform t, string name, bool counterScale)>();
        int n = cage.childCount;
        for (int i = 0; i < n; i++)
        {
            var child = cage.GetChild(i);
            if (child == null) continue;
            string nm = child.name ?? "";
            if (IsChestChain(nm)) continue;                 // the bust chain grows with the cage
            bool isTrunk = trunk != null && Ptr(child) == Ptr(trunk);
            var c = Touch(rig, child, isTrunk ? "cage-trunk" : "cage-comp");
            // trunk: keep the inherited scale (its own skin/garment is chest and must grow),
            //        pin only the position; everything else: counter-scale and pin.
            pinned.Add((c, child, nm, !isTrunk));
        }

        // The arm/head attachment points inside the trunk: counter-scaled and pinned so the
        // clavicles and the neck/head land exactly where they were. In clavicle-grow mode they
        // keep the inherited cage scale and only their attachment children are compensated, so
        // the garment hem/straps that ride on the clavicles grow with the skin under them.
        if (trunk != null)
        {
            foreach (var t in new[] { clavL, clavR, neck })
            {
                if (t == null) continue;
                if (_clavGrow)
                {
                    var trunkNode = Touch(rig, t, "cage-trunk");
                    pinned.Add((trunkNode, t, t.name ?? "", false));
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var ch = t.GetChild(i);
                        if (ch == null) continue;
                        var cn = Touch(rig, ch, "cage-comp");
                        pinned.Add((cn, ch, ch.name ?? "", true));
                    }
                }
                else
                {
                    var c = Touch(rig, t, "cage-comp");
                    pinned.Add((c, t, t.name ?? "", true));
                }
            }
            // anything else the trunk carries (hitboxes, holders) is an accessory: pin it too
            for (int i = 0; i < trunk.childCount; i++)
            {
                var child = trunk.GetChild(i);
                if (child == null) continue;
                if (Ptr(child) == Ptr(clavL) || Ptr(child) == Ptr(clavR) || Ptr(child) == Ptr(neck)) continue;
                string nm = child.name ?? "";
                if (nm.IndexOf("clavicle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.IndexOf("neck", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var c = Touch(rig, child, "cage-comp");
                pinned.Add((c, child, nm, true));
            }
        }

        cage.localScale = node.Scale * s;
        foreach (var p in pinned)
        {
            try
            {
                // write BOTH branches from the recorded originals: a node that changed role (e.g.
                // the clavicle-grow experiment toggled live) must not keep the other branch's scale
                p.t.localScale = p.counterScale ? p.node.Scale / s : p.node.Scale;
                p.t.localPosition = p.node.Pos / s;
                p.node.TargetPos = p.t.localPosition;
            }
            catch { }
        }
        notes.Add($"cage={s:F3}x on '{cage.name}' (total chest {ChestTotalScale:F2}x) trunk='{(trunk != null ? trunk.name : "none")}' " +
                  $"pinned=[{string.Join(",", pinned.ConvertAll(p => p.name + (p.counterScale ? "" : "*")))}] (* = position only)");
    }

    // pelvis: uniform s, direct children counter-scaled to 1/s with their world offset from the
    // pelvis kept in Y and multiplied by s in X/Z. That is the widening itself: the hip mass
    // inflates (all verts weighted to pelvis), the leg roots move apart and the accessories move
    // with them, while the torso keeps its height and the feet stay on the floor.
    // Offsets are recorded once, before anything is scaled, so re-applying is idempotent.
    private void ScaleHips(Rig rig, Transform pelvis, List<string> notes)
    {
        var node = Touch(rig, pelvis, "pelvis");
        float widthBefore = HipWidth(pelvis);
        // current pelvis scale factor relative to the original, so a child added later is measured
        // back into "natural" units instead of being scaled twice
        float rel = node.Scale.x > 1e-6f ? pelvis.localScale.x / node.Scale.x : 1f;
        Vector3 p0 = pelvis.position;

        int n = pelvis.childCount;
        var kids = new List<(Node node, Transform t, Vector3 offset)>();
        for (int i = 0; i < n; i++)
        {
            var child = pelvis.GetChild(i);
            if (child == null) continue;
            var c = Touch(rig, child, "pelvis-child");
            Vector3 d = child.position - p0;
            if (c.Offset == Vector3.zero) c.Offset = new Vector3(d.x / rel, d.y, d.z / rel);
            kids.Add((c, child, c.Offset));
        }

        pelvis.localScale = node.Scale * HipScale;
        var names = new List<string>();
        bool firstChildWritten = false;
        foreach (var k in kids)
        {
            k.t.localScale = k.node.Scale / HipScale;
            var target = new Vector3(k.offset.x * HipScale, k.offset.y, k.offset.z * HipScale);
            k.t.localPosition = pelvis.InverseTransformVector(target);
            k.node.TargetPos = k.t.localPosition;
            names.Add(k.t.name);
            if (!firstChildWritten)
            {
                firstChildWritten = true;
                Inject("midHips");
            }
        }
        rig.HipHalfWidth = widthBefore * 0.5f;

        if (names.Count > 0)
            notes.Add($"hips={HipScale}x on '{pelvis.name}' (hipWidth={widthBefore:F3}m), children counter-scaled: [{string.Join(",", names)}]");
        else notes.Add($"hips={HipScale}x on '{pelvis.name}' (no children)");
    }

    // World-X distance between thigh_l and thigh_r, i.e. the hip width the change is applied to.
    private static float HipWidth(Transform pelvis)
    {
        try
        {
            Transform l = null, r = null;
            int n = pelvis.childCount;
            for (int i = 0; i < n; i++)
            {
                var child = pelvis.GetChild(i);
                if (child == null) continue;
                string nm = child.name ?? "";
                if (nm.Equals("thigh_l", StringComparison.OrdinalIgnoreCase) && l == null) l = child;
                else if (nm.Equals("thigh_r", StringComparison.OrdinalIgnoreCase) && r == null) r = child;
            }
            if (l == null || r == null) return 0f;
            return Mathf.Abs(l.position.x - r.position.x);
        }
        catch { return 0f; }
    }

    // Re-apply one recorded node from its original values (used by the periodic drift check).
    private void SetNode(Rig rig, Node node)
    {
        if (node == null || node.T == null) return;
        switch (node.Role)
        {
            case "pelvis":
                node.T.localScale = node.Scale * HipScale;
                break;
            case "pelvis-child":
                node.T.localScale = node.Scale / HipScale;
                var pelvis = FindRole(rig, "pelvis");
                if (pelvis != null && pelvis.T != null)
                {
                    var target = new Vector3(node.Offset.x * HipScale, node.Offset.y, node.Offset.z * HipScale);
                    node.T.localPosition = pelvis.T.InverseTransformVector(target);
                    node.TargetPos = node.T.localPosition;
                }
                break;
            case "cage":
                node.T.localScale = node.Scale * ChestCageScale;
                break;
            case "cage-comp":
                // pinned: size / s and position / s cancel the cage's uniform scale exactly
                node.T.localScale = node.Scale / ChestCageScale;
                node.T.localPosition = node.Pos / ChestCageScale;
                node.TargetPos = node.T.localPosition;
                break;
            case "cage-trunk":
                // position only: the trunk's own skin/garment is chest and must grow with the cage
                node.T.localPosition = node.Pos / ChestCageScale;
                node.TargetPos = node.T.localPosition;
                break;
            default:
                node.T.localScale = node.Scale * BreastLeafScale;
                break;
        }
    }

    private static Node FindRole(Rig rig, string role)
    {
        foreach (var n in rig.Nodes) if (n.Role == role) return n;
        return null;
    }

    private static Node Touch(Rig rig, Transform t, string role)
    {
        foreach (var n in rig.Nodes)
            if (Ptr(n.T) == Ptr(t))
            {
                // Re-role an existing node (the recorded originals are kept): the compensation each
                // node needs depends on the current chest mode, and a stale role would leave the
                // other mode's counter-scale applied.
                n.Role = role;
                return n;
            }
        var node = new Node { T = t, Scale = t.localScale, Pos = t.localPosition, Role = role };
        rig.Nodes.Add(node);
        return node;
    }

    // ---------------------------------------------------------------- request hooks (test)
    private void PollRequest()
    {
        float now = Time.unscaledTime;
        if (now < _nextReqPoll) return;
        _nextReqPoll = now + 1f;
        string path;
        try { path = Path.Combine(_log.ScreensDir, RequestName); } catch { return; }
        try
        {
            if (!File.Exists(path)) return;
            var mt = File.GetLastWriteTimeUtc(path);
            if (mt == _reqMtime) return;
            _reqMtime = mt;

            bool dump = false, restore = false;
            float breastScale = _breastScale, hipScale = _hipScale, cageScale = _cageScale;
            bool clavGrow = _clavGrow;
            bool chestFix = _chestFix;
            string fail = "";
            foreach (var raw in File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                bool on = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                switch (k)
                {
                    case "fail": fail = v; break;
                    case "dump": dump = on; break;
                    case "restore": restore = on; break;
                    case "breastscale": breastScale = Parse(v, breastScale); break;
                    case "hipscale": hipScale = Parse(v, hipScale); break;
                    case "chestscale": cageScale = Parse(v, cageScale); break;
                    case "clavgrow": clavGrow = !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                    case "chestfix": chestFix = !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                    default: _log.LogWarning($"[bounds:request] key '{k}' unknown; ignored"); break;
                }
            }

            if (dump) DumpRigs("request");
            if (restore)
            {
                foreach (var kv in _rigs) Restore(kv.Value, "request restore");
                _rigs.Clear();
                _log.LogWarning("[bounds:restore] restore=1: every rig restored from its recorded originals " +
                                "and forgotten; the next sweep re-applies and must record unit originals");
            }
            if (fail.Length > 0)
            {
                _failPoint = fail;
                _log.LogWarning($"[bounds:inject] armed fail='{fail}' (one-shot, consumed by the next apply)");
            }
            SetClavicleGrow(clavGrow, "proportions.request");
            SetChestFix(chestFix, "proportions.request");
            SetScales(breastScale, hipScale, cageScale, "proportions.request");
        }
        catch (Exception e)
        {
            _log.LogWarning($"[bounds:request] read failed: {e.Message}");
        }
    }

    private static float Parse(string v, float fallback) =>
        float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    private void DumpRigs(string why)
    {
        _log.LogInfo($"[bounds:dump] {_rigs.Count} rig(s) registered ({why})");
        foreach (var kv in _rigs)
        {
            var rig = kv.Value;
            _log.LogInfo($"[bounds:dump] rig='{rig.Name}' model=0x{rig.Model:X} female={rig.Female} " +
                         $"nodes={rig.Nodes.Count} verifies={rig.Verifies} nextVerify={rig.NextVerify:F1}");
            int lines = 0;
            foreach (var node in rig.Nodes)
            {
                if (lines++ >= 40) { _log.LogInfo("[bounds:dump]   ... truncated"); break; }
                try
                {
                    if (node.T == null) { _log.LogInfo($"[bounds:dump]   {node.Role} '<gone>'"); continue; }
                    var cur = node.T.localScale;
                    float ds = (cur - node.Scale).magnitude;
                    float dp = (node.Role == "pelvis-child" || node.Role == "cage-comp" || node.Role == "cage-trunk")
                        ? (node.T.localPosition - node.Pos).magnitude : 0f;
                    _log.LogInfo($"[bounds:dump]   {node.Role} '{node.T.name}' now=({cur.x:F3},{cur.y:F3},{cur.z:F3}) " +
                                 $"orig=({node.Scale.x:F3},{node.Scale.y:F3},{node.Scale.z:F3}) dScale={ds:F4} " +
                                 $"posDelta={dp:F4}" + (ds > 1e-4f ? " MODIFIED" : "") +
                                 ((node.Role != "pelvis-child" && node.Role != "cage-comp" && node.Role != "cage-trunk") && Math.Abs(node.Scale.x - 1f) > 1e-4f ? " ORIGIN-NOT-UNIT" : "") +
                                 (node.LatePins > 0 ? $" latePins={node.LatePins}" : ""));
                }
                catch (Exception e) { _log.LogInfo($"[bounds:dump]   {node.Role} <{e.Message}>"); }
            }
        }
    }

    // ---------------------------------------------------------------- verify / restore
    private void VerifyDue(float t)
    {
        if (_rigs.Count == 0) return;
        // Only scan when a rig is actually due, so this costs nothing on the average frame.
        bool due = false;
        foreach (var kv in _rigs) if (t >= kv.Value.NextVerify) { due = true; break; }
        if (!due) return;
        var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
        for (int i = 0; i < arr.Length; i++)
        {
            var m = arr[i].TryCast<ThirdViewPlayerModel>();
            if (m == null) continue;
            if (_rigs.TryGetValue(Ptr(m), out var rig) && t >= rig.NextVerify) Verify(rig, m);
        }
    }

    private void Verify(Rig rig, ThirdViewPlayerModel model)
    {
        rig.Verifies++;
        rig.NextVerify = Time.unscaledTime + (rig.Verifies < 3 ? 8f : 60f);
        int drift = 0, posDrift = 0, checkedNodes = 0;
        float posDelta = 0f;
        foreach (var node in rig.Nodes)
        {
            try
            {
                if (node.T == null) continue;
                checkedNodes++;
                bool posPinned = node.Role == "pelvis-child" || node.Role == "cage-comp" || node.Role == "cage-trunk";
                var want = node.Role == "pelvis-child" ? node.Scale / HipScale
                    : node.Role == "cage-comp" ? node.Scale / ChestCageScale
                    : node.Role == "cage" ? node.Scale * ChestCageScale
                    : node.Role == "cage-trunk" ? node.T.localScale   // inherits the cage scale
                    : node.Scale * (node.Role == "pelvis" ? HipScale : BreastLeafScale);
                if ((node.T.localScale - want).sqrMagnitude > 1e-6f) drift++;
                if (posPinned && node.TargetPos != Vector3.zero)
                {
                    float d = (node.T.localPosition - node.TargetPos).magnitude;
                    if (d > 1e-5f) { posDrift++; if (d > posDelta) posDelta = d; }
                }
            }
            catch { }
        }
        string cloth = ScanCloth(model, rig, allowInit: false);
        if (rig.Verifies <= 3 || drift > 0)
            _log.LogInfo($"[bounds:verify] rig='{rig.Name}' verify#{rig.Verifies} nodes={checkedNodes} " +
                         $"scaleDrift={drift} posDrift={posDrift} posDelta={posDelta:F4} cloth={cloth}" + (drift > 0 ? " -> re-applying" : ""));
        if (drift > 0)
        {
            // Something overwrote a scale (animation curve, ragdoll switch). Our record keeps the
            // original values, so re-applying is idempotent.
            foreach (var node in rig.Nodes)
            {
                try { SetNode(rig, node); } catch { }
            }
        }
    }

    private void Restore(Rig rig, string why)
    {
        if (rig == null) return;
        if (rig.ChestFixes != null)
        {
            try { ChestWeights.Restore(_log, rig.ChestFixes); } catch (Exception e) { _log.LogWarning($"[chest:restore-failed] {e.Message}"); }
            rig.ChestFixes = null;
        }
        int restored = 0;
        var sample = new StringBuilder();
        foreach (var node in rig.Nodes)
        {
            try
            {
                if (node.T == null) continue;
                if (sample.Length < 220 && node.Role != "pelvis-child")
                    sample.Append($" {node.T.name}:{node.T.localScale.x:F2}->{node.Scale.x:F2}");
                node.T.localScale = node.Scale;
                node.T.localPosition = node.Pos;
                restored++;
            }
            catch { }
        }
        _log.LogInfo($"[bounds:restore] rig='{rig.Name}' bones={restored} reason='{why}'" +
                     (sample.Length > 0 ? $" scaleNow->original:{sample}" : ""));
    }

    // ---------------------------------------------------------------- cloth
    // "Breast" BoneCloth = the MagicaCloth whose rootBones include a chest_01_* bone. Reports
    // whether it is present, valid (built) and running, and initialises it if it is not.
    private string ScanCloth(ThirdViewPlayerModel model, Rig rig, bool allowInit)
    {
        int all = 0, breast = 0, running = 0;
        string detail = "";
        bool initTried = false;
        try
        {
            var comps = model.GetComponentsInChildren(Il2CppType.Of<MagicaCloth>(), true);
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var mc = comps[i]?.TryCast<MagicaCloth>();
                if (mc == null) continue;
                all++;
                if (!IsBreastCloth(mc, out string roots)) continue;
                breast++;
                bool valid = false, run = false;
                try { var p = mc.process; valid = p != null && p.IsValid(); run = p != null && p.IsRunning(); } catch { }
                if (!valid && allowInit && !initTried)
                {
                    initTried = true;
                    try
                    {
                        mc.Initialize();
                        mc.BuildAndRun();
                        var p = mc.process;
                        valid = p != null && p.IsValid();
                        run = p != null && p.IsRunning();
                        _log.LogInfo($"[bounds:cloth-init] '{PathOf(mc.transform)}' after Initialize/BuildAndRun valid={valid} running={run}");
                    }
                    catch (Exception e) { _log.LogWarning($"[bounds:cloth-init-failed] '{PathOf(mc.transform)}': {e.Message}"); }
                }
                if (run) running++;
                if (detail.Length == 0)
                {
                    string pars = "";
                    try
                    {
                        var sd = mc.serializeData;
                        if (sd != null)
                            pars = $"type={sd.clothType} gravity={sd.gravity:F2} blendWeight={sd.blendWeight:F2} " +
                                   $"animationPoseRatio={sd.animationPoseRatio:F2}";
                    }
                    catch { }
                    string bound = "";
                    try
                    {
                        var used = mc.GetUsedTransform();
                        if (used != null)
                        {
                            var chestNames = new List<string>();
                            foreach (var t in used)
                                if (t != null && (t.name ?? "").IndexOf("chest_0", StringComparison.OrdinalIgnoreCase) >= 0)
                                    chestNames.Add(t.name);
                            bound = $" usedTransforms={used.Count} chestTransforms=[{string.Join(",", chestNames)}]";
                            // Are the transforms the cloth simulates the very objects we scaled?
                            var usedPtrs = new System.Collections.Generic.HashSet<long>();
                            foreach (var t in used) if (t != null) usedPtrs.Add(t.Pointer.ToInt64());
                            int hit = 0, want = 0;
                            foreach (var n in rig?.Nodes ?? new List<Node>())
                            {
                                if (n.Role != "chest_02_l" && n.Role != "chest_02_r") continue;
                                want++;
                                if (n.T != null && usedPtrs.Contains(n.T.Pointer.ToInt64())) hit++;
                            }
                            bound += $" scaledLeavesInCloth={hit}/{want}";
                        }
                    }
                    catch (Exception e) { bound = $" usedTransforms=<{e.GetType().Name}>"; }
                    detail = $" breastCloth='{PathOf(mc.transform)}' rootBones=[{roots}] valid={valid} running={run}{bound} {pars}";
                }
            }
        }
        catch (Exception e) { return $"scan-failed({e.Message})"; }

        if (rig != null)
        {
            rig.ClothCount = all; rig.BreastCloths = breast; rig.BreastClothsRunning = running;
            rig.ClothInitTried |= initTried;
        }
        return all == 0
            ? "none (this rig has no MagicaCloth at all)"
            : $"present={all} breastJiggleCloth={breast} running={running}" + detail;
    }

    private static bool IsBreastCloth(MagicaCloth mc, out string roots)
    {
        roots = "";
        try
        {
            var rb = mc.serializeData?.rootBones;
            if (rb == null || rb.Count == 0) return false;
            var names = new List<string>();
            bool chest = false;
            for (int i = 0; i < rb.Count; i++)
            {
                var t = rb[i];
                if (t == null) continue;
                string n = t.name ?? "";
                names.Add(n);
                if (n.IndexOf("chest_01", StringComparison.OrdinalIgnoreCase) >= 0) chest = true;
            }
            roots = string.Join(",", names);
            return chest;
        }
        catch { return false; }
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        for (int guard = 0; p != null && guard < 12; guard++) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(_current, this)) _current = null;
        try { _harmony?.UnpatchSelf(); } catch (Exception e) { _log.LogWarning($"[bounds:restore-failed] unpatch: {e.Message}"); }
        _harmony = null;
        // Teardown (hot reload / shutdown) must not leave scaled skeletons in the scene.
        foreach (var kv in _rigs) Restore(kv.Value, "teardown");
        _rigs.Clear();
        _log.LogInfo($"[bounds:disposed] generation={_id} patchesRemoved={_harmony == null}");
    }
}
