using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// "Clothes off": hide the garments on every player model in the Lobby - the local player's third-
// and first-person bodies, every wardrobe mannequin and any remote player's model.
//
// Mechanism: Renderer.forceRenderingOff, never Renderer.enabled.
//   * enabled is already owned by other machinery in this build: the game's own
//     RenderingController hide keys (death/cutscene/equipment), MirrorPlayerVisibility's
//     Drive/Selected state (it writes r.enabled every Rebind and restores the snapshot on
//     teardown), and the first-person screen policy. Flipping it here would fight all three and
//     would change what [mirror:body-check] reports as driven=N.
//   * forceRenderingOff is a separate per-renderer flag. All cameras (screen, mirror, the
//     mirror's isolated layer 14 body view) share one Renderer, so one flag covers every view.
//     It leaves enabled, layer, bounds, shadowCastingMode, LOD state and mesh weights untouched:
//     TryGetBodyBounds keeps the real body volume, BodyLayerCensus keeps its counts, the mirror's
//     isolated/driven census is unchanged, and ChestWeights' clone stays bound and animated (the
//     undershirt is our reweight target - hiding it is fine, disabling it is not).
//   * Restore is exact: the value found before the first hide is recorded per renderer and
//     written back when the toggle goes on.
//
// Two hide modes, because the game's body meshes are authored only where the garments leave them
// visible (see shots/clothes-toggle/):
//   * Gated (F5, `gated`): a garment is hidden only where there is real skin under it. For each
//     garment, `samples` of its vertices are taken in world bind pose and the distance to the
//     nearest *skin* vertex is measured (skin = renderers classified keep whose name is a body
//     part). A garment vertex is covered when that distance is <= `tolerance`; the garment hides
//     when covered/samples >= `threshold`. Under a missing torso there is no skin, so the shirt
//     stays and no disembodied part is exposed. Decisions are logged per garment per model with
//     the measured coverage, so the rule is auditable (`dump`, `coverage`).
//   * All (Shift+F5, `all`, legacy `off`): hide every garment unconditionally - the old behaviour,
//     kept for the "show me the missing geometry" captures.
//
// Classification is by renderer (GameObject) name, tokenised on non-alphanumerics, camelCase and
// letter/digit boundaries. keep tokens win over garment tokens (Hair_cap is hair, not a cap), an
// unmatched name is kept. The full table is logged on the first "off" and on `dump`.
//
// Triggers:
//   * F5 = gated on/off, Shift+F5 = hide-everything on/off,
//   * "<ScreensDir>/clothes.request", line-oriented, deleted on pickup:
//       gated|gate           hide only garments with skin under them (F5)
//       all|hide|off|0       hide every garment unconditionally (Shift+F5)
//       on|show|1            restore
//       toggle               flip gated <-> on
//       dump                 rule + full per-model renderer table + the coverage decisions
//       coverage             recompute and print the per-garment coverage decision table
//       state                current counts
//       tolerance=<m>        coverage distance (default 0.03)
//       threshold=<0..1>     coverage share needed to hide (default 0.80)
//       samples=<n>          garment vertices sampled per renderer (default 600)
//       mirror=<tag>         save the mirror's own RenderTexture to "<ScreensDir>/clothes/<tag>_mirror.png"
//                            (one frame later, so a toggle in the same request is reflected)
//
// The model set is re-scanned (0.5 s cadence and on every scene change), so a wardrobe switch, a
// respawn or a joining player is picked up. Toggle state does not survive a hot reload: a new
// generation starts clothed, and any forceRenderingOff a previous generation left on a garment is
// cleared when that model is first seen (logged as a stale scrub).
internal static class ClothesToggle
{
    // The F5 / Shift+F5 rows were removed with the controls prune; the request file is the only
    // trigger. `SetMode` is unchanged, so `clothes.request on|gated|all|toggle` drives exactly what
    // the keys used to.
    private const string RequestFileName = "clothes.request";
    private const float PollSeconds = 0.1f;
    private const float RescanSeconds = 0.5f;
    // A generation must never re-assert more often than this when something resets the flag.
    private const float ReassertLogSeconds = 2f;

    // ------------------------------------------------------------------ classification tokens
    // Kept first: a name that carries a keep token is never hidden, even if it also carries a
    // garment token (Hair_cap -> hair + cap).
    private static readonly HashSet<string> KeepTokens = Set(
        "body head hair hairstyle scalp face skin eyebrow eyelash eyelid eye eyes teeth tooth tongue " +
        "hand hands arm arms forearm finger fingers thumb nail nails " +
        "beard moustache " +
        "boot boots shoe shoes foot feet leg legs " +
        "glass glasses " +
        // Worn accessories that are not garments: jewellery and kit carried on the body.
        "watch chain medallion token pouch bag scrunchie necklace earring ring bracelet bandage tattoo");

    private static readonly HashSet<string> GarmentTokens = Set(
        "shirt undershirt tshirt tank blouse tee top sweater hoodie jacket coat raincoat poncho cloak " +
        "robe apron vest suspenders tie scarf " +
        "pants jeans trousers skirt shorts kilt overalls jumpsuit dress uniform " +
        "belt " +
        "hat helmet cap sleeve sleeves glove gloves mitten mittens sock socks stocking stockings");

    // The subset of the keep set that is actual body surface. Only these supply the "is there a
    // body under this garment" evidence: boots, hair, jewellery, glasses and beards are kept but
    // are not skin, so a shirt cannot be justified by the boots below it.
    private static readonly HashSet<string> SkinTokens = Set(
        "body skin neck chest torso bust belly back waist " +
        "head face scalp " +
        "shoulder shoulders collarbone " +
        "arm arms upperarm forearm hand hands palm finger fingers thumb " +
        "leg legs thigh thighs knee calf foot feet toe toes heel");

