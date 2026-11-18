using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Gameplay.Wardrobe;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// The wardrobe PREVIEW body.
//
// The wardrobe's model display is `Gameplay.Wardrobe.WardrobeModelDisplayer`: it instantiates a
// `ThirdViewPlayerModel` per `PlayerModelData` under `_modelsParent` (a second, separate set of
// bodies from the local player's), keys them by that data in `_models`, and shows one of them while
// the list is open. A row click ends in `DisplayModel(PlayerModelData)`, which looks the clicked
// row's data up in `_models` and throws `KeyNotFoundException` when it is not a key.
//
// The displayer builds that map once in `Awake`/`InstantiateModels` from the database as it was
// then. Our entries are appended later, so the map only covers the rows that existed at that moment
// - in the live lobby it was the 10 native rows plus the first two of ours, and every row from
// `Golubiro` on was absent, which is why a first click on those rows did nothing. `EnsureInstances`
// below extends the map with the missing rows through the displayer's own `InstantiateModels`, in
// one call, and the orphan sweep keeps the instance count flat across opens and generations.
//
// With the map complete, a row click shows that row's own `ThirdViewPlayerModel` instance. Take the
// instance the displayer is showing, hide its body renderers, and attach the matching model to
// *its* skeleton:
//
//   rigged bakes (Sahur, Capuchino, the Nordbo set) -> SahurRig.AttachExternal (own skeleton,
//                                retargeted from this preview's game bones)
//   rigid bakes (Golubiro and the other ModelKind.Rigid entries) -> SahurModel.BuildPreviewBody
//                                (pieces bound to
//                                this preview's bones, bindposes from its own renderers)
//
// The preview is destroyed/recreated by the displayer on selection changes, so every tick compares
// the instance it is showing with the one we are attached to; a different instance (or a dead one)
// tears the old rig down and builds a new one on the new instance. A native entry restores the
// native renderers and leaves no rig behind. Builds/disposes are counted and logged.
internal static class SahurPreview
{
    private const float PollSeconds = 0.2f;

    private static WardrobeModelDisplayer _displayer;
    private static ThirdViewPlayerModel _model;      // preview instance we are attached to
    private static int _entry = -1;                  // SahurWardrobe entry index, -1 = none
    private static SahurRig.RigLease _rig;           // entry 0
    private static GameObject _rigidGo;            // any ModelKind.Rigid entry
    private static Mesh _rigidMesh;
    private static float _nextPoll;
    private static WardrobeModelDisplayer _mapFor;   // displayer whose map we last verified
    private static int _mapLen = -1;                 // database length at that verification
    private static int _mapAdds;                     // instances we created for missing rows
    private static int _mapSweeps;                   // orphan preview instances destroyed
    private static Harmony _displayProbe;            // logging-only prefix on DisplayModel
    private static bool _displayProbeTried;
    private static IHostContext _probeLog;
    private static int _builds, _disposes, _restores;
    private static readonly List<HostRenderer> _hidden = new();

    private struct HostRenderer
    {
        public Renderer R;
        public bool HadEnabled;
    }
    private static string _lastWhy = "init";
    private static bool _dumpedScene;

    internal static int Builds => _builds;
    internal static int Disposes => _disposes;
    internal static bool Attached => _rig != null || _rigidGo != null;

