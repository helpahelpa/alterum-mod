using System;
using AlterumModelSwap.Hot;

// The lobby precedence decision, driven through the SHIPPING ModelWantedPolicy (linked source, not a
// copy). Every case is anchored to a line of a real captured log; the paths and line numbers are in
// the case comment. This is the seam for the "game applied one of our rows while the wanted record
// says native" family: the decision is the bug, not the renderer.
//
// Run: dotnet run -c Release --project tests/model-sync/real-path
// Exit 0 = every case matches; 1 = a policy case is wrong (the failure names the log evidence).
internal static class Program
{
    private sealed class Case
    {
        public string Name;
        public string Evidence;
        public WantedKind Wanted;
        public bool WantedDataReady;
        public bool ExplicitChoice;
        public SelectionAction Expect;
    }

    private static readonly Case[] Cases =
    {
        // THE REPORTED RED. B at join: the game's spawn applied 'TC2 Demoman' (index 17, one of our
        // rows) while the wanted record was explicit NATIVE from the 01:02 wardrobe pick. The body
        // must follow the applied row, or the HUD/wardrobe (which the game feeds from that row) name
        // Demoman while the native James body renders - on B and on A's view of B.
        // Evidence: .temp/alterum-live/20261116T192223Z/models/logs/b.log:90543
        //   [model:identity] actor=local wantedSlug=- ... appliedRow=TC2 Demoman bodySlug=- ...
        //   [sahur:map] lobby: keeping the explicit wanted entry 'none' - the game re-applied 'TC2 Demoman'
        new Case
        {
            Name = "join_custom_applied_over_explicit_native",
            Evidence = "b.log:90542-90543 (B join, TC2 Demoman applied, wanted=native explicit)",
            Wanted = WantedKind.Native, WantedDataReady = false, ExplicitChoice = true,
            Expect = SelectionAction.AdoptApplied,
        },

        // An explicit CUSTOM pick is still defended against the lobby's stale re-apply: the host saw
        // 'TC2 Scout' re-applied while its wanted entry was TC2 Soldier, and correctly kept Soldier.
        // Evidence: a.log:97618 (host) and b.log:20935 (client, 'Golubiro' vs 'TC2 Soldier').
        //   [sahur:map] lobby: keeping the explicit wanted entry 'TC2 Soldier' - the game re-applied
        //   'TC2 Scout' (index=14), which is an unprovenanced applied row ...
        new Case
        {
            Name = "explicit_custom_held_against_stale_custom",
            Evidence = "a.log:97618 (wanted tc2-soldier, applied TC2 Scout)",
            Wanted = WantedKind.Custom, WantedDataReady = true, ExplicitChoice = true,
            Expect = SelectionAction.HoldWanted,
        },

        // Same hold against a NATIVE re-apply of a custom choice.
        // Evidence: a.log:94861 ('TC2 Soldier' wanted, <native> re-applied) and b.log:2178.
        new Case
        {
            Name = "explicit_custom_held_against_native",
            Evidence = "a.log:94861 (wanted tc2-soldier, applied <native> index=6)",
            Wanted = WantedKind.Custom, WantedDataReady = true, ExplicitChoice = true,
            Expect = SelectionAction.HoldWanted,
        },

        // A recorded custom choice whose row is not registered yet has nothing to hold; the caller
        // adopts the applied row and the normal pending/registration path takes over.
        new Case
        {
            Name = "explicit_custom_pending_adopts",
            Evidence = "shipping behaviour: Wanted.Data == null -> adopt",
            Wanted = WantedKind.Custom, WantedDataReady = false, ExplicitChoice = true,
            Expect = SelectionAction.AdoptApplied,
        },

        // AN ADOPTED CUSTOM RECORD IS THE IDENTITY AT A MEMBERSHIP START. The 2026-11-17 real
        // leave/rejoin: B's wanted record was Golubiro (adopted), and the NEW netcode client's actor
        // was spawned with a row that was not the record - first the game's NATIVE row (Sarah,
        // index 4; client 7), then a DIFFERENT CUSTOM row (TC2 Soldier, index 15, the host's model;
        // client 8). Both were adopted by the old branch, rewrote the record, and the join roll
        // replaced the identity (capuchino body under a native portrait, b.log:202531).
        // A differing join row is never a decision, whatever it names: hold the record and re-assert
        // it through the game's own selection path.
        // Evidence: b.log:201609 (native index=4), b.log:209756 (tc2-soldier adopted over golubiro)
        new Case
        {
            Name = "adopted_custom_held_against_native_join_row",
            Evidence = "b.log:201609 (adopted golubiro, <native> index=4 after real rejoin), b.log:202531 (roll overwrote it)",
            Wanted = WantedKind.Custom, WantedDataReady = true, ExplicitChoice = false,
            Expect = SelectionAction.HoldWanted,
        },

        new Case
        {
            Name = "adopted_custom_held_against_custom_join_row",
            Evidence = "b.log:209756 (adopted golubiro, game applied TC2 Soldier index=15 after the next real rejoin)",
            Wanted = WantedKind.Custom, WantedDataReady = true, ExplicitChoice = false,
            Expect = SelectionAction.HoldWanted,
        },

        // No record at all: follow the game's row (the roll/seed paths run before this).
        new Case
        {
            Name = "undecided_adopts",
            Evidence = "b.log:88873/89970 membership without a wanted record",
            Wanted = WantedKind.Unknown, WantedDataReady = false, ExplicitChoice = false,
            Expect = SelectionAction.AdoptApplied,
        },
    };