    // Sorted, so the logged rule string is identical in every generation (HashSet order is not a
    // contract, and this string is the audit trail).
    private static readonly string KeepRule = "keep=[" + string.Join(",", Sorted(KeepTokens)) + "]";
    private static readonly string GarmentRule = "garment=[" + string.Join(",", Sorted(GarmentTokens)) + "]";
    private static readonly string SkinRule = "skin=[" + string.Join(",", Sorted(SkinTokens)) + "]";
    private const string DefaultRule = "no-rule -> keep";

    // ------------------------------------------------------------------ coverage gate
    // Distance from a garment vertex to the nearest skin vertex at which the garment is deemed to
    // be lying on real skin. Tuned on the live meshes (see shots/clothes-toggle-gated/README.md:
    // any threshold inside 0.78..0.89 and any tolerance inside 20..50 mm decide identically).
    private const float DefaultTolerance = 0.030f;
    // Share of a garment's sampled vertices that must be covered before it may be hidden.
    private const float DefaultThreshold = 0.80f;
    // Vertices sampled per garment, spread by a fixed stride over the whole mesh.
    private const int DefaultSamples = 600;
    // Search cell size. It is also the search radius: the 3x3x3 cells around a query point are
    // exactly the cells that can hold a vertex within `CoverageSearch` metres of it, so every
    // distance <= CoverageSearch is exact and anything farther is reported as CoverageSearch + 1 mm
    // (a value no tolerance uses, so "not found" can never be counted as a hit).
    private const float CellSize = 0.05f;
    private const float CoverageSearch = CellSize;

    private static float _tolerance = DefaultTolerance;
    private static float _threshold = DefaultThreshold;
    private static int _samples = DefaultSamples;

    // ------------------------------------------------------------------ state
    private enum HideMode { On, Gated, All }

    internal sealed class Entry
    {
        public Renderer R;
        public int RPtr;
        public string Name = "";
        public bool Garment;
        public bool Skin;                    // keep-classified body part: supplies coverage surface
        public string Rule = "";
        public bool HadForce;
        public bool Applied;
        public int ModelPtr;
        // Coverage gate (bind pose; sampled once per model renderer set).
        public bool GateComputed;
        public float[] Dist;                 // nearest skin vertex per sample, capped at CoverageSearch
        public float Coverage;               // share of samples within _tolerance
        public bool Hide;                    // decision at the current _threshold
        public string GateNote = "";
    }

    private sealed class ModelEntry
    {
        public int Ptr;
        public string Kind = "third";
        public string Name = "";
        public string Path = "";
        public bool Seen;
        public bool StaleScrubbed;
        public bool GateDirty = true;
        public string SkinDesc = "none";
        public int SkinVerts;
        public float GateMs;
        public readonly List<Entry> Renderers = new();
    }

    private static readonly Dictionary<int, ModelEntry> _models = new();
    private static readonly Dictionary<int, Entry> _entries = new();

    private static HideMode _mode = HideMode.On;
    private static bool _toggledOnce;
    private static bool _announced;
    private static float _nextPoll, _nextScan, _nextReassertLog;
    private static int _reasserted;
    private static string _lastScene = "";
    private static int _lastFingerprint;

    // Deferred mirror-RT dump: the mirror draws in LateUpdate, so a request handled in Update has
    // to wait for the next frame to read pixels that include this frame's toggle.
    private static string _pendingMirrorTag;
    private static int _pendingMirrorFrame = -1;
    private static Func<RenderTexture> _mirrorTexture;

    internal static bool IsOff => _mode != HideMode.On;
    internal static int HiddenCount => CountApplied();

    // Frame hook. Call once per Update, after the mirror's own Visibility.Update(): that sweep can
    // write forceRenderingOff=false on the local body (Rebind) and this re-assert has to follow it.
    internal static void Tick(IHostContext log, Func<RenderTexture> mirrorTexture)
    {
        _mirrorTexture = mirrorTexture;
        if (log == null) return;
        try
        {
            if (!_announced)
            {
                _announced = true;
                string request = RequestPath(log) ?? "<no screens dir>";
                log.LogInfo($"[clothes] ready trigger=none (request file only) request={request} " +
                            "values=on/gated/all/toggle/dump/coverage mechanism=Renderer.forceRenderingOff " +
                            "(enabled is left to the game and the mirror)");
                log.LogInfo($"[clothes] rule {KeepRule} {GarmentRule} {SkinRule} {DefaultRule}");
                log.LogInfo($"[clothes] gate tolerance={Mm(_tolerance)} threshold={_threshold:F2} " +
                            $"samples={_samples} search={Mm(CoverageSearch)} " +
                            "(a garment hides only where skin lies under it)");
            }

            string scene = SceneName();
            if (scene != _lastScene)
            {
                _lastScene = scene;
                _nextScan = 0f;   // a scene change invalidates every model pointer we hold
            }

            // The default mode is ON, where nothing is hidden and the table is not read by Apply.
            // Scanning it every 0.5 s cost 2-5 ms in the lobby for no effect; SetMode() takes a
            // fresh scan on the frame a hide mode is entered, so this is the only path that needs
            // the periodic refresh.
            if (_mode != HideMode.On && Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + RescanSeconds;
                Scan(log, verbose: false);
            }

            try { PollRequestFile(log); } catch (Exception e) { log.LogWarning($"[clothes] request poll failed: {e.Message}"); }

            if (_mode != HideMode.On) Apply(log);
            else RestoreAll(log, reason: null, quiet: true);

            try { FlushMirrorDump(log); } catch (Exception e) { log.LogWarning($"[clothes] mirror dump failed: {e.Message}"); }
        }
        catch (Exception e) { log.LogWarning($"[clothes] tick failed: {e.Message}"); }
    }

