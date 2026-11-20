using System;
using System.Threading;
using System.Threading.Tasks;
using AlterumModelSwap;
using AlterumModelSwap.Contracts;

// The host-owned native prefix holder is the one process-lifetime fact the native-wire guard needs:
// how many PlayerModelDatabase.Models rows shipped before this mod appended its own. It must survive
// a hot reload, keep the first positive capture, refuse a disagreeing later capture, and fail closed
// when the first database it ever sees is already expanded (the native boundary is then unknown).
//
// This harness compiles the SHIPPING interface and holder by link (not a copy and not a
// re-implementation), and uses a fresh instance for each case. Only the singleton case touches the
// real host instance, because that is the process-lifetime object the hot module receives.
//
// Run: export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$PATH
//      dotnet run -c Release --project tests/native-model-prefix
// Exit 0 = every case matches; 1 = a case is wrong (the failure shows its expectation).
internal static class Program
{
    private const int Shipped = 10;      // the pinned build's shipped model count
    private const int Expanded = 24;     // a database with this mod's rows already appended
    private const int MaxCount = 65536;  // ushort.MaxValue + 1, the widest addressable table

    private static int _bad;

    private static int Main()
    {
        UnknownBeforeCapture();
        CaptureOncePositive();
        RetainsAndRejectsDisagreement();
        InvalidCountsDoNotPoison();
        ExpandedInitialDatabaseFailsClosed();
        MarkUnknownAfterCaptureIsNoOp();
        SingletonIsTheProcessLifetimeInstance();
        ConcurrentReadersAndWriters();

        Console.WriteLine(_bad == 0
            ? "PREFIX OK (8 groups)"
            : $"PREFIX FAILED ({_bad} failing checks)");
        Console.WriteLine(_bad == 0 ? "VERDICT OK" : "VERDICT FAILED (" + _bad + ")");
        return _bad == 0 ? 0 : 1;
    }

    // Before the first capture there is no value to read, and that is not an error: the caller must
    // capture before its first append. The reason is absent because nothing has failed yet.
    private static void UnknownBeforeCapture()
    {
        var holder = Fresh();
        int count = -1;
        Check(!holder.TryGet(out count), "unknown_before_capture", "TryGet -> false (want false)");
        Check(count == 0, "unknown_before_capture_value", $"count={count} (want 0)");
        Check(holder.UnknownReason == null, "unknown_reason_before_capture",
            $"reason={Quote(holder.UnknownReason)} (want <null>)");
    }

    // The first positive capture is kept. Both ends of the addressable range are legal; 0 and
    // negative lengths are not a native table at all.
    private static void CaptureOncePositive()
    {
        var holder = Fresh();
        var result = holder.CaptureBeforeAppend(Shipped);
        Check(result == NativePrefixCapture.Captured, "first_capture_result",
            $"CaptureBeforeAppend({Shipped}) -> {result} (want Captured)");
        int count = -1;
        Check(holder.TryGet(out count) && count == Shipped, "first_capture_value",
            $"TryGet -> {holder.TryGet(out count)}/{count} (want true/{Shipped})");
        Check(holder.UnknownReason == null, "first_capture_reason",
            $"reason={Quote(holder.UnknownReason)} (want <null>)");

        var one = Fresh();
        Check(one.CaptureBeforeAppend(1) == NativePrefixCapture.Captured, "capture_one_row",
            "CaptureBeforeAppend(1) -> Captured");

        var widest = Fresh();
        Check(widest.CaptureBeforeAppend(MaxCount) == NativePrefixCapture.Captured, "capture_widest_table",
            $"CaptureBeforeAppend({MaxCount}) -> Captured");
        Check(widest.TryGet(out count) && count == MaxCount, "capture_widest_value",
            $"TryGet -> {count} (want {MaxCount})");
    }

    // Repeating the same count is idempotent; a different count is rejected but never replaces the
    // captured one. This is what lets one hot generation verify the value another captured.
    private static void RetainsAndRejectsDisagreement()
    {
        var holder = Fresh();
        holder.CaptureBeforeAppend(Shipped);

        Check(holder.CaptureBeforeAppend(Shipped) == NativePrefixCapture.AlreadyKnown, "same_capture_result",
            $"CaptureBeforeAppend({Shipped}) again -> AlreadyKnown");

        Check(holder.CaptureBeforeAppend(Expanded) == NativePrefixCapture.Disagrees, "disagreement_result",
            $"CaptureBeforeAppend({Expanded}) -> Disagrees (want Disagrees)");

        int count = -1;
        Check(holder.TryGet(out count) && count == Shipped, "disagreement_retains_original",
            $"TryGet -> {count} (want {Shipped}, the first capture)");

        // A disagreement does not turn a known value into an unknown one.
        Check(holder.UnknownReason == null, "disagreement_not_unknown",
            $"reason={Quote(holder.UnknownReason)} (want <null>)");

        Check(holder.CaptureBeforeAppend(1) == NativePrefixCapture.Disagrees, "disagreement_lower_count",
            "CaptureBeforeAppend(1) after 10 -> Disagrees");
        Check(holder.TryGet(out count) && count == Shipped, "disagreement_lower_count_retains",
            $"TryGet -> {count} (want {Shipped})");
    }

