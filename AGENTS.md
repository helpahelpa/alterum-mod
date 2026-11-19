# Agent rules for alterum-mod

## RE feedback

`alterum-re` is the source of truth for the shipped game; the mod is its best test.

When mod work finds a discrepancy, update `alterum-re` in the same session before calling the mod
work done.

A discrepancy is any place where the mod's observed behavior, a runtime log, an asset/localization
value, or the metadata contradicts `alterum-re`:

* a signature, field type/offset, token/RVA, or method behavior differs;
* a role/map/spell/item/setting formula, ID, count, or code path is wrong;
* a decompiler artifact was mistaken for game logic;
* a type/method is missing, or a generated index/link is stale.

Steps:

1. Capture the observation and the evidence.
2. Update the authoritative file in `alterum-re` (`docs/`, `data/`, `tools/`).
3. If unresolved, add it to `docs/OPEN-QUESTIONS.md` with what would settle it.
4. Regenerate the affected tables and run `python3 alterum-re/tools/verify.py`.
5. Commit inside `alterum-re` (it has its own `.git`).
6. Add one row to `alterum-re/docs/DISCREPANCIES.md`.

`src/` is generated output: change the docs/data/tools that produce or interpret it, not the
generated file.

Keep the mod moving: when the correct fix needs more investigation, record the contradiction and
evidence as an open question, use the narrowest workaround, and link the workaround to that entry.

Done when `git -C alterum-re status --short` is empty,
`python3 alterum-re/tools/verify.py` passes, and the mod-side commit/PR/note names the
`alterum-re` commit.

Full procedure, evidence standard, and examples:
[`alterum-re/docs/RE-FEEDBACK.md`](alterum-re/docs/RE-FEEDBACK.md).
