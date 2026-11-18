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
    internal int Pelvis3FloatOffset = -1; // 3 floats/frame: full pelvis offset, source model space
    internal string[] Bones;          // LC metarig paths, in pack order
    internal IkCurve IkL;             // baked arm IK target, left
    internal IkCurve IkR;             // baked arm IK target, right
    internal IkCurve LegL;            // baked leg IK target, left (root-relative)
    internal IkCurve LegR;            // baked leg IK target, right
    // Per-clip decision (manifest `armconform <clip> 0|1`, default on): apply the rest-frame arm
    // conform. Off is correct where the clip's arm IK weight is 0 and the authored arm rotations
    // are the visible pose.
    internal bool ArmConform = true;
    // Arms-only arm chain bake: when present, these eight bones are driven by the arms-only
    // skeleton's global deltas (see extract_emotes.AO_ARM_BONES) and its rest is the conform rest.
    internal int ArmAoOffset = -1;
    internal int ArmAoFrames;
    private Dictionary<string, int> _index;

    internal bool HasIk => IkL != null || IkR != null;
    internal bool HasLegIk => LegL != null || LegR != null;
    internal bool HasPelvis3 => Pelvis3FloatOffset >= 0;
    internal bool HasArmAo => ArmAoOffset >= 0;

    internal IkCurve Ik(string side) => side == "L" ? IkL : side == "R" ? IkR : null;
    internal IkCurve Leg(string side) => side == "L" ? LegL : side == "R" ? LegR : null;

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

    // Global rotation delta of arms-only chain slot `slot` at `frame`, relative to that bone's
    // own rest (same form as Delta(), just from the arms-only bake).
    internal Quaternion ArmAoDelta(int slot, int frame)
    {
        frame = Wrap(frame);
        int i = ArmAoOffset + (frame * 8 + slot) * 4;
        return new Quaternion(EmotePack.Data[i], EmotePack.Data[i + 1], EmotePack.Data[i + 2], EmotePack.Data[i + 3]);
    }

    internal float PelvisY(int frame)
    {
        frame = Wrap(frame);
        return EmotePack.Data[PelvisFloatOffset + frame];
    }

    // Full 3-axis pelvis offset in source model space (the source's animated `spine` local
    // position rotated by the metarig root), relative to the clip's rest pelvis.
    internal Vector3 Pelvis3(int frame)
    {
        frame = Wrap(frame);
        if (Pelvis3FloatOffset < 0) return new Vector3(0f, PelvisY(frame), 0f);
        int i = Pelvis3FloatOffset + frame * 3;
        return new Vector3(EmotePack.Data[i], EmotePack.Data[i + 1], EmotePack.Data[i + 2]);
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
    internal int RootPosOffset = -1; // 3 floats per frame: target - chain root (upperarm/thigh)
    internal int RotOffset;      // 4 floats per frame: target global rotation
    internal int WeightOffset;   // 1 float per frame: constraint weight (0 disables IK)
    internal int Frames;
    internal string ParentSrc;   // LC metarig path of the target's parent (spine.003) or "" for root
    internal bool RootRelative;  // true: Pos is relative to the model root (legs), not a bone
    // LC's hint (pole) node, model-root relative, in the same source model space as Pos. Constant
    // per clip; legs only (arms keep the current elbow plane). Zero = no hint baked.
    internal Vector3 Hint;
    internal bool HasHint => Hint.sqrMagnitude > 1e-8f;

    internal Vector3 Pos(int frame)
    {
        int i = PosOffset + frame * 3;
        return new Vector3(EmotePack.Data[i], EmotePack.Data[i + 1], EmotePack.Data[i + 2]);
    }

    internal bool HasRootPos => RootPosOffset >= 0;

    internal Vector3 RootPos(int frame)
    {
        int i = RootPosOffset + frame * 3;
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
internal sealed class EmotePack
{
    internal static float[] Data;
    internal float Fps = 60f;
    internal readonly List<EmoteClip> Clips = new();
    internal readonly Dictionary<string, string> Map = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Quaternion> SrcRestRot = new(StringComparer.Ordinal);
    // arm target bone -> arms-only source path, and the slot order of the `armao` blob
    internal readonly Dictionary<string, string> AoBone = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, int> AoBoneSlot = new(StringComparer.Ordinal);
    internal readonly Quaternion[] AoRestRot = new Quaternion[8];
    internal readonly Vector3[] AoRestPos = new Vector3[8];
    internal readonly Dictionary<string, Vector3> SrcRestPos = new(StringComparer.Ordinal);
    internal string Root;

    internal EmoteClip ByName(string name)
    {
        foreach (var c in Clips)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
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
                    pack.Clips.Add(c);
                }
                else if (t[0] == "pelvis3" && t.Length >= 4)
                {
                    var c = pack.ByName(t[1]);
                    if (c == null) { log.LogWarning($"[emote] PORT_PACK_INVALID pelvis3 for unknown clip '{t[1]}'"); continue; }
                    c.Pelvis3FloatOffset = (int)F(t[2]);
                }
                else if (t[0] == "ik" && t.Length >= 8)
                {
                    // ik <clip> <L|R> <posFloat> <rotFloat> <wFloat> <frames> <parentSrcPath>
                    var c = pack.ByName(t[1]);
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
                else if (t[0] == "ikroot" && t.Length >= 5)
                {
                    // ikroot <clip> <L|R> <offset> <frames>
                    var c = pack.ByName(t[1]);
                    if (c == null) { log.LogWarning($"[emote] PORT_PACK_INVALID ikroot for unknown clip '{t[1]}'"); continue; }
                    var ik = t[2] == "L" ? c.IkL : t[2] == "R" ? c.IkR : null;
                    if (ik != null) ik.RootPosOffset = (int)F(t[3]);
                }
                else if (t[0] == "legroot" && t.Length >= 5)
                {
                    var c = pack.ByName(t[1]);
                    if (c == null) { log.LogWarning($"[emote] PORT_PACK_INVALID legroot for unknown clip '{t[1]}'"); continue; }
                    var ik = t[2] == "L" ? c.LegL : t[2] == "R" ? c.LegR : null;
                    if (ik != null) ik.RootPosOffset = (int)F(t[3]);
                }
                else if (t[0] == "leghint" && t.Length >= 6)
                {
                    // leghint <clip> <L|R> <x> <y> <z>
                    var c = pack.ByName(t[1]);
                    if (c == null) { log.LogWarning($"[emote] PORT_PACK_INVALID leghint for unknown clip '{t[1]}'"); continue; }
                    var ik = t[2] == "L" ? c.LegL : t[2] == "R" ? c.LegR : null;
                    if (ik != null) ik.Hint = new Vector3(F(t[3]), F(t[4]), F(t[5]));
                }
                else if (t[0] == "aorest" && t.Length >= 9)
                {
                    // aorest <slot> <qx> <qy> <qz> <qw> <px> <py> <pz>  (arms-only rest globals)
                    int slot;
                    if (!int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) || slot < 0 || slot >= 8)
                    { log.LogWarning($"[emote] PORT_PACK_INVALID aorest slot '{t[1]}'"); continue; }
                    pack.AoRestRot[slot] = new Quaternion(F(t[2]), F(t[3]), F(t[4]), F(t[5]));
                    pack.AoRestPos[slot] = new Vector3(F(t[6]), F(t[7]), F(t[8]));
                }
                else if (t[0] == "aobone" && t.Length >= 4)
                {
                    // aobone <slot> <targetBone> <armsOnlySourcePath>
                    int slot;
                    if (!int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) || slot < 0 || slot >= 8)
                    { log.LogWarning($"[emote] PORT_PACK_INVALID aobone slot '{t[1]}'"); continue; }
                    pack.AoBone[t[2]] = t[3];
                    pack.AoBoneSlot[t[2]] = slot;
                }
                else if (t[0] == "armao" && t.Length >= 4)
                {
                    // armao <clip> <offset> <frames>
                    var c = pack.ByName(t[1]);
                    if (c == null) { log.LogWarning($"[emote] PORT_PACK_INVALID armao for unknown clip '{t[1]}'"); continue; }
                    c.ArmAoOffset = (int)F(t[2]);
                    c.ArmAoFrames = (int)F(t[3]);
                    if (c.ArmAoFrames <= 0) { c.ArmAoOffset = -1; log.LogWarning($"[emote] PORT_PACK_INVALID armao frames for '{t[1]}'"); }
                }
                else if (t[0] == "armconform" && t.Length >= 3)
                {
                    var c = pack.ByName(t[1]);
                    if (c == null) { log.LogWarning($"[emote] PORT_PACK_INVALID armconform for unknown clip '{t[1]}'"); continue; }
                    c.ArmConform = F(t[2]) > 0.5f;
                }
                else if (t[0] == "legik" && t.Length >= 7)
                {
                    // legik <clip> <L|R> <posFloat> <rotFloat> <wFloat> <frames>
                    var c = pack.ByName(t[1]);
                    if (c == null) { log.LogWarning($"[emote] PORT_PACK_INVALID legik for unknown clip '{t[1]}'"); continue; }
                    var ik = new IkCurve
                    {
                        PosOffset = (int)F(t[3]), RotOffset = (int)F(t[4]), WeightOffset = (int)F(t[5]),
                        Frames = (int)F(t[6]), ParentSrc = "", RootRelative = true,
                    };
                    if (t[2] == "L") c.LegL = ik;
                    else if (t[2] == "R") c.LegR = ik;
                    else log.LogWarning($"[emote] PORT_PACK_INVALID legik side '{t[2]}'");
                }
                else log.LogWarning($"[emote] PORT_PACK_INVALID unknown line '{line}'");
            }

            int need = 0;
            foreach (var c in pack.Clips)
            {
                need = Math.Max(need, Math.Max(c.RotFloatOffset + c.Frames * c.Bones.Length * 4,
                                                c.PelvisFloatOffset + c.Frames));
                if (c.Pelvis3FloatOffset >= 0) need = Math.Max(need, c.Pelvis3FloatOffset + c.Frames * 3);
                if (c.ArmAoOffset >= 0) need = Math.Max(need, c.ArmAoOffset + c.ArmAoFrames * 8 * 4);
                foreach (var ik in new[] { c.IkL, c.IkR, c.LegL, c.LegR })
                    if (ik != null)
                    {
                        need = Math.Max(need, Math.Max(ik.PosOffset + ik.Frames * 3,
                                     Math.Max(ik.RotOffset + ik.Frames * 4, ik.WeightOffset + ik.Frames)));
                        if (ik.RootPosOffset >= 0)
                            need = Math.Max(need, ik.RootPosOffset + ik.Frames * 3);
                    }
            }
            if (need > Data.Length)
            {
                log.LogError($"[emote] PORT_PACK_INVALID needs {need} floats, {binName} has {Data.Length}");
                return null;
            }
            log.LogInfo($"[emote] pack '{dir}': {pack.Clips.Count} clips, {pack.Map.Count} bone mappings, " +
                        $"{Data.Length} floats, fps={pack.Fps}; clips: {string.Join(",", pack.Clips.ConvertAll(c => c.Name).ToArray())}");
            foreach (var c in pack.Clips)
                if (c.HasIk || c.HasLegIk || c.HasPelvis3)
                    log.LogInfo($"[emote] ik clip '{c.Name}': pelvis3={(c.HasPelvis3 ? "yes" : "no")} " +
                            $"armao={(c.HasArmAo ? $"{c.ArmAoFrames}f" : "no")} " +
                                $"arm L={(c.IkL == null ? "-" : $"w[{c.IkL.Weight(0):F2}..{MaxWeight(c.IkL):F2}] {c.IkL.Frames}f")} " +
                                $"R={(c.IkR == null ? "-" : $"w[{c.IkR.Weight(0):F2}..{MaxWeight(c.IkR):F2}] {c.IkR.Frames}f")} " +
                                $"leg L={(c.LegL == null ? "-" : $"w[{c.LegL.Weight(0):F2}..{MaxWeight(c.LegL):F2}] {c.LegL.Frames}f")} " +
                                $"R={(c.LegR == null ? "-" : $"w[{c.LegR.Weight(0):F2}..{MaxWeight(c.LegR):F2}] {c.LegR.Frames}f")}");
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