    // True while a live preview body is drawing from that spec's decoded asset. The asset caches
    // consult this before evicting, so a selection change cannot pull a texture out from under the
    // preview instance the displayer is still showing (it detaches on the next 0.2 s poll).
    internal static bool IsSpecAttached(string slug)
    {
        if (string.IsNullOrEmpty(slug) || _entry < 0) return false;
        var spec = SahurWardrobe.SpecAt(_entry);
        return spec != null && string.Equals(spec.Slug, slug, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== tick

    private static bool _inLobby = true;

    private static bool IsLobbyScene()
    {
        try
        {
            string s = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
            return s.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    internal static void Tick(IHostContext log, float dt)
    {
        if (log == null) return;
        if (!IsLobbyScene())
        {
            if (_inLobby) { _inLobby = false; Teardown(log, "not-lobby-scene"); }
            return;
        }
        _inLobby = true;
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;
        try { Update(log); }
        catch (Exception e) { log.LogWarning($"[sahur:preview] tick failed: {e.Message}"); }
    }

    // The retarget has to run after every skeleton writer, exactly like the player body's.
    internal static void LateTick(IHostContext log)
    {
        if (_rig == null) return;
        try
        {
            if (_model == null || _model.gameObject == null) return;
            SahurRig.PoseExternal(log, _rig);
        }
        catch (Exception e) { log.LogWarning($"[sahur:preview] pose failed: {e.Message}"); }
    }

    private static void Update(IHostContext log)
    {
        var displayer = FindDisplayer();
        if (displayer == null)
        {
            Teardown(log, "no-displayer");
            return;
        }
        if (displayer != _displayer)
        {
            _displayer = displayer;
            if (!_dumpedScene)
            {
                _dumpedScene = true;
                DumpScene(log, displayer);
            }
        }

        // The displayer's instance map must hold a key for every row before any click can use it.
        // Runs as soon as the displayer exists (it lives in the lobby, so this happens long before
        // the wardrobe is opened), and again when the view is recreated or the database changes.
        EnsureDisplayProbe(log);
        EnsureInstances(log, displayer);

        // Follow the row currently selected in the list, not the applied model: a click on a
        // native row must show that row's body even while one of our entries is still applied.
        int want = SahurWardrobe.PreviewIndex;
        if (want < 0)
        {
            Teardown(log, "preview-not-ours");
            return;
        }

        // Point the displayer at the row the list is on. This is the call a row click itself makes;
        // doing it here too means the preview follows the click even if the game's own call did not
        // land, and it then leaves the displayer alone (one call per selection change). Native rows
        // return above and are the game's business only.
        PlayerModelData wanted = SahurWardrobe.DataAt(want);
        PlayerModelData cur = null;
        try { cur = displayer._currentplayerModeData; } catch { }
        if (wanted != null && !SahurWardrobe.SameData(cur, wanted))
        {
            try
            {
                displayer.DisplayModel(wanted);
                log.LogInfo($"[sahur:preview] displayer was showing '{(cur == null ? "null" : cur.name)}' " +
                            $"-> DisplayModel(entry {want} '{wanted.name}')");
            }
            catch (Exception e) { log.LogWarning($"[sahur:preview] DisplayModel('{wanted.name}') failed: {e.Message}"); }
        }

        var preview = ActivePreview(displayer);
        if (preview == null || preview.gameObject == null)
        {
            Teardown(log, "no-preview-instance");
            return;
        }

        // The host must be the body our entry was cloned from (both entries clone the James
        // prefab). Attaching to another model's preview would hide a body that is not ours and
        // put our skeleton-driven mesh on a skeleton it was not baked for.
        string hostName = null;
        try { hostName = preview.gameObject.name ?? ""; } catch { }
        string wantName = null;
        try
        {
            var pd = SahurWardrobe.DataAt(want);
            if (pd != null && pd.ThirdViewPlayerModel != null && pd.ThirdViewPlayerModel.gameObject != null)
                wantName = pd.ThirdViewPlayerModel.gameObject.name ?? "";
        }
        catch { }
        if (!string.IsNullOrEmpty(wantName) && hostName != null &&
            !hostName.StartsWith(wantName, StringComparison.Ordinal))
        {
            Teardown(log, $"host-mismatch '{hostName}'");
            return;
        }

        if (_model == preview && _entry == want) return;
        Teardown(log, _model == null ? "first" : (_model != preview ? "instance-changed" : "variant-changed"));
        Build(log, want, preview);
    }

    // ================================================================== instance map

    private static int MapCount(WardrobeModelDisplayer d)
    {
        try { return d._models == null ? -1 : d._models.Count; } catch { return -1; }
    }

    private static bool HasKey(WardrobeModelDisplayer d, PlayerModelData data, out ThirdViewPlayerModel inst)
    {
        inst = null;
        try { return data != null && d._models != null && d._models.TryGetValue(data, out inst) && inst != null; }
        catch { return false; }
    }

    // The displayer keys `_models` by PlayerModelData and builds it once from
    // `PlayerModelDatabase.Models` as it was at Awake time. Our entries are appended afterwards, so
    // any row added since then is not a key, and the row click's DisplayModel(data) throws
    // KeyNotFoundException for it (measured: 12 keys - 10 native + entries 0/1 - and "key=NO" for
    // entries 2..15, the whole Golubiro-and-below list).
    //
    // Give the displayer exactly the rows it is missing: point the static database at them for the
    // duration of one synchronous call to its own creation path (`InstantiateModels`, which
    // instantiates a body per database row and adds it under that row) and then put the database
    // back. Calling it with the full list instead would re-add the existing keys and throw.
    // Any row it still does not map is instantiated the same way it would - same prefab, same
    // parent, inactive until DisplayModel activates it - and keyed here.
    private static void EnsureInstances(IHostContext log, WardrobeModelDisplayer d)
    {
        var models = PlayerModelDatabase.Models;
        int len = models == null ? -1 : models.Length;
        if (d == null || models == null) return;
        if (d == _mapFor && len == _mapLen) return;   // already verified for this view + database
        _mapFor = d;
        _mapLen = len;

        var missing = new List<PlayerModelData>();
        for (int i = 0; i < models.Length; i++)
        {
            var data = models[i];
            if (data == null) continue;
            ThirdViewPlayerModel inst;
            if (!HasKey(d, data, out inst)) missing.Add(data);
        }

        int before = MapCount(d);
        if (missing.Count > 0)
        {
            var only = new Il2CppReferenceArray<PlayerModelData>(missing.Count);
            for (int i = 0; i < missing.Count; i++) only[i] = missing[i];
            string names = Names(missing);
            var saved = models;
            try
            {
                PlayerModelDatabase._models = only;
                d.InstantiateModels();
            }
            catch (Exception e) { log.LogWarning($"[sahur:preview] displayer InstantiateModels failed: {e.Message}"); }
            finally { PlayerModelDatabase._models = saved; }

            int added = 0;
            foreach (var data in missing)
            {
                ThirdViewPlayerModel inst;
                if (HasKey(d, data, out inst)) { added++; continue; }
                if (AddInstance(log, d, data)) added++;
            }
            _mapAdds += added;
            log.LogInfo($"[sahur:preview] instance map: {before} -> {MapCount(d)} keys " +
                        $"({models.Length} rows, {missing.Count} missing [{names}], {added} instanced, " +
                        $"total added {_mapAdds})");
        }

        SweepOrphans(log, d, models);
    }

    // Fallback creation, identical to what InstantiateModels does: one clone of that row's own
    // third-view prefab under the displayer's model parent, inactive until DisplayModel shows it.
    private static bool AddInstance(IHostContext log, WardrobeModelDisplayer d, PlayerModelData data)
    {
        try
        {
            var prefab = data == null ? null : data.ThirdViewPlayerModel;
            if (prefab == null || prefab.gameObject == null)
            {
                log.LogWarning($"[sahur:preview] '{(data == null ? "null" : data.name)}' has no third-view prefab to instance");
                return false;
            }
            var parent = d._modelsParent != null ? d._modelsParent : d.transform;
            if (parent == null) { log.LogWarning("[sahur:preview] displayer has no model parent"); return false; }
            var clone = UnityEngine.Object.Instantiate(prefab, parent)?.TryCast<ThirdViewPlayerModel>();
            if (clone == null || clone.gameObject == null) { log.LogWarning($"[sahur:preview] clone of '{data.name}' failed"); return false; }
            try { if (clone.gameObject.activeSelf) clone.gameObject.SetActive(false); } catch { }
            d._models[data] = clone;
            log.LogInfo($"[sahur:preview] instanced '{clone.gameObject.name}' for row '{data.name}' " +
                        $"under '{PathOf(parent)}' activeSelf=false");
            return true;
        }
        catch (Exception e)
        {
            log.LogWarning($"[sahur:preview] instancing '{(data == null ? "null" : data.name)}' failed: {e.Message}");
            return false;
        }
    }

    // A preview body under the model parent that no database row maps to is unreachable: the
    // displayer cannot show it, and it stays in the scene as a duplicate. That is what a failed
    // InstantiateModels leaves behind (observed: a 13th active Female_01 clone after the throw), and
    // what a generation that appended before this fix left behind. Destroy those, never a mapped
    // instance, so the census stays one per row.
    private static void SweepOrphans(IHostContext log, WardrobeModelDisplayer d, Il2CppReferenceArray<PlayerModelData> models)
    {
        int killed = 0;
        try
        {
            var parent = d._modelsParent != null ? d._modelsParent : d.transform;
            if (parent == null) return;
            var arr = parent.GetComponentsInChildren(Il2CppType.Of<ThirdViewPlayerModel>(), true);
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.gameObject == null) continue;
                bool mapped = false;
                for (int r = 0; r < models.Length && !mapped; r++)
                {
                    var data = models[r];
                    if (data == null) continue;
                    ThirdViewPlayerModel inst;
                    if (HasKey(d, data, out inst) && inst.Pointer == m.Pointer) mapped = true;
                }
                if (mapped) continue;
                string name = m.gameObject.name ?? "";
                log.LogInfo($"[sahur:preview] orphan preview instance '{name}' id={m.GetInstanceID()} " +
                            $"is not in the displayer map - destroying");
                try { UnityEngine.Object.Destroy(m.gameObject); killed++; } catch (Exception e) { log.LogWarning($"[sahur:preview] orphan destroy failed: {e.Message}"); }
            }
        }
        catch (Exception e) { log.LogWarning($"[sahur:preview] orphan sweep failed: {e.Message}"); }
        if (killed > 0)
        {
            _mapSweeps += killed;
            log.LogInfo($"[sahur:preview] orphan sweep: {killed} destroyed, {MapCount(d)} keys, " +
                        $"instances under parent now {InstanceCensus(d)} (swept total {_mapSweeps})");
        }
    }

    private static int InstanceCensus(WardrobeModelDisplayer d)
    {
        try
        {
            var parent = d._modelsParent != null ? d._modelsParent : d.transform;
            var arr = parent == null ? null : parent.GetComponentsInChildren(Il2CppType.Of<ThirdViewPlayerModel>(), true);
            return arr == null ? 0 : arr.Length;
        }
        catch { return -1; }
    }

    private static string Names(List<PlayerModelData> list)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('\'').Append(list[i] == null ? "null" : list[i].name).Append('\'');
        }
        return sb.ToString();
    }

