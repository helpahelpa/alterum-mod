using System;
using System.Collections.Generic;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Selection paths must never use ClothInternals.PathOf: it is a 96-character log formatter.
internal static class UiPath
{
    internal static string Full(Transform transform)
    {
        var names = new List<string>();
        for (var t = transform; t != null; t = t.parent)
        {
            if (names.Count >= 256) throw new InvalidOperationException("UI hierarchy too deep");
            names.Add(t.name);
        }
        names.Reverse();
        return string.Join("/", names);
    }
}
