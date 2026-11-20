using System;

namespace AlterumModelSwap.Hot;

// Maps a model ID to a row present on an unmodified peer. Callers must identify
// the native prefix of the database, not use the expanded database size.
internal static class NativeModelPolicy
{
    internal static ushort Fallback(ushort requested, int nativeCount, ulong clientId)
    {
        if (nativeCount <= 0 || nativeCount > ushort.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(nativeCount));
        return requested < nativeCount ? requested : (ushort)(clientId % (ulong)nativeCount);
    }
}
