using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// "Can a player standing in the lobby actually see it?" for the Reel of Fortune.
//
// The placement itself is geometry-driven (floor raycast + headroom), which says nothing about the
// game camera. These probes answer the question directly, from the camera the player is looking
// through, and are cheap enough to run on demand:
//
//   [prop:view]     camera pose, distance/angle, viewport point of the bounds, corner count inside,
//                   and a one-word verdict (VISIBLE / OUT_OF_FRAME(side) / BEHIND_CAMERA / OCCLUDED)
//   [prop:player]   where the body is and which way it faces relative to the prop
//   [prop:occl]     which collider (if any) is between the camera and the prop's silhouette samples
//   [prop:inside]   whether world geometry sits inside the prop's own bounding volume
//
// An unattended `prop.request` file (in ScreensDir) re-runs them, moves the prop, or captures:
//
//   probe
//   move x=1.75 z=-9.75 yaw=-12
//   shot tag=reel_check
//
// Nothing here runs unless the prop exists; every entry point swallows its own exceptions.
internal static class PropView
{
    internal const string RequestFile = "prop.request";

    // The request file is polled a few times a second, not every frame.
    private const float PollSeconds = 0.35f;
    private static float _nextPoll;

    // Points across the face that looks at the camera: 3 columns x 5 rows.
    private static readonly float[] Cols = { -0.42f, 0f, 0.42f };
    private static readonly float[] Rows = { 0.16f, 0.95f, 1.55f, 2.20f, 2.86f };

    // ---- public entry points ---------------------------------------------------------------

