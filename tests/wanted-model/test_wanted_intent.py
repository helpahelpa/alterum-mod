#!/usr/bin/env python3
"""Pin the wanted-intent contract of SahurWardrobe's PROGRAMMATIC model selection.

The bug this guards against: `wardrobe=select=<custom>`, `wardrobe=apply` and
`wardrobe=index=<one of our rows>` wrote the game's model row, the native-wire guard
(RemapModelIndex on PlayersAppearanceManager.SetBaseModelIndex/SaveModel/LinkPlayerToPlayer) replaced
that row with a native donor index - an unmodified peer cannot resolve a custom row - and the
module's selection sync then read the donor back as "the game's row". Nothing had recorded that the
client had picked OUR entry, so the body module was released again and the donor body was shown: a
programmatic pick behaved as if it had never happened, while the same pick through the wardrobe tab
(the UI confirm, OnLocalModelConfirmed) worked.

The fix is not a second record. It is ONE shared, scoped write path (RecordWanted) that the UI
confirm and the programmatic explicit selectors both use, written only after an apply took:

  * a single writer for `_wantedSlug` (plus the session-scope reset), so no other code - preview,
    the game's join row, the mod's random roll - can change what this client wants;
  * both callers record the same fields the scope check compares (`_wantedLobby`, `_wantedClient`),
    so a record from either path is dropped when the lobby/client session changes;
  * `ApplyNative` (select= / apply / revert / the protocol test path) and the `index=` command both
    hand ApplyIndex an explicit intent instead of applying with no intent;
  * ApplyIndex records only for a write that landed (every `return true` is gated by `applied`), so a
    refused apply - no controller, no entry point, no setuper - never rewrites the wanted state;
  * the selection helpers stay private to SahurWardrobe.cs, so the preview and random paths cannot
    select (and cannot record) at all.

These tests read the SHIPPING file and check that structure. They are static on purpose: the code
under test is an Il2Cpp/Harmony class that cannot be instantiated outside the game, so the invariants
that make the wanted record trustworthy are pinned where they are visible. The source is masked first
(comments and string literals blanked, interpolated holes kept) so a word or brace inside a log
string can neither satisfy nor break a check.

Run:  python3 tests/wanted-model/test_wanted_intent.py
Exit: 0 every pin holds, 1 a pin is broken (the failure names the invariant and the evidence).
"""

from __future__ import annotations

import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HOT = ROOT / "tracks/merged/mod/AlterumModelSwap.Hot"
SOURCE = HOT / "SahurWardrobe.cs"


# --------------------------------------------------------------------------------------------
# a small C# masker
#
# Only what is needed to read structure safely: comments and string/char literals are blanked out
# (their characters become spaces, newlines are kept), while interpolation holes stay code, because
# they ARE code (`$"{a ? "x" : "y"}"` nests quotes). Braces inside a log string therefore never
# count, and a method name mentioned in a comment never looks like a call.
# --------------------------------------------------------------------------------------------

