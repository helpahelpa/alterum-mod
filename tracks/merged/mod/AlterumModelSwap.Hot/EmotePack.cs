using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// One baked Lethal Company emote: 60 Hz source-bone rotation deltas plus a pelvis vertical offset.
internal sealed class EmoteClip
{
    internal string Name;
    internal float Duration;
    internal bool Loop;
    internal int Frames;
    internal float Fps;
    internal int RotFloatOffset;      // index into EmotePack.Data (quaternions, frame-major)
    internal int PelvisFloatOffset;   // index into EmotePack.Data (one float per frame)
    internal string[] Bones;          // LC metarig paths, in pack order
    internal IkCurve IkL;             // baked arm IK target, left
    internal IkCurve IkR;             // baked arm IK target, right
    private Dictionary<string, int> _index;

    internal bool HasIk => IkL != null || IkR != null;

    internal IkCurve Ik(string side) => side == "L" ? IkL : side == "R" ? IkR : null;

    internal int IndexOf(string srcPath)
    {
        if (_index == null)
        {
            _index = new Dictionary<string, int>(Bones.Length, StringComparer.Ordinal);
            for (int i = 0; i < Bones.Length; i++) _index[Bones[i]] = i;
        }
        return _index.TryGetValue(srcPath, out var i2) ? i2 : -1;
    }

    internal Quaternion Delta(int frame, int boneIndex)
    {
        if (boneIndex < 0) return Quaternion.identity;
        frame = Wrap(frame);
        int i = RotFloatOffset + (frame * Bones.Length + boneIndex) * 4;
        return new Quaternion(EmotePack.Data[i], EmotePack.Data[i + 1], EmotePack.Data[i + 2], EmotePack.Data[i + 3]);
    }

    internal float PelvisY(int frame)
    {
        frame = Wrap(frame);
        return EmotePack.Data[PelvisFloatOffset + frame];
    }

    private int Wrap(int frame)
    {
        if (Loop && Frames > 1) frame %= (Frames - 1);
        if (frame < 0) frame = 0;
        if (frame >= Frames) frame = Frames - 1;
        return frame;
    }
}

// Baked arm IK target for one side of one clip: the target's global position *relative to its
// parent bone* (spine.003), its global rotation, and the clip's own ChainIKConstraint weight.
// All in LC source model space, one entry per baked frame.
internal sealed class IkCurve
{
    internal int PosOffset;      // 3 floats per frame: target - parent, source model space
    internal int RotOffset;      // 4 floats per frame: target global rotation
    internal int WeightOffset;   // 1 float per frame: ChainIKConstraint weight (0 disables IK)
    internal int Frames;
    internal string ParentSrc;   // LC metarig path of the target's parent (spine.003)

    internal Vector3 Pos(int frame)
    {
        int i = PosOffset + frame * 3;
        return new Vector3(EmotePack.Data[i], EmotePack.Data[i + 1], EmotePack.Data[i + 2]);
    }

    internal Quaternion Rot(int frame)
    {
        int i = RotOffset + frame * 4;
        return new Quaternion(EmotePack.Data[i], EmotePack.Data[i + 1], EmotePack.Data[i + 2], EmotePack.Data[i + 3]);
    }

    internal float Weight(int frame) => EmotePack.Data[WeightOffset + frame];
}

// utf-8 text manifest + one flat float32 blob. No JSON/zip dependency inside IL2CPP.
//
// The manifest still describes all 12 clips and the blob still holds all of them at their original
// offsets (nothing is repacked). `Clips` is the *exposed* catalogue only: the EmoteCatalog
// allowlist, in its order. The rest is parsed, validated and then has no lookup path, so a removed
// clip cannot be started, enumerated or published from anywhere in this assembly.
internal sealed class EmotePack
{
    internal static float[] Data;
    internal float Fps = 60f;
    internal readonly List<EmoteClip> Clips = new();          // exposed: allowlist, catalogue order
    private readonly List<EmoteClip> _parsed = new();          // every clip the manifest describes
    internal readonly Dictionary<string, string> Map = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Quaternion> SrcRestRot = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Vector3> SrcRestPos = new(StringComparer.Ordinal);
    internal string Root;

