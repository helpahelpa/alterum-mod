using System;

namespace AlterumModelSwap.Hot;

// The wanted-vs-applied precedence for the lobby selection pass, as a PURE decision.
//
// Why it is a file of its own: this is the one branch that decides whether the body follows the
// wanted record or the game's applied row, and getting it wrong is invisible in a screenshot until
// it is a player standing in the native body under a custom row's portrait. It has no Unity types,
// so tests/model-sync/real-path compiles THIS file and drives the shipping decision - not a
// copy, not a re-implementation.
//
// The bug this branch has to get right (2026-11-17 03:50, actor B, TC2 Demoman):
// the game's own spawn (`SpawnPlayerModelOnClient` -> `SetupModel('TC2 Demoman')`, index 17) applied
// one of OUR custom rows while the wanted record was an explicit NATIVE row from an earlier
// wardrobe pick. The old branch held the native record, so the body module stayed off and the native
// donor body rendered - while the game's applied row drove the HUD portrait and the wardrobe tab.
// Display said Demoman; the bodies (B and, because B advertised native, A's view of B) were James.
//
// The distinction that matters: an explicit CUSTOM pick is a choice this module has to defend from
// the lobby's stale join-time re-apply (that is what the hold is for). "Native" carries no custom
// body to defend, and the game's applied row is what its own HUD and wardrobe tab show, so following
// it is the only answer whose body and display agree.
internal enum WantedKind
{
    Unknown = 0,     // no record at all
    Custom = 1,      // one of the roster entries
    Native = 2,      // the game's own row, deliberately (or previously adopted)
}

internal enum SelectionAction
{
    AdoptApplied = 0,   // record the game's applied row (what the HUD and the wardrobe show)
    HoldWanted = 1,     // keep the wanted entry; the applied row is the lobby's stale re-apply
}

internal static class ModelWantedPolicy
{
    // Only consulted when the applied row differs from the wanted entry (`hit != Wanted`). For an
    // explicit native wanted record the differing applied row can only be one of our custom rows;
    // for a custom wanted entry it can be another custom row or a native one.
    //
    // The 2026-11-17 08:58Z real leave/rejoin forced a second change here. B's wanted record was
    // Golubiro (ADOPTED, not a deliberate pick) and the new netcode client's actor was spawned with
    // a row that was NOT the record:
    //
    //   * first rejoin, client 6: the game applied Golubiro (index 12) -> equal, this function is
    //     not consulted; body, HUD and wardrobe agreed.
    //   * second rejoin, client 7: the game applied its own NATIVE row (Sarah, index 4) - the
    //     game's stored menu selection. The old branch adopted it, which rewrote the record to
    //     native and let the join roll replace the identity with capuchino two seconds later (a
    //     custom body under a native portrait; b.log:201609/202531).
    //   * third rejoin, client 8 (the fix's own first run): the game applied a DIFFERENT CUSTOM row
    //     (TC2 Soldier, index 15 - the host's model) and the `appliedIsOurs` variant adopted that
    //     too, because it is one of our rows (b.log:209756).
    //
    // So a differing join row is never a decision, whatever it names: the membership spawns a NEW
    // actor and the game feeds its spawn index from stale session/menu state. A custom wanted
    // record (explicit OR adopted) is the identity and is held; the caller re-asserts it through the
    // game's own selection path so the HUD, the wardrobe tab and every peer agree. The record is
    // still replaced by a proven manual pick (the wardrobe RPC hook writes it), and a ROLL record is
    // still demoted by DemoteRolledWanted's own four-fact rule at membership start - those are
    // decisions, this is not.
    //
    // An explicit NATIVE record is still NEVER held: there is no custom body to defend, and the
    // applied row is what the game's HUD/wardrobe show; the old condition (hold for `Wanted == null`
    // too) is what left the native donor body under the Demoman portrait on 2026-11-17 03:50.
    // A custom record whose row is not registered yet has nothing to hold onto either; the caller
    // records the applied row and the normal registration/pending path takes over.
    internal static SelectionAction Lobby(WantedKind wanted, bool wantedDataReady, bool explicitChoice)
    {
        if (wanted == WantedKind.Custom && wantedDataReady) return SelectionAction.HoldWanted;
        return SelectionAction.AdoptApplied;
    }

    // ------------------------------------------------------------------ effective body
    //
    // The second half of the same incident, and the half the 2026-11-17 log could not answer: the
    // identity line reported `bodySlug=-` with no `bodyState=` at all while the wardrobe tab and the
    // HUD portrait named a custom row. "The module is on" is NOT "a body was built": after a
    // skipped bind, a failed asset load or a failed build the game's own body is what renders. This
    // decision is the ONLY place that decides what a display surface may call the body, so the
    // evidence line and the verifier (`body_state_truthful`, `assignment_converges`) read the same
    // answer the renderer does.
    //
    //   native  - no body module is on: the game's own body is the truth
    //   live    - our body object exists, carries a mesh and its spec is the reported slug
    //   pending - the module wants a body but has not built one yet (bind/registration in flight)
    //   failed  - the build failed (asset, parse, renderer or bind exception); a failure line exists
    //
    // A `pending` or `failed` body must report an EMPTY slug: naming a slug there is the false pass
    // the acceptance forbids ("display said Demoman; both bodies were James").
    internal readonly struct BodyReport
    {
        internal readonly string Slug;    // the EFFECTIVE body slug ("" when nothing of ours is on screen)
        internal readonly string State;   // native | live | pending | failed
        internal BodyReport(string slug, string state) { Slug = slug ?? ""; State = state; }
    }

