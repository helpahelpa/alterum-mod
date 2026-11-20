#!/usr/bin/env python3
"""Pin how SahurWardrobe turns "how many model rows shipped?" into a replicated-index decision.

`PlayerModelDatabase.Models` is the game's whole model-index space: a value in it can be replicated
to every peer, and `SpawnPlayerModelOnClient` indexes the receiver's own array with it WITHOUT a
bounds check (alterum-re docs/NETWORKING.md §5). The count of shipped rows at the front of that array
is the only safe wire range.

The rules these tests pin:

  * the prefix comes from the game's shipped resource set and nothing else - not from
    `models.Length` (that array grows), not from where the first row this generation recognises sits
    (an older generation's row has no pointer in this generation's Entries), and not from
    `PlayerModelData.Index`. The proof is re-derived by each generation, so the hot assembly has no
    dependency on a host contract member (a running host may predate one):
  * the capture is PROVEN against the shipped resource set, `Resources.LoadAll<PlayerModelData>(
    ResourcePathes.PlayerModelDatas)` - the exact way `PlayerModelDatabase.Initialize` fills the
    array. Rows [0, shipped.Length) of the live array must be exactly that set by native pointer, so
    an already-expanded database (a previous generation's rows follow the shipped ones) still yields
    the SHIPPED count. A foreign, duplicate or null row inside the candidate prefix fails the proof
    one-way instead; an unproven prefix is fail-closed (no append, no prune, the wire remap and the
    local confirmation clamp to row 0). A roster label is NEVER evidence of the boundary;
  * the guard installation is all-or-nothing: `_wireGuard` opens only after every mandatory prefix is
    attached, a failed install withdraws every row it can prove is ours whenever no session is live
    (a live session suppresses the shrink and reports that peers cannot be protected), and the
    Harmony owner id is unique per hot generation so a retiring generation's `UnpatchSelf` cannot
    strip the guard the fresh generation just installed;
  * nothing shrinks `PlayerModelDatabase.Models` while a network session is live: the game's own
    dictionaries can hold an index at or above the prefix that a shrink would retarget or truncate.
    A prune therefore also requires a captured prefix, no live session and the guard; a failsafe
    withdrawal cannot shrink either, and reports "CANNOT PROTECT PEERS" when our rows stay;
  * the failsafe withdrawal cuts only a CONTIGUOUS TAIL of rows this module can prove are its own, so
    every surviving index keeps its value, and residual rows that cannot be cut are reported even
    when nothing was cut. It NEVER cuts with an unproven prefix (a shipped row could be in the
    candidate suffix) and it reports every non-shipped row that stays, not only the ones this
    generation recognises as its own;
  * the outbound wire index is ONE decision: the local confirmation clamps before any session, wanted
    or `_ourApply` gating, the programmatic host write and the guest RPC both use the same decision,
    and that decision prefers a donor the caller can prove is a shipped row (the row the body already
    carries, else the row the entry was cloned from) to an arbitrary per-client row;
  * a guest's RPC send is not a confirmation: the record is `pending` and only an observed applied
    row confirms it (our entry's row, or the row the send carried - never our row for a native pick).

Static structure is pinned here; the DECISIONS are exercised behaviorally by
tests/native-wire-guard/behavior, which compiles the marked pure block from this same
SahurWardrobe.cs verbatim (and the shipping NativeModelPolicy) and runs the review's edge cases. The
masker and the structure readers are the wanted-model ones, imported from the sibling test (one
implementation, not a copy).

Run:  python3 tests/native-wire-guard/test_native_wire_guard.py
Exit: 0 every pin holds, 1 a pin is broken (the failure names the invariant and the code it read).
"""

from __future__ import annotations

import importlib.util
import os
import re
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "tracks/merged/mod/AlterumModelSwap.Hot/SahurWardrobe.cs"
WANTED = ROOT / "tests/wanted-model/test_wanted_intent.py"
BEHAVIOR = ROOT / "tests/native-wire-guard/behavior"
GEN_PURE = BEHAVIOR / "gen_pure.py"


