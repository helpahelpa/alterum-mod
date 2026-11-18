using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using MagicaCloth2;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Runtime control rig: a brand new 3-bone BoneCloth that this mod owns end to end. No prefab, no
// game code, no culling renderers, no owner gating - just MagicaCloth2 building and running it.
// If the leaves of this chain do not fall under a non-zero gravity, MagicaCloth2 is not simulating
// in this build at all, and nothing about the player's body matters.
internal static class ClothControl
{
    internal sealed class Rig
    {
        public GameObject Root;
        public MagicaCloth Cloth;
        public Transform Leaf;
        public Quaternion StartRot;
        public Vector3 StartPos;
        public bool Built;
        public float WarmupUntil;
        public string Last = "";
    }

    internal static Rig Spawn(Transform parent, Vector3 localPos)
    {
        var rig = new Rig();
        var root = new GameObject("AlterumClothControlRig");
        root.hideFlags = HideFlags.DontSave;
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.localPosition = localPos;
        root.transform.localRotation = Quaternion.identity;

        Transform Make(string name, Transform parent, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        var t0 = Make("cc_0", root.transform, Vector3.zero);
        // Horizontal chain: gravity must visibly bend it. A vertical chain would already be in
        // equilibrium and prove nothing.
        var t1 = Make("cc_1", t0, new Vector3(0.12f, 0f, 0f));
        var t2 = Make("cc_2", t1, new Vector3(0.12f, 0f, 0f));
        rig.Root = root;
        rig.Leaf = t2;
        rig.StartRot = t2.localRotation;
        rig.StartPos = t2.localPosition;

        var go = new GameObject("cc_cloth");
        go.transform.SetParent(root.transform, false);
        var mc = go.AddComponent(Il2CppType.Of<MagicaCloth>()).TryCast<MagicaCloth>();
        rig.Cloth = mc;
        if (mc == null) return rig;

        var sd = mc.serializeData;
        sd.clothType = ClothProcess.ClothType.BoneCloth;
        var roots = new Il2CppSystem.Collections.Generic.List<Transform>();
        roots.Add(t0);
        sd.rootBones = roots;
        sd.connectionMode = RenderSetupData.BoneConnectionMode.Line;
        sd.gravity = 1.0f;
        sd.blendWeight = 1.0f;
        sd.animationPoseRatio = 0.0f;
        try { sd.cullingSettings.cameraCullingMode = CullingSettings.CameraCullingMode.Off; } catch { }

        mc.Initialize();
        rig.Built = mc.BuildAndRun();
        rig.WarmupUntil = Time.unscaledTime + 1.0f;
        return rig;
    }

    internal static string Line(Rig rig)
    {
        if (rig == null || rig.Cloth == null) return " control=null";
        var sb = new StringBuilder(" control=");
        try
        {
            var p = rig.Cloth.process;
            sb.Append($"built={rig.Built} valid={(p != null && p.IsValid())} run={(p != null && p.IsRunning())}");
        }
        catch (Exception e) { sb.Append($" <{e.GetType().Name}>"); }
        try
        {
            sb.Append($" leafRotDev={Quaternion.Angle(rig.StartRot, rig.Leaf.localRotation):F3}deg" +
                      $" leafPosDev={(rig.Leaf.localPosition - rig.StartPos).magnitude * 1000f:F2}mm" +
                      $" leafLocal={rig.Leaf.localPosition.x:F3}/{rig.Leaf.localPosition.y:F3}/{rig.Leaf.localPosition.z:F3}" +
                      $" rootLocal={rig.Root.transform.localPosition.x:F2}/{rig.Root.transform.localPosition.y:F2}/{rig.Root.transform.localPosition.z:F2}");
        }
        catch (Exception e) { sb.Append($" <leaf {e.GetType().Name}>"); }
        try { sb.Append(ClothInternals.Proc(rig.Cloth)); } catch { }
        return sb.ToString();
    }

    internal static void Dispose(Rig rig)
    {
        if (rig == null) return;
        try { if (rig.Root != null) UnityEngine.Object.Destroy(rig.Root); } catch { }
    }
}