    // The one clip lookup the hot module has. An id outside the allowlist has no clip at all, so
    // PosePlayer.Start, RemoteEmotes and the request file cannot reach a hidden one even by name.
    internal EmoteClip ByName(string name)
    {
        int i = IndexOf(name);
        return i < 0 ? null : Clips[i];
    }

    // Index in catalogue order (the same index space EmoteRuntime.IdAt uses), or -1.
    internal int IndexOf(string name)
    {
        if (!EmoteCatalog.Contains(name)) return -1;
        for (int i = 0; i < Clips.Count; i++)
            if (string.Equals(Clips[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // Parse-time lookup against everything the manifest describes: the `ik` lines are resolved
    // before the allowlist projection, so the hidden clips are still fully validated.
    private EmoteClip ByNameParsed(string name)
    {
        for (int i = 0; i < _parsed.Count; i++)
            if (string.Equals(_parsed[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _parsed[i];
        return null;
    }

    private bool IsExposed(string name)
    {
        for (int i = 0; i < Clips.Count; i++)
            if (string.Equals(Clips[i].Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static EmotePack TryLoad(IHostContext log)
    {
        var pack = new EmotePack();
        try
        {
            // The pack sits beside the host assembly (BepInEx/plugins/AlterumEmotes/).
            string hostDir = null;
            try
            {
                var loc = typeof(AlterumModelSwap.Contracts.IHostContext).Assembly.Location;
                if (!string.IsNullOrEmpty(loc)) hostDir = Path.GetDirectoryName(loc);
            }
            catch { }

            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(hostDir)) candidates.Add(Path.Combine(hostDir, "AlterumEmotes"));
            try
            {
                var selfDir = Path.GetDirectoryName(typeof(EmotePack).Assembly.Location);
                if (!string.IsNullOrEmpty(selfDir)) candidates.Add(Path.Combine(selfDir, "AlterumEmotes"));
            }
            catch { }

            string dir = null;
            foreach (var c in candidates)
                if (File.Exists(Path.Combine(c, "manifest.txt"))) { dir = c; break; }
            if (dir == null)
            {
                log.LogWarning($"[emote] no pack found; looked in: {string.Join(", ", candidates)}");
                return null;
            }
            pack.Root = dir;
            var lines = File.ReadAllLines(Path.Combine(dir, "manifest.txt"));
            string binName = "emote.bin";
            var pending = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("file ", StringComparison.Ordinal)) binName = line.Substring(5).Trim();
                else if (line.StartsWith("fps ", StringComparison.Ordinal)) pack.Fps = F(line.Substring(4));
                else if (line.StartsWith("version", StringComparison.Ordinal)) { }
                else pending.Add(line);
            }

            var binPath = Path.Combine(dir, binName);
            var bytes = File.ReadAllBytes(binPath);
            if (bytes.Length % 4 != 0) { log.LogError($"[emote] PORT_PACK_INVALID {binName} size={bytes.Length} not a float array"); return null; }
            Data = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, Data, 0, bytes.Length);

            foreach (var line in pending)
            {
                var t = line.Split(' ');
                if (t[0] == "map" && t.Length >= 3) pack.Map[t[1]] = t[2];
                else if (t[0] == "rest" && t.Length >= 9)
                {
                    pack.SrcRestRot[t[1]] = new Quaternion(F(t[2]), F(t[3]), F(t[4]), F(t[5]));
                    pack.SrcRestPos[t[1]] = new Vector3(F(t[6]), F(t[7]), F(t[8]));
                }
                else if (t[0] == "clip" && t.Length >= 9)
                {
                    var c = new EmoteClip
                    {
                        Name = t[1], Duration = F(t[2]), Loop = F(t[3]) > 0.5f, Frames = (int)F(t[4]),
                        Fps = F(t[5]), RotFloatOffset = (int)F(t[6]), PelvisFloatOffset = (int)F(t[7]),
                    };
                    c.Bones = new string[t.Length - 8];
                    Array.Copy(t, 8, c.Bones, 0, c.Bones.Length);
                    pack._parsed.Add(c);
                }
                else if (t[0] == "ik" && t.Length >= 8)
                {
                    // ik <clip> <L|R> <posFloat> <rotFloat> <wFloat> <frames> <parentSrcPath>
                    var c = pack.ByNameParsed(t[1]);
                    if (c == null) { log.LogWarning($"[emote] PORT_PACK_INVALID ik for unknown clip '{t[1]}'"); continue; }
                    var ik = new IkCurve
                    {
                        PosOffset = (int)F(t[3]), RotOffset = (int)F(t[4]), WeightOffset = (int)F(t[5]),
                        Frames = (int)F(t[6]), ParentSrc = t[7],
                    };
                    if (t[2] == "L") c.IkL = ik;
                    else if (t[2] == "R") c.IkR = ik;
                    else log.LogWarning($"[emote] PORT_PACK_INVALID ik side '{t[2]}'");
                }
                else log.LogWarning($"[emote] PORT_PACK_INVALID unknown line '{line}'");
            }

            int need = 0;
            foreach (var c in pack._parsed)
            {
                need = Math.Max(need, Math.Max(c.RotFloatOffset + c.Frames * c.Bones.Length * 4,
                                                c.PelvisFloatOffset + c.Frames));
                foreach (var ik in new[] { c.IkL, c.IkR })
                    if (ik != null)
                        need = Math.Max(need, Math.Max(ik.PosOffset + ik.Frames * 3,
                                     Math.Max(ik.RotOffset + ik.Frames * 4, ik.WeightOffset + ik.Frames)));
            }
            if (need > Data.Length)
            {
                log.LogError($"[emote] PORT_PACK_INVALID needs {need} floats, {binName} has {Data.Length}");
                return null;
            }
            log.LogInfo($"[emote] pack '{dir}': {pack._parsed.Count} clips parsed, {pack.Map.Count} bone mappings, " +
                        $"{Data.Length} floats, fps={pack.Fps} (blob is not repacked)");

            // ---- allowlist projection ----------------------------------------------------
            // Everything was parsed and validated above. Only the fixed catalogue is exposed, in
            // EmoteCatalog order; a survivor the manifest does not carry is a hard error for that
            // id and never a reason to offer a different clip.
            pack.Clips.Clear();
            foreach (var id in EmoteCatalog.Ids)
            {
                var c = pack.ByNameParsed(id);
                if (c == null) { log.LogError($"[emote:catalog] '{id}' is missing from this pack; it cannot be offered"); continue; }
                if (pack.IsExposed(id)) continue;
                pack.Clips.Add(c);
            }
            var hidden = new List<string>();
            foreach (var c in pack._parsed) if (!pack.IsExposed(c.Name)) hidden.Add(c.Name);
            log.LogInfo($"[emote:catalog] {EmoteCatalog.Describe(pack)} hidden=[{string.Join(",", hidden.ToArray())}] " +
                        $"parsed={pack._parsed.Count} source='{dir}'");
            if (pack.Clips.Count == 0)
            {
                log.LogError("[emote] PORT_PACK_INVALID the allowlist exposes no clip from this pack; feature idle");
                return null;
            }
            foreach (var c in pack.Clips)
                if (c.HasIk)
                    log.LogInfo($"[emote] ik clip '{c.Name}': L={(c.IkL == null ? "-" : $"w[{c.IkL.Weight(0):F2}..{MaxWeight(c.IkL):F2}] {c.IkL.Frames}f")} " +
                                $"R={(c.IkR == null ? "-" : $"w[{c.IkR.Weight(0):F2}..{MaxWeight(c.IkR):F2}] {c.IkR.Frames}f")}");
            return pack;
        }
        catch (Exception e) { log.LogError($"[emote] PORT_PACK_INVALID {e.Message}"); return null; }
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static float MaxWeight(IkCurve ik)
    {
        float m = 0f;
        for (int i = 0; i < ik.Frames; i++) m = Math.Max(m, ik.Weight(i));
        return m;
    }
}
