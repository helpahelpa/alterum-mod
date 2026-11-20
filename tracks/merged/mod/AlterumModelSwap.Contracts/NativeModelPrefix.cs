using System;

namespace AlterumModelSwap.Contracts;

// Process-lifetime native prefix of PlayerModelDatabase.Models: the count of shipped rows at the
// front of the array, before this mod appends any row of its own. The value is owned by the HOST
// assembly (NativeModelPrefixHolder), not by a hot generation, so host-side consumers share one
// capture. Hot code reaches the holder through IHostContext.NativeModelPrefix when the host
// provides it; the shipped hot module instead proves the boundary from the shipped resource set
// (SahurWardrobe.EnsureNativePrefix), so a host that predates the member can still load it.
public interface INativeModelPrefix
{
    /// <summary>
    /// True only while the captured native prefix is known and positive. False is the fail-closed
    /// state: an unknown prefix must not be guessed from the current database length.
    /// </summary>
    bool TryGet(out int count);

    /// <summary>
    /// Capture the length of PlayerModelDatabase.Models BEFORE this process appends its first
    /// custom row. The first successful capture fixes the value for the process lifetime, so later
    /// hot generations see exactly the same count.
    ///
    /// Must NOT be used on an already expanded database: if the first observation may contain rows
    /// appended before this holder was captured, call MarkUnknown instead. Inferring the prefix
    /// from an expanded initial database is exactly the case that must fail closed.
    ///
    /// A later capture of the same value is already-known; a different value is rejected and the
    /// original retained. Non-positive counts and counts past the ushort model-index space are
    /// invalid.
    /// </summary>
    NativePrefixCapture CaptureBeforeAppend(int count);

    /// <summary>
    /// Permanently marks the native prefix unknown (fail closed), for example when the first
    /// database observation is already expanded and its native boundary cannot be established.
    /// Unknown is irreversible: TryGet keeps returning false and CaptureBeforeAppend returns
    /// Unknown. A no-op once a positive value has been captured, because that capture was made
    /// before append and is authoritative.
    /// </summary>
    void MarkUnknown(string reason);

    /// <summary>The reason passed to MarkUnknown, or null while known or never captured.</summary>
    string UnknownReason { get; }
}

public enum NativePrefixCapture
{
    /// <summary>No value existed; this positive count is now the process-lifetime value.</summary>
    Captured = 0,

    /// <summary>The same count was captured before; the retained value is unchanged.</summary>
    AlreadyKnown = 1,

    /// <summary>A different count was already captured; the retained value was NOT replaced.</summary>
    Disagrees = 2,

    /// <summary>The count is not a positive length inside the ushort model-index space.</summary>
    Invalid = 3,

    /// <summary>The prefix was already marked unknown; fail closed, nothing was captured.</summary>
    Unknown = 4,
}
