**Re-run (scans + mirror fixed, control map + external vanilla source): see `REPORT-2.md`.**

# Perf — mod vs vanilla. `C:\AlterumPerf\{V,L,F}`, 2026-11-15

**Verdict: the mod is too heavy in production terms — but the headline comparison this task
asked for (full-minus-vanilla in three scenes) is *not* delivered, and the two reasons are in
"Untrustworthy / not delivered" below. What was measured says: the full mod adds 4.4 ms of
mod-owned main-thread work per frame in the lobby and 7.5 ms in a map, with recurring 10–50 ms
scans and 3–12× worse p99, on an otherwise unchanged median.**

## What was actually run

| arm | build | features | scenes captured |
|---|---|---|---|
| V vanilla | copy of the pin, `winhttp.dll` + `doorstop_config.ini` moved aside (`*.off`) | none (no loader) | **launched and reached the menu only** — no numbers |
| L loaded / features off | same install copy, bench build, `BepInEx/bench.cfg: features=off` | all feature ticks **and their pollers** skipped; only the window keeper, scene line, `ui.request` driver, Perf and the CSV run | Lobby, 2×120 s |
| F full mod | same install copy, bench build, `features=on` | custom body `Sahur (Classic)` bound and on screen (log `[sahur] ON`, 8 body renderers swapped), hat (`[sahur:bat]` built on `hand_r`), wardrobe/cosmetics/emotes/reels live | Lobby 2×120 s, **Lair of Occultists map** 2×120 s, Menu 2×120 s |

Bench build = the merged tree at `src/` with: `Bench.cs` (gate + per-frame CSV), `Perf` window-accumulation
fixed, `EnforceFrameCap` compiled out under `-p:DefineConstants=BENCH`. **The pin was never written to**;
it is running again with the production dll and its own 15 fps cap (pid 94212 at hand-over).
Arms were run one at a time, same install copy (hardlinked from the pin), 640x360 windowed, same
graphics settings, alternating L/F sessions. Warm-up 25–45 s inside each scene before a window.
Each 120 s window = one repeat; 2 repeats per arm/scene (task asked ≥3 — see gaps).

## Numbers (frame time, ms)

| arm / scene | frames | median | p95 | p99 | worst | >33 ms | >66.7 ms (15 fps budget) | mod_ms/frame | main-thread CPU ms/frame | alloc B/frame | GC0 per 120 s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| L Lobby r1 | 10800 | 11.09 | 11.6 | 12.2 | 16.2 | 0 | 0 | **0.015** | 3.28 | 2 007 | 1 |
| L Lobby r2 | 10800 | 11.09 | 11.6 | 12.3 | 17.2 | 0 | 0 | **0.016** | 3.11 | 2 007 | 0 |
| F Lobby r1 | 10350 | 10.97 | 19.7 | 35.4 | 52.9 | 119 | 0 | **4.41** | 8.57 | 45 086 | 9 |
| F Lobby r2 | 10290 | 11.00 | 19.1 | 36.6 | 53.4 | 148 | 0 | **4.47** | 8.66 | 45 120 | 10 |
| F Menu r1 | 10680 | 11.10 | 13.6 | 32.2 | 47.0 | 82 | 0 | 1.45 | 3.12 | 41 471 | 9 |
| F Lair r1 | 6720 | 11.21 | 53.5 | 154.9 | 261.9 | 639 | **244** | **7.67** | 15.53 | 48 366 | 10 |
| F Lair r2 | 6840 | 11.10 | 53.6 | 143.4 | 221.3 | 673 | **278** | **7.47** | 15.25 | 48 327 | 8 |

All arms ran **uncapped** (bench build: no internal cap; game's own FRAME RATE LIMIT = 300, display
vsync off in effect — 90 fps ≈ 11 ms in the lobby), so these are the mod's own costs, not a capped
floor. `mod_ms` = sum of the mod's top-level `Perf` scopes in that frame; main-thread CPU =
`GetThreadTimes` on the calling thread (15.6 ms scheduler quantisation, so the per-frame column is
lumpy — the column is exact only as a window sum; CPU per window: L ≈ 34 s / 120 s, F Lobby ≈ 89 s,
F Lair ≈ 104 s).

