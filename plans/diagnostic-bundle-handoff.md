# Handoff: one-command Alterum diagnostic bundle

## Assignment and boundary

Implement one agent-runnable command that collects a fresh diagnostic bundle from the existing Alterum A/B processes. It must replace the repeated sequence of request-file writes, waits, log searches, process checks, and screenshots with one bounded invocation.

Repository: `~/alterum-mod`. Implement here, not in the old monorepo. Read `README.md`, `LAYOUT.md`, and `LAUNCH-POLICY.md` before editing. This handoff authorizes implementation and tests, not deployment/restart of the user's live processes. Report any live verification requiring new DLL deployment separately.

Deliver only collection and diagnosis. Instance synchronization, launching/stopping, model selection, join/rejoin, POV changes, configuration repair, and a general scenario framework are separate work. Preserve the game's model assignment and reservation rules.

## Why this exists

The Nov 18 thread exposed three failures this command must make obvious:

- After switching from TF2 Medic to a native model, the active main profile had `ShowLocalPlayer=false`, while another profile had it enabled. Reading an arbitrary config would give the wrong answer.
- A/B `GameAssembly.dll` hashes already differed before B was launched, but that fact did not become an actionable incompatibility finding. Hardlinks can diverge when an updater replaces the source file.
- B rendered/advertised Peter while its game-applied row was Spy. The host rejected the occupied Peter row. A live custom body and a derived HUD label were insufficient proof of agreement.

These are historical examples, not descriptions of the current processes. Discover current state; never copy historical PIDs, model indices, hashes, or lobby codes into implementation.

## Start here: reuse existing seams

Inspect these focused sources; verify current behavior rather than trusting old comments:

- `tracks/merged/mod/AlterumModelSwap.Hot/GameConsole.cs`: existing `alterum.test.snapshot`, `identity`, `remote-list`, pose, assertions, and capture handlers; manifest generation and dispatch.
- `tracks/merged/mod/AlterumModelSwap.Hot/ModelPresentation.cs`: game-applied identity versus wanted/advertised identity; current HUD sprite observation and its limitations.
- `tracks/merged/mod/AlterumModelSwap/Plugin.cs` and `AlterumModelSwap.Contracts/Contracts.cs`: effective configuration, reloads, instance name, `ConfigPath`, `ScreensDir`.
- `win/instance-label-common.ps1`: reusable process/start-time and evidence validation.
- `win/run-in-session1.ps1`: interactive-session bridge; inspect its fixed wait and completion semantics before reuse.
- `win/status.ps1` and `win/wshot.ps1`: reading aids only. Their current first-process/name-based selection is not safe for this collector.
- `plans/locked-desktop-capture.md`: capture limitations if the desktop is locked.
- If present, inspect `win/console.py` and the current `exec <request-id> <command>` support. They were in flight when this handoff was written; establish their current contract before depending on them. A dispatch acknowledgement alone may not mean an asynchronous artifact is finished.

The repository already had uncommitted runtime edits when this file was created. Recheck `git status`; preserve unrelated work. Do not revert, stage, commit, or deploy another task's edits. Reuse the current transport; add only narrowly necessary observation fields, not a second control system.

Completion: identify the supported transport, runtime fields that already exist, and the few missing observations before coding.

## Command and artifacts

Provide one stable entry point, preferably `win/collect-diagnostics.ps1` (an equally small existing-language wrapper is acceptable if it reuses the current transport better). It accepts explicit main/secondary install paths, an output directory, and a total timeout; secondary is optional. Document an exact tested invocation from WSL through Windows SSH, including any required script staging. No manually authored per-run helper should be necessary.

One invocation produces a unique run directory containing:

- `report.json`: versioned machine-readable report.
- `summary.txt`: compact findings first, then process/build/config/model/capture facts.
- Per-instance fresh runtime manifests and targeted log excerpts.
- One current-window PNG per running target where safely obtainable, plus capture metadata and hashes.

Use per-run IDs, UTC timestamps, monotonic elapsed time, per-stage timings, source paths, and evidence references. Emit absolute output paths to stdout. Prefer a default total deadline around 30 seconds; make it configurable and enforce it across all stages, rather than granting each stage the full timeout. Keep partial evidence on timeout and continue independent collection when one target fails.

## Required observations and verdicts

Each field distinguishes an observed value from `unavailable` with a reason. Distinguish collection completeness from diagnostic health: a successfully collected model mismatch is not healthy; missing evidence is not a pass. Document exit codes for healthy/complete, findings or incomplete collection, and invocation failure.