    // ---------------------------------------------------------------- effective body
    // The second half of the same incident: the identity line named no body state at all while the
    // HUD/wardrobe named the applied custom row (b.log:90543, `bodySlug=-` with no `bodyState=`).
    // The reported red is a module that is ON with nothing built: reporting its spec is the lie.
    private sealed class BodyCase
    {
        public string Name;
        public bool On, Live, Failed;
        public string Spec;
        public string ExpectSlug, ExpectState;
    }

    private static readonly BodyCase[] BodyCases =
    {
        // THE FALSE PASS. Active with no built body (bind retrying, asset in flight) must NOT report
        // the spec: the pre-fix LiveBodySlug returned Spec.Slug here.
        new BodyCase { Name = "on_but_unbuilt_is_pending", On = true, Live = false, Failed = false,
                       Spec = "tc2-demoman", ExpectSlug = "", ExpectState = "pending" },
        // A built body is the only case that may name a slug.
        new BodyCase { Name = "live_reports_slug", On = true, Live = true, Failed = false,
                       Spec = "tc2-demoman", ExpectSlug = "tc2-demoman", ExpectState = "live" },
        // Failure is its own state, and it carries no slug (the native body is what renders).
        new BodyCase { Name = "failed_is_failed_and_slugless", On = true, Live = false, Failed = true,
                       Spec = "tc2-demoman", ExpectSlug = "", ExpectState = "failed" },
        // Module off: the game's own body is the truth, whatever spec is remembered.
        new BodyCase { Name = "off_is_native", On = false, Live = false, Failed = false,
                       Spec = "tc2-demoman", ExpectSlug = "", ExpectState = "native" },
        // A live body whose spec has no slug cannot invent one.
        new BodyCase { Name = "live_without_slug_is_pending", On = true, Live = true, Failed = false,
                       Spec = "", ExpectSlug = "", ExpectState = "pending" },
    };

    private static int BodyChecks()
    {
        int bad = 0;
        foreach (var c in BodyCases)
        {
            var got = ModelWantedPolicy.EffectiveBody(c.On, c.Live, c.Failed, c.Spec);
            bool ok = got.Slug == c.ExpectSlug && got.State == c.ExpectState;
            Console.WriteLine("{0} {1,-44} on={2,-5} live={3,-5} failed={4,-5} spec='{5}' -> slug='{6}' state={7} (want '{8}'/{9})",
                ok ? "PASS" : "FAIL", c.Name, c.On, c.Live, c.Failed, c.Spec, got.Slug, got.State, c.ExpectSlug, c.ExpectState);
            if (!ok) bad++;
        }
        // Remote: the advertised slug is the WANT; only a build may be reported as the body.
        var rb = ModelWantedPolicy.EffectiveRemoteBody("tc2-demoman", "", false);
        bool rOk = rb.Slug == "" && rb.State == "pending";
        Console.WriteLine("{0} {1,-44} advertised='tc2-demoman' built='' -> slug='{2}' state={3} (want ''/pending)",
            rOk ? "PASS" : "FAIL", "remote_advertised_pending", rb.Slug, rb.State);
        if (!rOk) bad++;
        var rb2 = ModelWantedPolicy.EffectiveRemoteBody("tc2-demoman", "tc2-demoman", false);
        rOk = rb2.Slug == "tc2-demoman" && rb2.State == "live";
        Console.WriteLine("{0} {1,-44} advertised+built -> slug='{2}' state={3} (want 'tc2-demoman'/live)",
            rOk ? "PASS" : "FAIL", "remote_built_is_live", rb2.Slug, rb2.State);
        if (!rOk) bad++;
        var rb3 = ModelWantedPolicy.EffectiveRemoteBody("", "", false);
        rOk = rb3.Slug == "" && rb3.State == "native";
        Console.WriteLine("{0} {1,-44} advertised='' built='' -> slug='{2}' state={3} (want ''/native)",
            rOk ? "PASS" : "FAIL", "remote_native", rb3.Slug, rb3.State);
        if (!rOk) bad++;
        return bad;
    }