- **full − loaded/off, Lobby:** median −0.1 % (11.00 vs 11.09), p95 +65 %, p99 **+195 %**
  (36.0 vs 12.25), worst 53 ms vs 17 ms, mod-owned main-thread **+4.4 ms/frame**, allocation
  **22×** (45 KB vs 2 KB per frame), GC0 9–10 vs 0–1.
- **full − loaded/off, Lair:** no L pair was captured (see gaps); F alone is p99 149 ms, 261 ms worst,
  24–28 % of frames over the 15 fps budget. Not attributable to the mod by subtraction — only by
  the `Perf` rows below.
- **full − vanilla:** not delivered.

## Noise floor

Repeat-to-repeat spread over identical windows: median identical to 2 dp; p99 spread **0.4 %** (L),
**3.1 %** (F Lobby), **7.4 %** (F Lair). The p99 differences above (195 %, 1200 %) are 6–50× the
noise. Nothing here is within noise. (Only 2 repeats per cell, so this is a spread estimate, not a
confidence interval.)

## Attribution (bench pass, `Perf` on for every loaded arm)

`Perf` window accumulation was fixed first: the old code returned before folding a frame's rows into
the window, so a 30-frame window summed 1 frame's rows and divided 30 frames of cost — window totals
were ~30× light. Also: nested scopes are excluded from the total (depth-aware), so rows marked `.`
below are children and must not be summed with their parent.

F Lobby, per frame: `late.mirror` **2.41 ms every frame** (a second full camera render for the
mirror — the single largest item), `runtime` 1.73 (of which `wardrobe` 0.70, `emote` 0.26,
`clothes` 0.22, `cosmetics` 0.11, remainder scans), 709 frames out of 10 350 ran a
`FindObjectsOfTypeAll` (0.77 ms/frame amortised, 10 ms per scan, ~6 types × 1 Hz TTL).
F Lair, per frame: `runtime` **7.19**, `emote` 1.68, `wardrobe` 1.19, `clothes` 0.73,
`reelplace` 0.68, and **scans 6.55 ms/frame amortised** — 680 frames ran a scan, 6–7 types
(`GameMapData`, `CurrentPlayerProvider`, `RebindActionUI`, `WardrobeModelDisplayer`,
`ThirdViewPlayerModel`, `PlayerPOVController`) at **44–50 ms each**, ~1 Hz. That is the same
collapse class as the earlier 1 fps incident: a 1 s TTL on a scan that costs 45 ms is 4.5 % of a
second per type, and 6 types make ~27 % of a wall second. `runtime=247 ms` was seen in one frame.
L Lobby for contrast: 0.010 ms/frame (runtime), no scans at all, total 0.015.

## Verdict per scene, against the thresholds fixed before measuring

- **Lobby — Fail.** Median is flat, but mod-owned main-thread work is **4.4 ms/frame** (threshold
  1 ms), recurring >2 ms spikes (mirror 2.4 ms/frame, scans 10 ms at 1 Hz), p99 **3×** the control
  (threshold: 10 %), allocation 22× and 9–10 gen-0 collections per 120 s against 0–1 (recurring
  mod-caused GC, threshold: fail). It does **not** miss the 15 fps budget at 640x360 — the game has
  ~55 ms of headroom per frame in the lobby — so the failure is cost, not visible stutter, in this
  scene at this resolution.
- **Lair (map) — Fail.** p99 149 ms, 24–28 % of frames over the 15 fps budget, 261 ms worst, scans
  6.55 ms/frame with 45 ms individual scans, 27 mod exceptions in the log during the run, and the
  process exited during/just after the second window (no crash line; not diagnosed). Repeated
  budget misses + renewed scan collapse = fail by the stated rule.
- **Overall: too heavy.** Not by median frame time (unchanged), but by the two thresholds that were
  set in advance: mod-owned main-thread >1 ms/frame (4.4–7.5), and regular subsystem spikes >2 ms
  (mirror 2.4 ms/frame, scans 10–50 ms). The map adds a second, worse failure on the budget.

Memory: external sampler covered F only — private bytes 4.70 GB → 7.58 GB across the crash run
(+2.9 GB; treated as the retained-memory investigation trigger, and it coincides with the map +
scan collapse), and 4.70 → 5.15 GB (+450 MB) across the menu run. No L sampler ran (bug in my
background launcher), so there is **no** L-vs-F retained-memory delta.