    // Invalid counts are refused before they can become the process value, and a later valid
    // capture still works: a transient bad observation must not poison the holder.
    private static void InvalidCountsDoNotPoison()
    {
        (int Count, string Why)[] invalid =
        {
            (0, "an empty database has no native prefix to stand on"),
            (-1, "negative length"),
            (int.MinValue, "the most negative length"),
            (MaxCount + 1, "one past the widest table a ushort index can address"),
            (70000, "beyond the ushort address space"),
            (int.MaxValue, "the largest length"),
        };

        foreach (var c in invalid)
        {
            var holder = Fresh();
            var result = holder.CaptureBeforeAppend(c.Count);
            Check(result == NativePrefixCapture.Invalid, $"invalid_{c.Count}_result",
                $"CaptureBeforeAppend({c.Count}) -> {result} (want Invalid)");
            if (result != NativePrefixCapture.Invalid) Console.WriteLine("     why: " + c.Why);

            int count = -1;
            Check(!holder.TryGet(out count), $"invalid_{c.Count}_not_known",
                $"TryGet -> false (want false; {c.Why})");
            Check(holder.UnknownReason == null, $"invalid_{c.Count}_not_unknown",
                $"reason={Quote(holder.UnknownReason)} (want <null>: invalid is not fail-closed)");

            Check(holder.CaptureBeforeAppend(Shipped) == NativePrefixCapture.Captured,
                $"invalid_{c.Count}_then_valid", $"CaptureBeforeAppend({Shipped}) after the invalid one -> Captured");
            Check(holder.TryGet(out count) && count == Shipped, $"invalid_{c.Count}_valid_value",
                $"TryGet -> {count} (want {Shipped})");
        }
    }

    // The dangerous first observation: the database already contains rows this process appended
    // before the holder existed (or could capture). Its native boundary is not knowable, so the
    // only safe state is unknown; the expanded length must never be captured as "native".
    private static void ExpandedInitialDatabaseFailsClosed()
    {
        var holder = Fresh();
        holder.MarkUnknown("already expanded initial database (observed " + Expanded + " rows)");

        int count = -1;
        Check(!holder.TryGet(out count), "expanded_initial_unknown",
            $"TryGet -> false (want false; observed {Expanded} rows)");
        Check(count == 0, "expanded_initial_value", $"count={count} (want 0)");
        Check(holder.UnknownReason != null && holder.UnknownReason.Contains("expanded"),
            "expanded_initial_reason", $"reason={Quote(holder.UnknownReason)} (want the expanded-database reason)");

        // Unknown is irreversible: neither the expanded length nor a later pristine-looking length
        // can be promoted to the process value.
        Check(holder.CaptureBeforeAppend(Expanded) == NativePrefixCapture.Unknown,
            "expanded_capture_refused", $"CaptureBeforeAppend({Expanded}) -> Unknown (want Unknown)");
        Check(holder.CaptureBeforeAppend(Shipped) == NativePrefixCapture.Unknown,
            "expanded_pristine_capture_refused", $"CaptureBeforeAppend({Shipped}) -> Unknown (want Unknown)");
        Check(holder.CaptureBeforeAppend(0) == NativePrefixCapture.Unknown,
            "expanded_invalid_capture_refused", "CaptureBeforeAppend(0) after unknown -> Unknown (want Unknown)");
        Check(!holder.TryGet(out count), "expanded_still_unknown_after_captures",
            "TryGet -> false after both capture attempts (want false)");
        Check(holder.UnknownReason != null && holder.UnknownReason.Contains("expanded"),
            "expanded_reason_retained", $"reason={Quote(holder.UnknownReason)} (want the first reason)");

        var blank = Fresh();
        blank.MarkUnknown(null);
        Check(blank.UnknownReason == "unspecified", "unspecified_reason",
            $"reason={Quote(blank.UnknownReason)} (want 'unspecified')");
        blank.MarkUnknown("second reason");
        Check(blank.UnknownReason == "unspecified", "first_unknown_reason_wins",
            $"reason={Quote(blank.UnknownReason)} (want 'unspecified')");
    }

