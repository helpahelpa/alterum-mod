using System;
using System.Collections.Generic;
using AlterumModelSwap.Hot;

// Behavioral harness for the native-wire DECISIONS in SahurWardrobe.cs. It compiles the shipping
// decision block (extracted verbatim by gen_pure.py) and the shipping NativeModelPolicy, so these
// are the production branches, not a model of them. The cases are the review's edge cases:
//
//   * the shipped prefix is proven from the SHIPPED RESOURCE SET, not from roster labels: an
//     already-expanded database whose front is the shipped set still proves the shipped count, while
//     a foreign / duplicate / null row inside the candidate prefix fails closed;
//   * no prune while a session is live, even with the guard installed (dictionary indexes may be
//     stale), and no prune into the shipped prefix;
//   * a withdrawal cuts only the provable tail, never with an unproven prefix, and reports BOTH the
//     rows of ours it could not cut and every other non-shipped row that stays (an unrecognised row
//     is just as unresolvable on a stock peer, and is not ours to delete);
//   * the outbound index is decided once, independently of whether the wanted intent is recorded:
//     a proven shipped donor the caller already carries wins over an arbitrary per-client row, and
//     with no proven prefix the only value is row 0;
//   * a guest's sent RPC stays PENDING until an applied row confirms it: our own entry's row or the
//     row the send carried, and never our row for a deliberate-native intent.
//
// Run: export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$PATH
//      dotnet run -c Release --project tests/native-wire-guard/behavior
// Exit 0 = every case matches; 1 = a case is wrong (the failure shows the expectation).
internal static class Program
{
    private const ulong MaxClient = ulong.MaxValue;
    private static int _checks;
    private static int _bad;