### Process and freshness

Resolve each target by canonical executable path and actual instance argument, then capture PID, process start UTC, session, HWND, scene, and loaded Hot generation/hash. Multiple candidates require an explicit selector or an ambiguity finding; never choose the first process.

Validate the same PID/start time before and after collection. Associate runtime responses and images with that identity and generation. Reject stale artifacts from previous runs, old log lines, log truncation, process replacement, and generation changes as current proof. Sequential A/B observations are acceptable; record their times rather than claiming an atomic cross-process snapshot.

### Effective configuration and compatibility

Report the config path actually used by the running host and its effective `ShowLocalPlayer` value, current POV, and relevant visibility state. Label disk-only reads separately from in-memory effective values; never infer the active profile merely from a filename.

Compare game build ID when available and hashes of `GameAssembly.dll`, Unity player, host/contracts, and Hot artifacts. Distinguish loaded DLL evidence from files currently on disk. Report version/hash differences explicitly with compatibility `mismatch` or `unknown`, not a speculative guarantee that every differing hash is incompatible. A known game-binary mismatch is a prominent blocker for multiplayer diagnosis. Any link-ID observation is optional supporting evidence, not a substitute for content parity.

### Model and presentation

For local and known remote actors, collect client ID, game-applied row/index/slug, body identity/health, advertised identity, any remaining preference/provenance, and actual HUD/wardrobe observations where supported. Preserve distinctions between host-authoritative data and this client's replicated view.

Compare only independently observed facts. An expected/canonical label is not proof of the visible HUD sprite. Mark a remote applied row, hidden UI surface, or unknown sprite unavailable when it cannot be measured. Explicitly flag applied/body/advertised disagreement. Record occupied-row rejection evidence; do not bypass reservations to make the report pass.

### Logs and screenshot

Read the appropriate per-instance log and bounded recent game-log excerpts; tolerate embedded NUL bytes. Preserve localized messages and match numeric actor/model identifiers as well as English words. A shared `Player.log` is shared evidence: state attribution uncertainty instead of assigning it to A or B by assumption. Whitelist diagnostic excerpts; avoid copying credentials or whole configuration/environment dumps.

Capture the identified game's current window in the interactive session, without walking, changing POV, unlocking the console, or operating UI. A current-window image is preferred because it includes the HUD; an in-game camera render may omit overlay UI and must be labelled accordingly. A successful PNG encode or zero magenta pixels does not prove the body is visible. Record backend, dimensions, PID/start time, HWND, capture UTC, and hash. Locked desktop, missing window, unsupported backend, or suspicious blank capture yields unavailable/suspect evidence, not fabricated success. Capture failure must not discard text diagnostics.

## Safety and transport

Only observation requests, artifact writes, and a minimal capture worker are allowed. Keep existing access gates intact; if a capture requires unlock, report that limitation or use a permitted external capture path. Preserve focus if a backend needs foreground access; default to no focus/input changes. Release capture resources and clean up only workers/tasks created by this invocation.

Publish requests atomically and preserve any existing mailbox request. Bound contention and polling; use acknowledgement/artifact identity instead of repeated model turns or long fixed sleeps. Do not introduce a general command queue, daemon, or orchestration state machine. Report a busy mailbox without overwriting it.

## Acceptance and delivery

Add fixture/unit coverage for:

1. Active main config false versus inactive pinned config true: report the effective false and correct path.
2. A/B game hash mismatch: prominent mismatch, no repair performed.
3. Applied Spy versus body/advertised Peter: mismatch even if a canonical HUD label says Peter.
4. Missing actual HUD observation: unavailable, never synthesized as agreement.
5. Wrong PID/start time, ambiguous candidates, old artifacts, log rotation, and generation changes: no stale evidence accepted.
6. Mailbox contention and response timeout: bounded failure, partial report retained.
7. NUL-containing localized rejection logs: relevant rejection preserved.
8. Locked desktop/capture failure and optional absent B: structured partial results with text evidence intact.
9. A complete healthy fixture: valid JSON, consistent references, concise summary and documented exit code.

Run syntax/build/tests relevant to changed files. If approved live observation is possible without deployment, collect one bundle, report stage timings, and confirm that process identity, model, pose, POV, and configuration were unchanged. Otherwise label live validation pending and give the exact command needed; fixture success is not live acceptance.

Finish with changed files, exact invocation, report schema/exit codes, test results, measured runtime if available, and explicit unsupported observations. Keep implementation bounded: first make the existing facts arrive together, freshly and honestly; leave unrelated fixes for separate tasks.
