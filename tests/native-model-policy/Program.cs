using System;
using AlterumModelSwap.Hot;

// NativeModelPolicy.Fallback is the one clamp between a model ID that names a row an unmodified
// peer does not have and an ID that peer can resolve. Two rules, and both are silent when wrong:
//
//   requested < nativeCount  -> the row exists on the native peer, keep the ID EXACTLY. A native
//                               row is a body of its own; re-deriving it from the client id would
//                               put a different body on the player than the one they picked.
//   otherwise                -> clientId % nativeCount: a deterministic slot that needs no
//                               agreement between peers.
//
// "Deterministic" is the whole point of the fallback: two peers that both fail to resolve a custom
// row must land on the SAME native slot for the same actor, so the choice may be a collision but
// never a different body per viewer. nativeCount must be the count captured BEFORE the mod appends
// custom rows; <= 0 (and, per the shipping guard, more rows than a ushort ID can address) is a
// caller bug and throws.
//
// This harness compiles the SHIPPING file by link (not a copy, not a re-implementation).
//
// Run: export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$PATH
//      dotnet run -c Release --project tests/native-model-policy
// Exit 0 = every case matches; 1 = a case is wrong (the failure shows its inputs and both values).
internal static class Program
{
    // One HAND-COMPUTED expectation per case, not a formula. If the harness recomputed
    // clientId % nativeCount it would agree with any wrong modulus the policy used; every Expect
    // below is the literal value the rule requires.
    private sealed class Case
    {
        public string Name;
        public ushort Requested;
        public int NativeCount;
        public ulong ClientId;
        public ushort Expect;
        public string Why;
    }

    private const ulong MaxClient = ulong.MaxValue; // 18446744073709551615

    private static readonly Case[] Cases =
    {
        // ---------- native rows: the ID passes through untouched, whatever the client is ----------
        new Case
        {
            Name = "only_slot", Requested = 0, NativeCount = 1, ClientId = 0, Expect = 0,
            Why = "one native row: slot 0 is the only answer",
        },
        new Case
        {
            Name = "first_slot_ignores_client", Requested = 0, NativeCount = 6, ClientId = 12345, Expect = 0,
            Why = "row 0 is native even when clientId would map to 3; the id must not be re-derived",
        },
        new Case
        {
            Name = "last_slot_max_client", Requested = 5, NativeCount = 6, ClientId = MaxClient, Expect = 5,
            Why = "last native slot, largest possible client id",
        },
        new Case
        {
            Name = "last_slot_ushort_count", Requested = 65534, NativeCount = 65535, ClientId = MaxClient, Expect = 65534,
            Why = "last slot when the native count is ushort.MaxValue",
        },
        new Case
        {
            Name = "max_count_max_request", Requested = 65535, NativeCount = 65536, ClientId = MaxClient, Expect = 65535,
            Why = "the widest native table (65536 rows) still resolves the largest ushort id natively",
        },
        new Case
        {
            Name = "mid_slot_zero_client", Requested = 3, NativeCount = 6, ClientId = 0, Expect = 3,
            Why = "in range with client 0: the fallback must not shadow a native row",
        },
        new Case
        {
            Name = "mid_slot_large_client", Requested = 3, NativeCount = 6, ClientId = 999999, Expect = 3,
            Why = "in range with a large client: 999999 % 6 = 3 coincides, so this pins the native path",
        },
        new Case
        {
            Name = "custom_index_in_range", Requested = 7, NativeCount = 100, ClientId = 1, Expect = 7,
            Why = "a custom index below the native count is treated as native, not remapped",
        },

        // ---------- no native row: deterministic client slot, expected value written out ----------
        new Case
        {
            Name = "requested_equals_count", Requested = 6, NativeCount = 6, ClientId = 7, Expect = 1,
            Why = "boundary: requested == nativeCount is NOT native; 7 % 6 = 1",
        },
        new Case
        {
            Name = "one_past_count", Requested = 7, NativeCount = 6, ClientId = 0, Expect = 0,
            Why = "the first appended custom row; client 0 -> slot 0",
        },
        new Case
        {
            Name = "max_ushort_requested", Requested = 65535, NativeCount = 6, ClientId = 10, Expect = 4,
            Why = "largest ushort requested; 10 % 6 = 4",
        },
        new Case
        {
            Name = "custom_row_index", Requested = 17, NativeCount = 6, ClientId = 0, Expect = 0,
            Why = "a real appended row index (e.g. TC2 Demoman at 17); client 0 -> slot 0",
        },
        new Case
        {
            Name = "client_zero", Requested = 99, NativeCount = 6, ClientId = 0, Expect = 0,
            Why = "client id 0 must not be special-cased away from its slot",
        },
        new Case
        {
            Name = "max_ulong_client", Requested = 6, NativeCount = 6, ClientId = MaxClient, Expect = 3,
            Why = "2^64-1 % 6 = 3 (a 32-bit or signed path would give a different slot)",
        },
        new Case
        {
            Name = "max_ulong_client_1000", Requested = 1000, NativeCount = 1000, ClientId = MaxClient, Expect = 615,
            Why = "2^64-1 % 1000 = 615",
        },
        new Case
        {
            Name = "max_ulong_client_65535", Requested = 65535, NativeCount = 65535, ClientId = MaxClient, Expect = 0,
            Why = "2^64-1 is an exact multiple of 65535, so the slot wraps to 0",
        },
        new Case
        {
            Name = "big_client_1e18", Requested = 6, NativeCount = 6, ClientId = 1000000000000000000, Expect = 4,
            Why = "1e18 % 6 = 4 (no truncation to int)",
        },
        new Case
        {
            Name = "client_above_32_bits", Requested = 6, NativeCount = 6, ClientId = 4294967301, Expect = 3,
            Why = "2^32+5 % 6 = 3; truncating clientId to int would yield 5",
        },
        new Case
        {
            Name = "wrap_at_count", Requested = 5, NativeCount = 5, ClientId = 1000, Expect = 0,
            Why = "1000 % 5 = 0: out-of-range requested with a clean wrap",
        },
        new Case
        {
            Name = "wrap_one_past", Requested = 5, NativeCount = 5, ClientId = 1001, Expect = 1,
            Why = "1001 % 5 = 1: one past the wrap",
        },
    };