    internal static void Probe(IHostContext log, LobbyPropRuntime rt, string why)
    {
        if (log == null || rt == null || rt.Root == null) return;
        try
        {
            var cam = ScreenShot.FindGameCamera(log);
            Bounds b = rt.Bounds;
            if (b.size.sqrMagnitude < 0.0001f) b = WorldBounds(rt);

            Vector3 centre = b.center;
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            Vector3 fwd = cam != null ? cam.transform.forward : Vector3.zero;

            LogCamera(log, cam, why);
            LogPlayer(log, eye, centre);

            float dist = Vector3.Distance(eye, centre);
            float angle = cam != null ? Vector3.Angle(fwd, centre - eye) : -1f;
            Vector3 vp = cam != null ? cam.WorldToViewportPoint(centre) : Vector3.zero;

            // The eight world-AABB corners plus the centre: how much of the prop is on screen.
            int inside = 0, total = 0;
            float minX = 9f, maxX = -9f, minY = 9f, maxY = -9f, minZ = 9f, maxZ = -9f;
            var v = b.center; var e = b.extents;
            for (int i = 0; i < 8; i++)
            {
                var p = v + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                SampleViewport(cam, p, ref inside, ref total, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
            }
            SampleViewport(cam, centre, ref inside, ref total, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);

            string side = Side(vp);
            log.LogInfo($"[prop:view] why={why} dist={dist:F2} angle={angle:F1}deg viewport={V(vp)} " +
                        $"inside={inside}/{total} screen x=[{minX:F2},{maxX:F2}] y=[{minY:F2},{maxY:F2}] depth=[{minZ:F2},{maxZ:F2}] side={side}");

            // Occlusion from the camera to the silhouette samples on the face turned toward it.
            int blocked = 0, samples = 0;
            string firstBlock = "none";
            float firstBlockD = 0f;
            var census = new Dictionary<string, int>();
            var rowText = new StringBuilder();
            if (cam != null && vp.z > 0f)
            {
                var pts = FaceSamples(b, eye);
                int perRow = Cols.Length;
                for (int i = 0; i < pts.Count; i++)
                {
                    float d;
                    string what = FirstHit(eye, pts[i], rt, out d);
                    samples++;
                    if (i % perRow == 0) rowText.Append($"row{i / perRow}[");
                    rowText.Append(what == null ? "." : "#");
                    if (i % perRow == perRow - 1) rowText.Append($"@{pts[i].y:F2}] ");
                    if (what == null) continue;
                    blocked++;
                    int n;
                    census.TryGetValue(what, out n);
                    census[what] = n + 1;
                    if (firstBlock == "none" || d < firstBlockD) { firstBlock = what; firstBlockD = d; }
                }
            }
            var top = new StringBuilder();
            foreach (var kv in Sorted(census, 4)) top.Append($"'{kv.Key}'x{kv.Value} ");
            log.LogInfo($"[prop:occl] eye->prop blocked={blocked}/{samples} nearest='{firstBlock}' d={firstBlockD:F2}m " +
                        $"propDist={dist:F2}m blockers=[{top}] rows[{rowText}](# = blocked)");

            // Geometry inside the prop's own volume: rays along each axis, from just outside inward.
            log.LogInfo($"[prop:inside] {InsideReport(rt, b)}");

            string verdict;
            if (cam == null) verdict = "NO_CAMERA";
            else if (vp.z <= 0f) verdict = "BEHIND_CAMERA";
            else if (inside == 0) verdict = "OUT_OF_FRAME(" + side + ")";
            else if (blocked > 0 && blocked >= (samples + 1) / 2) verdict = "OCCLUDED('" + firstBlock + "'@" + firstBlockD.ToString("F2") + ")";
            else if (blocked > 0) verdict = "PARTLY_OCCLUDED(" + blocked + "/" + samples + " '" + firstBlock + "')";
            else verdict = "VISIBLE";
            log.LogInfo($"[prop:view] verdict={verdict}");
        }
        catch (Exception ex) { log.LogWarning($"[prop:view] probe failed: {ex.Message}"); }
    }

    // Grid-scan the floor in front of the game camera and score each spot on the things that
    // actually matter for "can the player see it": sign-face visibility from the camera, angle off
    // the camera axis, headroom over the prop's footprint, and clearance around it.
    // Logs the best candidates; the winner is then applied with Move().
    internal static void Scan(IHostContext log, LobbyPropRuntime rt)
    {
        if (log == null) return;
        try
        {
            var cam = ScreenShot.FindGameCamera(log);
            if (cam == null) { log.LogWarning("[prop:scan] no game camera"); return; }
            Vector3 eye = cam.transform.position;
            Vector3 fwd = cam.transform.forward;

            var hits = new List<Candidate>();
            int rejFloor = 0, rejHead = 0, rejClear = 0, rejFace = 0, rejAngle = 0, rejView = 0, grid = 0;
            for (float dxx = -9f; dxx <= 2f; dxx += 0.5f)
            {
                for (float dzz = -9f; dzz <= 2f; dzz += 0.5f)
                {
                    var p = new Vector3(eye.x + dxx, 0f, eye.z + dzz);
                    float flat = Vector2.Distance(new Vector2(eye.x, eye.z), new Vector2(p.x, p.z));
                    if (flat < 2.2f || flat > 7.0f) continue;
                    grid++;
                    float floorY = FloorAt(p.x, p.z);
                    if (float.IsNaN(floorY)) { rejFloor++; continue; }

                    float yaw = Mathf.Atan2(eye.x - p.x, eye.z - p.z) * Mathf.Rad2Deg;
                    float head = HeadroomAt(p.x, p.z, floorY, yaw);
                    if (head < 3.10f) { rejHead++; continue; }
                    float clear = ClearanceAt(p.x, p.z, floorY, yaw);
                    if (clear < 0.75f) { rejClear++; continue; }

                    // Sign face (the art stack is 1.62..2.34, aimed at the camera) must be visible.
                    var dir = new Vector3(Mathf.Sin(yaw * Mathf.Deg2Rad), 0f, Mathf.Cos(yaw * Mathf.Deg2Rad));
                    var right = new Vector3(dir.z, 0f, -dir.x);
                    int seen = 0; float worst = 99f;
                    for (int k = -1; k <= 1; k++)
                    {
                        var pt = p + right * (k * 0.38f) + Vector3.up * 1.95f + dir * 0.2f;
                        float d;
                        string what = FirstHit(eye, pt, rt, out d);
                        if (what == null) seen++;
                        else worst = Mathf.Min(worst, d);
                    }
                    if (seen < 3) { rejFace++; continue; }

                    Vector3 centre = p + Vector3.up * 1.475f;
                    float ang = Vector3.Angle(fwd, centre - eye);
                    if (ang > 26f) { rejAngle++; continue; }
                    Vector3 vp = cam.WorldToViewportPoint(centre);
                    if (vp.z <= 0f || vp.x < 0.12f || vp.x > 0.88f || vp.y < 0.10f || vp.y > 0.90f) { rejView++; continue; }

                    hits.Add(new Candidate
                    {
                        X = p.x, Z = p.z, FloorY = floorY, Yaw = yaw, Head = head, Clear = clear,
                        Seen = seen, Angle = ang, Flat = flat, Vp = vp,
                        Score = 100f - ang * 1.6f - Mathf.Abs(flat - 3.4f) * 2.2f + Mathf.Min(head - 3.10f, 0.6f) * 5f
                    });
                }
            }

            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            log.LogInfo($"[prop:scan] eye={V(eye)} fwd={V(fwd)} fov={cam.fieldOfView:F1} candidates={hits.Count} (clear face, <=26deg, headroom>=3.10, clearance>=0.75)");
            log.LogInfo($"[prop:scan] rejected grid={grid} noFloor={rejFloor} headroom={rejHead} clearance={rejClear} face={rejFace} angle={rejAngle} viewport={rejView}");
            for (int i = 0; i < Mathf.Min(6, hits.Count); i++)
            {
                var c = hits[i];
                log.LogInfo($"[prop:scan] #{i} pos=({c.X:F2},{c.FloorY:F3},{c.Z:F2}) yaw={c.Yaw:F1} score={c.Score:F1} " +
                            $"flat={c.Flat:F2} angle={c.Angle:F1} vp=({c.Vp.x:F2},{c.Vp.y:F2}) head={c.Head:F2} clear={c.Clear:F2}");
            }
            if (hits.Count == 0) log.LogWarning("[prop:scan] no candidate spot in front of the camera");
        }
        catch (Exception e) { log.LogWarning($"[prop:scan] failed: {e.Message}"); }
    }

    private struct Candidate
    {
        public float X, Z, FloorY, Yaw, Head, Clear, Angle, Flat, Score;
        public int Seen;
        public Vector3 Vp;
    }

    // Floor under (x,z) as seen from above eye level; NaN when nothing believable is there.
    private static float FloorAt(float x, float z)
    {
        try
        {
            var from = new Vector3(x, 3.3f, z);
            var rh = Physics.RaycastAll(from, Vector3.down, 8f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; float y = float.NaN;
            for (int i = 0; i < rh.Length; i++)
            {
                var c = rh[i].collider;
                if (c == null || IsPlayerPart(c.transform)) continue;
                if (rh[i].distance < 1.0f) continue;      // overhead collider
                if (rh[i].distance < best) { best = rh[i].distance; y = 3.3f - rh[i].distance; }
            }
            return y;
        }
        catch { return float.NaN; }
    }

    // Lowest upward hit over the prop's footprint (5 points) minus the floor.
    private static float HeadroomAt(float x, float z, float floorY, float yaw)
    {
        float best = 99f;
        var dir = new Vector3(Mathf.Sin(yaw * Mathf.Deg2Rad), 0f, Mathf.Cos(yaw * Mathf.Deg2Rad));
        var right = new Vector3(dir.z, 0f, -dir.x);
        var pts = new[]
        {
            new Vector3(x, 0f, z),
            new Vector3(x, 0f, z) + right * 0.5f, new Vector3(x, 0f, z) - right * 0.5f,
            new Vector3(x, 0f, z) + dir * 0.17f, new Vector3(x, 0f, z) - dir * 0.17f
        };
        for (int i = 0; i < pts.Length; i++)
        {
            try
            {
                var from = new Vector3(pts[i].x, floorY + 0.04f, pts[i].z);
                var rh = Physics.RaycastAll(from, Vector3.up, 6f, ~0, QueryTriggerInteraction.Ignore);
                for (int k = 0; k < rh.Length; k++)
                {
                    var c = rh[k].collider;
                    if (c == null || IsPlayerPart(c.transform)) continue;
                    if (rh[k].distance < 0.10f) continue;
                    float top = floorY + 0.04f + rh[k].distance;
                    if (top < best) best = top;
                }
            }
            catch { }
        }
        return best == 99f ? 8.0f : best;
    }

    // Nearest horizontal obstruction around the prop's footprint, at four heights.
    private static float ClearanceAt(float x, float z, float floorY, float yaw)
    {
        float best = 99f;
        var dir = new Vector3(Mathf.Sin(yaw * Mathf.Deg2Rad), 0f, Mathf.Cos(yaw * Mathf.Deg2Rad));
        var right = new Vector3(dir.z, 0f, -dir.x);
        float[] heights = { 0.35f, 1.20f, 2.00f, 2.80f };
        try
        {
            for (int h = 0; h < heights.Length; h++)
            {
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI * 2f / 8f;
                    var rd = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                    var origin = new Vector3(x, floorY + heights[h], z) + rd * 0.05f;
                    var rh = Physics.RaycastAll(origin, rd, 1.4f, ~0, QueryTriggerInteraction.Ignore);
                    for (int k = 0; k < rh.Length; k++)
                    {
                        var c = rh[k].collider;
                        if (c == null || IsPlayerPart(c.transform)) continue;
                        if (rh[k].distance < 0.02f) continue;
                        if (rh[k].distance < best) best = rh[k].distance;
                    }
                }
            }
        }
        catch { }
        return best == 99f ? 9.0f : best;
    }

    private static List<KeyValuePair<string, int>> Sorted(Dictionary<string, int> d, int take)
    {
        var list = new List<KeyValuePair<string, int>>(d);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        if (list.Count > take) list.RemoveRange(take, list.Count - take);
        return list;
    }

    // Re-anchors the existing prop (all children are local): no rebuild, no new meshes.
    internal static bool Move(IHostContext log, LobbyPropRuntime rt, float x, float z, float yaw)
    {
        if (log == null || rt == null || rt.Root == null) return false;
        try
        {
            float floorY = LobbyPropFactory.GroundY(log, x, z);
            rt.Anchor = new Vector3(x, floorY, z);
            rt.Yaw = yaw;
            rt.Root.transform.position = rt.Anchor;
            rt.Root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            rt.Measure(log);
            LobbyPropFactory.Probes(log, x, z, rt.Anchor);
            log.LogInfo($"[prop:move] pose={rt.Anchor} euler=(0,{yaw:0.##},0) floorY={floorY:F3} " +
                        $"colliders={rt.Root.GetComponentsInChildren(Il2CppType.Of<Collider>()).Length}");
            return true;
        }
        catch (Exception e) { log.LogWarning($"[prop:move] failed: {e.Message}"); return false; }
    }

    // Unattended control file, polled by LobbyPropRuntime.Tick.
    internal static void Poll(IHostContext log, LobbyPropRuntime rt, Action<string> capture)
    {
        if (log == null || rt == null) return;
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;
        string path = null;
        try
        {
            string dir = log.ScreensDir;
            if (string.IsNullOrEmpty(dir)) return;
            path = Path.Combine(dir, RequestFile);
            if (!File.Exists(path)) return;
            string text = File.ReadAllText(path);
            try { File.Delete(path); } catch { }

            bool probe = false;
            bool scan = false;
            bool haveMove = false;
            bool hasX = false, hasZ = false, hasYaw = false;
            float x = 0f, z = 0f, yaw = 0f;
            string tag = null;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                foreach (var rawToken in raw.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string token = rawToken.Trim();
                    if (token.Length == 0) continue;
                    if (token.Equals("probe", StringComparison.OrdinalIgnoreCase)) { probe = true; continue; }
                    if (token.Equals("scan", StringComparison.OrdinalIgnoreCase)) { scan = true; continue; }
                    if (token.Equals("move", StringComparison.OrdinalIgnoreCase)) { haveMove = true; continue; }
                    int eq = token.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = token.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = token.Substring(eq + 1).Trim();
                    if (key == "tag") { tag = val; continue; }
                    float f;
                    if (!float.TryParse(val, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out f)) continue;
                    if (key == "x") { x = f; hasX = true; haveMove = true; }
                    else if (key == "z") { z = f; hasZ = true; haveMove = true; }
                    else if (key == "yaw") { yaw = f; hasYaw = true; haveMove = true; }
                }
            }

            if (scan) Scan(log, rt);
            if (haveMove)
            {
                // Missing keys keep the current anchor.
                float nx = hasX ? x : rt.Anchor.x;
                float nz = hasZ ? z : rt.Anchor.z;
                float nyaw = hasYaw ? yaw : rt.Yaw;
                Move(log, rt, nx, nz, nyaw);
                probe = true;
            }
            if (!string.IsNullOrEmpty(tag))
            {
                if (capture != null) capture(tag);
                else ScreenShot.Capture(log, tag);
            }
            if (probe) Probe(log, rt, "request");
        }
        catch (Exception e)
        {
            log.LogWarning($"[prop:view] request '{path}' failed: {e.Message}");
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    // ---- internals -------------------------------------------------------------------------

    private static void LogCamera(IHostContext log, Camera cam, string why)
    {
        if (cam == null) { log.LogWarning($"[prop:view] why={why} no game camera"); return; }
        var t = cam.transform;
        log.LogInfo($"[prop:view] why={why} camera='{PathOf(t)}' eye={V(t.position)} fwd={V(t.forward)} " +
                    $"fov={cam.fieldOfView:F1} aspect={cam.aspect:F3} px={cam.pixelWidth}x{cam.pixelHeight} " +
                    $"near={cam.nearClipPlane:F2} far={cam.farClipPlane:F0}");
    }

    private static void LogPlayer(IHostContext log, Vector3 eye, Vector3 centre)
    {
        try
        {
            // The netcode root ('LobbyPlayer_Separated | Owner: 0') sits at the origin, so its
            // transform says nothing about where the player is. The body model is what the lobby
            // actually shows, so report that, plus the camera the player sees through.
            Bounds body = default(Bounds);
            bool have = false;
            var rends = SceneCache.Scan(Il2CppType.Of<Renderer>());
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i].TryCast<Renderer>();
                if (r == null) continue;
                var tr = r.transform;
                if (tr == null || !IsPlayerPart(tr)) continue;
                if (!have) { body = r.bounds; have = true; }
                else body.Encapsulate(r.bounds);
            }
            if (!have) { log.LogInfo("[prop:player] no local body renderer in the scene"); return; }
            Vector3 p = body.center;
            float flat = Vector2.Distance(new Vector2(eye.x, eye.z), new Vector2(centre.x, centre.z));
            log.LogInfo($"[prop:player] body centre={V(p)} size=({body.size.x:F2},{body.size.y:F2},{body.size.z:F2}) " +
                        $"feetY={body.min.y:F2} eye={V(eye)} eyeToProp={Vector3.Distance(eye, centre):F2} " +
                        $"eyeToPropFlat={flat:F2} (camera forward and the prop's viewport point are in [prop:view])");
        }
        catch (Exception e) { log.LogWarning($"[prop:player] probe failed: {e.Message}"); }
    }

