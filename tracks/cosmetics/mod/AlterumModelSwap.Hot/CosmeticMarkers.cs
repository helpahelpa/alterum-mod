using System;
using System.Collections.Generic;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Marker for renderers that belong to a mod-owned cosmetic.
//
// Injected MonoBehaviour types have to live in the stable host assembly (they cannot be
// re-registered after a hot reload), and this feature must not force a host/Contracts rebuild.
// The marker is therefore a pointer registry owned by the live hot generation: it is consulted
// *live* by MirrorPlayerVisibility (same assembly, no interface change) so a cosmetic renderer is
// never snapshotted, re-layered, enabled or restored by the mirror body sweep. Cleared on Dispose.
//
// A native address is not an identity: IL2CPP reuses addresses once a generation's objects become
// collectible. Every entry therefore carries the object's Unity instance id (unique per live
// object). A lookup whose captured id differs from the live one proves the entry is stale, so the
// entry is purged on sight (orphan recovery) and the object is reported as game-owned. Without
// this, address reuse made the mirror body sweep skip a legitimate body renderer and leave it
// out of the mirror isolation/restoration.
internal static class CosmeticMarkers
{
    private static readonly Dictionary<IntPtr, int> RendererPointers = new();
    private static readonly Dictionary<IntPtr, int> RootPointers = new();

    internal static int RendererCount => RendererPointers.Count;
    internal static int RootCount => RootPointers.Count;

    internal static void Add(Renderer renderer)
    {
        if (renderer == null) return;
        try { RendererPointers[renderer.Pointer] = renderer.GetInstanceID(); }
        catch { }
    }

    internal static void AddRoot(Transform root)
    {
        if (root == null) return;
        try { RootPointers[root.Pointer] = root.GetInstanceID(); }
        catch { }
    }

    // Unregister by address, so removal works even after the wrapper was destroyed by a scene
    // unload (the wrapper can no longer report its pointer, the recorded one still can).
    internal static void Remove(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero) RendererPointers.Remove(pointer);
    }

    internal static void RemoveRoot(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero) RootPointers.Remove(pointer);
    }

    internal static bool IsCosmetic(Renderer renderer)
    {
        if (renderer == null) return false;
        IntPtr pointer;
        int instanceId;
        try { pointer = renderer.Pointer; instanceId = renderer.GetInstanceID(); }
        catch { return false; }

        if (RendererPointers.TryGetValue(pointer, out var known))
        {
            if (known == instanceId) return true;
            // Address reused by a different object: our entry is an orphan, never a marker for this
            // renderer. Drop it here so it cannot poison a later sweep either.
            RendererPointers.Remove(pointer);
        }
        if (RootPointers.Count == 0) return false;

        // Defensive: a renderer created under a marked root but not yet registered itself.
        var t = renderer.transform;
        for (int i = 0; t != null && i < 8; i++, t = t.parent)
        {
            if (!RootPointers.TryGetValue(t.Pointer, out var rootInstanceId)) continue;
            int liveId;
            try { liveId = t.GetInstanceID(); }
            catch { continue; }
            if (rootInstanceId == liveId) return true;
            RootPointers.Remove(t.Pointer);
        }
        return false;
    }

    internal static void Clear()
    {
        RendererPointers.Clear();
        RootPointers.Clear();
    }
}