    // Client ids every native-path sweep runs: 0, 1, the count itself, and the maximum.
    private static readonly ulong[] Clients = { 0, 1, 6, MaxClient };

    private static int Main()
    {
        int bad = 0;
        bad += Table();
        bad += NativeRangeSweep();
        bad += FallbackSweep();
        bad += FallbackStaysInRange();
        bad += HighIdSweep();
        bad += Determinism();
        bad += Duplicates();
        bad += InvalidCounts();
        Console.WriteLine(bad == 0
            ? $"POLICY OK ({Cases.Length} cases, 7 check groups)"
            : $"POLICY FAILED ({bad} failing checks)");
        Console.WriteLine(bad == 0 ? "VERDICT OK" : "VERDICT FAILED (" + bad + ")");
        return bad == 0 ? 0 : 1;
    }

    // The hand-computed table above.
    private static int Table()
    {
        int bad = 0;
        foreach (var c in Cases)
        {
            ushort got = NativeModelPolicy.Fallback(c.Requested, c.NativeCount, c.ClientId);
            bool ok = got == c.Expect;
            Report(ok, c.Name,
                $"requested={c.Requested} native={c.NativeCount} client={c.ClientId} -> {got} (want {c.Expect})");
            if (!ok)
            {
                bad++;
                Console.WriteLine("     why: " + c.Why);
            }
        }
        return bad;
    }

    // Every in-range requested id survives with every client id: the fallback must not shadow a
    // native row, and the native path must be client-independent.
    private static int NativeRangeSweep()
    {
        int bad = 0;
        const int count = 6;
        foreach (ushort requested in new ushort[] { 0, 1, 2, 3, 4, 5 })
        {
            foreach (ulong client in Clients)
            {
                ushort got = NativeModelPolicy.Fallback(requested, count, client);
                bool ok = got == requested;
                Report(ok, $"native_sweep_{requested}",
                    $"native=6 client={client} -> {got} (want {requested})");
                if (!ok) bad++;
            }
        }
        return bad;
    }