    // ---------------------------------------------------------------- failure backoff
    // A failing build must be attempted once per WINDOW, not once per frame, and the window must be
    // bounded. `_buildFailStreak`/`FailBackoffBase/Max` existed unset before this fix (hot loop).
    private static int BackoffChecks()
    {
        int bad = 0;
        float baseS = 1.0f, maxS = 15.0f;
        float[] expect = { 1f, 2f, 4f, 8f, 15f, 15f, 15f };
        for (int streak = 1; streak <= expect.Length; streak++)
        {
            float got = ModelWantedPolicy.RetryDelay(streak, baseS, maxS);
            bool ok = Math.Abs(got - expect[streak - 1]) < 0.001f;
            Console.WriteLine("{0} backoff_streak_{1} delay={2:F0}s (want {3:F0}s, max {4:F0}s)",
                ok ? "PASS" : "FAIL", streak, got, expect[streak - 1], maxS);
            if (!ok) bad++;
        }
        if (!ModelWantedPolicy.BuildAllowed(10f, 10f) || ModelWantedPolicy.BuildAllowed(9.9f, 10f))
        {
            Console.WriteLine("FAIL build_gate               (allowed before the deadline or refused at it)");
            bad++;
        }
        else Console.WriteLine("PASS build_gate               no attempt before the deadline, allowed at it");
        if (Math.Abs(ModelWantedPolicy.NextBuildAttempt(100f, 0, baseS, maxS) - 100f) > 0.001f)
        {
            Console.WriteLine("FAIL backoff_reset            (streak 0 did not clear the gate)");
            bad++;
        }
        else Console.WriteLine("PASS backoff_reset            streak 0 clears the gate to now");
        return bad;
    }