    // Hot reload teardown: put every renderer we touched back the way we found it. The mirror's own
    // RestoreBinding runs after this and may write its snapshot value; a new generation's stale
    // scrub (see Scan) clears any garment flag that survives that.
    internal static void Shutdown(IHostContext log)
    {
        if (_entries.Count > 0)
        {
            int restored = 0;
            foreach (var e in _entries.Values)
            {
                if (!e.Applied) continue;
                try { if (e.R != null) { e.R.forceRenderingOff = e.HadForce; restored++; } } catch { }
                e.Applied = false;
            }
            if (restored > 0)
                try { log?.LogInfo($"[clothes] dispose: restored {restored} renderer(s) our generation had hidden"); } catch { }
        }
        _entries.Clear();
        _models.Clear();
        _mode = HideMode.On;
        _pendingMirrorTag = null;
    }

    // ------------------------------------------------------------------ scanning
    private static void Scan(IHostContext log, bool verbose)
    {
        foreach (var m in _models.Values) m.Seen = false;
        int third = 0, first = 0;

        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var model = arr[i].TryCast<ThirdViewPlayerModel>();
                if (model != null) { if (Register(log, model.transform, (Component)model, "third")) third++; }
            }
        }
        catch (Exception e) { log.LogWarning($"[clothes] third-view scan failed: {e.Message}"); }

        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<FirstViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var model = arr[i].TryCast<FirstViewPlayerModel>();
                if (model != null) { if (Register(log, model.transform, (Component)model, "first")) first++; }
            }
        }
        catch (Exception e) { log.LogWarning($"[clothes] first-view scan failed: {e.Message}"); }

        // Models that went away: forget them. A destroyed renderer has nothing to restore.
        List<int> gone = null;
        foreach (var kv in _models)
            if (!kv.Value.Seen) (gone ??= new List<int>()).Add(kv.Key);
        if (gone != null)
        {
            foreach (int ptr in gone) _models.Remove(ptr);
            List<int> deadEntries = null;
            foreach (var kv in _entries)
                if (!_models.ContainsKey(kv.Value.ModelPtr)) (deadEntries ??= new List<int>()).Add(kv.Key);
            if (deadEntries != null) foreach (int p in deadEntries) _entries.Remove(p);
            _lastFingerprint = 0;   // force a table line on the next change
        }

        int fp = Fingerprint();
        if (verbose || fp != _lastFingerprint)
        {
            _lastFingerprint = fp;
            log.LogInfo($"[clothes] set: {_models.Count} model(s) (third={third} first={first}) " +
                        $"{_entries.Count} renderer(s) {CountGarments()} garment(s) hidden={CountApplied()} state={ModeName(_mode)}");
        }
    }

    private static bool Register(IHostContext log, Transform root, Component model, string kind)
    {
        if (root == null) return false;
        var go = root.gameObject;
        if (go == null) return false;
        // Prefab assets and the wardrobe's shared templates carry no scene; mutating one would bake
        // the hide into every later instance. Same filter the body sweep uses.
        string scene = go.scene.name;
        if (scene == null || scene.Length == 0) return false;

        int ptr = Ptr(model);
        if (ptr == 0) return false;
        bool changed = false;
        if (!_models.TryGetValue(ptr, out var entry))
        {
            entry = new ModelEntry { Ptr = ptr, Kind = kind, Name = go.name ?? "?", Path = PathOf(root) };
            _models[ptr] = entry;
            log.LogInfo($"[clothes] new {kind}-view model '{entry.Name}' path='{entry.Path}'");
            changed = true;
        }
        entry.Seen = true;

        var current = new HashSet<int>();
        var comps = model.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
        for (int i = 0; comps != null && i < comps.Length; i++)
        {
            var r = comps[i]?.TryCast<Renderer>();
            if (r == null) continue;
            if (CosmeticMarkers.IsCosmetic(r)) continue;   // mod cosmetics are not the game's garments
            int rptr = Ptr(r);
            if (rptr == 0) continue;
            current.Add(rptr);
            if (!_entries.TryGetValue(rptr, out var e))
            {
                e = new Entry { R = r, RPtr = rptr, ModelPtr = ptr };
                Classify(e);
                _entries[rptr] = e;
                entry.Renderers.Add(e);
                changed = true;
                if (e.Garment) log.LogInfo($"[clothes]   garment '{e.Name}' rule={e.Rule} model='{entry.Name}'");
                else log.LogInfo($"[clothes]   keep    '{e.Name}' rule={e.Rule} skin={e.Skin} model='{entry.Name}'");
            }
            else if (!entry.Renderers.Contains(e))
            {
                entry.Renderers.Add(e);
                changed = true;
            }
        }
        // A SetupModel rebuild can replace the children of the same model object (the wardrobe
        // switch path), leaving entries whose Renderer was destroyed. Drop them: there is nothing
        // left to hide or restore, and they would distort the renderer/garment counts.
        for (int i = entry.Renderers.Count - 1; i >= 0; i--)
        {
            var e = entry.Renderers[i];
            if (e.RPtr != 0 && current.Contains(e.RPtr)) continue;
            _entries.Remove(e.RPtr);
            entry.Renderers.RemoveAt(i);
            changed = true;
        }
        // A different renderer set means a different body: the coverage numbers are stale.
        if (changed) entry.GateDirty = true;

        // A previous generation's hide can survive it in two ways: our own flag left behind, or the
        // mirror writing its clamped snapshot back on teardown. Clear it once per model while the
        // toggle is on, so a hot reload always starts clothed.
        if (_mode == HideMode.On && !entry.StaleScrubbed)
        {
            entry.StaleScrubbed = true;
            int stale = 0;
            foreach (var e in entry.Renderers)
            {
                if (!e.Garment || e.Applied) continue;
                try
                {
                    if (!e.R.forceRenderingOff) continue;
                    e.R.forceRenderingOff = false;
                    stale++;
                }
                catch { }
            }
            if (stale > 0)
                log.LogInfo($"[clothes] cleared stale forceRenderingOff on {stale} garment renderer(s) of '{entry.Name}' " +
                            "(previous generation's hide; this generation starts clothed)");
        }
        return true;
    }

    private static void Classify(Entry e)
    {
        e.Name = SafeName(e.R);
        e.Rule = DefaultRule;
        e.Garment = false;
        e.Skin = false;
        var tokens = Tokens(e.Name);
        foreach (var t in tokens)
        {
            if (KeepTokens.Contains(t))
            {
                e.Rule = $"keep token '{t}'";
                foreach (var s in tokens) if (SkinTokens.Contains(s)) { e.Skin = true; break; }
                return;
            }
        }
        foreach (var t in tokens)
            if (GarmentTokens.Contains(t)) { e.Garment = true; e.Rule = $"garment token '{t}'"; return; }
    }

    private static string SafeName(Renderer r)
    {
        try { return r.gameObject != null ? (r.gameObject.name ?? "") : ""; } catch { return ""; }
    }

    // Lower-case tokens split on non-alphanumerics, camelCase and letter/digit boundaries:
    // "Hair_cap" -> hair, cap | "HairCap" -> hair, cap | "Jane_Undershirt" -> jane, undershirt |
    // "Ryan_Pants1" -> ryan, pants, 1 | "T-Shirt" -> t, shirt.
    private static List<string> Tokens(string name)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(name)) return list;
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            list.Add(sb.ToString());
            sb.Length = 0;
        }
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (sb.Length > 0)
            {
                char prev = name[i - 1];
                bool digitBoundary = char.IsDigit(c) != char.IsDigit(prev);
                bool camel = char.IsUpper(c) && char.IsLower(prev);
                if (digitBoundary || camel) Flush();
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        Flush();
        return list;
    }

    private static List<string> Sorted(HashSet<string> set)
    {
        var list = new List<string>(set);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static HashSet<string> Set(string words)
    {
        var set = new HashSet<string>();
        foreach (var w in words.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(w);
        return set;
    }

    // ------------------------------------------------------------------ coverage gate
    // Nearest skin vertex per sampled garment vertex, in world bind pose. Bind pose is used on
    // purpose: mesh vertices are authored in the renderer's bind space, so the measurement does
    // not depend on the current animation, the mannequin pose or the model's orientation, and it is
    // stable across re-samples.
    private sealed class SkinCloud
    {
        private readonly List<Vector3> _pts = new();
        private readonly Dictionary<long, List<int>> _grid = new();
        private Vector3[] _pos = Array.Empty<Vector3>();

        internal int Verts => _pos.Length;

        // Appends one skin renderer. Returns the vertex count (>0) it contributed, 0 when the mesh
        // cannot be read (the caller logs it: an unreadable skin mesh means "no evidence", and
        // every garment on that model stays on).
        internal int Add(SkinnedMeshRenderer r)
        {
            try
            {
                var mesh = r.sharedMesh;
                if (mesh == null) return 0;
                if (!mesh.isReadable) return 0;
                var verts = mesh.vertices;
                if (verts == null || verts.Length == 0) return 0;
                var mtx = r.transform.localToWorldMatrix;
                for (int v = 0; v < verts.Length; v++) _pts.Add(mtx.MultiplyPoint3x4(verts[v]));
                return verts.Length;
            }
            catch { return 0; }
        }

        internal void Build()
        {
            _pos = _pts.ToArray();
            _pts.Clear();
            _pts.TrimExcess();
            for (int i = 0; i < _pos.Length; i++)
            {
                long k = Key(_pos[i]);
                if (!_grid.TryGetValue(k, out var list)) { list = new List<int>(); _grid[k] = list; }
                list.Add(i);
            }
        }

        private static int Cell(float v) => Mathf.FloorToInt(v / CellSize);
        private static long Key(Vector3 p) => Pack(Cell(p.x), Cell(p.y), Cell(p.z));
        private static long Pack(int x, int y, int z) =>
            ((long)(x & 0xFFF) << 24) | ((long)(y & 0xFFF) << 12) | (long)(z & 0xFFF);

        // Exact nearest skin vertex distance up to CoverageSearch; CoverageSearch + 1 mm when the
        // nearest skin vertex is farther than that (all we need: it is then uncovered at any
        // tolerance we use, which is clamped to CoverageSearch).
        internal float Nearest(Vector3 p)
        {
            // CoverageSearch + 1 mm means "nothing within CoverageSearch": a distinct value, so an
            // uncapped bucket count cannot be mistaken for a hit at exactly the search radius.
            if (_pos.Length == 0) return CoverageSearch + 0.001f;
            int cx = Cell(p.x), cy = Cell(p.y), cz = Cell(p.z);
            float bestSq = CoverageSearch * CoverageSearch;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!_grid.TryGetValue(Pack(cx + dx, cy + dy, cz + dz), out var cell)) continue;
                        for (int i = 0; i < cell.Count; i++)
                        {
                            var q = _pos[cell[i]];
                            float ax = q.x - p.x, ay = q.y - p.y, az = q.z - p.z;
                            float sq = ax * ax + ay * ay + az * az;
                            if (sq >= bestSq) continue;
                            bestSq = sq;
                            if (bestSq <= 1e-6f) return 0f;   // touching: covered at any tolerance
                        }
                    }
            return bestSq >= CoverageSearch * CoverageSearch ? CoverageSearch + 0.001f : Mathf.Sqrt(bestSq);
        }
    }

    // Builds the model's skin surface and measures every garment on it. One pass per model per
    // renderer-set change: bind pose does not move, so the numbers are stable until the model is
    // rebuilt (wardrobe switch / respawn).
    private static void ComputeGate(IHostContext log, ModelEntry m)
    {
        m.GateDirty = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cloud = new SkinCloud();
        var desc = new StringBuilder();
        int unreadable = 0;
        foreach (var e in m.Renderers)
        {
            if (e.Garment || !e.Skin) continue;
            var sk = e.R != null ? e.R.TryCast<SkinnedMeshRenderer>() : null;
            if (sk == null || sk.sharedMesh == null) { unreadable++; continue; }
            int n = cloud.Add(sk);
            if (n > 0) desc.Append(desc.Length == 0 ? "" : ",").Append($"'{e.Name}':{n}v");
            else unreadable++;
        }
        cloud.Build();
        m.SkinVerts = cloud.Verts;
        m.SkinDesc = desc.Length == 0 ? "none" : desc.ToString();

        int measured = 0;
        foreach (var e in m.Renderers)
            if (e.Garment) { Measure(e, cloud); measured++; }
        m.GateMs = (float)sw.Elapsed.TotalMilliseconds;
        log.LogInfo($"[clothes] gate model='{m.Name}' skin={m.SkinDesc} verts={m.SkinVerts} " +
                    $"garments={measured} unreadable={unreadable} tol={Mm(_tolerance)} " +
                    $"threshold={_threshold:F2} samples={_samples} took={m.GateMs:F0}ms");
    }

    private static void Measure(Entry e, SkinCloud cloud)
    {
        e.GateComputed = true;
        e.Dist = null;
        e.Coverage = 0f;
        e.Hide = false;
        e.GateNote = "";
        if (cloud.Verts == 0) { e.GateNote = "no skin renderer on this model"; return; }
        var sk = e.R != null ? e.R.TryCast<SkinnedMeshRenderer>() : null;
        if (sk == null) { e.GateNote = "not a skinned mesh"; return; }
        var mesh = sk.sharedMesh;
        if (mesh == null) { e.GateNote = "no mesh"; return; }
        if (!mesh.isReadable) { e.GateNote = "mesh not readable"; return; }
        try
        {
            var verts = mesh.vertices;
            if (verts == null || verts.Length == 0) { e.GateNote = "no vertices"; return; }
            int n = verts.Length;
            int want = Mathf.Clamp(_samples, 32, n);
            int stride = Mathf.Max(1, n / want);
            var mtx = sk.transform.localToWorldMatrix;
            var dist = new List<float>(want + 1);
            for (int v = 0; v < n && dist.Count < want; v += stride)
                dist.Add(cloud.Nearest(mtx.MultiplyPoint3x4(verts[v])));
            e.Dist = dist.ToArray();
            Recompute(e);
            if (e.Dist.Length == 0) e.GateNote = "no samples";
        }
        catch (Exception ex) { e.GateNote = "sample failed: " + ex.Message; }
    }

    // Applies the current tolerance/threshold to the cached distances. Cheap, so the tuning request
    // keys can re-decide every garment without touching a mesh again.
    private static void Recompute(Entry e)
    {
        if (e.Dist == null || e.Dist.Length == 0) { e.Coverage = 0f; e.Hide = false; return; }
        int covered = 0;
        for (int i = 0; i < e.Dist.Length; i++) if (e.Dist[i] <= _tolerance) covered++;
        e.Coverage = covered / (float)e.Dist.Length;
        e.Hide = e.Coverage >= _threshold;
    }

    private static void EnsureGate(IHostContext log, bool force)
    {
        foreach (var m in _models.Values)
            if (force || m.GateDirty) ComputeGate(log, m);
    }

    private static void Retune(IHostContext log, string why)
    {
        int n = 0;
        foreach (var e in _entries.Values) if (e.Dist != null) { Recompute(e); n++; }
        log.LogInfo($"[clothes] gate tuned ({why}): tolerance={Mm(_tolerance)} threshold={_threshold:F2} " +
                    $"samples={_samples} re-decided {n} garment(s) from their cached distances");
    }

    // ------------------------------------------------------------------ apply / restore
    private static void Apply(IHostContext log)
    {
        if (_mode == HideMode.Gated) EnsureGate(log, force: false);
        int hidden = 0, reset = 0, kept = 0, shown = 0;
        foreach (var model in _models.Values)
        {
            foreach (var e in model.Renderers)
            {
                if (!e.Garment) continue;
                bool want = _mode == HideMode.All || e.Hide;
                try
                {
                    if (e.R == null) continue;
                    if (want)
                    {
                        if (!e.Applied) { e.HadForce = e.R.forceRenderingOff; e.Applied = true; hidden++; }
                        else if (!e.R.forceRenderingOff) reset++;
                        e.R.forceRenderingOff = true;
                    }
                    else
                    {
                        // Gated and kept (no skin under it): make sure a previous decision is undone
                        // in the same frame, e.g. switching from `all` to `gated`.
                        kept++;
                        if (!e.Applied) continue;
                        e.R.forceRenderingOff = e.HadForce;
                        e.Applied = false;
                        shown++;
                    }
                }
                catch (Exception ex) { log.LogWarning($"[clothes] hide failed on '{e.Name}': {ex.Message}"); }
            }
        }
        if (shown > 0)
            log.LogInfo($"[clothes] kept {shown} garment(s) visible in gated mode (no skin under them)");
        // A re-assert is only interesting when the flag was already ours and something else wrote it
        // back: the mirror's Rebind (forceRenderingOff=false on the renderers it drives) or a native
        // enable. The first hide is not a re-assert and is reported by the [clothes] off counts.
        if (reset > 0)
        {
            _reasserted += reset;
            if (Time.unscaledTime >= _nextReassertLog)
            {
                _nextReassertLog = Time.unscaledTime + ReassertLogSeconds;
                log.LogInfo($"[clothes] re-asserted forceRenderingOff on {_reasserted} renderer(s) " +
                            "that had been reset (mirror Rebind / native enable)");
                _reasserted = 0;
            }
        }
    }

    private static void RestoreAll(IHostContext log, string reason, bool quiet)
    {
        int restored = 0, models = 0;
        foreach (var model in _models.Values)
        {
            int mine = 0;
            foreach (var e in model.Renderers)
            {
                if (!e.Applied) continue;
                try
                {
                    if (e.R != null) { e.R.forceRenderingOff = e.HadForce; restored++; mine++; }
                }
                catch (Exception ex) { log?.LogWarning($"[clothes] restore failed on '{e.Name}': {ex.Message}"); }
                e.Applied = false;
            }
            if (mine > 0) models++;
        }
        if (restored > 0 && !quiet)
            log?.LogInfo($"[clothes] on: restored={restored} models={models} reason={reason ?? "toggle/request"}");
    }

    // ------------------------------------------------------------------ triggers
    private static void PollRequestFile(IHostContext log)
    {
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;
        string path = RequestPath(log);
        if (path == null) return;
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;

        string text = "";
        try { text = File.ReadAllText(path); }
        catch (Exception e)
        {
            log.LogWarning($"[clothes] request read failed: {e.Message}");
            return;
        }
        try { File.Delete(path); } catch { }

        string action = null;
        bool dump = false, coverage = false, tuned = false, resample = false;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            string key = (eq < 0 ? line : line.Substring(0, eq)).Trim().ToLowerInvariant();
            string val = eq < 0 ? "" : line.Substring(eq + 1).Trim();
            switch (key)
            {
                // `off` keeps the old unconditional meaning; the gated mode has its own key.
                case "off": case "hide": case "all": case "0": action = "all"; break;
                case "gated": case "gate": action = "gated"; break;
                case "on": case "show": case "1": action = "on"; break;
                case "toggle": action = "toggle"; break;
                case "dump": case "table": dump = true; break;
                case "coverage": case "cov": coverage = true; break;
                case "state": LogCounts(log, "state"); break;
                case "tolerance": case "tol":
                    if (ParseFloat(val, out float tol) && tol > 0f)
                    {
                        _tolerance = Mathf.Clamp(tol, 0.001f, CoverageSearch);
                        tuned = true;
                    }
                    else log.LogWarning($"[clothes] tolerance='{val}' ignored (metres, > 0)");
                    break;
                case "threshold": case "thr":
                    if (ParseFloat(val, out float thr) && thr > 0f && thr <= 1f)
                    {
                        _threshold = thr;
                        tuned = true;
                    }
                    else log.LogWarning($"[clothes] threshold='{val}' ignored (share, 0..1)");
                    break;
                case "samples":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int samples) && samples >= 32)
                    {
                        _samples = Mathf.Clamp(samples, 32, 4000);
                        tuned = true;
                        resample = true;
                    }
                    else log.LogWarning($"[clothes] samples='{val}' ignored (>= 32)");
                    break;
                case "mirror":
                    _pendingMirrorTag = Sanitize(val.Length == 0 ? "mirror" : val);
                    _pendingMirrorFrame = Time.frameCount + 1;
                    break;
                default: log.LogWarning($"[clothes] request key '{key}' unknown; ignored"); break;
            }
        }

        if (resample)
        {
            // A different sample count needs new distances; drop the cached ones and take them again.
            foreach (var m in _models.Values) m.GateDirty = true;
        }
        if (tuned) { EnsureGate(log, force: false); Retune(log, "request"); }
        if (coverage) { EnsureGate(log, force: true); DumpGate(log, "request coverage"); }
        if (dump) { EnsureGate(log, force: false); DumpTable(log, "request"); }
        if (action == null) return;
        if (action == "toggle") { SetMode(log, _mode == HideMode.Gated ? HideMode.On : HideMode.Gated, "request toggle"); return; }
        if (action == "all")
        {
            if (_mode == HideMode.All && CountApplied() > 0) { LogCounts(log, "all(already)"); return; }
            SetMode(log, HideMode.All, "request all");
        }
        else if (action == "gated")
        {
            if (_mode == HideMode.Gated) { LogCounts(log, "gated(already)"); return; }
            SetMode(log, HideMode.Gated, "request gated");
        }
        else
        {
            if (_mode == HideMode.On && CountApplied() == 0) { LogCounts(log, "on(already)"); return; }
            SetMode(log, HideMode.On, "request on");
        }
    }

    private static void SetMode(IHostContext log, HideMode mode, string why)
    {
        Scan(log, verbose: true);
        HideMode prev = _mode;
        _mode = mode;
        if (_mode == HideMode.On) RestoreAll(log, why, quiet: false);
        else Apply(log);
        if (!_toggledOnce && _mode != HideMode.On)
        {
            _toggledOnce = true;
            DumpGate(log, "first " + ModeName(_mode));
        }
        LogCounts(log, ModeName(_mode));
        log.LogInfo($"[clothes] {ModeLabel(_mode)} trigger={why}" +
                    (prev != mode ? $" (was {ModeName(prev)})" : ""));
    }

    private static string ModeName(HideMode m) =>
        m == HideMode.On ? "on" : m == HideMode.Gated ? "gated" : "all";

    private static string ModeLabel(HideMode m) =>
        m == HideMode.On ? "ON (garments restored)"
        : m == HideMode.Gated ? "GATED (only garments with skin under them are hidden)"
        : "ALL (every garment hidden, unconditional)";

    // ------------------------------------------------------------------ logging
    private static void LogCounts(IHostContext log, string label)
    {
        int garments = 0, models = 0, renderers = 0, gated = 0, hideable = 0;
        foreach (var m in _models.Values)
        {
            bool any = false;
            foreach (var e in m.Renderers)
            {
                renderers++;
                if (!e.Garment) continue;
                garments++;
                if (e.GateComputed) gated++;
                if (e.Hide) hideable++;
                if (e.Applied) any = true;
            }
            if (any) models++;
        }
        log.LogInfo($"[clothes] {label}: garments={CountApplied()}/{garments} models={models} " +
                    $"renderers={renderers} gated={gated} skinUnder={hideable} state={ModeName(_mode)} " +
                    $"tol={Mm(_tolerance)} thr={_threshold:F2}");
    }

    private static void DumpTable(IHostContext log, string why)
    {
        log.LogInfo($"[clothes] classification ({why}): {KeepRule} {GarmentRule} {SkinRule} {DefaultRule} " +
                    "mechanism=Renderer.forceRenderingOff");
        int models = 0, garments = 0, keeps = 0;
        var sb = new StringBuilder();
        foreach (var m in _models.Values)
        {
            int g = 0;
            foreach (var e in m.Renderers) if (e.Garment) g++;
            sb.AppendLine($"[clothes] model[{m.Kind}] '{m.Name}' path='{m.Path}' renderers={m.Renderers.Count} " +
                          $"garments={g} skin=[{m.SkinDesc}]");
            foreach (var e in m.Renderers)
            {
                string cov = e.GateComputed
                    ? string.Format(CultureInfo.InvariantCulture, " cov={0:F3} samples={1} hide={2}", e.Coverage, e.Dist?.Length ?? 0, e.Hide)
                    : " cov=n/a";
                // mesh/enabled are the evidence that a hidden garment is only un-drawn, not
                // unbound: the chest-weight clone stays the renderer's mesh and the renderer stays
                // enabled, so the skin keeps following the animated skeleton while it is hidden.
                sb.AppendLine($"[clothes]   {(e.Garment ? "GARMENT" : "keep   ")} '{e.Name}' rule={e.Rule}" +
                              $" skin={e.Skin}{cov} forceOff={ForceOf(e.R)} applied={e.Applied}" +
                              $" enabled={EnabledOf(e.R)} mesh={MeshOf(e.R)} bounds={BoundsOf(e.R)}");
            }
            models++;
            garments += g;
            keeps += m.Renderers.Count - g;
        }
        log.LogInfo($"[clothes] table: {models} model(s), {garments} garment, {keeps} keep renderer(s)");
        log.LogInfo(sb.ToString().TrimEnd());
        log.LogInfo($"[clothes] counts: hidden={CountApplied()} garments={garments} state={ModeName(_mode)} " +
                    $"tol={Mm(_tolerance)} thr={_threshold:F2}");
    }

    // The audit trail of the gate: one line per garment with the coverage number and the decision,
    // one header per model naming the skin renderers that supplied the surface.
    private static void DumpGate(IHostContext log, string why)
    {
        int hide = 0, keep = 0, noEvidence = 0;
        log.LogInfo($"[clothes] gate ({why}): {SkinRule} tolerance={Mm(_tolerance)} " +
                    $"threshold={_threshold:F2} samples={_samples} search={Mm(CoverageSearch)} " +
                    $"mode={ModeName(_mode)}");
        foreach (var m in _models.Values)
        {
            log.LogInfo($"[clothes] gate model[{m.Kind}] '{m.Name}' skin=[{m.SkinDesc}] " +
                        $"skinVerts={m.SkinVerts} took={m.GateMs:F0}ms");
            foreach (var e in m.Renderers)
            {
                if (!e.Garment) continue;
                if (!e.GateComputed || e.Dist == null || e.Dist.Length == 0)
                {
                    noEvidence++;
                    keep++;
                    log.LogInfo($"[clothes]   keep  '{e.Name}' cov=n/a ({e.GateNote})");
                    continue;
                }
                int covered = 0;
                for (int i = 0; i < e.Dist.Length; i++) if (e.Dist[i] <= _tolerance) covered++;
                log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[clothes]   {0} '{1}' cov={2:F3} ({3}/{4}<={5}) d10={6:F2} d20={7:F2} d30={8:F2} d40={9:F2} d50={10:F2}",
                    e.Hide ? "HIDE " : "keep ", e.Name, e.Coverage, covered, e.Dist.Length, Mm(_tolerance),
                    Share(e, 0.010f), Share(e, 0.020f), Share(e, 0.030f), Share(e, 0.040f), Share(e, 0.050f)));
                if (e.Hide) hide++; else keep++;
            }
        }
        log.LogInfo($"[clothes] gate summary: hide={hide} keep={keep} (no-evidence={noEvidence}) " +
                    $"mode={ModeName(_mode)}");
    }

    // Share of a garment's samples within `metres` of skin (for the audit line: lets the tolerance
    // be re-chosen from one run's log without resampling).
    private static float Share(Entry e, float metres)
    {
        if (e.Dist == null || e.Dist.Length == 0) return 0f;
        int n = 0;
        for (int i = 0; i < e.Dist.Length; i++) if (e.Dist[i] <= metres) n++;
        return n / (float)e.Dist.Length;
    }

    private static string Mm(float metres) =>
        (metres * 1000f).ToString("F0", CultureInfo.InvariantCulture) + "mm";

    private static string EnabledOf(Renderer r)
    {
        try { return r != null ? r.enabled.ToString() : "dead"; } catch { return "?"; }
    }

    private static string MeshOf(Renderer r)
    {
        try
        {
            var sk = r != null ? r.TryCast<SkinnedMeshRenderer>() : null;
            if (sk == null) return "not-skinned";
            var mesh = sk.sharedMesh;
            var bones = sk.bones;
            return $"'{mesh?.name ?? "null"}' bones={(bones?.Length ?? 0)}";
        }
        catch { return "?"; }
    }

    private static string ForceOf(Renderer r)
    {
        try { return r != null ? r.forceRenderingOff.ToString() : "dead"; } catch { return "?"; }
    }

    private static string BoundsOf(Renderer r)
    {
        try
        {
            if (r == null) return "dead";
            var b = r.bounds;
            return string.Format(CultureInfo.InvariantCulture,
                "c=({0:F2},{1:F2},{2:F2}) e=({3:F2},{4:F2},{5:F2})", b.center.x, b.center.y, b.center.z,
                b.extents.x, b.extents.y, b.extents.z);
        }
        catch { return "unreadable"; }
    }

    private static void LogOnce(IHostContext log, string message)
    {
        // Only one line per cause: the input path fails identically every frame when it fails.
        if (_inputFailureLogged) return;
        _inputFailureLogged = true;
        log.LogWarning(message);
    }
    private static bool _inputFailureLogged;

    // ------------------------------------------------------------------ mirror RT evidence
    private static void FlushMirrorDump(IHostContext log)
    {
        if (_pendingMirrorTag == null || Time.frameCount <= _pendingMirrorFrame) return;
        string tag = _pendingMirrorTag;
        _pendingMirrorTag = null;
        var rt = _mirrorTexture?.Invoke();
        if (rt == null) { log.LogWarning("[clothes] mirror=<tag>: no mirror (not placed yet); nothing captured"); return; }
        var previous = RenderTexture.active;
        Texture2D tex = null;
        try
        {
            if (!rt.IsCreated()) { log.LogWarning("[clothes] mirror RT not created; nothing captured"); return; }
            RenderTexture.active = rt;
            tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            tex.Apply();
            var png = ImageConversion.EncodeToPNG(tex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];
            string dir = Path.Combine(log.ScreensDir, "clothes");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, tag + "_mirror.png");
            File.WriteAllBytes(path, bytes);
            log.LogInfo($"[clothes] mirror dump tag='{tag}' {rt.width}x{rt.height} -> {path} ({bytes.Length} bytes)");
        }
        catch (Exception e) { log.LogWarning($"[clothes] mirror dump failed: {e.Message}"); }
        finally { RenderTexture.active = previous; if (tex != null) UnityEngine.Object.Destroy(tex); }
    }

    // ------------------------------------------------------------------ helpers
    private static int CountApplied()
    {
        int n = 0;
        foreach (var e in _entries.Values) if (e.Applied) n++;
        return n;
    }

    private static int CountGarments()
    {
        int n = 0;
        foreach (var e in _entries.Values) if (e.Garment) n++;
        return n;
    }

    private static int Fingerprint()
    {
        int h = _models.Count * 397;
        foreach (var m in _models.Values) h = h * 31 + m.Ptr;
        foreach (var e in _entries.Values) h = h * 31 + e.RPtr + (e.Garment ? 7 : 0);
        return h;
    }

    private static int Ptr(Il2CppObjectBase o)
    {
        try { return o == null ? 0 : (int)o.Pointer.ToInt64(); } catch { return 0; }
    }

    private static string SceneName()
    {
        try { var s = SceneManager.GetActiveScene(); return s.name ?? ""; } catch { return ""; }
    }

    private static string RequestPath(IHostContext log)
    {
        try { return log.ScreensDir == null ? null : Path.Combine(log.ScreensDir, RequestFileName); }
        catch { return null; }
    }

    private static bool ParseFloat(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "mirror";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length && sb.Length < 48; i++)
            sb.Append(char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_' ? s[i] : '_');
        return sb.Length == 0 ? "mirror" : sb.ToString();
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var sb = new StringBuilder(t.name);
        for (var p = t.parent; p != null; p = p.parent)
        {
            sb.Insert(0, p.name + "/");
            if (sb.Length > 400) break;
        }
        return sb.ToString();
    }
}