    // Out-of-range ids land on the client's slot. The formula here is the agreement half: the table
    // carries the literal expectations, this pins the whole client range to the same modulus.
    private static int FallbackSweep()
    {
        int bad = 0;
        const int count = 7;
        const ushort requested = 1000; // far outside the native table
        for (ulong client = 0; client <= 200; client++)
        {
            ushort got = NativeModelPolicy.Fallback(requested, count, client);
            ushort want = (ushort)(client % (ulong)count);
            bool ok = got == want;
            Report(ok, $"fallback_sweep_{client}",
                $"native=7 requested=1000 client={client} -> {got} (want {want})");
            if (!ok) bad++;
        }
        return bad;
    }

    // Whatever the table width and however hostile the requested id, the answer is a row the peer
    // actually has: 0 <= result < nativeCount.
    private static int FallbackStaysInRange()
    {
        int bad = 0;
        foreach (int count in new[] { 1, 2, 3, 5, 6, 7, 12, 65535, 65536 })
        {
            foreach (ulong client in new ulong[] { 0, 1, 13, MaxClient })
            {
                ushort got = NativeModelPolicy.Fallback(ushort.MaxValue, count, client);
                bool ok = got < count;
                Report(ok, $"in_range_{count}",
                    $"requested={ushort.MaxValue} native={count} client={client} -> {got} (want < {count})");
                if (!ok) bad++;
            }
        }
        return bad;
    }

    // Client ids above every machine word boundary must not wrap, truncate or change sign.
    private static int HighIdSweep()
    {
        int bad = 0;
        // 2^32, 2^48, 2^63, 2^64-1 scaled to 8 native rows: hand-computed, not recomputed.
        (ulong Client, ushort Expect)[] pinned =
        {
            (4294967296UL, 0),              // 2^32 % 8 = 0
            (281474976710656UL, 0),         // 2^48 % 8 = 0
            (9223372036854775808UL, 0),     // 2^63 % 8 = 0
            (9223372036854775807UL, 7),     // 2^63-1 % 8 = 7
            (18446744073709551615UL, 7),    // 2^64-1 % 8 = 7
        };
        foreach (var p in pinned)
        {
            ushort got = NativeModelPolicy.Fallback(8, 8, p.Client);
            bool ok = got == p.Expect;
            Report(ok, "high_id_boundary",
                $"native=8 requested=8 client={p.Client} -> {got} (want {p.Expect})");
            if (!ok) bad++;
        }
        // And every one of those ids maps inside the table.
        foreach (var p in pinned)
        {
            ushort got = NativeModelPolicy.Fallback(8, 8, p.Client);
            bool ok = got < 8;
            Report(ok, "high_id_in_range", $"client={p.Client} -> {got} (want < 8)");
            if (!ok) bad++;
        }
        return bad;
    }

    // Same inputs, same answer: a repeat call, a 1000-call loop and an out-of-order interleaving of
    // other inputs must all return the slot selected the first time (no cached state, no counter).
    private static int Determinism()
    {
        int bad = 0;
        const ushort requested = 6;
        const int count = 6;
        ushort first = NativeModelPolicy.Fallback(requested, count, MaxClient);
        bool okFirst = first == 3;
        Report(okFirst, "determinism_seed", $"-> {first} (want 3)");
        if (!okFirst) bad++;

        bool stable = true;
        for (int i = 0; i < 1000; i++)
        {
            // Interleave a different actor's decision so any internal state would show up.
            NativeModelPolicy.Fallback(0, 1, (ulong)i);
            NativeModelPolicy.Fallback((ushort)(i % 4), 4, MaxClient - (ulong)i);
            if (NativeModelPolicy.Fallback(requested, count, MaxClient) != first) { stable = false; break; }
        }
        Report(stable, "determinism_repeat", "1000 interleaved calls -> same slot");
        if (!stable) bad++;

        bool repeatedTwice = true;
        foreach (var c in Cases)
        {
            ushort a = NativeModelPolicy.Fallback(c.Requested, c.NativeCount, c.ClientId);
            ushort b = NativeModelPolicy.Fallback(c.Requested, c.NativeCount, c.ClientId);
            if (a != b || a != c.Expect) { repeatedTwice = false; break; }
        }
        Report(repeatedTwice, "determinism_table", "every table case repeated -> identical");
        if (!repeatedTwice) bad++;
        return bad;
    }

