# One-command diagnostic collection

From WSL, in `~/alterum-mod` (Python 3.10+, stdlib; `ssh windows` must already work):

```sh
python3 win/collect-diagnostics.py \
  --main-root /mnt/c/AlterumPinned \
  --secondary-root /mnt/c/AlterumB \
  --output "$HOME/assets/monorepo/alterum-data/validation/diagnostics" \
  --timeout 30
```

This exact invocation is tested against the existing processes. The Python entry point automatically
stages `diagnostic-bridge.ps1`, `diagnostic-worker.ps1`, and `instance-label-common.ps1` under a unique
`C:\AlterumTools\diagnostic-worker\<run-id>` directory, then invokes Windows PowerShell through SSH
with an encoded command. No per-run script, PID, lobby code, unlock, or UI operation is needed.
SSH host alias: `--ssh-host windows` (the default). The interactive worker must run in session 1.
Override staging with `--staging /mnt/c/<writable-directory>`; Windows Temp can impose ACLs that
prevent WSL reading the worker output, so it is deliberately not the default.

Omit `--secondary-root` for a single target. Discovery uses executable path plus the actual instance
argument; it never picks the first process. If ambiguous, supply `--main-instance NAME`,
`--secondary-instance NAME`, `--main-pid PID`, or `--secondary-pid PID`. An explicitly empty instance
selector means an unnamed process; without a selector, one unique process at that path is accepted.
Windows-style install paths are accepted too. Installs must be mounted under `/mnt/<drive>` in WSL.

Optionally pass `--game-log '<explicit path to Player.log>'` to include bounded shared Unity log
excerpts. The collector never guesses a Windows user/profile. Such excerpts are marked shared,
recent context with uncertain attribution, **not fresh state of A or B**. NUL bytes are tolerated;
localized wardrobe rejection lines retain their actor/model numbers. Diagnostic excerpts are
whitelisted and obvious credentials, long external IDs and IPv4 addresses are redacted. Bundles
still contain private install paths and game screenshots: review before sharing outside the machine.

## Output and exit codes

Stdout names absolute paths to `report.json` and `summary.txt` in a unique UTC/run-ID directory.
The directory also contains before/after runtime manifests, dispatch/log excerpts, worker metadata,
and one current-window PNG per safely captured target. Failed stages preserve partial evidence.

* **0**: required supported evidence collected, no diagnostic findings.
* **1**: diagnostic findings **or** incomplete/unavailable evidence. A fully collected mismatch is
  not healthy; an absent field is never filled with an expected value to make a pass.
* **2**: invalid invocation or inability to create/write the output bundle.

`--timeout` is a **total monotonic deadline**, default 30 seconds (range 1–300), not a timeout per
step. Interactive stages get bounded slices; snapshot dispatches get at most three seconds each.
When a worker cannot finish B, completed A worker metadata can survive via a checkpoint. Filesystem
writes/final report formatting add a small unavoidable finalization overhead after expiry.

## Report schema: `alterum.diagnostics/1`

Top-level keys: `schema`, `runId`, UTC start/end, monotonic `elapsedSeconds`, `stages`, `instances`,
`compatibility`, `findings`, `sharedGameLog`, `complete`, `health`, `exitCode`.

An observation is `{status:"observed", value:..., source:...}` or
`{status:"unavailable", reason:...}`; captures may additionally be `suspect`. Composite sections
contain individual observations. Findings carry a stable code, role, severity, message and evidence
reference. References to bundled evidence are relative to the run directory; original source paths
are retained separately. Raw manifests preserve their own unavailable reasons.

Per-instance facts include:

* `process`: PID, full executable path, precise OS start UTC, actual instance argument, session,
  unique Unity window handle. Exact identity is revalidated after collection. Older snapshot/1
  manifests round start UTC to milliseconds; only that runtime comparison permits <1 ms loss.
* `runtime`: accepted only after matching unique snapshot tags, command/artifact completion, UTC,
  PID/start/exe/argument, and matching pre/post Hot generation, MVID and backing-file hash. Log
  replacement, truncation or truncate/regrow invalidate freshness. A dispatch acknowledgement
  alone never proves that the manifest exists or belongs to this request.
* `disk`: hashes of GameAssembly, UnityPlayer, host, contracts and the discovered instance's Hot
  slot. These are **current disk files**, not claims about loaded memory. Loaded Hot shadow-copy
  and host/contracts assembly backing-file observations are separate, with MVID and source.
* `config`, `pov`, `visibility`, `pose`, `scene`, `authority`, `actors`, `unchanged`: in-memory runtime
  observations, never arbitrary disk-config guesses. Authority is reported as seen by that client.
  Independently observed applied/body/advertised disagreements are explicit findings, except when
  a live confirmed wanted body and canonical slug agree over a native applied donor row. The report
  identifies that case as `appliedRole=native-donor-row`; differing advertised slugs remain findings.
  There the HUD portrait must present the confirmed body, not the donor row, so any other observed
  sprite is still a presentation-mismatch. Canonical claims are not substituted for HUD observations.
  The legacy `wanted` field is preference/provenance evidence, not another authoritative row.
* `capture`: backend, dimensions, sample color diversity, identity/HWND, UTC, SHA-256, output reference,
  focus-change observation, and the generation association bracket. Locked/non-default desktops,
  minimized/missing windows, failed backends or near-uniform pixels are unavailable/suspect.