    // ---------------------------------------------------------------- per-lease build health
    // The shipping SahurRig swap operations (ModelWantedPolicy.BuildHealthLease) around an external
    // preview/remote Build. A successful external build must not clear the local actor's failure,
    // a failed external build must not install one, and the one-shot selftest fault must not be
    // consumable by the external lease. The old snapshot omitted all of this state.
    private static int BuildHealthChecks()
    {
        int bad = 0;

        void Expect(string name, bool ok, string detail)
        {
            Console.WriteLine("{0} {1,-52} {2}", ok ? "PASS" : "FAIL", name, detail);
            if (!ok) bad++;
        }

        var local = new ModelWantedPolicy.BuildHealth
        {
            Failed = true, Why = "asset=null", Streak = 2, NextAttempt = 42.5f,
            TestFault = "body", AssetError = "parse failed",
        };
        var saved = ModelWantedPolicy.BuildHealthLease.Copy(local);
        var lease = saved;

        ModelWantedPolicy.BuildHealthLease.EnterExternal(ref lease);
        Expect("lease_enter_fresh", !lease.Failed && lease.Streak == 0 && lease.NextAttempt == 0f &&
               lease.TestFault == "" && lease.AssetError == "", "external lease starts with no local episode");

        lease.RecordSuccess();                         // a remote build succeeded
        ModelWantedPolicy.BuildHealthLease.Restore(ref lease, saved);
        Expect("remote_success_keeps_local_failure",
               lease.Failed && lease.Why == "asset=null" && lease.Streak == 2 &&
               Math.Abs(lease.NextAttempt - 42.5f) < 0.001f && lease.AssetError == "parse failed",
               $"local failed={lease.Failed} streak={lease.Streak} next={lease.NextAttempt} why='{lease.Why}'");

        var healthy = ModelWantedPolicy.BuildHealth.Fresh();
        var healthySaved = ModelWantedPolicy.BuildHealthLease.Copy(healthy);
        var lease2 = healthySaved;
        ModelWantedPolicy.BuildHealthLease.EnterExternal(ref lease2);
        lease2.RecordFailure("remote-only failure", 4, 8f);   // a remote build failed
        ModelWantedPolicy.BuildHealthLease.Restore(ref lease2, healthySaved);
        Expect("remote_failure_keeps_local_healthy",
               !lease2.Failed && lease2.Streak == 0 && lease2.NextAttempt == 0f && lease2.Why == "",
               $"local failed={lease2.Failed} streak={lease2.Streak} next={lease2.NextAttempt}");

        // The fault belongs to the local lease. An external Build that reaches the same phase must
        // NOT consume it, or the local actor silently loses the one-shot injection.
        var faulted = ModelWantedPolicy.BuildHealth.Fresh();
        faulted.TestFault = "body";
        var faultedSaved = ModelWantedPolicy.BuildHealthLease.Copy(faulted);
        var lease3 = faultedSaved;
        ModelWantedPolicy.BuildHealthLease.EnterExternal(ref lease3);
        bool externalTook = lease3.TakeFault("body");
        ModelWantedPolicy.BuildHealthLease.Restore(ref lease3, faultedSaved);
        bool localTook = lease3.TakeFault("body");
        bool localAgain = lease3.TakeFault("body");
        Expect("fault_not_consumed_by_external_lease", !externalTook && localTook && !localAgain,
               $"externalTook={externalTook} localTook={localTook} secondTake={localAgain}");

        // Restore completeness: every field, including the asset error, survives an external mutate.
        var original = new ModelWantedPolicy.BuildHealth
        {
            Failed = true, Why = "w", Streak = 7, NextAttempt = 9.25f, TestFault = "renderer",
            AssetError = "a",
        };
        var origSaved = ModelWantedPolicy.BuildHealthLease.Copy(original);
        var lease4 = origSaved;
        ModelWantedPolicy.BuildHealthLease.EnterExternal(ref lease4);
        lease4.RecordSuccess();
        lease4.RecordFailure("other", 99, 123f);
        lease4.TestFault = "skeleton";
        lease4.AssetError = "other-asset";
        ModelWantedPolicy.BuildHealthLease.Restore(ref lease4, origSaved);
        Expect("lease_restore_complete",
               lease4.Failed == original.Failed && lease4.Why == original.Why &&
               lease4.Streak == original.Streak && Math.Abs(lease4.NextAttempt - original.NextAttempt) < 0.001f &&
               lease4.TestFault == original.TestFault && lease4.AssetError == original.AssetError,
               $"failed={lease4.Failed} why='{lease4.Why}' streak={lease4.Streak} next={lease4.NextAttempt} " +
               $"fault='{lease4.TestFault}' asset='{lease4.AssetError}'");

        return bad;
    }

    private static int Main()
    {
        int bad = 0;
        foreach (var c in Cases)
        {
            var got = ModelWantedPolicy.Lobby(c.Wanted, c.WantedDataReady, c.ExplicitChoice);
            bool ok = got == c.Expect;
            Console.WriteLine("{0} {1,-44} wanted={2,-7} dataReady={3,-5} explicit={4,-5} -> {5} (want {6})",
                ok ? "PASS" : "FAIL", c.Name, c.Wanted, c.WantedDataReady, c.ExplicitChoice, got, c.Expect);
            if (!ok)
            {
                bad++;
                Console.WriteLine("     evidence: " + c.Evidence);
            }
        }
        Console.WriteLine(bad == 0
            ? "POLICY OK (" + Cases.Length + " cases)"
            : "POLICY FAILED (" + bad + "/" + Cases.Length + " cases)");
        bad += BodyChecks();
        bad += BackoffChecks();
        bad += BuildHealthChecks();
        Console.WriteLine(bad == 0 ? "VERDICT OK" : "VERDICT FAILED (" + bad + ")");
        return bad == 0 ? 0 : 1;
    }
}