    // Collisions are ALLOWED and expected: with more actors than native rows, two actors share a
    // body. That must not throw, must not fall outside the table, and must hit every slot.
    private static int Duplicates()
    {
        int bad = 0;
        const int count = 6;
        int[] hits = new int[count];
        for (ulong client = 0; client < (ulong)(2 * count); client++)
        {
            ushort got = NativeModelPolicy.Fallback((ushort)count, count, client);
            bool inRange = got < count;
            Report(inRange, $"duplicate_in_range_{client}", $"client={client} -> {got} (want < {count})");
            if (!inRange) { bad++; continue; }
            hits[got]++;
        }
        bool allTwice = true;
        foreach (int h in hits) if (h != 2) allTwice = false;
        Report(allTwice, "duplicate_every_slot_twice",
            $"hits=[{string.Join(",", hits)}] over clients 0..{2 * count - 1} (want 2 each)");
        if (!allTwice) bad++;

        // The explicit collision: two different actors, one body, both deterministic.
        ushort one = NativeModelPolicy.Fallback((ushort)count, count, 1);
        ushort seven = NativeModelPolicy.Fallback((ushort)count, count, 7);
        bool collided = one == 1 && seven == 1;
        Report(collided, "duplicate_allowed_collision", $"client 1 -> {one}, client 7 -> {seven} (want 1/1)");
        if (!collided) bad++;
        return bad;
    }

    // nativeCount <= 0 is a caller bug (the count must be positive and captured before the mod
    // appended rows). The shipping guard also refuses counts past the ushort address space; both
    // are checked as an invalid count, and neither may fall through to a row decision.
    private static int InvalidCounts()
    {
        int bad = 0;
        (int Count, string Why)[] invalid =
        {
            (0, "no rows at all: any answer would index out of the native table"),
            (-1, "negative count"),
            (int.MinValue, "the most negative count"),
            (65537, "one past the widest table a ushort ID can address"),
            (70000, "beyond the ushort address space"),
            (int.MaxValue, "the largest count"),
        };
        foreach (var c in invalid)
        {
            // requested 0 is the most tempting passthrough: 0 < 0 is false, so the count check has to
            // come first; the client id is varied to prove the guard is not value-dependent.
            bad += ThrowsCount($"invalid_count_{c.Count}", 0, c.Count, 0, c.Why);
            bad += ThrowsCount($"invalid_count_{c.Count}_max_client", 65535, c.Count, MaxClient, c.Why);
        }
        return bad;
    }

    private static int ThrowsCount(string name, ushort requested, int count, ulong client, string why)
    {
        try
        {
            ushort got = NativeModelPolicy.Fallback(requested, count, client);
            Report(false, name, $"requested={requested} native={count} client={client} -> {got}, no throw (want ArgumentOutOfRangeException)");
            Console.WriteLine("     why: " + why);
            return 1;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            bool named = ex.ParamName == "nativeCount";
            Report(named, name,
                $"requested={requested} native={count} client={client} -> ArgumentOutOfRangeException(param={ex.ParamName ?? "<null>"})");
            if (!named) Console.WriteLine("     why: the thrown exception must name the nativeCount parameter");
            return named ? 0 : 1;
        }
        catch (Exception ex)
        {
            Report(false, name,
                $"requested={requested} native={count} client={client} -> {ex.GetType().Name} (want ArgumentOutOfRangeException)");
            Console.WriteLine("     why: " + why);
            return 1;
        }
    }

    private static void Report(bool ok, string name, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name,-40} {detail}");
    }
}
