using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Chest / hip enhancement on the IMPORTED bodies (SahurModel's rigid bakes, SahurRig's rigged ones).
//
// Two mechanisms, both chosen per model by ModelEnhancementProfiles:
//
//   RiggedBones  the model's own skeleton is live, so the profile's chest/hip bones are scaled about
//                their own origin. Scaling a bone in a skinned rig inflates the skin it owns; the
//                snapshot of every local scale is taken once and restored verbatim on release.
//   RestDeform   no usable bone (rigid region bakes bind one bone per piece), so the mesh itself is
//                deformed in REST space: vertices inside a height band of the model are pushed
//                radially out, with a smooth cosine falloff at the band edges.
//
// The rule both mechanisms obey: the FIRST application stores the immutable original (bone local
// scales, or the mesh's original vertex array), and every later application re-derives from that
// original. Nothing ever multiplies the current state, so apply/release cycles cannot accumulate
// and the four toggle combinations are exact - measured by `[enh:custom]` written vs original.
//
// The mesh is never re-read after being recorded once: `_records` is keyed by the renderer pointer,
// which changes when the body module rebuilds, so a new build is a new record.
internal static class CustomEnhancements
{
    internal const string RequestName = "enh.request";

    private sealed class Record
    {
        public string Slug;
        public Renderer R;
        public Mesh Mesh;
        public Vector3[] Original;            // immutable rest-space vertices
        public float HeightMin, HeightMax;
        public readonly List<BoneSnap> Bones = new();
        public Transform[] BonesFrom;         // the imported array these bones were resolved in
        public bool Rigid;
        public EnhanceMode ChestMode, HipMode;
        public bool ChestOn, HipsOn;
        public bool External;               // attached to a REMOTE body (stage 3A), not the local one
        public Transform SearchRoot;         // the caller's diagnostic root; NOT used for bone lookup
        public IntPtr RendererPointer;
        public int Applies;
        public float MaxDeltaY;                // measured: largest vertex displacement this apply
    }

    private sealed class BoneSnap
    {
        public Transform T;
        public Vector3 Scale;
        public string Name;                   // the profile name that matched
        public string Object;                 // the imported transform's own name at snapshot time
        public int Index;                     // its index in the lease's bone array
    }

    private static readonly Dictionary<int, Record> _records = new();
    private static bool _announced;
    private static float _nextPoll;

    internal static int RecordCount => _records.Count;

    // Which imported model is worn right now, as a slug. The hands lease keys on it: a different
    // body is a different hand geometry decision.
    internal static string CurrentSlug
    {
        get
        {
            try
            {
                var s = SahurModel.LiveSpec ?? SahurRig.LiveSpec;
                if (s != null) return s.Slug;
            }
            catch { }
            return null;
        }
    }

    internal static string DumpAll()
    {
        var sb = new StringBuilder();
        foreach (var kv in _records)
        {
            var r = kv.Value;
            if (r == null) continue;
            sb.Append(r.Slug).Append("(chest=").Append(r.ChestMode).Append(':')
              .Append(r.ChestOn ? "on" : "off").Append(",hip=").Append(r.HipMode).Append(':')
              .Append(r.HipsOn ? "on" : "off").Append(",bones=").Append(r.Bones.Count)
              .Append(",verts=").Append(r.Original != null ? r.Original.Length : 0).Append(") ");
        }
        return sb.ToString().Trim();
    }

    // ================================================================== tick

