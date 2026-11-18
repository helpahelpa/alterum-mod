using UnityEngine;

namespace AlterumModelSwap.Hot;

// A managed cache/lease is not a Unity scene reference. During leave/rejoin there are no
// renderers referencing idle cache assets, and Resources.UnloadUnusedAssets can destroy them
// even while our managed Asset/Lease still exists. Protect only objects we create and own;
// their existing cache eviction/teardown paths still explicitly Destroy them. Never apply this
// to borrowed game materials, shaders or scene objects.
internal static class ModelAssetLifetime
{
    internal static T Own<T>(T resource) where T : Object
    {
        if (resource != null) resource.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return resource;
    }
}