def mask(source: str) -> str:
    out = list(source)
    n = len(source)

    def blank(a: int, b: int) -> None:
        for k in range(a, min(b, n)):
            if out[k] not in "\r\n":
                out[k] = " "

    # Mode stack. "code" entries carry the brace depth of an interpolation hole (0 for real code);
    # "str" entries remember whether the literal is verbatim and whether it interpolates.
    stack: list[list] = [["code", 0]]
    i = 0
    while i < n:
        mode = stack[-1]
        c = source[i]

        if mode[0] == "str":
            verbatim, interpolation = mode[1], mode[2]
            if verbatim and c == '"':
                if i + 1 < n and source[i + 1] == '"':
                    blank(i, i + 2)
                    i += 2
                    continue
                blank(i, i + 1)
                stack.pop()
                i += 1
                continue
            if not verbatim and c == "\\":
                blank(i, i + 2)
                i += 2
                continue
            if c == '"':
                blank(i, i + 1)
                stack.pop()
                i += 1
                continue
            if interpolation and c == "{":
                if i + 1 < n and source[i + 1] == "{":
                    blank(i, i + 2)
                    i += 2
                    continue
                blank(i, i + 1)
                stack.append(["code", 0])
                i += 1
                continue
            if interpolation and c == "}":
                if i + 1 < n and source[i + 1] == "}":
                    blank(i, i + 2)
                    i += 2
                    continue
                # A lone '}' inside an interpolation literal is an escape-form; keep it out of code.
                blank(i, i + 1)
                i += 1
                continue
            blank(i, i + 1)
            i += 1
            continue

        # code (real, or the body of an interpolation hole)
        if c == "/" and i + 1 < n and source[i + 1] == "/":
            end = source.find("\n", i)
            end = n if end < 0 else end
            blank(i, end)
            i = end
            continue
        if c == "/" and i + 1 < n and source[i + 1] == "*":
            end = source.find("*/", i + 2)
            end = n if end < 0 else end + 2
            blank(i, end)
            i = end
            continue
        if c == "@" or c == "$":
            j = i
            verbatim = False
            interpolation = False
            while j < n and source[j] in "@$":
                verbatim = verbatim or source[j] == "@"
                interpolation = interpolation or source[j] == "$"
                j += 1
            if j < n and source[j] == '"':
                blank(i, j + 1)
                stack.append(["str", verbatim, interpolation])
                i = j + 1
                continue
        if c == '"':
            blank(i, i + 1)
            stack.append(["str", False, False])
            i += 1
            continue
        if c == "'":
            j = i + 1
            while j < n:
                if source[j] == "\\":
                    j += 2
                    continue
                if source[j] == "'":
                    j += 1
                    break
                j += 1
            blank(i, j)
            i = j
            continue
        if len(stack) > 1 and mode[0] == "code":
            # inside an interpolation hole: its closing brace ends the hole
            if c == "{":
                mode[1] += 1
            elif c == "}":
                if mode[1] == 0:
                    blank(i, i + 1)
                    stack.pop()
                    i += 1
                    continue
                mode[1] -= 1
        i += 1
    return "".join(out)


# --------------------------------------------------------------------------------------------
# structure readers over the masked text
# --------------------------------------------------------------------------------------------

DECL = re.compile(
    r"\b(?:private|internal|public|protected)\s+"
    r"(?:static\s+)?(?:readonly\s+)?"
    r"(?:[\w<>\[\],\.\?]+)\s+"
    r"(?P<name>\w+)\s*\(")


def _match(text: str, start: int, open_char: str, close_char: str) -> int:
    """Index just past the balanced pair that opens at `start`."""
    depth = 0
    for k in range(start, len(text)):
        if text[k] == open_char:
            depth += 1
        elif text[k] == close_char:
            depth -= 1
            if depth == 0:
                return k + 1
    raise AssertionError(f"unbalanced {open_char}{close_char} from offset {start}")


def bodies(text: str, name: str) -> list[tuple[int, int, str]]:
    """Every method body of `name` (definition form only) as (start, end, body), braces balanced.

    `text` is the masked source and every mask preserves length, so (start, end) addresses the same
    slice of the raw file - that is how a check that needs a string VALUE (a case label, an empty
    slug) reads the literal while structure still comes from the masked text.
    """
    found = []
    for m in DECL.finditer(text):
        if m.group("name") != name:
            continue
        params_end = _match(text, m.end() - 1, "(", ")")
        brace = text.find("{", params_end)
        if brace < 0:
            continue
        end = _match(text, brace, "{", "}")
        found.append((brace, end, text[brace:end]))
    return found


def span(text: str, name: str) -> tuple[int, int]:
    found = bodies(text, name)
    if len(found) != 1:
        raise AssertionError(f"expected exactly one definition of {name}, found {len(found)}")
    return found[0][0], found[0][1]


def body(text: str, name: str) -> str:
    return bodies(text, name)[0][2]


def calls(text: str, name: str) -> list[str]:
    """Argument lists of every call to `name`, parentheses included.

    Only used on a body that does not define `name`, so a definition cannot be mistaken for a call.
    """
    out = []
    for m in re.finditer(r"\b" + re.escape(name) + r"\s*\(", text):
        out.append(text[m.end() - 1:_match(text, m.end() - 1, "(", ")")])
    return out


def arg_count(args: str) -> int:
    inner = args[1:-1].strip()
    if not inner:
        return 0
    depth = 0
    count = 1
    for c in inner:
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == "," and depth == 0:
            count += 1
    return count