    internal static void Tick(IHostContext log, float dt)
    {
        if (log == null) return;
        if (!_announced)
        {
            _announced = true;
            EnhancementSettings.EnsureLoaded(log);
            try { log.LogInfo($"[enh] ready request={Path.Combine(log.ScreensDir ?? "", RequestName)} " + EnhancementSettings.Dump()); } catch { }
        }
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _nextPoll) return;
        _nextPoll = now + 0.25f;
        try { PollRequest(log); } catch (Exception e) { log.LogWarning($"[enh] request poll failed: {e.Message}"); }
        try { Reconcile(log); } catch (Exception e) { log.LogWarning($"[enh] reconcile failed: {e.Message}"); }
    }

    private static void PollRequest(IHostContext log)
    {
        if (string.IsNullOrEmpty(log.ScreensDir)) return;
        string path = Path.Combine(log.ScreensDir, RequestName);
        string text;
        try
        {
            if (!File.Exists(path)) return;
            text = File.ReadAllText(path);
            File.Delete(path);
        }
        catch { return; }
        if (string.IsNullOrEmpty(text)) return;

        string chest = ParseValue(text, "chest");
        if (chest != null) EnhancementSettings.SetChest(Truthy(chest), "request");
        string hips = ParseValue(text, "hips");
        if (hips != null) EnhancementSettings.SetHips(Truthy(hips), "request");
        if (text.IndexOf("chesttoggle", StringComparison.OrdinalIgnoreCase) >= 0) EnhancementSettings.ToggleChest("request");
        if (text.IndexOf("hipstoggle", StringComparison.OrdinalIgnoreCase) >= 0) EnhancementSettings.ToggleHips("request");
        if (Has(text, "matrix")) Matrix(log);
        if (Has(text, "probe") || Has(text, "dump")) Dump(log, "request");
        if (Has(text, "save")) { EnhancementSettings.Apply(log, "request"); }
    }

    private static bool Truthy(string v)
    {
        v = (v ?? "").Trim().ToLowerInvariant();
        return v == "1" || v == "on" || v == "true" || v == "yes";
    }

    private static bool Has(string text, string w) => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string ParseValue(string text, string key)
    {
        int i = text.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int s = i + key.Length + 1, e = s;
        while (e < text.Length && text[e] != '\n' && text[e] != '\r' && text[e] != ' ' && text[e] != '\t') e++;
        return text.Substring(s, e - s).Trim();
    }

    // ================================================================== apply / release

    // Called from EnhancementSettings.Apply and from the per-frame reconcile (a body rebuilt while a
    // toggle is on must get the current state without waiting for a toggle).
    internal static void ApplyAll(IHostContext log, bool chest, bool hips, string why)
    {
        ApplyOne(log, SahurModel.LiveSpec, SahurModel.LiveRenderer, chest, hips, why, rigid: true);
        ApplyOne(log, SahurRig.LiveSpec, SahurRig.LiveRenderer, chest, hips, why, rigid: false);
        log.LogInfo($"[enh:custom] apply chest={EnhancementSettings.OnOff(chest)} hips={EnhancementSettings.OnOff(hips)} " +
                    $"why={why} records={_records.Count} [{DumpAll()}]");
    }

    // ================================================================ remote bodies (stage 3A)
    //
    // The observer applies the WEARER's two booleans to the wearer's remote body with the same
    // per-model profile machinery and the same writer keys as the local body. Only the search root
    // differs: a remote body's bones live under the remote model, never under the local one.

    internal static void ApplyExternal(IHostContext log, ModelSpec spec, Renderer r, Transform searchRoot,
                                       bool chest, bool hips, string why)
    {
        ApplyOne(log, spec, r, chest, hips, why, rigid: spec != null && spec.Kind == ModelKind.Rigid,
                 searchRoot, external: true);
    }

    // Drop one remote body's record and put its bones/mesh back before the body is destroyed or
    // rebuilt. Silent when there is nothing recorded for that renderer.
    internal static void ReleaseExternal(IHostContext log, Renderer r, string why)
    {
        if (r == null) return;
        int key = KeyOf(r);
        if (!_records.TryGetValue(key, out var rec)) return;
        if (!rec.External) return;
        Release(log, rec, why);
        _records.Remove(key);
        log.LogInfo($"[enh:custom] external release key={key} '{rec.Slug}' why={why} records={_records.Count}");
    }

    internal static int ExternalRecordCount
    {
        get { int n = 0; foreach (var kv in _records) if (kv.Value != null && kv.Value.External) n++; return n; }
    }

    internal static void ReleaseAllExternal(IHostContext log, string why)
    {
        var keys = new List<int>();
        foreach (var kv in _records) if (kv.Value != null && kv.Value.External) keys.Add(kv.Key);
        foreach (var k in keys)
        {
            if (!_records.TryGetValue(k, out var rec)) continue;
            Release(log, rec, why);
            _records.Remove(k);
        }
        if (keys.Count > 0) log.LogInfo($"[enh:custom] external release-all ({why}) released={keys.Count} records={_records.Count}");
    }

    private static void Reconcile(IHostContext log)
    {
        // A body (re)built after the last apply: pick it up without a toggle. Detects exactly the
        // case the lease/apply code cannot see - the renderer pointer changing under a live slug.
        var spec = SahurModel.LiveSpec;
        var r = SahurModel.LiveRenderer;
        if (spec != null && r != null && !_records.ContainsKey(KeyOf(r)))
            ApplyOne(log, spec, r, EnhancementSettings.ChestEnabled, EnhancementSettings.HipsEnabled, "body-built", true);
        var rspec = SahurRig.LiveSpec;
        var rr = SahurRig.LiveRenderer;
        if (rspec != null && rr != null && !_records.ContainsKey(KeyOf(rr)))
            ApplyOne(log, rspec, rr, EnhancementSettings.ChestEnabled, EnhancementSettings.HipsEnabled, "body-built", false);
    }

    internal static void ReleaseAll(IHostContext log)
    {
        foreach (var kv in _records) Release(log, kv.Value, "release-all");
        _records.Clear();
    }

    private static void Release(IHostContext log, Record rec, string why)
    {
        if (rec == null) return;
        if (rec.Original != null && rec.Mesh != null && rec.Mesh.vertices != null)
        {
            try { rec.Mesh.vertices = ToArray(rec.Original); } catch { }
        }
        int restored = 0;
        foreach (var b in rec.Bones)
        {
            try { if (b.T != null) { b.T.localScale = b.Scale; restored++; } } catch { }
        }
        log.LogInfo($"[enh:custom] release '{rec.Slug}' (why={why}): {restored} bone scale(s), " +
                    $"{(rec.Original != null ? rec.Original.Length : 0)} vertex(es) back to the original");
    }

    private static int KeyOf(Renderer r) { try { return (int)r.Pointer.ToInt64(); } catch { return 0; } }

    private static void ApplyOne(IHostContext log, ModelSpec spec, Renderer r, bool chest, bool hips, string why, bool rigid,
                                 Transform searchRootOverride = null, bool external = false)
    {
        if (spec == null || r == null) return;
        var profile = ModelEnhancementProfiles.ForSpec(spec);
        int key;
        key = KeyOf(r);

        // Address reuse is not identity: a rebuilt body can land on the pointer of a released record.
        // Treat "same key, different live object" as a different body, never as the recorded one.
        if (_records.TryGetValue(key, out var stale) && stale != null &&
            stale.RendererPointer != IntPtr.Zero && stale.RendererPointer != r.Pointer)
        {
            Release(log, stale, "pointer-reused");
            _records.Remove(key);
        }

        if (!_records.TryGetValue(key, out var rec))
        {
            rec = new Record
            {
                Slug = spec.Slug, R = r, ChestMode = profile.Chest, HipMode = profile.Hips,
                External = external, SearchRoot = searchRootOverride, RendererPointer = r.Pointer,
                Rigid = rigid,
            };
            try
            {
                var smr = r.TryCast<SkinnedMeshRenderer>();
                rec.Mesh = smr != null ? smr.sharedMesh : null;
            }
            catch { }
            if (rec.Mesh != null)
            {
                try
                {
                    var v = rec.Mesh.vertices;
                    rec.Original = new Vector3[v.Length];
                    float min = float.MaxValue, max = float.MinValue;
                    for (int i = 0; i < v.Length; i++)
                    {
                        rec.Original[i] = v[i];
                        if (v[i].y < min) min = v[i].y;
                        if (v[i].y > max) max = v[i].y;
                    }
                    rec.HeightMin = min;
                    rec.HeightMax = max;
                }
                catch (Exception e) { log.LogWarning($"[enh:custom] '{spec.Slug}' vertex snapshot failed: {e.Message}"); rec.Original = null; }
            }
            // Bone snapshot: the profile's chest / hip names, resolved in the IMPORTED skeleton's own
            // bone array (`SahurRig`'s `SB_<bake name>` transforms, i.e. the rig lease's `Bones`) and
            // nowhere else.
            //
            // This used to be an exact-name search over the live player-model subtree, which also
            // contains the GAME's skeleton. The UE-named bakes then resolved profile names to game
            // bones: Nordbo's spine_03 / pelvis scaled the hidden native body (nothing visible, and a
            // second writer competing with Proportions) while the imported bones stayed at 1.0. The
            // array used now holds nothing but imported transforms, so the native skeleton is not
            // reachable from this path by construction - there is no traversal and no search root,
            // only this array. A name missing from it is a logged miss, never a native fallback.
            BindImported(log, spec, profile, rec, rigid ? null : SahurRig.ImportedBones(r));
            _records[key] = rec;
            log.LogInfo($"[enh:custom] '{spec.Slug}' profiled{(rec.External ? " (remote)" : "")}: chest={rec.ChestMode}/{profile.ChestScale:F2} " +
                        $"hips={rec.HipMode}/{profile.HipScale:F2} bones=[{BoneNames(rec, profile.ChestBones)}|{BoneNames(rec, profile.HipBones)}] " +
                        $"verts={(rec.Original != null ? rec.Original.Length : 0)} height={(rec.HeightMax - rec.HeightMin):F3} " +
                        $"profile='{(ModelEnhancementProfiles.HasProfile(spec.Slug) ? "table" : "fallback")}' ({profile.Note})");
        }

        rec.ChestOn = chest;
        rec.HipsOn = hips;
        rec.Applies++;

        // A body rebuilt onto a reused renderer pointer has a NEW imported bone array: the record
        // follows the lease's array instead of holding transforms that no longer exist.
        if (!rigid)
        {
            var live = SahurRig.ImportedBones(r);
            if (!ReferenceEquals(rec.BonesFrom, live))
            {
                log.LogInfo($"[enh:custom] '{rec.Slug}' rebinding: the imported skeleton under this renderer is a new build");
                BindImported(log, spec, profile, rec, live);
            }
        }

        // Bone mechanism first: it covers the sub-cases where the mesh must NOT be touched. One
        // writer only: WriteBones touches the imported transforms this record resolved and nothing
        // else, so with an empty list (a miss) it writes exactly zero bones.
        int bonesWritten = 0;
        if (rec.ChestMode == EnhanceMode.RiggedBones)
            bonesWritten += WriteBones(rec, profile.ChestBones, chest ? profile.ChestScale : 1f);
        if (rec.HipMode == EnhanceMode.RiggedBones)
            bonesWritten += WriteBones(rec, profile.HipBones, hips ? profile.HipScale : 1f);

        bool deform = (rec.ChestMode == EnhanceMode.RestDeform && chest) ||
                      (rec.HipMode == EnhanceMode.RestDeform && hips);
        float maxDelta = 0f;
        if (rec.Original != null && rec.Mesh != null)
        {
            // Always from the immutable original: no read-modify-write, so the Nth apply equals the 1st.
            var v = new Vector3[rec.Original.Length];
            Array.Copy(rec.Original, v, v.Length);
            float h = Mathf.Max(1e-4f, rec.HeightMax - rec.HeightMin);
            for (int i = 0; i < v.Length; i++)
            {
                float t = (v[i].y - rec.HeightMin) / h;
                float s = 1f;
                if (rec.ChestMode == EnhanceMode.RestDeform && chest)
                    s = Mathf.Max(s, BandScale(t, profile.ChestCenterFrac, profile.ChestHalfFrac, profile.ChestScale));
                if (rec.HipMode == EnhanceMode.RestDeform && hips)
                    s = Mathf.Max(s, BandScale(t, profile.HipCenterFrac, profile.HipHalfFrac, profile.HipScale));
                if (Mathf.Abs(s - 1f) < 1e-5f) continue;
                var d = new Vector3(v[i].x * (s - 1f), 0f, v[i].z * (s - 1f));
                v[i] += d;
                float m = Mathf.Sqrt(d.x * d.x + d.z * d.z);
                if (m > maxDelta) maxDelta = m;
            }
            if (deform || rec.ChestOn || rec.HipsOn || rec.Applies > 1)
            {
                try { rec.Mesh.vertices = ToArray(v); } catch (Exception e) { log.LogWarning($"[enh:custom] '{rec.Slug}' vertex write failed: {e.Message}"); }
            }
        }
        rec.MaxDeltaY = maxDelta;

        log.LogInfo($"[enh:custom] '{rec.Slug}'{(rec.External ? " (remote)" : "")} chest={rec.ChestMode}:{(chest ? "on" : "off")} " +
                    $"hips={rec.HipMode}:{(hips ? "on" : "off")} " +
                    $"bonesWritten={bonesWritten} deform={(deform ? 1 : 0)} maxDelta={maxDelta:F4}m apply#{rec.Applies} why={why}");
    }

    // Smooth, bounded radial expansion: 1 at the band centre, falling to 1 at the edges on a cosine.
    private static float BandScale(float t, float center, float half, float scale)
    {
        float d = Mathf.Abs(t - center);
        if (d >= half) return 1f;
        float w = 0.5f + 0.5f * Mathf.Cos(Mathf.PI * d / half);   // 1 at centre, 0 at the edge
        return 1f + (scale - 1f) * w;
    }

    // Resolve the profile's chest / hip names in the IMPORTED bone array and snapshot their scales.
    // Runs once per body, and again only when the lease hands this record a different array (a build
    // that landed on a reused renderer pointer). `imported == null` means there is no imported
    // skeleton for this renderer: every name is then a miss, and the body drops to RestDeform (or,
    // for a rigid bake, keeps the bands it always used - it has no skeleton at all).
    private static void BindImported(IHostContext log, ModelSpec spec, EnhancementProfile profile, Record rec, Transform[] imported)
    {
        rec.Bones.Clear();
        rec.BonesFrom = imported;
        rec.ChestMode = profile.Chest;
        rec.HipMode = profile.Hips;
        if (rec.Rigid) return;      // a rigid bake is 1 bone per piece: the bands are its mechanism

        var miss = new StringBuilder();
        foreach (var name in Concat(profile.ChestBones, profile.HipBones))
        {
            var t = ResolveImported(imported, name, out int index);
            if (t == null)
            {
                if (miss.Length < 160) miss.Append(miss.Length == 0 ? "" : ",").Append(name);
                continue;
            }
            if (rec.Bones.Exists(b => b.T != null && b.T.Pointer.ToInt64() == t.Pointer.ToInt64())) continue;
            rec.Bones.Add(new BoneSnap
            {
                T = t, Scale = t.localScale, Name = name, Object = SafeName(t), Index = index,
            });
        }

        // A rigged model whose profile bones are absent falls back to the rest-space deformation;
        // that decision is recorded per body so the matrix states what actually happened.
        if (rec.ChestMode == EnhanceMode.RiggedBones && !HasBone(rec, profile.ChestBones)) rec.ChestMode = EnhanceMode.RestDeform;
        if (rec.HipMode == EnhanceMode.RiggedBones && !HasBone(rec, profile.HipBones)) rec.HipMode = EnhanceMode.RestDeform;

        // The line that names the bone that will actually be scaled: profile name -> the imported
        // transform's own object name and its index in the lease's array.
        log.LogInfo($"[enh:custom] '{spec.Slug}' imported bones [{ResolvedNames(rec)}] " +
                    $"skeleton={(imported != null ? imported.Length : 0)} miss={(miss.Length == 0 ? "none" : miss.ToString())} " +
                    "(resolved in the lease's imported array; the native skeleton is not searched)");
        if (miss.Length > 0)
            log.LogWarning($"[enh:custom] '{spec.Slug}' bone miss: '{miss}' is not in this model's imported skeleton; " +
                           "no fallback search, so that toggle uses the rest-space band");
    }

    // Profile bone name -> the imported transform carrying it. The array is the rig lease's own
    // (SahurRig.Build creates one `SB_<bake name>` transform per baked bone), so the only thing this
    // can match is the imported model; a miss returns null and the caller records it as a miss.
    private static Transform ResolveImported(Transform[] bones, string name, out int index)
    {
        index = -1;
        if (bones == null || string.IsNullOrEmpty(name)) return null;
        for (int i = 0; i < bones.Length; i++)
        {
            var t = bones[i];
            if (t == null) continue;
            string n = SafeName(t);
            if (n.StartsWith(SahurRig.BonePrefix, StringComparison.OrdinalIgnoreCase))
                n = n.Substring(SahurRig.BonePrefix.Length);
            if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            return t;
        }
        return null;
    }

    private static string SafeName(Transform t)
    {
        try { return (t != null ? t.name : null) ?? "?"; } catch { return "?"; }
    }

    private static string ResolvedNames(Record rec)
    {
        var sb = new StringBuilder();
        foreach (var b in rec.Bones)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(b.Name).Append("->'").Append(b.Object ?? "?").Append("'#").Append(b.Index);
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    private static int WriteBones(Record rec, string[] names, float scale)
    {
        if (names == null) return 0;
        int n = 0;
        foreach (var b in rec.Bones)
        {
            if (b.T == null) continue;
            bool wanted = false;
            foreach (var name in names)
                if (string.Equals(name, b.Name, StringComparison.OrdinalIgnoreCase)) { wanted = true; break; }
            if (!wanted) continue;
            try { b.T.localScale = b.Scale * scale; n++; } catch { }
        }
        return n;
    }

    private static bool HasBone(Record rec, string[] names)
    {
        if (names == null) return false;
        foreach (var b in rec.Bones)
            foreach (var name in names)
                if (string.Equals(name, b.Name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string BoneNames(Record rec, string[] names)
    {
        if (names == null) return "-";
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(name).Append(HasBone(rec, new[] { name }) ? "" : "?");
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    private static IEnumerable<string> Concat(string[] a, string[] b)
    {
        if (a != null) foreach (var x in a) yield return x;
        if (b != null) foreach (var x in b) yield return x;
    }

    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> ToArray(Vector3[] src)
    {
        var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(src.Length);
        for (int i = 0; i < src.Length; i++) arr[i] = src[i];
        return arr;
    }

    // ================================================================== reports

    internal static void Dump(IHostContext log, string why)
    {
        log.LogInfo($"[enh:dump] why={why} {EnhancementSettings.Dump()} custom=[{DumpAll()}] " +
                    $"native rigs={(Proportions.Current != null ? "bound" : "none")}");
        foreach (var kv in _records)
        {
            var r = kv.Value;
            if (r == null) continue;
            log.LogInfo($"[enh:dump] '{r.Slug}' key={kv.Key} chest={r.ChestMode}:{(r.ChestOn ? "on" : "off")} " +
                        $"hips={r.HipMode}:{(r.HipsOn ? "on" : "off")} bones={r.Bones.Count} verts={(r.Original != null ? r.Original.Length : 0)} " +
                        $"applies={r.Applies} lastMaxDelta={r.MaxDeltaY:F4}");
            foreach (var b in r.Bones)
                log.LogInfo($"[enh:dump]   bone '{b.Name}' -> '{b.Object ?? "?"}'#{b.Index} " +
                            $"snapshotScale=({b.Scale.x:F3},{b.Scale.y:F3},{b.Scale.z:F3}) " +
                            $"now=({(b.T != null ? b.T.localScale.x : 0f):F3},{(b.T != null ? b.T.localScale.y : 0f):F3},{(b.T != null ? b.T.localScale.z : 0f):F3})");
        }
    }

    // The measured capability matrix: one line per roster entry with the mechanism it would use and
    // whether the live body is currently responding. The static half is the profile table; the live
    // half is read from the records, so a model that silently failed cannot be claimed as working.
    internal static void Matrix(IHostContext log)
    {
        var all = ModelRoster.All;
        log.LogInfo($"[enh:matrix] roster={all.Length} chest={EnhancementSettings.OnOff(EnhancementSettings.ChestEnabled)} " +
                    $"hips={EnhancementSettings.OnOff(EnhancementSettings.HipsEnabled)} records={_records.Count}");
        for (int i = 0; i < all.Length; i++)
        {
            var s = all[i];
            if (s == null) continue;
            var p = ModelEnhancementProfiles.For(s.Slug);
            var rec = FindRecord(s.Slug);
            log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[enh:matrix] {0,-16} kind={1,-6} plannedChest={2,-11} plannedHips={3,-11} scale={4:F2}/{5:F2} profile={6,-8} measured={7} {8}",
                s.Slug, s.Kind, p.Chest, p.Hips, p.ChestScale, p.HipScale,
                ModelEnhancementProfiles.HasProfile(s.Slug) ? "table" : (p.Chest == EnhanceMode.RiggedBones || p.Hips == EnhanceMode.RiggedBones ? "derived" : "fallback"),
                rec == null ? "not-worn" : $"chest={rec.ChestMode}:{OnOff(rec.ChestOn)},hips={rec.HipMode}:{OnOff(rec.HipsOn)},bones={rec.Bones.Count},verts={(rec.Original != null ? rec.Original.Length : 0)}",
                p.Note));
        }
    }

    private static Record FindRecord(string slug)
    {
        foreach (var kv in _records)
            if (kv.Value != null && string.Equals(kv.Value.Slug, slug, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static string OnOff(bool v) => v ? "on" : "off";

    internal static void Dispose(IHostContext log)
    {
        try { ReleaseAll(log); } catch { }
        _records.Clear();
        EnhancementSettings.Dispose();
    }
}