    private static int Main()
    {
        PrefixProofCases();
        PrefixProofSweep();
        PruneCases();
        PruneNeverPrunesIntoThePrefix();
        WithdrawalCases();
        WithdrawalNeverCutsWithoutAProvenPrefix();
        WireIndexCases();
        ConfirmCases();
        ConfirmSafetySweep();
        PendingConfirmCases();

        Console.WriteLine(_bad == 0
            ? $"NATIVE-WIRE OK ({_checks} checks)"
            : $"NATIVE-WIRE FAILED ({_bad} of {_checks} checks)");
        Console.WriteLine(_bad == 0 ? "VERDICT OK" : "VERDICT FAILED (" + _bad + ")");
        return _bad == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ shipped-prefix proof

    private static void PrefixProofCases()
    {
        IntPtr[] shipped = Pointers(100, 3);                 // the resource set: 100,101,102

        Prefix(shipped, Pointers(100, 3), PrefixProof.Proven, 3,
            "exact shipped set -> the shipped count");
        Prefix(shipped, new[] { P(102), P(100), P(101) }, PrefixProof.Proven, 3,
            "the same shipped set in another order is still the shipped prefix (order is not proof)");
        Prefix(shipped, new[] { P(100), P(101), P(102), P(999) }, PrefixProof.Proven, 3,
            "an ALREADY EXPANDED array whose front is the shipped set proves the SHIPPED count, not the length");
        Prefix(shipped, new[] { P(100), P(101) }, PrefixProof.Wait, 0,
            "a database shorter than the resource set is still initializing: wait, never capture");
        Prefix(shipped, Array.Empty<IntPtr>(), PrefixProof.Wait, 0,
            "an empty candidate waits");
        Prefix(Array.Empty<IntPtr>(), Pointers(100, 3), PrefixProof.Wait, 0,
            "no shipped rows loaded: wait, never capture from the live length");
        Prefix(null, Pointers(100, 3), PrefixProof.Wait, 0,
            "a missing shipped set waits");

        Prefix(shipped, new[] { P(100), P(999), P(102) }, PrefixProof.FailClosed, 0,
            "a foreign row inside the candidate prefix fails closed");
        Prefix(shipped, new[] { P(999), P(100), P(101) }, PrefixProof.FailClosed, 0,
            "a foreign row PREPENDED before shipped rows fails closed");
        Prefix(shipped, new[] { P(100), P(100), P(101) }, PrefixProof.FailClosed, 0,
            "a duplicate pointer (one shipped row missing) fails closed");
        Prefix(shipped, new[] { P(100), IntPtr.Zero, P(102) }, PrefixProof.FailClosed, 0,
            "a null row inside the prefix fails closed");
        Prefix(shipped, new[] { P(100), P(101), P(103) }, PrefixProof.FailClosed, 0,
            "a shipped row replaced by an unknown pointer fails closed");

        var one = Pointers(7, 1);
        Prefix(one, new[] { P(7) }, PrefixProof.Proven, 1, "a one-row shipped table is legal");
        Prefix(one, new[] { P(8) }, PrefixProof.FailClosed, 0, "a one-row table with a stranger fails closed");
    }

    private static void PrefixProofSweep()
    {
        for (int n = 1; n <= 8; n++)
        {
            IntPtr[] shipped = Pointers(1000, n);

            // Reversed order everywhere: the proof must be membership, not position.
            var reversed = new IntPtr[n];
            for (int i = 0; i < n; i++) reversed[i] = shipped[n - 1 - i];
            Prefix(shipped, reversed, PrefixProof.Proven, n, $"n={n}: reversed shipped set");

            // An arbitrary foreign tail is not part of the prefix and must not affect the count.
            var expanded = new IntPtr[n + 3];
            for (int i = 0; i < n; i++) expanded[i] = shipped[i];
            expanded[n] = P(9000); expanded[n + 1] = P(9001); expanded[n + 2] = P(9002);
            Prefix(shipped, expanded, PrefixProof.Proven, n, $"n={n}: expanded tail keeps the shipped count");

            // Every single-position substitution must fail closed.
            for (int at = 0; at < n; at++)
            {
                var candidate = new IntPtr[n];
                for (int i = 0; i < n; i++) candidate[i] = shipped[i];
                candidate[at] = P(8000 + at);
                Prefix(shipped, candidate, PrefixProof.FailClosed, 0,
                    $"n={n}: foreign row at {at} fails closed");
            }

            // Every single duplicate (with one shipped row dropped) must fail closed.
            if (n > 1)
            {
                var duplicate = new IntPtr[n];
                for (int i = 0; i < n; i++) duplicate[i] = shipped[i == n - 1 ? 0 : i];
                Prefix(shipped, duplicate, PrefixProof.FailClosed, 0,
                    $"n={n}: a duplicate shipped row fails closed");
            }

            Prefix(shipped, new IntPtr[n - 1], PrefixProof.Wait, 0, $"n={n}: short candidate waits");
        }
    }

    // ------------------------------------------------------------------ prune gate

    private static void PruneCases()
    {
        Prune(false, true, false, 3, new[] { 3 }, ShrinkVerdict.WaitForPrefix,
            "unknown prefix refuses even a row above the (unknown) boundary");
        Prune(true, true, false, 0, new[] { 3 }, ShrinkVerdict.WaitForPrefix,
            "a non-positive captured prefix is not a boundary");
        Prune(true, true, true, 3, new[] { 3 }, ShrinkVerdict.DeferSession,
            "a live session defers a prune even WITH the guard installed");
        Prune(true, false, true, 3, new[] { 3 }, ShrinkVerdict.DeferSession,
            "session wins over the missing guard (both refuse; the reason is the one reported)");
        Prune(true, false, false, 3, new[] { 3 }, ShrinkVerdict.DeferGuard,
            "no guard defers a prune with no live session");
        Prune(true, true, false, 3, new[] { 2 }, ShrinkVerdict.RefuseShippedRow,
            "a row inside the shipped prefix is never removable");
        Prune(true, true, false, 3, new[] { 2, 3 }, ShrinkVerdict.RefuseShippedRow,
            "one shipped row in the drop set refuses the whole prune");
        Prune(true, true, false, 3, new[] { 3 }, ShrinkVerdict.Allow,
            "the first appended row may be pruned when nothing is live");
        Prune(true, true, false, 3, new[] { 3, 4, 9 }, ShrinkVerdict.Allow,
            "only rows at or above the prefix may be dropped");
        Prune(true, true, false, 3, Array.Empty<int>(), ShrinkVerdict.Allow,
            "an empty drop set is a no-op, not a refusal");
        Prune(true, true, false, 3, null, ShrinkVerdict.Allow,
            "a null drop set is a no-op, not a refusal");
        Prune(true, true, false, -1, new[] { 0 }, ShrinkVerdict.WaitForPrefix,
            "a negative native count is not a boundary");
    }

    private static void PruneNeverPrunesIntoThePrefix()
    {
        // Exhaustive: for every fleet of candidate drop rows inside a small range, Allow implies
        // every dropped row is at or above the prefix.
        for (int native = 1; native <= 4; native++)
        {
            for (int a = 0; a <= 6; a++)
            {
                for (int b = 0; b <= 6; b++)
                {
                    var drop = new[] { a, b };
                    var verdict = NativeWirePolicy.CanPrune(true, true, false, native, drop);
                    if (verdict != ShrinkVerdict.Allow) continue;
                    foreach (int row in drop)
                        Check(row >= native, $"prune-safety native={native} drop=[{a},{b}]",
                            $"row {row} was allowed while the shipped prefix is {native}");
                }
            }
        }
    }

    // ------------------------------------------------------------------ withdrawal plan

    private static void WithdrawalCases()
    {
        // S = shipped, C = ours, F = someone else's. Indexes are positions in the live array.
        // With a proven prefix of 2: rows 0,1 are shipped, everything at or above 2 is non-shipped.
        Withdraw(true, Ours(3, 2), 2, 2, Array.Empty<int>(), Array.Empty<int>(),
            "one ours row above the prefix is cut to the prefix");
        Withdraw(true, Ours(4, 2, 3), 2, 2, Array.Empty<int>(), Array.Empty<int>(),
            "a contiguous ours tail is cut completely");
        Withdraw(true, Ours(5, 2, 4), 2, 4, new[] { 2 }, new[] { 3 },
            "a foreign row blocks the tail: only the last row is cut; the blocked ours row is residual " +
            "and the foreign non-shipped row is unprotected");
        Withdraw(true, Ours(3, 1), 2, 3, Array.Empty<int>(), new[] { 2 },
            "nothing may be cut (a non-shipped row is not ours) but it is still reported");
        Withdraw(true, Ours(4, 0, 2), 2, 4, new[] { 2 }, new[] { 3 },
            "a shipped row marked ours is never a candidate (row 0 is below the prefix): the ours row " +
            "above the prefix is residual because a non-ours row blocks the tail, and that row is unprotected");
        Withdraw(true, Ours(3, 0), 2, 3, Array.Empty<int>(), new[] { 2 },
            "every non-shipped row is foreign: nothing may be cut, and the trailer is reported");
        Withdraw(true, Ours(4, 2, 3), 4, 4, Array.Empty<int>(), Array.Empty<int>(),
            "a prefix at the end leaves no non-shipped row");
        Withdraw(true, Ours(3, 2), 9, 3, Array.Empty<int>(), Array.Empty<int>(),
            "a prefix past the end is clamped, never throws");
        Withdraw(false, Ours(2, 1), 2, 2, new[] { 1 }, Array.Empty<int>(),
            "NO PROVEN PREFIX: nothing is cut even for a provable ours tail, and the row is reported");
        Withdraw(false, Ours(3, 0, 2), 4, 3, new[] { 0, 2 }, Array.Empty<int>(),
            "NO PROVEN PREFIX: every ours row stays and is reported; a shipped row could be among them");
        Withdraw(false, Ours(3), 0, 3, Array.Empty<int>(), Array.Empty<int>(),
            "NO PROVEN PREFIX: nothing is cut and a foreign array makes no non-shipped claim");
        Withdraw(true, Ours(0), 0, 0, Array.Empty<int>(), Array.Empty<int>(),
            "an empty array has nothing to cut");
        Withdraw(true, null, 3, 0, Array.Empty<int>(), Array.Empty<int>(),
            "a missing ownership array is an empty array");
        Withdraw(false, null, 3, 0, Array.Empty<int>(), Array.Empty<int>(),
            "a missing ownership array is empty with no prefix too");

        // The cut never enters the shipped prefix, even when every row looks like ours.
        for (int native = 1; native <= 5; native++)
        {
            var flags = new bool[native + 3];
            for (int i = 0; i < flags.Length; i++) flags[i] = true;
            var plan = NativeWirePolicy.PlanWithdrawal(true, flags, native);
            Check(plan.Cut == native, $"withdraw-floor native={native}",
                $"cut={plan.Cut} (want {native}); a shrink must not touch a shipped row");
            Check(plan.Residual.Length == 0, $"withdraw-floor-residual native={native}",
                $"residual=[{Join(plan.Residual)}] (want none)");
            Check(plan.Unprotected.Length == 0, $"withdraw-floor-unprotected native={native}",
                $"unprotected=[{Join(plan.Unprotected)}] (want none)");
            Check(plan.Floor == native && plan.Bounded, $"withdraw-floor-fields native={native}",
                $"floor={plan.Floor} bounded={plan.Bounded}");
        }
    }

    private static void WithdrawalNeverCutsWithoutAProvenPrefix()
    {
        // Exhaustive over small arrays: without a proven prefix the cut is the whole length (nothing
        // may be removed), all ours rows are residual, and no non-shipped claim is made. With a
        // proven prefix every cut index is at or above it, and every non-shipped row that stays is
        // reported either as residual (ours) or as unprotected (not ours).
        for (int n = 0; n <= 5; n++)
        {
            for (int mask = 0; mask < (1 << n); mask++)
            {
                var flags = new bool[n];
                for (int i = 0; i < n; i++) flags[i] = (mask & (1 << i)) != 0;

                var open = NativeWirePolicy.PlanWithdrawal(false, flags, n);
                Check(open.Cut == n, $"withdraw-open-not-aggressive n={n} mask={mask}",
                    $"cut={open.Cut} (want {n}): a row could be shipped");
                Check(open.Unprotected.Length == 0, $"withdraw-open-claim n={n} mask={mask}",
                    $"unprotected=[{Join(open.Unprotected)}] claims non-shipped rows without a prefix");
                int openOurs = 0;
                for (int i = 0; i < n; i++) if (flags[i]) openOurs++;
                Check(open.Residual.Length == openOurs, $"withdraw-open-residual n={n} mask={mask}",
                    $"residual=[{Join(open.Residual)}] (want every one of the {openOurs} ours rows)");

                for (int native = 0; native <= n + 2; native++)
                {
                    var plan = NativeWirePolicy.PlanWithdrawal(true, flags, native);
                    if (native <= 0)
                    {
                        Check(plan.Cut == n, $"withdraw-zero-prefix n={n} mask={mask} native={native}",
                            $"cut={plan.Cut} (want {n}): a non-positive prefix is not a boundary");
                        continue;
                    }
                    int floor = Math.Min(native, n);
                    Check(plan.Cut >= floor, $"withdraw-cut-floor n={n} mask={mask} native={native}",
                        $"cut={plan.Cut} floor={plan.Floor} (want >= {floor})");
                    // [Cut, n) is REMOVED, so those rows must be ours; [Floor, Cut) stays and must be
                    // reported: ours as residual, everything else as unprotected.
                    var residualSet = new bool[n];
                    foreach (int i in plan.Residual) residualSet[i] = true;
                    var unprotectedSet = new bool[n];
                    foreach (int i in plan.Unprotected) unprotectedSet[i] = true;
                    for (int i = plan.Cut; i < n; i++)
                        Check(flags[i], $"withdraw-cut-ours n={n} mask={mask} native={native}",
                            $"row {i} would be removed but is not ours");
                    for (int i = 0; i < n; i++)
                    {
                        if (i < plan.Floor || i >= plan.Cut)
                        {
                            Check(!residualSet[i] && !unprotectedSet[i],
                                $"withdraw-report-outside n={n} mask={mask} native={native}",
                                $"row {i} is outside [floor={plan.Floor}, cut={plan.Cut}) but reported");
                            continue;
                        }
                        Check(flags[i] ? residualSet[i] : unprotectedSet[i],
                            $"withdraw-report n={n} mask={mask} native={native}",
                            $"staying non-shipped row {i} (ours={flags[i]}) is not reported as " +
                            (flags[i] ? "residual" : "unprotected"));
                    }
                    foreach (int i in plan.Residual)
                        Check(flags[i], $"withdraw-residual-own n={n} mask={mask} native={native}",
                            $"row {i} is reported residual but is not ours");
                    foreach (int i in plan.Unprotected)
                        Check(!flags[i], $"withdraw-unprotected-own n={n} mask={mask} native={native}",
                            $"row {i} is reported unprotected but IS ours");
                }
            }
        }
    }

    // ------------------------------------------------------------------ guest outbound index

    private static void WireIndexCases()
    {
        Check(NativeWirePolicy.ShippedWireIndex(false, 0, 0, -1, 0) == 0, "wire:no-prefix row0",
            "no proven prefix must map even row 0 to 0");
        Check(NativeWirePolicy.ShippedWireIndex(false, 0, 42, 3, MaxClient) == 0, "wire:no-prefix custom",
            "no proven prefix must not pass any custom index");
        Check(NativeWirePolicy.ShippedWireIndex(false, 9, 2, 3, MaxClient) == 0, "wire:no-prefix-donor",
            "no proven prefix must not pass a donor either");
        Check(NativeWirePolicy.ShippedWireIndex(true, 0, 41, 3, 0) == 0, "wire:zero-count",
            "a non-positive count is not a wire range");

        Check(NativeWirePolicy.ShippedWireIndex(true, 5, 4, 1, 3) == 4, "wire:shipped-passthrough",
            "a shipped index passes through unchanged, whatever the donor says");
        Check(NativeWirePolicy.ShippedWireIndex(true, 5, 7, 2, 3) == 2, "wire:known-donor",
            "a proven shipped donor is preferred to the per-client row");
        Check(NativeWirePolicy.ShippedWireIndex(true, 5, 7, 0, 3) == 0, "wire:donor-zero",
            "row 0 is a legal donor");
        Check(NativeWirePolicy.ShippedWireIndex(true, 5, 7, 4, 3) == 4, "wire:donor-last",
            "the last shipped row is a legal donor");
        foreach (int bad in new[] { -1, -7, 5, 6, 400, int.MaxValue })
            Check(NativeWirePolicy.ShippedWireIndex(true, 5, 7, bad, 3) == NativeModelPolicy.Fallback(7, 5, 3),
                $"wire:bad-donor {bad}",
                "a donor outside [0, nativeCount) is not a shipped row: the deterministic row wins");

        for (int n = 1; n <= 8; n++)
        {
            foreach (ulong client in new[] { 0UL, 1UL, 7UL, MaxClient })
            {
                for (int donor = -1; donor <= n; donor++)
                {
                    for (int requested = 0; requested <= n + 3; requested++)
                    {
                        ushort got = NativeWirePolicy.ShippedWireIndex(true, n, (ushort)requested, donor, client);
                        ushort want = requested < n
                            ? (ushort)requested
                            : (donor >= 0 && donor < n
                                ? (ushort)donor
                                : NativeModelPolicy.Fallback((ushort)requested, n, client));
                        Check(got == want, $"wire:n={n} requested={requested} donor={donor} client={client}",
                            $"got {got} (want {want})");
                        Check(got < n, $"wire-range:n={n} requested={requested} donor={donor} client={client}",
                            $"got {got} is outside the {n} shipped row(s)");
                        if (requested >= n && donor >= 0 && donor < n)
                            Check(got == donor, $"wire-preserve-donor:n={n} requested={requested} donor={donor}",
                                $"got {got} (want the proven donor {donor})");
                    }
                }
            }
        }

    }

    // ------------------------------------------------------------------ local confirm plan

    private static void ConfirmCases()
    {
        Confirm(false, 0, 9, true, "sahur", -1, 3, ConfirmBranch.NoPrefix, 0, false, null,
            "no prefix: row 0, no wanted record, even for an otherwise valid ours row");
        Confirm(false, 0, 0, true, "", -1, 0, ConfirmBranch.NoPrefix, 0, false, null,
            "no prefix and row 0: still the fail-closed branch");

        Confirm(true, 3, 7, false, "", -1, 4, ConfirmBranch.StaleRow,
            NativeModelPolicy.Fallback(7, 3, 4), false, null,
            "a stale index with no donor falls back deterministically");
        Confirm(true, 3, 7, false, "", 2, 4, ConfirmBranch.StaleRow, 2, false, null,
            "a stale index uses the shipped donor when there is one");
        Confirm(true, 3, 7, false, "", 3, 4, ConfirmBranch.StaleRow,
            NativeModelPolicy.Fallback(7, 3, 4), false, null,
            "an out-of-range donor is not a shipped row and is ignored");
        Confirm(true, 3, 1, false, "", 2, 4, ConfirmBranch.StaleRow, 2, false, null,
            "a stale index BELOW the prefix still prefers the applied shipped donor to the dead index");

        Confirm(true, 3, 0, true, "", -1, 0, ConfirmBranch.NativeRow, 0, true, "",
            "a shipped row is kept and recorded as deliberate-native");
        Confirm(true, 3, 2, true, "", -1, 0, ConfirmBranch.NativeRow, 2, true, "",
            "the last shipped row is still native");
        Confirm(true, 3, 3, true, "", -1, 5, ConfirmBranch.ForeignRow, 2, true, "",
            "the first row past the prefix is foreign, not native");
        Confirm(true, 3, 4, true, "sahur", -1, 1, ConfirmBranch.OurRow, 1, true, "sahur",
            "one of our rows falls back to a shipped row and keeps the slug");
        Confirm(true, 3, 4, true, "sahur", 0, 1, ConfirmBranch.OurRow, 0, true, "sahur",
            "the applied shipped donor wins over the fallback");
        Confirm(true, 3, 4, true, "sahur", 9, 1, ConfirmBranch.OurRow,
            NativeModelPolicy.Fallback(4, 3, 1), true, "sahur",
            "an out-of-range donor on one of our rows falls back instead of going on the wire");
        Confirm(true, 3, 2, true, "capuchino", -1, 2, ConfirmBranch.OurRow,
            NativeModelPolicy.Fallback(2, 3, 2), true, "capuchino",
            "a slug at a shipped position is still recorded as ours (the row decides, not the index)");
    }

    private static void ConfirmSafetySweep()
    {
        // The property that matters: with a proven prefix, NO confirm plan can send an index at or
        // above it, no matter the row, slug, donor or client - and a plan that does not record the
        // intent still carries a safe index. Without a prefix the index is always 0.
        for (int n = 1; n <= 7; n++)
        {
            foreach (ushort chosen in new ushort[] { 0, (ushort)(n - 1), (ushort)n, (ushort)(n + 1), (ushort)(n + 3), ushort.MaxValue })
            {
                foreach (int donor in new[] { -1, 0, n - 1, n, n + 5 })
                {
                    foreach (string slug in new[] { null, "", "sahur" })
                    {
                        foreach (bool row in new[] { true, false })
                        {
                            foreach (ulong client in new[] { 0UL, 3UL, MaxClient })
                            {
                                var plan = NativeWirePolicy.PlanConfirm(true, n, chosen, row, slug, donor, client);
                                Check(plan.Index < n, $"confirm-range n={n} chosen={chosen} row={row} slug='{slug}'",
                                    $"index {plan.Index} is outside the {n} shipped row(s)");
                                bool ours = row && !string.IsNullOrEmpty(slug);
                                Check(ours == (plan.Branch == ConfirmBranch.OurRow),
                                    $"confirm-branch n={n} chosen={chosen} row={row} slug='{slug}'",
                                    $"branch {plan.Branch} (want ours={ours})");
                                if (!row) Check(!plan.Record, "confirm-no-record-stale",
                                    "a stale row must not record an intent");
                                if (ours) Check(plan.Record && plan.Slug == slug, "confirm-ours-record",
                                    $"record={plan.Record} slug='{plan.Slug}' (want true/'{slug}')");
                            }
                        }
                    }
                }
            }

            foreach (ushort chosen in new ushort[] { 0, 3, ushort.MaxValue })
            {
                var plan = NativeWirePolicy.PlanConfirm(false, 0, chosen, true, "sahur", -1, MaxClient);
                Check(plan.Index == 0, "confirm-no-prefix-index",
                    $"chosen={chosen} -> {plan.Index} (want 0)");
                Check(!plan.Record, "confirm-no-prefix-record", "no prefix must not record an intent");
            }
        }
    }

    // ------------------------------------------------------------------ pending guest confirmation

    private static void PendingConfirmCases()
    {
        // An applied row can only confirm a pending pick when it IS the row the pick named. A native
        // intent is never confirmed by our own row: the player asked for the game's model.
        Pending(false, false, false, PendingVerdict.Pending,
            "nothing observed: a send alone never confirms");
        Pending(false, true, false, PendingVerdict.OurRow,
            "our entry's row applied directly: confirmed by our row");
        Pending(false, false, true, PendingVerdict.SentRow,
            "the shipped row the send carried is applied: confirmed by the sent row");
        Pending(false, true, true, PendingVerdict.OurRow,
            "both match: our own row is the stronger source");
        Pending(true, true, false, PendingVerdict.Pending,
            "deliberate-native intent and our row applied: NOT convergence, stays pending");
        Pending(true, false, true, PendingVerdict.SentRow,
            "deliberate-native intent and the sent shipped row applied: confirmed");
        Pending(true, false, false, PendingVerdict.Pending,
            "deliberate-native intent, a different row applied: stays pending");
        Pending(true, true, true, PendingVerdict.SentRow,
            "native intent: the sent row confirms even if our row also matched (it cannot be canonical)");

        // Exhaustive: confirmation requires evidence, and only ONE of the two can be the source of a
        // confirmed native intent.
        for (int mask = 0; mask < 8; mask++)
        {
            bool native = (mask & 1) != 0, ours = (mask & 2) != 0, sent = (mask & 4) != 0;
            var got = NativeWirePolicy.PendingConfirm(native, ours, sent);
            bool confirmed = got != PendingVerdict.Pending;
            Check(confirmed == (sent || (!native && ours)),
                $"pending-evidence native={native} ours={ours} sent={sent}",
                $"got {got} (want a verdict only when the send carried the row or our (non-native) row matched)");
            if (got == PendingVerdict.OurRow)
                Check(!native && ours, $"pending-our-native native={native} ours={ours}",
                    $"confirmed OurRow for a native intent");
            if (got == PendingVerdict.SentRow)
                Check(sent, $"pending-sent-source native={native} ours={ours} sent={sent}",
                    "SentRow without the sent row applied");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static IntPtr P(int value) => new IntPtr(value);

    private static IntPtr[] Pointers(int first, int count)
    {
        var pointers = new IntPtr[count];
        for (int i = 0; i < count; i++) pointers[i] = P(first + i);
        return pointers;
    }

    // Ownership flags: `count` rows, the given indexes are ours.
    private static bool[] Ours(int count, params int[] ours)
    {
        var flags = new bool[count];
        foreach (int i in ours) flags[i] = true;
        return flags;
    }

    private static void Prefix(IntPtr[] shipped, IntPtr[] candidate, PrefixProof want, int wantCount, string what)
    {
        var verdict = NativeWirePolicy.ProvePrefix(shipped, candidate);
        bool ok = verdict.Proof == want && verdict.Count == wantCount;
        Check(ok, "prefix:" + what,
            $"proof={verdict.Proof}/{verdict.Count} (want {want}/{wantCount}) detail='{verdict.Detail}'");
    }

    private static void Prune(bool prefixKnown, bool guard, bool session, int native, int[] drop,
                              ShrinkVerdict want, string what)
    {
        var got = NativeWirePolicy.CanPrune(prefixKnown, guard, session, native, drop);
        Check(got == want, "prune:" + what,
            $"got {got} (want {want}) known={prefixKnown} guard={guard} session={session} native={native} " +
            $"drop=[{(drop == null ? "null" : string.Join(",", drop))}]");
    }

    private static void Withdraw(bool prefixKnown, bool[] flags, int native, int wantCut, int[] wantResidual,
                                 int[] wantUnprotected, string what)
    {
        var plan = NativeWirePolicy.PlanWithdrawal(prefixKnown, flags, native);
        bool bounded = prefixKnown && native > 0;
        bool ok = plan.Cut == wantCut && Same(plan.Residual, wantResidual) &&
                  Same(plan.Unprotected, wantUnprotected) && plan.Bounded == bounded;
        Check(ok, "withdraw:" + what,
            $"cut={plan.Cut} residual=[{Join(plan.Residual)}] unprotected=[{Join(plan.Unprotected)}] " +
            $"floor={plan.Floor} bounded={plan.Bounded} " +
            $"(want cut={wantCut} residual=[{Join(wantResidual)}] " +
            $"unprotected=[{Join(wantUnprotected)}] bounded={bounded})");
        for (int i = 0; i < plan.Residual.Length; i++)
        {
            int at = plan.Residual[i];
            Check(at >= plan.Floor && at < plan.Cut, "withdraw-residual-range:" + what,
                $"residual index {at} is outside [floor={plan.Floor}, cut={plan.Cut})");
            Check(flags != null && flags[at], "withdraw-residual-not-ours:" + what,
                $"residual index {at} is not an ours row");
        }
        for (int i = 0; i < plan.Unprotected.Length; i++)
        {
            int at = plan.Unprotected[i];
            Check(at >= plan.Floor && at < (flags == null ? 0 : flags.Length), "withdraw-unprotected-range:" + what,
                $"unprotected index {at} is outside [floor={plan.Floor}, count)");
            Check(flags != null && !flags[at], "withdraw-unprotected-is-ours:" + what,
                $"unprotected index {at} IS an ours row");
        }
    }

    private static void Confirm(bool known, int native, ushort chosen, bool row, string slug, int donor,
                                ulong client, ConfirmBranch wantBranch, ushort wantIndex, bool wantRecord,
                                string wantSlug, string what)
    {
        var plan = NativeWirePolicy.PlanConfirm(known, native, chosen, row, slug, donor, client);
        bool ok = plan.Branch == wantBranch && plan.Index == wantIndex && plan.Record == wantRecord &&
                  (wantSlug == null ? plan.Slug == null : plan.Slug == wantSlug);
        Check(ok, "confirm:" + what,
            $"branch={plan.Branch} index={plan.Index} record={plan.Record} slug='{plan.Slug}' " +
            $"(want {wantBranch}/{wantIndex}/{wantRecord}/'{wantSlug}')");
    }

    private static bool Same(int[] a, int[] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static string Join(int[] values) =>
        values == null ? "null" : string.Join(",", Array.ConvertAll(values, v => v.ToString()));

    private static void Pending(bool wantedNative, bool ourRow, bool sentRow, PendingVerdict want, string what)
    {
        var got = NativeWirePolicy.PendingConfirm(wantedNative, ourRow, sentRow);
        Check(got == want, "pending:" + what,
            $"got {got} (want {want}) native={wantedNative} ourRow={ourRow} sentRow={sentRow}");
    }

    private static void Check(bool ok, string what, string detail)
    {
        _checks++;
        if (ok) return;
        _bad++;
        Console.WriteLine($"FAIL {what}\n     {detail}");
    }
}