def enclosing(text: str, offset: int) -> str:
    """Name of the innermost method definition whose body contains `offset`."""
    best, best_name = -1, "<none>"
    for m in DECL.finditer(text):
        params_end = _match(text, m.end() - 1, "(", ")")
        brace = text.find("{", params_end)
        if brace < 0:
            continue
        end = _match(text, brace, "{", "}")
        if brace <= offset < end and brace > best:
            best, best_name = brace, m.group("name")
    return best_name


def block_after(text: str, marker: str) -> str:
    """Text from `marker` up to its next `break;` (a switch case)."""
    start = text.index(marker)
    end = text.index("break;", start)
    return text[start:end]

class WantedIntent(unittest.TestCase):
    raw = SOURCE.read_text(encoding="utf-8")
    src = mask(raw)

    # ---------------------------------------------------------------- one writer, one scope

    def test_wanted_slug_has_a_single_writer(self):
        """Preview/random cannot change the wanted state: only the recorder writes it."""
        sites = [m.start() for m in re.finditer(r"\b_wantedSlug\s*=(?!=)", self.src)]
        self.assertTrue(sites, "no assignment to _wantedSlug at all")
        owners = sorted({enclosing(self.src, s) for s in sites})
        self.assertEqual(owners, ["CheckWantedScope", "RecordWanted"],
                         f"_wantedSlug is written by {owners}; every write must be the session reset "
                         "(CheckWantedScope) or the one shared recorder (RecordWanted)")

    def test_scope_reset_only_clears(self):
        reset = body(self.src, "CheckWantedScope")
        self.assertRegex(reset, r"_wantedSlug\s*=\s*null\s*;",
                         "CheckWantedScope must only clear the record, never set a slug")

    def test_recorder_scopes_the_record_like_the_scope_check(self):
        """A record from either path is dropped when the lobby/client session changes."""
        record = body(self.src, "RecordWanted")
        for field in ("_wantedLobby", "_wantedClient"):
            self.assertRegex(record, rf"{field}\s*=", f"RecordWanted must record {field}")
        check = body(self.src, "CheckWantedScope")
        for field in ("_wantedLobby", "_wantedClient"):
            self.assertIn(field, check, f"CheckWantedScope must compare {field}")
        self.assertIn("IsInLobby", record, "RecordWanted must refuse to record outside a lobby")
        self.assertIn("IsConnectedClient", record,
                      "RecordWanted must refuse to record without a local network session")

    # ---------------------------------------------------------------- who may record

    def test_ui_confirm_uses_the_shared_recorder(self):
        confirm = body(self.src, "OnLocalModelConfirmed")
        self.assertRegex(confirm, r"\bRecordWanted\s*\(",
                         "the wardrobe confirm must record through the shared writer")
        self.assertNotRegex(confirm, r"\b_wantedSlug\s*=(?!=)",
                            "the wardrobe confirm must not write _wantedSlug itself")
        self.assertIn("_ourApply", confirm,
                      "the confirm must keep ignoring this module's own apply (_ourApply guard)")

    def test_programmatic_path_uses_the_shared_recorder(self):
        note = body(self.src, "NoteExplicitWanted")
        self.assertRegex(note, r"\bRecordWanted\s*\(",
                         "the programmatic pick must record through the same writer")
        apply_index = body(self.src, "ApplyIndex")
        self.assertRegex(apply_index, r"\bNoteExplicitWanted\s*\(",
                         "ApplyIndex must record the explicit caller's intent")

    def test_no_other_module_records_or_selects(self):
        """Preview and the random/join paths must not select - and so cannot record."""
        names = ("ApplyEntry", "ApplyNative", "ApplyIndex", "RecordWanted", "NoteExplicitWanted",
                 "WantedSlugForRow")
        offenders = []
        for path in sorted(HOT.glob("*.cs")):
            if path.name == SOURCE.name:
                continue
            text = mask(path.read_text(encoding="utf-8"))
            for name in names:
                for m in re.finditer(r"\b" + re.escape(name) + r"\s*\(", text):
                    offenders.append(f"{path.name}: {name}( at line "
                                     f"{text.count(chr(10), 0, m.start()) + 1}")
        self.assertEqual(offenders, [],
                         "selection/wanted helpers are SahurWardrobe's own; a preview or random "
                         "path calling them would change the wanted state: " + "; ".join(offenders))

    # ---------------------------------------------------------------- the explicit callers

    def test_apply_native_passes_an_intent(self):
        native = body(self.src, "ApplyNative")
        five = [a for a in calls(native, "ApplyIndex") if arg_count(a) == 5]
        self.assertTrue(five, "ApplyNative must hand ApplyIndex an explicit wanted intent (5th arg)")
        self.assertIn("WantedSlugForRow", native,
                      "a forced native index (revert/donor) must be read as the deliberate native "
                      "intent via WantedSlugForRow")

    def test_index_command_passes_an_intent(self):
        start, end = span(self.src, "Command")
        raw = self.raw[start:end]
        label = raw.index('case "index":')
        case_end = raw.index("break;", label)
        case = self.src[start + label:start + case_end]
        self.assertIn("ApplyIndex", case, "the index= command must still apply the row")
        self.assertIn("WantedSlugForRow", case,
                      "the index= command must derive a wanted intent for the requested row")

    def test_intent_distinguishes_our_row_from_a_native_one(self):
        start, end = span(self.src, "WantedSlugForRow")
        derive, raw = body(self.src, "WantedSlugForRow"), self.raw[start:end]
        self.assertIn("EntryOwningRow", derive,
                      "our rows must resolve to their entry (a native row owns no entry)")
        self.assertIn('return "";', raw,
                      "a native row must be the explicit-native intent (empty slug)")
        self.assertIn("EntrySlug", derive, "our row must carry its roster slug")

    # ---------------------------------------------------------------- only a landed write

    def test_every_success_marks_applied_first(self):
        apply_index = body(self.src, "ApplyIndex")
        returns = [m.start() for m in re.finditer(r"\breturn\s+true\s*;", apply_index)]
        marks = [m.start() for m in re.finditer(r"\bapplied\s*=\s*true\s*;", apply_index)]
        self.assertTrue(returns, "ApplyIndex has no success path?")
        self.assertEqual(len(returns), len(marks),
                         f"ApplyIndex has {len(returns)} success returns but {len(marks)} "
                         "`applied = true` marks; every success path must be marked")
        for r in returns:
            before = apply_index[:r].rstrip()
            self.assertTrue(before.endswith("applied = true;"),
                            "a success return is not immediately preceded by `applied = true;`: "
                            f"...{before[-90:]!r}")

    def test_record_is_in_the_finally_behind_the_applied_flag(self):
        apply_index = body(self.src, "ApplyIndex")
        note = re.search(r"\bNoteExplicitWanted\s*\(", apply_index)
        self.assertIsNotNone(note, "ApplyIndex never records the intent")
        finally_at = apply_index.rindex("finally")
        self.assertLess(finally_at, note.start(),
                        "the intent must be recorded in ApplyIndex's finally (after the write "
                        "outcome is known), not before the apply ran")
        guarded = apply_index[finally_at:note.start()]
        self.assertIn("_ourApply = false", guarded,
                      "the own-apply flag must be cleared before the intent is recorded")
        self.assertGreaterEqual(guarded.count("applied"), 1,
                                "the record must be gated by the `applied` success flag")
        self.assertIn("wantSlug != null", guarded,
                      "a path with no explicit intent (preview/random/no choice) must record nothing")

    # ---------------------------------------------------------------- guards kept

    def test_native_wire_remap_is_unchanged(self):
        remap = body(self.src, "RemapModelIndex")
        self.assertIn("IsServer", remap,
                      "the wire remap must still be the server's: a guest applying a custom row "
                      "sends it through the game's own RPC instead")
        self.assertIn("NativeWirePolicy.ShippedWireIndex", remap,
                      "the remap must use the ONE wire decision, which still lands on a native row an "
                      "unmodified peer can resolve")
        self.assertIn("NativeDonorRow", remap,
                      "the remap keeps a provable local donor (the applied shipped row, else the clone "
                      "donor) instead of an arbitrary per-client row")

    def test_confirm_and_remap_are_still_wired(self):
        probe = body(self.src, "InstallClickProbe")
        self.assertIn("OnLocalModelConfirmed", probe,
                      "WardrobeController.OnModelSelected must stay the confirm hook")
        self.assertIn("SafeModelIndex", probe,
                      "SetBaseModelIndex must keep the wire guard")


if __name__ == "__main__":
    unittest.main(verbosity=2)