The two targets are sampled **sequentially, not atomically**. Before/after state equality is evidence
of observed stability, not a promise that no transient event occurred. Disk hash parity means only
matching bytes, not guaranteed multiplayer compatibility. Game-binary differences are prominent
multiplayer-diagnosis blockers; unknown hashes/versions stay unknown. No repair is performed.

## Runtime additions and deployment boundary

The collector works against the existing `exec <request-id> <command>` transport and uses only
`alterum.test.snapshot <unique-tag>`. The source additions to snapshot/1 are additive:

* `[config] path, showLocalPlayer, source`: host-context effective values; `showLocalPlayer` is
  the stable snapshot/1 key for the effective `[Mirror] ReflectLocalPlayer` value. `path` fails
  closed on older permanent hosts whose ConfigPath accessor reported BasePlugin.Config instead of
  the named-instance file. `HostPlugin.ActiveConfigPath` and `HostContext.ConfigPath` fix that
  accessor.
* `[loaded]`: loaded host/contracts assembly path, MVID, SHA-256 of the backing file and hash scope.
* `[view]`: current local POV and counts of local renderers/enabled/active-enabled renderers.
  Counts include all renderers under the local actor; they are not proof of body visibility.
* `[identity] hudSource=visible-sprite-observer/1`: capability marker for independently reading the
  visible portrait sprite. Older canonical HUD labels are not trusted as observed pixels.
* Process start UTC retains full precision in new snapshots.

**No DLL was deployed and no game process was restarted for this implementation.** The existing
runtime lacks the new snapshot fields, so live bundles intentionally report incomplete effective
config/POV/visibility/loaded-host evidence. Full runtime-field acceptance requires a separately
approved host deployment/restart and Hot deployment; a Hot-only update cannot fix the old host's
active-profile accessor. After that approval/deployment, rerun the exact command above.

Unsupported observations remain explicit: authoritative Steam build ID when no install build manifest
is exposed; loaded game-memory hashes; remote applied row when the runtime cannot read it; native
renderer-controller hide keys; actual visible wardrobe selection/occupancy; and invisible/unknown HUD
surfaces. No test or report invents these from the canonical model. A screenshot's PNG integrity or
color diversity does not prove the body is visible or that DWM produced a new game frame.

Capture uses bounded external `PrintWindow(PW_RENDERFULLCONTENT)` **only on the unlocked default
input desktop**, without foreground activation, input, POV/camera changes or console unlock.
PrintWindow may return cached/stale pixels even when it reports success; metadata keeps this caveat.
There is no locked-desktop capture fallback, game-camera substitute, driver hook, or API roulette.
Only the invocation's unique scheduled worker task is stopped/deleted. Do not expose the mailbox
or collector as an untrusted network service.

## Tests

```sh
python3 -m unittest discover -s tests/diagnostics -v
python3 -m unittest discover -s tests/direct-console -v
```

Fixtures cover effective main-profile false versus inactive disk-profile true, game parity blockers,
Spy/Peter disagreement, confirmed wanted body over a native donor row (HUD expected to present that
body; a donor-row or other sprite still conflicts), unconfirmed/stale donor claims that stay findings,
unobserved/legacy HUD, wrong identities/tags/timestamps, ambiguity, log
rotation/truncation, generation changes, stale captures, localized NUL-containing reservation logs,
locked capture, absent optional B, deadline failures with partial reports, and a complete healthy
report with consistent references. Transport tests exercise atomic no-overwrite mailbox publication,
contention, errors and partial log writes. Fixtures do not replace live Unity acceptance.

## Validation of this implementation

* 19 collector tests and 7 existing direct-transport tests pass. Python syntax and `git diff --check`
  pass. Host and Hot Release builds pass; the final Hot build has 16 pre-existing warnings, 0 errors.
  Staged PowerShell scripts pass the Windows PowerShell AST parser.
* The documented A/B invocation produced
  `~/assets/monorepo/alterum-data/validation/diagnostics/20261118T050517Z-07cc343ba5614cc5a06757bda6895737/`
  in **11.6807 seconds**, exit 1. Stages: discover 2.1019s; main/secondary before snapshots
  0.7135/0.6253s; captures 2.8226s; main/secondary after snapshots 0.5900/0.6022s; revalidate
  2.1162s; main/secondary disk/logs 1.0644/1.0159s. Both captures were observed, identity-checked,
  generation-bracketed and recorded `focusChanged=false`. These timings are measured, not budgets.
* Process identity, local applied-model slug, body pose/forward, scene and QC access summaries were
  unchanged between samples. POV and effective config stability are **unavailable**, not confirmed:
  the deployed runtime predates the added fields. Source is built but not deployed. Body visibility
  is not accepted merely from valid PNGs. Current disk game/mod hashes matched; the secondary runtime
  reported an asset-health failure. No repair was attempted.
* A separate `--timeout 1` single-target live run produced
  `~/assets/monorepo/alterum-data/validation/diagnostics/20261118T051228Z-e407a0eb8d5c4feea9e8e38df251d9a9/`
  in **1.0459 seconds**, exit 1 with a partial report and unavailable evidence. Task Scheduler reported
  zero remaining `AlterumDiagnostics-*` tasks after this test.
* No deployment/restart, model change, movement, join/rejoin, input, POV change or config repair was
  performed. Full live acceptance of the new effective-config/POV/renderers/loaded-host fields is
  pending separately authorized deployment. The current report intentionally does not pass it.