def _load(name: str, path: Path):
    """Load a sibling test module by path (no package, no sys.path edit)."""
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


wanted = _load("_sahur_wanted_intent", WANTED)
gen_pure = _load("_native_wire_gen_pure", GEN_PURE)


class NativeWireGuard(unittest.TestCase):
    raw = SOURCE.read_text(encoding="utf-8")
    src = wanted.mask(raw)

    # ------------------------------------------------------------------ one prefix owner

    def test_prefix_has_no_host_contract_dependency(self):
        """The prefix is proven per generation; a running host's contract member is never required."""
        for gone in (".NativeModelPrefix", "INativeModelPrefix", "PrefixHolder",
                     "CaptureBeforeAppend", "MarkUnknown"):
            self.assertNotIn(gone, self.src,
                             f"{gone} must be gone: a hot generation has to bind against a host "
                             "whose contracts predate that member")
        known = wanted.body(self.src, "KnownPrefix")
        self.assertIn("_prefixKnown", known)
        self.assertIn("_nativeCount", known)
        self.assertNotIn("TryGet", known, "the prefix must not be read through a host holder")
        writers = sorted({wanted.enclosing(self.src, m.start())
                          for m in re.finditer(r"_nativeCount\s*=\s*verdict\.Count", self.src)})
        self.assertEqual(writers, ["EnsureNativePrefix"],
                         f"the proven count is written by {writers}; the resource proof is the one owner")
        opens = sorted({wanted.enclosing(self.src, m.start())
                        for m in re.finditer(r"_prefixKnown\s*=\s*true", self.src)})
        self.assertEqual(opens, ["EnsureNativePrefix"],
                         f"_prefixKnown is opened by {opens}; only a proven prefix may do that")

    def test_prefix_is_not_inferred_from_row_positions_or_labels(self):
        """A label is ownership evidence for a cut, never the shipped boundary."""
        for gone in ("FirstCustomRow", "SanitizeCustomTail", "ContainsOwnRow"):
            self.assertNotIn(gone, self.src,
                             f"{gone} must be gone: the boundary may not come from this generation's "
                             "own rows or their labels")
        self.assertNotIn("nativeCount = i", self.src,
                         "no code may take the boundary from a row POSITION in the array")
        for name in ("EnsureNativePrefix", "ShippedRows", "EnsureRegistered", "RemapModelIndex",
                     "OnLocalModelConfirmed"):
            text = wanted.body(self.src, name)
            for ident in ("IsOurData", "IsOurRow"):
                self.assertNotIn(ident, text,
                                 f"{name} must not classify rows to find the shipped prefix - the "
                                 "prefix is proven against the Resources set, not the roster")

    # ------------------------------------------------------------------ proof or fail closed

    def test_capture_is_proven_against_the_shipped_resource_set(self):
        """The proof is the game's resource set (identity), not `models.Length` and not a label."""
        rows = wanted.body(self.src, "ShippedRows")
        self.assertIn("Resources.LoadAll<PlayerModelData>", rows,
                      "the shipped rows must be loaded the way PlayerModelDatabase.Initialize loads them")
        self.assertIn("ResourcePathes.PlayerModelDatas", rows,
                      "the resource path must be the game's own constant, not a copy of the string")
        identity = wanted.body(self.src, "Identity")
        self.assertIn(".Pointer", identity,
                      "identity must be the native pointer: two peers resolve the same native object, "
                      "a name or an Index mirror is not proof")
        self.assertIn("Identity(shipped[", rows, "the shipped set must be reduced to native identities")
        self.assertNotIn("IsOurRow", rows, "the resource set does not need a roster-label guess")

        text = wanted.body(self.src, "EnsureNativePrefix")
        prove = text.find("NativeWirePolicy.ProvePrefix")
        assign = text.find("_nativeCount = verdict.Count")
        self.assertGreaterEqual(prove, 0, "EnsureNativePrefix must prove the prefix from the resource set")
        self.assertGreater(assign, prove,
                           "the proven count must be adopted only AFTER the shipped-set proof: "
                           "models.Length records the expanded size on a reloaded database")
        self.assertNotRegex(text, r"_nativeCount\s*=\s*models\.Length",
                            "the live array length must never become the shipped count")
        self.assertRegex(text, r"for \(int i = 0; i < shipped\.Length; i\+\+\)\s*candidate\[i\] = Identity\(models\[i\]\)",
                         "the proof must be stated over EXACTLY the first shipped.Length rows of the "
                         "live array")
        self.assertIn("_prefixFailed = true", text,
                      "a failed proof must record the one-way failure")
        self.assertIn("NoNativePrefix", text,
                      "the unknown path must go through the one fail-closed reporter")

    def test_shipped_resource_failure_waits_or_fails_closed(self):
        """No usable resource set -> never adopt; a bad shipped row -> one-way fail closed."""
        rows = wanted.body(self.src, "ShippedRows")
        for needle in ("shipped == null || shipped.Length == 0", "Identity(shipped[i])",
                       "IntPtr.Zero", "return null"):
            self.assertIn(needle, rows,
                          f"ShippedRows must refuse an unusable resource set ({needle})")
        start, end = wanted.span(self.src, "ShippedRows")
        raw = self.raw[start:end]
        self.assertIn("FAIL CLOSED (shipped rows)", raw,
                      "a failed Resources load must say it failed closed")
        text = wanted.body(self.src, "EnsureNativePrefix")
        wait = text.find("models.Length < shipped.Length")
        self.assertGreater(wait, 0,
                           "a database shorter than the resource set is initializing: wait, do not "
                           "fail the prefix")
        fail = text.find("case PrefixProof.FailClosed")
        mark = text.find("_prefixFailed = true", fail)
        fallthrough = text.find("default:", fail)
        self.assertGreaterEqual(fail, 0, "the proof must distinguish a disproven prefix")
        self.assertGreater(mark, fail,
                           "only a disproven prefix may fail the proof one-way")
        self.assertGreater(fallthrough, mark,
                           "_prefixFailed belongs to the FailClosed case, not the Wait default")
        proven = text.find("case PrefixProof.Proven")
        assign = text.find("_nativeCount = verdict.Count", proven)
        self.assertGreater(assign, proven, "the proven count is adopted in the Proven case")
        self.assertLess(assign, fail, "a disproven prefix never adopts a count")
        self.assertIn("if (_prefixFailed) return false;", text,
                      "a failed proof is one-way: no later pass may retry and adopt a guess")

    def test_unknown_prefix_fails_closed_everywhere_it_is_used(self):
        """No captured prefix -> no safe index is known, so every consumer clamps and says so."""
        remap = wanted.body(self.src, "RemapModelIndex")
        known = remap.find("KnownPrefix")
        fail = remap.find("NoNativePrefix")
        clamp = remap.find("index = 0")
        wire = remap.find("NativeWirePolicy.ShippedWireIndex")
        self.assertGreaterEqual(known, 0, "RemapModelIndex must read the captured prefix")
        self.assertGreaterEqual(fail, 0, "RemapModelIndex must report the fail-closed state")
        self.assertGreaterEqual(clamp, 0, "RemapModelIndex must clamp to row 0 when no prefix is known")
        self.assertGreater(clamp, fail, "report the state before the clamp")
        self.assertGreater(wire, clamp, "the clamped early return must come before the normal remap")
        block = self.pure_block()
        self.assertIn("NativeModelPolicy.Fallback", wanted.body(block, "ShippedWireIndex"),
                      "the ONE wire decision must still end on a native row an unmodified peer can resolve")

        # The local confirm's index now comes from the pure plan; row 0 in the no-prefix branch is
        # behavioral (tests/native-wire-guard/behavior), and the plan must run before the record.
        confirm = wanted.body(self.src, "OnLocalModelConfirmed")
        plan = confirm.find("NativeWirePolicy.PlanConfirm")
        assign = confirm.find("chosenModelIndex = plan.Index")
        record = confirm.find("RecordWanted")
        no_prefix = confirm.find("NoNativePrefix")
        own = confirm.find("_ourApply")
        session = confirm.find("ConfirmSession")
        self.assertGreaterEqual(plan, 0, "the confirm must use the pure confirm plan")
        self.assertGreaterEqual(assign, 0, "the confirm must assign the planned wire index")
        self.assertGreaterEqual(no_prefix, 0, "the no-prefix branch must report fail-closed")
        self.assertGreater(assign, plan, "the plan is computed before the index is assigned")
        self.assertGreater(record, assign,
                           "THE CLAMP MUST COME BEFORE THE WANTED RECORD: a failed record (or no "
                           "scoped lobby) must not leave a custom index on the wire")
        self.assertGreater(own, assign,
                           "THE CLAMP MUST COME BEFORE THE OWN-APPLY GATE: _ourApply returned before the "
                           "rewrite, so this module's own apply carried the raw custom index")
        self.assertGreater(session, assign,
                           "THE CLAMP MUST COME BEFORE THE SESSION GATE: a confirmation outside a scoped "
                           "lobby also carried the raw custom index to the game's RPC")
        self.assertIn("plan.Record", confirm,
                      "recording must be an output of the plan, not a precondition of the clamp")

    def test_guest_outbound_clamps_before_the_game_rpc(self):
        """A guest's own row never reaches a stock host through OnModelSelectedServerRpc."""
        text = wanted.body(self.src, "ApplyIndex")
        clamp = text.find("ShippedWireIndex")
        send = text.find("TryGuestSelect")
        self.assertGreaterEqual(clamp, 0,
                                "the guest path must clamp its outbound row to a shipped row")
        self.assertGreater(send, clamp, "the clamp must happen before the game's RPC")
        self.assertIn("NoNativePrefix", text,
                      "an unknown prefix must be reported on the guest path too")
        self.assertNotRegex(text, r"TryGuestSelect\([^)]*\(ushort\)target",
                            "TryGuestSelect must receive the clamped row, not the raw custom index")
        # The host write takes the same value: one decision, not two.
        host = text.find("SetBaseModelIndex")
        self.assertGreater(host, clamp, "the host write must receive the clamped row too")
        self.assertRegex(text, r"SetBaseModelIndex\(clientId, wireRow\)",
                         "the host write must not receive the raw target index")

    def test_programmatic_selection_prefers_a_provable_native_donor(self):
        """A per-client row is not a donor: keep the shipped row the body already carries."""
        text = wanted.body(self.src, "ApplyIndex")
        self.assertIn("NativeDonorRow", text,
                      "the programmatic path must ask for a provable donor, not an arbitrary row")
        self.assertRegex(text, r"ShippedWireIndex\(prefixKnown, nativeCount, requestedRow, donor, clientId\)",
                         "the donor must be handed to the ONE wire decision")
        confirm = wanted.body(self.src, "OnLocalModelConfirmed")
        self.assertIn("NativeDonorRow", confirm,
                      "the local confirmation must use the same provable donor")
        remap = wanted.body(self.src, "RemapModelIndex")
        self.assertIn("NativeDonorRow", remap,
                      "the server-side remap must keep a provable local donor, not an arbitrary row")
        self.assertRegex(remap, r"clientId\s*==\s*nm\.LocalClientId",
                         "another client's applied row cannot be read here; the donor is local-only")
        donor = wanted.body(self.src, "NativeDonorRow")
        self.assertIn("AppliedShippedRow", donor,
                      "the applied row wins when it is a shipped row")
        self.assertIn("Donor", donor,
                      "the clone donor is the fallback: the actor the body rig replaces")
        self.assertRegex(donor, r"i\s*<\s*bound",
                         "a donor must be inside the proven prefix, never one of our own rows")
        index = wanted.body(self.src, "DonorIndex")
        self.assertRegex(index, r"i\s*<\s*bound",
                         "the revert/donor lookup must stay inside the proven prefix as well")
        self.assertIn("KnownPrefix", index,
                      "the revert/donor lookup must consult the proven prefix")
        prune = wanted.body(self.src, "PruneRetired")
        self.assertIn("donorBound", prune,
                      "a prune that moves the applied row must also pick a SHIPPED donor, not a "
                      "custom row that no stock peer can resolve")
        self.assertIn("KnownPrefix", prune,
                      "the prune's donor bound must come from the proven prefix")

    def test_guest_send_is_pending_not_confirmed(self):
        """An RPC send has no acknowledgement: only an observed applied row may confirm the pick."""
        text = wanted.body(self.src, "ApplyIndex")
        pending = text.find("NotePendingWanted")
        explicit = text.find("NoteExplicitWanted")
        self.assertGreaterEqual(pending, 0, "the guest path must record a PENDING intent")
        self.assertGreater(explicit, pending,
                           "the confirmed record must not be the guest branch: it would claim the "
                           "send landed")
        self.assertRegex(text, r"if\s*\(guestSend\)\s*NotePendingWanted\s*\(",
                         "the pending record must be gated on the guest send flag")
        start, end = wanted.span(self.src, "ApplyIndex")
        raw = self.raw[start:end]
        self.assertNotIn("the host owns the row)", raw,
                         "the send must not be described as the row landing")
        self.assertIn("PENDING", raw, "the guest send line must say the intent is pending")
        # The wardrobe confirm is the same kind of send on a client: the pick is recorded, but as
        # pending, because the host owns the row there too.
        confirm = wanted.body(self.src, "OnLocalModelConfirmed")
        self.assertRegex(confirm, r"RecordWanted\([^)]*!NetRole\.IsServer",
                         "a client confirm must record the pick as pending, a host confirm as landed")
        start, end = wanted.span(self.src, "OnLocalModelConfirmed")
        self.assertIn("state=PENDING", self.raw[start:end],
                      "the client confirm line must say the record is pending, not that the row landed")
        note = wanted.body(self.src, "NotePendingWanted")
        self.assertRegex(note, r"RecordWanted\([^)]*true\s*,\s*wireRow",
                         "the pending record must carry the row the send carried")
        confirm = wanted.body(self.src, "ConfirmPending")
        self.assertIn("NativeWirePolicy.PendingConfirm", confirm,
                      "confirmation must be the pure decision, not an inline guess")
        self.assertIn("AppliedData", wanted.body(self.src, "SyncSelection"),
                      "the confirmation runs on the same applied-row read")
        sync = wanted.body(self.src, "SyncSelection")
        self.assertGreater(sync.find("ConfirmPending"), sync.find("AppliedData"),
                           "the pending record must be confirmed against the applied row")
        block = self.pure_block()
        pure = wanted.body(block, "PendingConfirm")
        self.assertRegex(pure, r"if\s*\(\s*!wantedNative\s*&&\s*ourRow\s*\)",
                         "a deliberate-native intent must never be confirmed by our own row")
        self.assertIn("sentRow", pure,
                      "the row the send carried is the other confirmation source")


    def test_registration_needs_the_guard_and_the_prefix_before_the_append(self):
        text = wanted.body(self.src, "EnsureRegistered")
        guard = text.find("_wireGuard")
        prefix = text.find("EnsureNativePrefix")
        append = text.find("PlayerModelDatabase._models = grown")
        self.assertGreaterEqual(guard, 0, "registration must stay gated on the wire guard")
        self.assertGreater(prefix, guard, "the prefix check follows the guard (both are required)")
        self.assertGreater(append, prefix,
                           "the row must be appended only after the shipped prefix is proven")
        self.assertRegex(text, r"models\.Length\s*<\s*_nativeCount",
                         "a database shorter than the captured shipped table must be refused, not "
                         "treated as a new prefix")

    # ------------------------------------------------------------------ guard install failsafe

    def test_guard_opens_only_after_every_mandatory_patch(self):
        text = wanted.body(self.src, "InstallClickProbe")
        # Method NAMES are string literals here (AccessTools.DeclaredMethod), which the mask blanks;
        # the declaring TYPES and the hook delegates are code, and they are what is pinned.
        targets = ("PlayersAppearanceManager", "PlayersModelsNetworkData", "PlayersModelSetuper",
                   "WardrobeController")
        for target in targets:
            self.assertIn(target, text,
                          f"{target} declares a mandatory native-wire prefix; without that patch an "
                          "index naming one of our rows can still reach an unmodified peer")
        for hook in ("SafeModelIndex", "SafeSavedModelIndex", "OnLocalModelConfirmed", "BeforeGameSpawn"):
            self.assertIn(hook, text, f"the {hook} prefix must stay attached by the installer")
        patches = [m.start() for m in re.finditer(r"\.Patch\s*\(", text)]
        opens = [m.start() for m in re.finditer(r"_wireGuard\s*=\s*true", text)]
        self.assertTrue(patches, "no Harmony.Patch calls in the installer")
        self.assertTrue(opens, "the installer never opens the gate")
        self.assertGreater(opens[0], patches[-1],
                           "_wireGuard may only be set after the LAST mandatory prefix is attached; "
                           "a partial install is as unsafe as none at all")

    def test_failed_install_unpatches_and_withdraws(self):
        text = wanted.body(self.src, "InstallClickProbe")
        catch = text[text.rindex("catch (Exception e)"):]
        self.assertIn("_wireGuard = false", catch,
                      "a failed install must close the gate it may have opened")
        self.assertIn("UnpatchSelf", catch, "a failed install must detach what it attached")
        self.assertIn("WithdrawCustomRows", catch,
                      "rows a previous generation left must not wait for the next sync pass")

    def test_guard_owner_is_unique_per_generation(self):
        """A shared Harmony owner id let the retiring generation strip the fresh guard."""
        self.assertIn("Guid.NewGuid()", self.src,
                      "the probe owner id must be minted per hot generation")
        self.assertNotRegex(self.src, r'new Harmony\s*\(\s*"',
                            "a fixed Harmony owner id is removed by the retiring generation's "
                            "UnpatchSelf, which is exactly the reload fail-open this closes")
        self.assertRegex(self.src, r"new Harmony\s*\(\s*ProbeId\s*\)",
                         "the installer must use the per-generation id")

    # ------------------------------------------------------------------ shrinking

    def test_every_database_shrink_is_one_of_the_reviewed_paths(self):
        owners = [wanted.enclosing(self.src, m.start())
                  for m in re.finditer(r"PlayerModelDatabase\._models\s*=(?!=)", self.src)]
        self.assertEqual(sorted(owners),
                         ["EnsureRegistered", "PruneDuplicates", "PruneRetired", "WithdrawCustomRows"],
                         f"the database is written by {sorted(owners)}: one append and three "
                         "reviewed shrink paths, each gated (see below)")

    def test_prune_paths_are_gated(self):
        for name in ("PruneDuplicates", "PruneRetired"):
            text = wanted.body(self.src, name)
            gate = text.find("CanShrink")
            shrink = text.find("PlayerModelDatabase._models =")
            self.assertGreaterEqual(gate, 0, f"{name} must ask CanShrink before it shrinks")
            self.assertGreater(shrink, gate,
                               f"{name} shrinks without the gate: a removed row shifts every index "
                               "above it")
        gate = wanted.body(self.src, "CanShrink")
        for needle in ("KnownPrefix", "_wireGuard", "SessionActive", "NativeWirePolicy.CanPrune"):
            self.assertIn(needle, gate,
                          f"CanShrink must consult {needle}: a prune needs the prefix, no live "
                          "session, the guard, and the shipped-boundary decision")
        block = self.pure_block()
        prune = wanted.body(block, "CanPrune")
        self.assertIn("if (sessionActive) return ShrinkVerdict.DeferSession;", prune,
                      "a live session must defer the prune even with the guard installed")
        self.assertRegex(prune, r"drop\[i\]\s*<\s*nativeCount",
                         "only rows at or above the shipped prefix may be removed")

    def test_withdrawal_never_shrinks_a_live_session(self):
        text = wanted.body(self.src, "WithdrawCustomRows")
        session = text.find("SessionActive")
        cut = text.find("PlayerModelDatabase._models = kept")
        self.assertGreaterEqual(session, 0,
                                "the withdrawal must consult the live session before cutting")
        self.assertGreater(cut, session,
                           "a shrink must be unreachable while a session is live")
        start, end = wanted.span(self.src, "WithdrawCustomRows")
        raw = self.raw[start:end]
        self.assertIn("CANNOT PROTECT PEERS", raw,
                      "rows that stay because a live session forbids the shrink must be reported as "
                      "an inability to protect stock peers, not silently kept")
        self.assertRegex(raw, r"NoteWire\(log, \$?\"\[model:native-wire\] CANNOT PROTECT PEERS",
                         "the inability report is an error line, not informational")

    def test_withdrawal_cuts_only_the_provable_tail_and_reports_residuals(self):
        text = wanted.body(self.src, "WithdrawCustomRows")
        known = text.find("KnownPrefix")
        plan = text.find("NativeWirePolicy.PlanWithdrawal")
        shrink = text.find("PlayerModelDatabase._models = kept")
        self.assertGreaterEqual(known, 0,
                                "the withdrawal must consult the captured prefix before cutting")
        self.assertGreater(plan, known, "the boundary is decided before the array is replaced")
        self.assertGreater(shrink, plan, "the plan decides the cut before the shrink")
        self.assertIn("IsOurRow(models[i])", text,
                      "ownership must be proven per row before a row is cut")
        self.assertRegex(text, r"PlanWithdrawal\(\s*havePrefix\b",
                         "the proven-prefix flag must reach the plan: with no prefix nothing may be cut")
        block = self.pure_block()
        plan_body = wanted.body(block, "PlanWithdrawal")
        self.assertRegex(plan_body, r"while\s*\(\s*cut\s*>\s*floor\s*&&\s*isOurs\[cut\s*-\s*1\]\s*\)",
                         "the cut must walk a contiguous suffix, not a drop list: a middle removal "
                         "shifts every surviving index above it")
        self.assertRegex(plan_body, r"for \(int i = floor; i < cut; i\+\+\) if \(isOurs\[i\]\)",
                         "rows of ours below the cut are residual, never silently dropped")
        self.assertRegex(plan_body, r"if \(bounded\)\s*\n\s*while \(cut > floor",
                         "the cut must be reachable only with a proven, positive prefix: on an "
                         "unknown boundary a shipped row could be in the candidate suffix")

        residual = text.find("if (plan.Residual.Length > 0)")
        self.assertGreater(residual, shrink,
                           "residual reporting must run after the cut decision")
        start, end = wanted.span(self.src, "WithdrawCustomRows")
        raw = self.raw[start:end]
        self.assertIn("could not withdraw", raw,
                      "a row of ours that cannot be cut must be reported, not silently kept")
        self.assertRegex(raw, r"could not withdraw[^\n]*custom row\(s\)",
                         "residual reporting must name every row that stays")
        # "even if nothing cut": the residual if must be a sibling of the shrink if, not nested.
        line = [l for l in text.splitlines() if "if (plan.Residual.Length > 0)" in l][0]
        self.assertEqual(len(line) - len(line.lstrip()), 8,
                         "the residual report must run even when plan.Cut == models.Length; it is "
                         "nested inside the shrink branch in the current source")

    def test_withdrawal_reports_every_unprotected_non_shipped_row(self):
        """Only reporting rows this generation recognises hides another module's row (or a stale
        rename) that a stock peer still cannot resolve, and it also hides the unknown-prefix case."""
        text = wanted.body(self.src, "WithdrawCustomRows")
        unprotected = text.find("if (plan.Unprotected.Length > 0)")
        self.assertGreaterEqual(unprotected, 0,
                                "the withdrawal must report the non-shipped rows it cannot remove")
        start, end = wanted.span(self.src, "WithdrawCustomRows")
        raw = self.raw[start:end]
        self.assertRegex(raw, r'NoteWire\(log, \$?"\[model:native-wire\] CANNOT PROTECT PEERS',
                         "the unprotected report is an error line, not informational")
        self.assertIn("provably this module", raw,
                      "the report must cover rows that are NOT recognised as ours: they are just as "
                      "unresolvable on a stock peer, and they are not ours to delete")
        # The live-session branch is the other place a non-shipped row cannot be removed: it must
        # report foreign non-shipped rows too, not only the ones it recognises as its own.
        self.assertIn("sessionPrefix", text,
                      "the live-session report must know the prefix before calling a row non-shipped")
        self.assertIn("are not this module's and stay too", raw,
                      "a live session must report foreign non-shipped rows as well as ours")
        session = text.find("SessionActive")
        session_known = text.find("sessionPrefix")
        session_foreign = text.find("listedForeign")
        self.assertGreater(session_known, session,
                           "the session branch decides what is non-shipped before it reports")
        self.assertGreater(session_foreign, session_known,
                           "the foreign rows are collected only when the prefix is known")
        line = [l for l in text.splitlines() if "if (plan.Unprotected.Length > 0)" in l][0]
        self.assertEqual(len(line) - len(line.lstrip()), 8,
                         "the unprotected report must also run when nothing was cut")
        block = self.pure_block()
        plan_body = wanted.body(block, "PlanWithdrawal")
        self.assertRegex(plan_body, r"for \(int i = floor; i < count; i\+\+\) if \(!isOurs\[i\]\)",
                         "unprotected rows are every NON-ours row above the prefix, not a label guess")
        self.assertRegex(plan_body, r"if \(bounded\)\s*\n\s*for \(int i = floor; i < count",
                         "without a proven prefix no row can be called non-shipped: make no such claim")

    def test_unproven_prefix_never_cuts(self):
        """A cut on an unknown boundary could delete a shipped row; the plan must refuse it."""
        block = self.pure_block()
        plan_body = wanted.body(block, "PlanWithdrawal")
        bounded = plan_body.find("bool bounded = prefixKnown && nativeCount > 0")
        cut = plan_body.find("int cut = count")
        walk = plan_body.find("while (cut > floor")
        self.assertGreaterEqual(bounded, 0, "the plan must decide boundedness explicitly")
        self.assertGreater(cut, bounded, "nothing may be cut before the boundary is known")
        self.assertGreater(walk, bounded, "the cut walk must be inside the bounded case")
        start, end = wanted.span(self.src, "WithdrawCustomRows")
        raw = self.raw[start:end]
        self.assertIn("no row can be proven to be above every", raw,
                      "the unknown-prefix residual must say why nothing was cut")
        self.assertIn("shipped row", raw,
                      "the unknown-prefix case must name the shipped row it refuses to risk")

    # ------------------------------------------------------------------ the pure block

    def pure_block(self) -> str:
        """The marked decision block, extracted the same way the behavioral harness extracts it."""
        return gen_pure.extract(SOURCE)

    def test_pure_block_has_no_game_surface(self):
        """The behavioral harness can only compile the block if it stays a pure decision."""
        masked = wanted.mask(self.pure_block())
        for game in ("UnityEngine", "Il2Cpp", "Harmony", "PlayerModelData", "SahurWardrobe",
                     "NetworkManager", "Gameplay."):
            self.assertNotIn(game, masked,
                             f"the pure block must not reference {game}; the harness compiles it "
                             "outside the game and would fail")

    def test_behavioral_harness_passes(self):
        """The decisions, not just their shape: compile the block verbatim and run its cases."""
        if not shutil.which("dotnet") and not (Path.home() / ".dotnet/dotnet").exists():
            self.skipTest("dotnet is not installed; the static structure pins still ran")
        env = dict(os.environ)
        dotnet_root = Path.home() / ".dotnet"
        env.setdefault("DOTNET_ROOT", str(dotnet_root))
        if dotnet_root.exists():
            env["PATH"] = str(dotnet_root) + os.pathsep + env.get("PATH", "")
        proc = subprocess.run(
            ["dotnet", "run", "-c", "Release", "--project", str(BEHAVIOR)],
            cwd=str(ROOT), env=env, capture_output=True, text=True, timeout=600)
        output = (proc.stdout or "") + (proc.stderr or "")
        self.assertEqual(proc.returncode, 0, output)
        self.assertIn("VERDICT OK", output,
                      "the behavioral harness did not report a passing verdict:\n" + output)


if __name__ == "__main__":
    unittest.main(verbosity=2)