    // Once a positive count was captured before append, it is authoritative. A later warning about
    // expansion must not erase it, and the same idempotent capture still works.
    private static void MarkUnknownAfterCaptureIsNoOp()
    {
        var holder = Fresh();
        holder.CaptureBeforeAppend(Shipped);
        holder.MarkUnknown("late expanded observation");

        int count = -1;
        Check(holder.TryGet(out count) && count == Shipped, "mark_unknown_after_capture_retains",
            $"TryGet -> {count} (want {Shipped})");
        Check(holder.UnknownReason == null, "mark_unknown_after_capture_reason",
            $"reason={Quote(holder.UnknownReason)} (want <null>)");
        Check(holder.CaptureBeforeAppend(Shipped) == NativePrefixCapture.AlreadyKnown,
            "mark_unknown_after_capture_idempotent", "same capture -> AlreadyKnown");
        Check(holder.CaptureBeforeAppend(Expanded) == NativePrefixCapture.Disagrees,
            "mark_unknown_after_capture_disagrees", "different capture -> Disagrees");
    }

    // The host publishes NativeModelPrefixHolder.Instance. A hot generation is replaced, the host is
    // not: every generation must get this same object with the value still in it.
    private static void SingletonIsTheProcessLifetimeInstance()
    {
        var generation1 = NativeModelPrefixHolder.Instance;
        var capture = generation1.CaptureBeforeAppend(Shipped);
        Check(capture == NativePrefixCapture.Captured || capture == NativePrefixCapture.AlreadyKnown,
            "singleton_capture", $"first use of the host instance -> {capture}");
        Check(ReferenceEquals(generation1, NativeModelPrefixHolder.Instance), "singleton_identity",
            "two lookups return the same object");

        // Simulate the next hot generation receiving the holder fresh from its IHostContext.
        INativeModelPrefix generation2 = NativeModelPrefixHolder.Instance;
        int count = -1;
        Check(ReferenceEquals(generation1, generation2), "singleton_across_generations",
            "generation 1 and generation 2 hold the same instance");
        Check(generation2.TryGet(out count) && count == Shipped, "singleton_value_survives_reload",
            $"TryGet after the simulated reload -> {count} (want {Shipped})");

        // A fresh instance is independent: this proves the value is per holder, not a hidden global
        // the tests accidentally share.
        var separate = Fresh();
        Check(!separate.TryGet(out count), "fresh_instance_independent",
            "a new holder starts unknown even after the singleton was captured");
    }

    // The holder is on the main thread in the game, but the lock must still make the invariant hold:
    // one value, same captures are idempotent, different captures are rejected, readers never see a
    // partial state.
    private static void ConcurrentReadersAndWriters()
    {
        var holder = Fresh();
        int wrong = 0, known = 0, captured = 0;

        Parallel.For(0, 64, i =>
        {
            var result = holder.CaptureBeforeAppend(Shipped);
            if (result == NativePrefixCapture.Captured) Interlocked.Increment(ref captured);
            else if (result == NativePrefixCapture.AlreadyKnown) Interlocked.Increment(ref known);
            else Interlocked.Increment(ref wrong);
        });
        Check(wrong == 0, "concurrent_same_value", $"wrong results={wrong} (want 0)");
        Check(captured == 1, "concurrent_one_capture", $"Captured={captured} (want 1)");
        Check(captured + known == 64, "concurrent_all_accounted",
            $"Captured+AlreadyKnown={captured + known} (want 64)");

        int rejected = 0;
        Parallel.For(0, 64, i =>
        {
            if (holder.CaptureBeforeAppend(Expanded) != NativePrefixCapture.Disagrees)
                Interlocked.Increment(ref rejected);
        });
        Check(rejected == 0, "concurrent_disagreement", $"non-Disagrees results={rejected} (want 0)");

        int count = -1;
        Check(holder.TryGet(out count) && count == Shipped, "concurrent_value_retained",
            $"TryGet -> {count} (want {Shipped})");
    }

    private static NativeModelPrefixHolder Fresh() => new NativeModelPrefixHolder();

    private static string Quote(string value) => value == null ? "<null>" : "'" + value + "'";

    private static void Check(bool ok, string name, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name,-44} {detail}");
        if (!ok) _bad++;
    }
}