    // Sample points on the prop's face turned toward the eye.
    private static List<Vector3> FaceSamples(Bounds b, Vector3 eye)
    {
        var list = new List<Vector3>(Cols.Length * Rows.Length + 1);
        Vector3 c = b.center;
        Vector3 d = eye - c;
        bool alongZ = Mathf.Abs(d.z) >= Mathf.Abs(d.x);
        float faceOffset = alongZ ? b.extents.z : b.extents.x;
        float faceSign = alongZ ? Mathf.Sign(d.z) : Mathf.Sign(d.x);
        float minY = b.min.y + 0.02f;
        for (int r = 0; r < Rows.Length; r++)
        {
            for (int k = 0; k < Cols.Length; k++)
            {
                float y = Mathf.Clamp(minY + Rows[r], b.min.y + 0.02f, b.max.y - 0.02f);
                float lateral = Cols[k];
                Vector3 p;
                if (alongZ)
                    p = new Vector3(c.x + lateral, y, c.z + faceSign * faceOffset);
                else
                    p = new Vector3(c.x + faceSign * faceOffset, y, c.z + lateral);
                list.Add(p);
            }
        }
        list.Add(c);
        return list;
    }

    // Nearest non-prop, non-player collider on the segment eye->point. Null when clear.
    private static string FirstHit(Vector3 eye, Vector3 point, LobbyPropRuntime rt, out float dist)
    {
        dist = 0f;
        try
        {
            Vector3 delta = point - eye;
            float len = delta.magnitude;
            if (len < 0.10f) return null;
            var hits = Physics.RaycastAll(eye, delta / len, len - 0.05f, ~0, QueryTriggerInteraction.Ignore);
            string best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i].collider;
                if (c == null) continue;
                var tr = c.transform;
                if (UnderRoot(tr, rt.Root.transform)) continue;
                if (IsPlayerPart(tr)) continue;
                if (hits[i].distance < 0.05f) continue;
                if (hits[i].distance < bestD) { bestD = hits[i].distance; best = PathOf(tr); }
            }
            dist = bestD == float.MaxValue ? 0f : bestD;
            return best;
        }
        catch { return null; }
    }

    // Which world colliders sit inside the prop's own bounding volume, probed along three axes.
    private static string InsideReport(LobbyPropRuntime rt, Bounds b)
    {
        var sb = new StringBuilder();
        try
        {
            var root = rt.Root.transform;
            Report(rt, sb, root, Vector3.up, b.center + new Vector3(0f, b.extents.y + 0.3f, 0f), Vector3.down, b.size.y + 0.6f, "Y");
            Report(rt, sb, root, Vector3.right, b.center + new Vector3(b.extents.x + 0.3f, 0f, 0f), Vector3.left, b.size.x + 0.6f, "X");
            Report(rt, sb, root, Vector3.forward, b.center + new Vector3(0f, 0f, b.extents.z + 0.3f), Vector3.back, b.size.z + 0.6f, "Z");
        }
        catch (Exception e) { sb.Append("probe failed: " + e.Message); }
        return sb.Length == 0 ? "no world collider inside the prop volume" : sb.ToString();
    }

    private static void Report(LobbyPropRuntime rt, StringBuilder sb, Transform root, Vector3 axis, Vector3 from, Vector3 dir, float len, string label)
    {
        var hits = Physics.RaycastAll(from, dir, len, ~0, QueryTriggerInteraction.Ignore);
        int n = 0;
        for (int i = 0; i < hits.Length; i++)
        {
            var c = hits[i].collider;
            if (c == null) continue;
            var tr = c.transform;
            if (UnderRoot(tr, root) || IsPlayerPart(tr)) continue;
            // Only count hits inside the box, not the ones the ray meets after leaving it.
            if (hits[i].point.y < rt.Bounds.min.y - 0.05f || hits[i].point.y > rt.Bounds.max.y + 0.05f) continue;
            if (n++ < 3) sb.Append($"{label}!='{PathOf(tr)}'@{V(hits[i].point)}; ");
        }
    }

    private static Bounds WorldBounds(LobbyPropRuntime rt)
    {
        var b = new Bounds(rt.Anchor + Vector3.up * 1.475f, Vector3.zero);
        bool first = true;
        for (int i = 0; i < rt.Renderers.Count; i++)
        {
            var r = rt.Renderers[i];
            if (r == null) continue;
            if (first) { b = r.bounds; first = false; }
            else b.Encapsulate(r.bounds);
        }
        return b;
    }

    private static void SampleViewport(Camera cam, Vector3 p, ref int inside, ref int total,
                                       ref float minX, ref float maxX, ref float minY, ref float maxY, ref float minZ, ref float maxZ)
    {
        total++;
        if (cam == null) return;
        Vector3 vp = cam.WorldToViewportPoint(p);
        if (vp.x < minX) minX = vp.x; if (vp.x > maxX) maxX = vp.x;
        if (vp.y < minY) minY = vp.y; if (vp.y > maxY) maxY = vp.y;
        if (vp.z < minZ) minZ = vp.z; if (vp.z > maxZ) maxZ = vp.z;
        if (vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f) inside++;
    }

    private static string Side(Vector3 vp)
    {
        if (vp.z <= 0f) return "behind";
        if (vp.x < 0f) return "left";
        if (vp.x > 1f) return "right";
        if (vp.y < 0f) return "below";
        if (vp.y > 1f) return "above";
        return "inside";
    }

    private static bool UnderRoot(Transform t, Transform root)
    {
        int guard = 0;
        while (t != null && guard++ < 16)
        {
            if (t == root) return true;
            t = t.parent;
        }
        return false;
    }

    private static bool IsPlayerPart(Transform t)
    {
        int guard = 0;
        while (t != null && guard++ < 12)
        {
            if (t.name != null && t.name.IndexOf("Owner: 0", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            t = t.parent;
        }
        return false;
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var p = t.name ?? "";
        for (int i = 0; i < 4 && t.parent != null; i++) { t = t.parent; p = (t.name ?? "") + "/" + p; }
        return p;
    }

    private static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";
}