    private static WardrobeModelDisplayer FindDisplayer()
    {
        try
        {
            var all = SceneCache.ScanPolled(Il2CppType.Of<WardrobeModelDisplayer>(), 1.0);
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var d = all[i].TryCast<WardrobeModelDisplayer>();
                if (d == null || d.gameObject == null) continue;
                var sc = d.gameObject.scene;
                if (string.IsNullOrEmpty(sc.name)) continue;   // prefab asset
                return d;
            }
        }
        catch { }
        return null;
    }

    // The instance the displayer is showing. One body is active at a time (that is what the wardrobe
    // shows); `_currentplayerModeData` says which row it considers current, which is the same
    // instance DisplayModel just activated. Prefer the active instance, then the current row's own
    // instance, and only then the first one. Ours are excluded by name so a stale attach can never
    // be picked up as the host.
    private static ThirdViewPlayerModel ActivePreview(WardrobeModelDisplayer d)
    {
        try
        {
            var parent = d._modelsParent != null ? d._modelsParent : d.transform;
            if (parent == null) return null;
            ThirdViewPlayerModel cur = null;
            try { HasKey(d, d._currentplayerModeData, out cur); } catch { }
            var arr = parent.GetComponentsInChildren(Il2CppType.Of<ThirdViewPlayerModel>(), true);
            ThirdViewPlayerModel first = null, active = null;
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.gameObject == null) continue;
                string n = m.gameObject.name ?? "";
                if (n.StartsWith("Sahur", StringComparison.Ordinal)) continue;
                if (first == null) first = m;
                bool on = false;
                try { on = m.gameObject.activeInHierarchy; } catch { }
                if (cur != null && m.Pointer == cur.Pointer && on) return m;
                if (on && active == null) active = m;
            }
            if (active != null) return active;
            return cur ?? first;
        }
        catch { return null; }
    }

    // Logging-only Harmony prefix on the game's own preview switch: the evidence that a row click
    // reaches DisplayModel and whether the map had the key at that moment. Never alters the call.
    private static void EnsureDisplayProbe(IHostContext log)
    {
        if (_displayProbeTried) return;
        _displayProbeTried = true;
        _probeLog = log;
        try
        {
            var m = AccessTools.DeclaredMethod(typeof(WardrobeModelDisplayer), "DisplayModel",
                                               new[] { typeof(PlayerModelData) });
            if (m == null) { log.LogWarning("[sahur:preview] DisplayModel not found; probe off"); return; }
            var pre = typeof(SahurPreview).GetMethod(nameof(OnDisplayModel), BindingFlags.Static | BindingFlags.NonPublic);
            _displayProbe = new Harmony("alterum.modelswap.previewdisplay");
            _displayProbe.Patch(m, prefix: new HarmonyMethod(pre));
            log.LogInfo("[sahur:preview] DisplayModel probe installed (prefix, logging only)");
        }
        catch (Exception e) { log.LogWarning($"[sahur:preview] DisplayModel probe install failed: {e.Message}"); _displayProbe = null; }
    }

    private static void OnDisplayModel(PlayerModelData playerModeData)
    {
        try
        {
            var d = _displayer ?? FindDisplayer();
            ThirdViewPlayerModel inst;
            bool key = HasKey(d, playerModeData, out inst);
            _probeLog?.LogInfo($"[sahur:preview] DisplayModel('{(playerModeData == null ? "null" : playerModeData.name)}') " +
                               $"key={(key ? "yes" : "NO")} map={MapCount(d)} " +
                               $"current='{(d == null || d._currentplayerModeData == null ? "null" : d._currentplayerModeData.name)}'");
        }
        catch { }
    }

    // ================================================================== attach / detach

    private static void Build(IHostContext log, int entry, ThirdViewPlayerModel preview)
    {
        var data = SahurWardrobe.DataAt(entry);
        var spec = SahurWardrobe.SpecAt(entry);
        string label = $"preview:{entry}:{(data == null ? "?" : data.name)}";
        _model = preview;
        _entry = entry;
        _builds++;
        try
        {
            // The path is a property of the model's bake (ModelRoster), not of the entry index:
            // rigged bakes keep their own skeleton and are retargeted, rigid bakes are bound 1.0 to
            // one game bone per piece. Both reuse the exact code the player body uses.
            if (spec != null && spec.Kind == ModelKind.Rigged)
            {
                _rig = SahurRig.AttachExternal(log, spec, preview, preview.transform, label, PreviewLayer(preview));
                if (_rig == null) { _model = null; _entry = -1; return; }
            }
            else
            {
                // The rigid mesh does not hide the host itself (the player module owns that
                // bookkeeping for the player body), so the preview does it here.
                HideHost(log, preview);
                _rigidGo = SahurModel.BuildPreviewBody(log, spec, preview, PreviewLayer(preview), out _rigidMesh, out var note);
                if (_rigidGo == null)
                {
                    log.LogWarning($"[sahur:preview] rigid build failed for '{(spec == null ? "?" : spec.Label)}': {note}");
                    _model = null;
                    _entry = -1;
                    return;
                }
            }
            log.LogInfo($"[sahur:preview] BUILD #{_builds} entry={entry} kind={(spec == null ? "?" : spec.Kind.ToString())} label='{label}' host='{PathOf(preview.transform)}' " +
                        $"activeSelf={(preview.gameObject != null && preview.gameObject.activeSelf)} " +
                        $"bones={PreviewBoneCount(preview)} " +
                        $"hostRenderers={preview.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true)?.Length ?? 0} " +
                        $"liveRigs={LiveRigs(preview)} builds={_builds} disposes={_disposes}");
        }
        catch (Exception e)
        {
            log.LogWarning($"[sahur:preview] build failed entry={entry}: {e}");
            _model = null;
            _entry = -1;
        }
    }

    private static void Teardown(IHostContext log, string why)
    {
        bool had = _rig != null || !ReferenceEquals(_rigidMesh, null);
        var model = _model;
        var rig = _rig;
        var rigid = _rigidGo;
        var mesh = _rigidMesh;
        int entry = _entry;
        _rig = null;
        _rigidGo = null;
        _rigidMesh = null;
        _model = null;
        _entry = -1;
        if (!had) return;
        _disposes++;
        _lastWhy = why;
        try
        {
            if (rig != null) SahurRig.DetachExternal(log, rig, why);
            if (!ReferenceEquals(mesh, null))
            {
                // Unity's destroyed-object == null must not skip releasing the managed asset lease.
                SahurModel.DestroyPreviewBody(log, rigid, mesh);
                _restores++;
            }
            RestoreHost(log, why);
            log.LogInfo($"[sahur:preview] DISPOSE #{_disposes} entry={entry} why='{why}' " +
                        $"host='{(model == null ? "none" : PathOf(model.transform))}' " +
                        $"liveRigs={(model == null ? 0 : LiveRigs(model))} builds={_builds} disposes={_disposes}");
        }
        catch (Exception e) { log.LogWarning($"[sahur:preview] teardown failed: {e.Message}"); }
    }

    internal static void Dispose(IHostContext log)
    {
        Teardown(log, "dispose");
        try { _displayProbe?.UnpatchSelf(); } catch { }
        _displayProbe = null;
        _displayProbeTried = false;
        _probeLog = null;
        _mapFor = null;
        _mapLen = -1;
        _displayer = null;
        _dumpedScene = false;
        if (log != null)
            try { log.LogInfo($"[sahur:preview] disposed builds={_builds} disposes={_disposes} restores={_restores} last='{_lastWhy}'"); } catch { }
    }

    // Snapshot + disable the host instance's own body renderers (rigid variant only; the rigged
    // variant's Build does the same on its own snapshot). Disabled, never destroyed, and restored
    // to their previous state on teardown.
    private static void HideHost(IHostContext log, ThirdViewPlayerModel model)
    {
        if (_hidden.Count > 0) return;
        try
        {
            var comps = model.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var r = comps[i].TryCast<Renderer>();
                if (r == null || CosmeticMarkers.IsCosmetic(r)) continue;
                string n = r.gameObject == null ? "" : (r.gameObject.name ?? "");
                if (n.StartsWith("Sahur", StringComparison.Ordinal)) continue;
                bool had;
                try { had = r.enabled; } catch { continue; }
                _hidden.Add(new HostRenderer { R = r, HadEnabled = had });
                try { r.enabled = false; } catch { }
            }
            log.LogInfo($"[sahur:preview] host body hidden: {_hidden.Count} renderer(s) under '{PathOf(model.transform)}'");
        }
        catch (Exception e) { log.LogWarning($"[sahur:preview] host hide failed: {e.Message}"); }
    }

    private static void RestoreHost(IHostContext log, string why)
    {
        int restored = 0;
        for (int i = 0; i < _hidden.Count; i++)
        {
            var e = _hidden[i];
            try { if (e.R != null) { e.R.enabled = e.HadEnabled; restored++; } } catch { }
        }
        _hidden.Clear();
        if (restored > 0) log.LogInfo($"[sahur:preview] host body restored: {restored} renderer(s) (why={why})");
    }

    // ================================================================== diagnostics

    // The layer the wardrobe camera actually draws: the preview instance's own. The player body
    // uses the mod's cosmetic layer 6, which no wardrobe camera includes.
    private static int PreviewLayer(ThirdViewPlayerModel m)
    {
        try { return m.gameObject.layer; } catch { return 2; }
    }

    private static int PreviewBoneCount(ThirdViewPlayerModel m)
    {
        try { return m.Bones != null && m.Bones._allBones != null ? m.Bones._allBones.Count : 0; }
        catch { return 0; }
    }

    // How many Sahur objects are currently under this preview root: the invariant is 0 or 1, never 2.
    private static int LiveRigs(ThirdViewPlayerModel m)
    {
        int n = 0;
        try
        {
            var all = m.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var t = all[i].TryCast<Transform>();
                if (t == null) continue;
                string s = t.name ?? "";
                if (s != null && s.StartsWith("Sahur", StringComparison.Ordinal) &&
                    (s.EndsWith("_Body", StringComparison.Ordinal) || s.EndsWith("_PreviewBody", StringComparison.Ordinal) ||
                     s.EndsWith("_Skeleton", StringComparison.Ordinal))) n++;
            }
        }
        catch { }
        return n;
    }

    private static void DumpScene(IHostContext log, WardrobeModelDisplayer d)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[sahur:preview] displayer='{PathOf(d.transform)}' " +
                          $"modelsParent='{(d._modelsParent == null ? "null" : PathOf(d._modelsParent))}' " +
                          $"current='{(d._currentplayerModeData == null ? "null" : d._currentplayerModeData.name)}'");
            int count = 0;
            try { count = d._models != null ? d._models.Count : -1; } catch { count = -1; }
            sb.AppendLine($"  _models count={count}");
            var parent = d._modelsParent != null ? d._modelsParent : d.transform;
            if (parent != null)
            {
                var arr = parent.GetComponentsInChildren(Il2CppType.Of<ThirdViewPlayerModel>(), true);
                for (int i = 0; arr != null && i < arr.Length; i++)
                {
                    var m = arr[i].TryCast<ThirdViewPlayerModel>();
                    if (m == null || m.gameObject == null) continue;
                    string prefab = PrefabSource(m.gameObject.name);
                    sb.AppendLine($"  [{i}] '{PathOf(m.transform)}' activeSelf={m.gameObject.activeSelf} " +
                                  $"active={m.gameObject.activeInHierarchy} layer={m.gameObject.layer} " +
                                  $"bones={PreviewBoneCount(m)} prefab={prefab} " +
                                  $"renderers={m.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true)?.Length ?? 0} " +
                                  $"sahurObjs={LiveRigs(m)}");
                }
            }
            try
            {
                var cams = SceneCache.Scan(Il2CppType.Of<Camera>());
                for (int i = 0; cams != null && i < cams.Length; i++)
                {
                    var c = cams[i].TryCast<Camera>();
                    if (c == null || c.gameObject == null) continue;
                    var sc = c.gameObject.scene;
                    string path = PathOf(c.transform);
                    if (path.IndexOf("Wardrobe", StringComparison.OrdinalIgnoreCase) < 0 &&
                        (string.IsNullOrEmpty(sc.name) || sc.name != d.gameObject.scene.name)) continue;
                    sb.AppendLine($"  camera '{path}' mask=0x{c.cullingMask:X} depth={c.depth} enabled={c.enabled}");
                }
            }
            catch { }
            var host = ActivePreview(d);
            if (host != null)
            {
                var rs = host.transform.lossyScale;
                sb.AppendLine($"  host root lossyScale={rs.ToString("F4")} localScale={host.transform.localScale.ToString("F4")}");
                var rs2 = host.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
                for (int i = 0; rs2 != null && i < rs2.Length; i++)
                {
                    var smr = rs2[i].TryCast<SkinnedMeshRenderer>();
                    if (smr == null) continue;
                    var ls = smr.transform.lossyScale;
                    var bs = smr.bounds.size;
                    sb.AppendLine($"  nativeSMR '{smr.gameObject.name}' lossyScale={ls.ToString("F4")} boundsWorld={bs.ToString("F3")} enabled={smr.enabled}");
                }
                var roots = host.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
                for (int i = 0; roots != null && i < roots.Length; i++)
                {
                    var t = roots[i].TryCast<Transform>();
                    if (t == null) continue;
                    string n = t.name ?? "";
                    if (!ModelRoster.IsOurObjectName(n)) continue;
                    var ls = t.lossyScale;
                    sb.AppendLine($"  rigObj '{n}' layer={t.gameObject.layer} lossyScale={ls.ToString("F4")} worldPos={t.position.ToString("F2")}");
                }
            }
            log.LogInfo(sb.ToString());
            DumpBones(log, ActivePreview(d));
        }
        catch (Exception e) { log.LogWarning($"[sahur:preview] scene dump failed: {e.Message}"); }
    }

    // Which PlayerModelData prefab an instance came from. `Instantiate` gives `<prefab>(Clone)`,
    // so the same name without the suffix is the prefab the preview was spawned from.
    private static string PrefabSource(string instanceName)
    {
        string bare = (instanceName ?? "").Replace("(Clone)", "").Trim();
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<PlayerModelData>());
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var d = arr[i].TryCast<PlayerModelData>();
                if (d == null || d.ThirdViewPlayerModel == null || d.ThirdViewPlayerModel.gameObject == null) continue;
                if ((d.name ?? "").StartsWith("Sahur", StringComparison.Ordinal)) continue;   // our clones share James's prefab
                if (string.Equals(d.ThirdViewPlayerModel.gameObject.name, bare, StringComparison.OrdinalIgnoreCase))
                    return $"'{d.name}' (index {d.Index})";
            }
        }
        catch { }
        return "unknown";
    }

    private static void DumpBones(IHostContext log, ThirdViewPlayerModel m)
    {
        if (m == null) { log.LogInfo("[sahur:preview] no active preview instance to describe"); return; }
        try
        {
            var all = m.Bones?._allBones;
            var names = new StringBuilder();
            int n = all != null ? all.Count : 0;
            for (int i = 0; i < n; i++)
            {
                var t = all[i];
                if (t == null) continue;
                if (names.Length > 0) names.Append(',');
                names.Append(t.name);
                if (names.Length > 240) { names.Append("..."); break; }
            }
            log.LogInfo($"[sahur:preview] preview skeleton '{PathOf(m.transform)}' bones={n} [{names}]");
        }
        catch (Exception e) { log.LogWarning($"[sahur:preview] bone dump failed: {e.Message}"); }
    }

    internal static void Dump(IHostContext log)
    {
        try
        {
            log.LogInfo($"[sahur:preview] attached={Attached} entry={_entry} " +
                        $"host='{(_model == null ? "none" : PathOf(_model.transform))}' " +
                        $"rig={(_rig != null)} rigid={(_rigidGo != null)} builds={_builds} disposes={_disposes} " +
                        $"last='{_lastWhy}' selection={SahurWardrobe.SelectedIndex}");
            if (_rig != null && _model != null) DumpBones(log, _model);
            var d = _displayer ?? FindDisplayer();
            if (d != null) DumpScene(log, d);
            else log.LogInfo("[sahur:preview] no WardrobeModelDisplayer in this scene (lobby only)");
        }
        catch (Exception e) { log.LogWarning($"[sahur:preview] dump failed: {e.Message}"); }
    }

    internal static void Command(IHostContext log, string line)
    {
        string v = (line ?? "").Trim().ToLowerInvariant();
        switch (v)
        {
            case "":
            case "1":
            case "dump":
                Dump(log);
                break;
            case "attach":
                _nextPoll = 0f;
                Update(log);
                Dump(log);
                break;
            case "detach":
            case "off":
                Teardown(log, "request");
                break;
            case "map":
                Probe(log);
                break;
            default:
                if (v.StartsWith("display="))
                {
                    Display(log, v.Substring(8));
                    break;
                }
                log.LogWarning($"[sahur:preview] unknown command '{line}' (dump|map|attach|detach|display=<name>)");
                break;
        }
    }

    // The displayer's own public preview switch: this is the call a row click ends in, without
    // needing the UI. `name` is any PlayerModelData label (a native row or one of ours).
    private static void Display(IHostContext log, string name)
    {
        var d = _displayer ?? FindDisplayer();
        if (d == null) { log.LogWarning("[sahur:preview] display: no WardrobeModelDisplayer"); return; }
        string want = (name ?? "").Trim();
        PlayerModelData pick = null;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<PlayerModelData>());
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var p = arr[i].TryCast<PlayerModelData>();
                if (p == null) continue;
                if (string.Equals((p.name ?? "").Trim(), want, StringComparison.OrdinalIgnoreCase)) { pick = p; break; }
                if (pick == null && (p.name ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) pick = p;
            }
        }
        catch { }
        if (pick == null) { log.LogWarning($"[sahur:preview] display: no PlayerModelData matching '{want}'"); return; }
        log.LogInfo($"[sahur:preview] display '{pick.name}' index={pick.Index} via WardrobeModelDisplayer.DisplayModel");
        try { d.DisplayModel(pick); } catch (Exception e) { log.LogWarning($"[sahur:preview] display threw: {e.Message}"); }
    }

    // ---------------------------------------------------------------- instance-map dump

    // Does the displayer's own instance map cover the database rows? For every
    // PlayerModelDatabase.Models entry: is that exact PlayerModelData a key, and which instance does
    // it map to. The wardrobe's click path ends in DisplayModel(data), which looks the key up, so a
    // missing key here is a click that throws instead of showing a preview.
    private static void Probe(IHostContext log)
    {
        var d = _displayer ?? FindDisplayer();
        if (d == null) { log.LogWarning("[sahur:preview] map: no WardrobeModelDisplayer"); return; }
        int count = MapCount(d);
        log.LogInfo($"[sahur:preview] map displayer='{PathOf(d.transform)}' _models={count} " +
                    $"parent='{(d._modelsParent == null ? "null" : PathOf(d._modelsParent))}' " +
                    $"current='{(d._currentplayerModeData == null ? "null" : d._currentplayerModeData.name)}' " +
                    $"database={(PlayerModelDatabase.Models == null ? -1 : PlayerModelDatabase.Models.Length)} ents={SahurWardrobe.EntryCount}");
        var models = PlayerModelDatabase.Models;
        for (int i = 0; models != null && i < models.Length; i++)
        {
            var data = models[i];
            ThirdViewPlayerModel inst;
            bool key = HasKey(d, data, out inst);
            string mine = "";
            for (int e = 0; e < SahurWardrobe.EntryCount; e++)
                if (SahurWardrobe.SameData(SahurWardrobe.DataAt(e), data)) { mine = $" entry={e}"; break; }
            log.LogInfo($"  map[{i}] '{(data == null ? "null" : data.name)}'{mine} key={(key ? "yes" : "NO")} " +
                        $"inst='{(inst == null ? "-" : inst.gameObject == null ? "dead" : inst.gameObject.name)}" +
                        $" active={(inst == null || inst.gameObject == null ? "?" : inst.gameObject.activeSelf.ToString())}'");
        }
        DumpInstances(log, d, "map");
    }

    private static void DumpInstances(IHostContext log, WardrobeModelDisplayer d, string tag)
    {
        try
        {
            var parent = d._modelsParent != null ? d._modelsParent : d.transform;
            var arr = parent == null ? null : parent.GetComponentsInChildren(Il2CppType.Of<ThirdViewPlayerModel>(), true);
            var sb = new StringBuilder();
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.gameObject == null) continue;
                sb.AppendLine($"  {tag}[{i}] id={m.GetInstanceID()} '{(m.gameObject.name ?? "")}' activeSelf={m.gameObject.activeSelf} sahur={LiveRigs(m)}");
            }
            log.LogInfo($"[sahur:preview] instances under parent ({arr?.Length ?? 0}):\n{sb}");
        }
        catch (Exception e) { log.LogWarning($"[sahur:preview] instance dump failed: {e.Message}"); }
    }

    private static string PathOf(Transform t)
    {
        try
        {
            if (t == null) return "null";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
        catch { return "?"; }
    }
}
