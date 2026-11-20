using System;
using AlterumModelSwap.Contracts;

namespace AlterumModelSwap;

// Process-lifetime holder for the native prefix of PlayerModelDatabase.Models. It lives in the
// permanent host assembly and is surfaced to host code through IHostContext.NativeModelPrefix, so a
// reload can neither lose nor re-derive the count. The reloadable hot module proves the boundary
// from the shipped resource set instead (SahurWardrobe.EnsureNativePrefix), so this holder is not a
// hot-module compatibility requirement.
//
// The one dangerous input is a database that is already expanded when the holder is first asked
// about it: its native prefix is then unknown, and the caller must MarkUnknown rather than capture
// the expanded length. TryGet reports false in that state, so every consumer fails closed.
internal sealed class NativeModelPrefixHolder : INativeModelPrefix
{
    internal static readonly NativeModelPrefixHolder Instance = new NativeModelPrefixHolder();

    // The widest table a ushort model index can address. Kept in step with NativeModelPolicy.
    private const int MaxNativeCount = ushort.MaxValue + 1;

    private readonly object _lock = new object();
    private bool _known;
    private int _count;
    private bool _unknown;
    private string _unknownReason;

    public bool TryGet(out int count)
    {
        lock (_lock)
        {
            count = _known ? _count : 0;
            return _known;
        }
    }

    public NativePrefixCapture CaptureBeforeAppend(int count)
    {
        lock (_lock)
        {
            if (_unknown) return NativePrefixCapture.Unknown;
            if (count <= 0 || count > MaxNativeCount) return NativePrefixCapture.Invalid;
            if (_known)
                return _count == count ? NativePrefixCapture.AlreadyKnown : NativePrefixCapture.Disagrees;

            _count = count;
            _known = true;
            return NativePrefixCapture.Captured;
        }
    }

    public void MarkUnknown(string reason)
    {
        lock (_lock)
        {
            if (_known || _unknown) return;
            _unknown = true;
            _unknownReason = string.IsNullOrEmpty(reason) ? "unspecified" : reason;
        }
    }

    public string UnknownReason
    {
        get { lock (_lock) return _known ? null : _unknownReason; }
    }
}
