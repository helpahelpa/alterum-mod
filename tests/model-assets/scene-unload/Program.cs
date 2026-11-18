// Contract probe: link the real ownership helper and cache; model the Unity native sweeper.
// Not a substitute for an actual leave/rejoin and image assertions in Unity.
using System;
using System.Collections.Generic;
using AlterumModelSwap.Hot;
using U = UnityEngine;

namespace UnityEngine
{
    [Flags] public enum HideFlags { None = 0, HideInHierarchy = 1, DontUnloadUnusedAsset = 32 }
    public class Object
    {
        internal static readonly List<Object> All = new();
        public HideFlags hideFlags;
        internal bool Dead;
        internal int DestroyCount;
        public Object() { All.Add(this); }
        public static void Destroy(Object obj) { obj.Dead = true; obj.DestroyCount++; }
        internal static void UnloadUnusedAssets()
        {
            foreach (var obj in All)
                if (!obj.Dead && (obj.hideFlags & HideFlags.DontUnloadUnusedAsset) == 0) obj.Dead = true;
        }
    }
    public class Mesh : Object { }
    public class Material : Object { }
    public class Texture2D : Object { }
}

static class Program
{
    sealed class Asset
    {
        internal readonly U.Mesh Mesh = ModelAssetLifetime.Own(new U.Mesh());
        internal readonly U.Texture2D Texture = ModelAssetLifetime.Own(new U.Texture2D());
        internal readonly U.Material[] Materials = { ModelAssetLifetime.Own(new U.Material()), ModelAssetLifetime.Own(new U.Material()) };
        internal bool Alive => !Mesh.Dead && !Texture.Dead && !Materials[0].Dead && !Materials[1].Dead;
        internal void Destroy()
        {
            U.Object.Destroy(Mesh); U.Object.Destroy(Texture);
            foreach (var m in Materials) U.Object.Destroy(m);
        }
    }
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static void Main()
    {
        // Red control: a managed reference/lease alone cannot prevent Unity unloading.
        var unprotected = new U.Material();
        U.Object.UnloadUnusedAssets();
        Check(unprotected.Dead, "fixture did not reproduce native sweep of a managed-only object");

        var cache = new ModelAssetCache<Asset>(2, a => a.Destroy());
        Asset localAsset, remoteAsset;
        using (var local = cache.Acquire("local-sniper", () => new Asset()))
        using (var remote = cache.Acquire("remote-peter", () => new Asset()))
        { localAsset = local.Value; remoteAsset = remote.Value; }
        // No scene renderer owns either asset now: menu/scene transition before next lobby.
        U.Object.UnloadUnusedAssets();
        Check(localAsset.Alive && remoteAsset.Alive, "scene unload destroyed idle cache materials; rejoin would be magenta");
        using (var local = cache.Acquire("local-sniper", () => throw new Exception("unexpected reload")))
        using (var remote = cache.Acquire("remote-peter", () => throw new Exception("unexpected reload")))
        {
            Check(local.Value.Alive && remote.Value.Alive, "rejoin reused dead resources");
            U.Object.UnloadUnusedAssets();
            Check(remote.Value.Alive, "native sweep destroyed leased remote body resources");
            cache.Dispose();
            Check(localAsset.Alive && remoteAsset.Alive, "retirement destroyed live resources");
        }
        Check(!localAsset.Alive && !remoteAsset.Alive, "protected resources leaked after final release");
        Check(remoteAsset.Materials[1].DestroyCount == 1, "material destroyed more than once");

        using (var small = new ModelAssetCache<Asset>(1, a => a.Destroy()))
        {
            Asset idle;
            using (var old = small.Acquire("old", () => new Asset())) idle = old.Value;
            using var next = small.Acquire("new", () => new Asset());
            Check(!idle.Alive && next.Value.Alive, "protection broke explicit idle eviction");
        }
        var owned = new U.Mesh { hideFlags = U.HideFlags.HideInHierarchy };
        Check(ReferenceEquals(owned, ModelAssetLifetime.Own(owned)), "ownership replaced the resource");
        Check((owned.hideFlags & U.HideFlags.HideInHierarchy) != 0, "ownership erased previous flags");
        Check(ModelAssetLifetime.Own<U.Mesh>(null) == null, "null ownership should be harmless");
        Console.WriteLine("PASS scene-gap/rejoin local+remote resources, native-sweep control, leased lifetime, idle eviction, retirement, flags");
    }
}