## Untrustworthy / not delivered (read before quoting anything)

1. **No vanilla frame times.** PresentMon 2.6 captured **zero** present events on this machine —
   target process, process id, and system-wide, with `--v1_metrics`, unique session, elevated token
   (`SeSystemProfilePrivilege` enabled): no CSV, no rows, for `Alterum.exe` and for `steamwebhelper`.
   It reports "Started/Stopped recording" and writes nothing. A loader-free arm has no in-process
   probe by definition, so the vanilla arm has *no* frame-time source until a working external
   collector is found. V was launched and confirmed running without the loader (menu at 2048x1152,
   its own saved resolution, no BepInEx log written); nothing numeric.
   → **full-minus-vanilla is missing; full-minus-loaded/off is the whole comparison here.** That is
   the stricter of the two (loader and host costs cancel), so the mod's own cost is not overstated
   by its absence — but "how much does the loader cost on top of vanilla" is unanswered.
2. **No L arm in the map, so the Lair comparison is one-sided.** Entering a map with one player is
   a mod-owned server write (`CanStartExpedition` / `RequiredPlayerNumberToStartGame`); in L the
   `reel.request startmap` hook sits behind the gate, so the control arm had no way into a map that
   vanilla could also use. Not fixed: no time left; a bench-only map-start verb in the gate is the
   fix and is a 20-line change.
3. **2 repeats, not 3; activity sequence not run.** Time box. Every window here is a stationary,
   fixed-camera 120 s capture. The full-mod arm does have the expensive thing on screen: custom
   body on, hat on, mirror rendering every frame, wardrobe/cosmetics/emote runtimes ticking.
4. **The activity/fps-cap pass is missing.** The game's own FRAME RATE LIMIT was 300 and no arm was
   capped, so only the *uncapped* pass exists; the 15 fps budget column is arithmetic
   (`wall_ms > 66.7`), not a capped measurement. The internal 15 fps cap was compiled out of all
   arms, so no arm was disadvantaged by it — but the "realistic capped" pass the task asked for was
   not run.
5. **Resolution.** L/F ran at 640x360 (launch args + `KeepQuiet`). V re-applied its saved 2048x1152,
   so even with a working collector V's frames would not have been the same resolution. Fixing that
   needs the game's own graphics settings (a UI click) — untested.
6. **Vanilla navigated by nothing**: it sat in the menu; no lobby was created for V (its navigation
   needs synthetic clicks, which I never got to). So the V arm proved only "runs without a loader".
7. `C:\AlterumB` (pid 93360, another leftover instance) and the pin (pid 82272) were stopped before
   measuring and are not in any number; the pin was restarted afterwards. RTSS was started and
   stopped during a capping experiment — no measurement ran with it resident.
8. The mod produced 27 exceptions in the Lair run and the process exited during the second window;
   the last logged frame is 16319 with the window complete, so both windows are whole, but the exit
   is unexplained and would need a run with the crash log to attribute.

## Reproduce

- `src/` — the merged tree as measured (bench build: `dotnet build -c Release -p:DefineConstants=BENCH
  -p:RefsDir=…/refs/`). `collector/` — gate + measurement sources (`Bench.cs`, patched `Perf.cs`,
  `HotModule.cs`, `ModelSwapRuntime.cs`) and the PowerShell harness (`mk2.ps1` install copies,
  `deploy.ps1`, `run-arm.ps1`, `windows.ps1` window markers, `sampler.ps1` external 2 Hz CPU/memory).
- `raw/` — every frame-time series quoted above, one CSV per window (columns: `frame, wall_ms,
  cpu_ms, wait_ms, mod_ms, unaccounted_ms, gc_alloc_bytes, gc_heap_bytes, gc0, gc1, gc2,
  unscaled_dt_s, delta_s, time_scale, scene, features_off, engine_frame, perf_rows`), plus the two
  F process samplers.
- Arms: `C:\AlterumPerf\{V,L,F}` (hardlink copies of the pin; V has `winhttp.dll.off` /
  `doorstop_config.ini.off`, L/F have `BepInEx/bench.cfg`). The pin is untouched and running.