    internal static BodyReport EffectiveBody(bool moduleOn, bool bodyLive, bool buildFailed, string specSlug)
    {
        if (!moduleOn) return new BodyReport("", "native");
        if (bodyLive && !string.IsNullOrEmpty(specSlug)) return new BodyReport(specSlug, "live");
        return new BodyReport("", buildFailed ? "failed" : "pending");
    }

    // A remote actor: the advertised slug is the vouched WANT, the built slug is the effective body.
    // An advertised row with no build is `pending` (the peer may be binding); a native advertisement
    // with no body is `native`.
    internal static BodyReport EffectiveRemoteBody(string advertisedSlug, string builtSlug, bool buildFailed)
    {
        if (!string.IsNullOrEmpty(builtSlug)) return new BodyReport(builtSlug, "live");
        if (buildFailed) return new BodyReport("", "failed");
        return new BodyReport("", string.IsNullOrEmpty(advertisedSlug) ? "native" : "pending");
    }

    // ------------------------------------------------------------------ build retry gate
    //
    // A failing build must not be attempted once per frame: that is a hot loop that re-reads and
    // re-parses the asset and re-logs the failure forever (the sweep found `_buildFailed`,
    // `_buildFailStreak` and `FailBackoffBase/Max` declared and never set). One attempt per window,
    // window doubling from `baseSeconds` and clamped at `maxSeconds`, reset by any success.
    internal static bool BuildAllowed(float now, float nextAttempt) => now >= nextAttempt;

    internal static float NextBuildAttempt(float now, int failStreak, float baseSeconds, float maxSeconds)
    {
        if (failStreak <= 0) return now;
        if (baseSeconds <= 0f) baseSeconds = 1f;
        if (maxSeconds < baseSeconds) maxSeconds = baseSeconds;
        double d = baseSeconds * Math.Pow(2.0, Math.Min(failStreak - 1, 16));
        if (d > maxSeconds) d = maxSeconds;
        return now + (float)d;
    }

    // The delay a FAILED line prints, from the same formula the gate uses (never a second opinion).
    internal static float RetryDelay(int failStreak, float baseSeconds, float maxSeconds)
        => NextBuildAttempt(0f, failStreak, baseSeconds, maxSeconds);

    // ------------------------------------------------------------------ per-lease build health
    //
    // Failure state and the one-shot test fault belong to ONE actor's build episode. SahurRig swaps
    // module state around the wardrobe preview / remote attachment and restores it afterwards; when
    // this block was left out of that swap, a successful remote build cleared the local actor's
    // failure, backoff deadline and test fault (the fault could even be consumed by the wrong body).
    // Keeping the block in one value type makes the swap a value copy, so "restore complete" is
    // checkable field by field instead of depending on eight separate statics.
    internal struct BuildHealth
    {
        internal bool Failed;
        internal string Why;
        internal int Streak;
        internal float NextAttempt;
        internal string TestFault;      // "" | pre | skeleton | body | renderer (one shot)
        internal string AssetError;     // first cause of the last failed asset load

        internal static BuildHealth Fresh() => new BuildHealth
        {
            Why = "", TestFault = "", AssetError = "",
        };

        internal void Normalize()
        {
            Why = Why ?? "";
            TestFault = TestFault ?? "";
            AssetError = AssetError ?? "";
        }

        // A successful build ends the episode: no failure, no streak, no deadline. Callers invoke
        // this inside the lease that owns the build, never on a lease they merely borrowed.
        internal void RecordSuccess()
        {
            Failed = false; Why = ""; Streak = 0; NextAttempt = 0f;
        }

        internal void RecordFailure(string why, int streak, float nextAttempt)
        {
            Failed = true;
            Why = why ?? "";
            Streak = streak;
            NextAttempt = nextAttempt;
        }

        // One-shot selftest. The phase must match the injection point; only the lease that armed it
        // can consume it, and consuming it clears it before the throw (a retry cannot re-fault).
        internal bool TakeFault(string phase)
        {
            if (!string.Equals(TestFault, phase ?? "", StringComparison.Ordinal)) return false;
            TestFault = "";
            return true;
        }
    }

    // The exact swap SahurRig.AttachExternal performs around a preview/remote Build. The harness
    // links this file, so these are the shipping operations, not a second implementation.
    internal static class BuildHealthLease
    {
        internal static BuildHealth Copy(in BuildHealth slot) => slot;

        internal static void EnterExternal(ref BuildHealth slot) => slot = BuildHealth.Fresh();

        internal static void Restore(ref BuildHealth slot, in BuildHealth saved)
        {
            slot = saved;
            slot.Normalize();
        }
    }
}
