# Perf re-run 3 — residual menu scan, Menu arm re-measured

Scope: the residual `scan:CurrentPlayerProvider` and the Menu p99. No maps, no respawn, no
networking. Production tree (`tracks/merged/mod/AlterumModelSwap.Hot`) is **unmodified by this
pass** — see "Task 1" for why, with the evidence that the retry is not restarted by anything.

**Verdict on the target: fails.** Menu p99 pooled **13.78 ms vs 12.12 ms control = +13.7 %**
(target ≤ +10 %). p95 +3.2 %, median −0.1 %, **1** frame over 66.7 ms (0.005 %), mod
**0.310 ms/frame**. The remaining gap is ordinary per-frame feature work plus its GC, not polling:
the p99 band (13–15 ms frames) carries 2.32 ms of mod work against 0.03 ms on control, and **zero**
scan or cache-invalidation rows in either window.

## Task 1 — the residual scan

**What "restarted" it: nothing does any more.** The 30 s row is the exponential-backoff ladder of
the *pre-cap* arm build, not a restart. Two independent pieces of evidence:

1. **The ladder, observed to completion.** The F arm's log from the run before the cap was measured
   (`C:\AlterumPerf\F\BepInEx\LogOutput.log`, generation 1, engine frames 2 → 44 046 in one Menu
   session) shows `scan:CurrentPlayerProvider` at frames **2, 66, 246, 606, 1326** and then **never
   again** for the remaining 42 720 frames (~8 min). That is `EmptyRetryMin/Max` = 1 s … 30 s with
   `EmptyRetryAttempts = 5` (t0, +1, +2, +4, +8 s), i.e. the ladder runs out inside 15 s. The 11:54
   Menu arm (`F_menu_w1.csv`) shows exactly the older shape: **4 provider scans in 120 s**, one per
   30 s, forever. The tree's cap is what removes it; the report's "still fires every ~30 s" is that
   older arm, and re-running the current build does not reproduce it.
2. **No restart path exists, and every candidate was checked.** The cache entry is dropped in only
   four places, all event-driven, never on a timer:
   - `InvalidateMenu()` ← `MenuScope.SetState` (Pauser open/close edge + page change) and
     `SceneCache.Tick` (cursor visibility edge);
   - `InvalidatePlayer()` ← `JoinTrace` spawn postfixes only;
   - `InvalidateBody()` ← `MapBody.PostSetupModel`; `InvalidateCamera()` ← `PovToggle` POV edges;
   - `_map.Clear()` ← `SceneCache.CheckScene`, only when `SceneManager.GetActiveScene().handle`
     actually changes (one clear per scene load; `MenuScope.Tick` uses the same handle and never
     clears).
   `InvalidateAll()` has no callers. There is no generation bump in steady state (hot reload is
   file-driven, one `[hot] generation 1 active` per launch) and `MenuScope` logs no repeated
   open/close edges (`grep menu:scope` = install + one attach per launch).

   To make that falsifiable I instrumented the measurement copy (`validation/perf/.../src`) with two
   counters, `cache:inv` (any entry dropped by an invalidation) and `cache:scene-clear`. They are
   per-frame CSV columns like every other `Perf` row. **Both are absent from every measured
   window below** — no invalidation fires in a Menu or Lobby steady state, so no retry can restart.
   The `Perf.Hit` channel is verified live in the same CSVs: `scanhit` (cache served without a scan)
   is present on 2 156 frames of `F_menu5_w2`, so the zero is "nothing invalidated", not
   "instrumentation off".

No source change was needed for this, and none was made: the production hot tree still builds from
the same bytes as the dll on the pin (`9a306bd8c11f3abe9ebb7056ef5333ae`).

### Zero-scan proof

Measurement build (`validation/perf/2026-11-15/src`, `BENCH` + `Bench.cs`), 640×360, uncapped, per-frame CSV.

| window | scene | frames | `scan:` rows | `cache:` rows |
|---|---|---:|---:|---:|
| F Menu, 120 s (`F_menu5_w2`) | Menu | 11 160 | **0** | **0** |
| F Menu, 120 s (`F_menu5_w1`) | Menu | 11 190 | 1 (`scan:CurrentPlayerProvider`, 5.2 ms — the last ladder step at +8 s) | 0 |
| F Lobby, 30 s (`F_lobby4_w1`) | Lobby | 2 880 | **0** | **0** |

So: a **120 s Menu window with zero scan rows** (and the only scan in the preceding window is the
one-shot last retry), and a **30 s Lobby window with zero scan rows**. Aggregated over both Menu
windows: one provider scan in 240 s, 5.2 ms total, at ~+8 s from the first lookup.

## Task 2 — Menu arm re-measured

Method identical to REPORT-2: `logman` DXGI provider `{CA11C036-…}`, ID 42, filtered by the arm's
PID, present-to-present deltas in 100 ns; 640×360 windowed, **uncapped** (`BENCH` compiles
`EnforceFrameCap` out), one arm at a time, 10 s warm-up, two 120 s windows per arm, F and L in the
same session back to back (L 17:07–17:11, F 17:02–17:06). Raw series and the analyser:
`rerun2/raw/dxgi_*.csv`, `rerun2/menu.py`.

| arm | window | frames | median | p95 | p99 | worst | >66.7 ms | mod ms/f | alloc B/f | GC0 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| L (control) | w1 | 10 802 | 11.11 | 11.62 | 12.12 | 16.23 | 0 | 0.017 | 1 991 | 0 |
| L (control) | w2 | 10 803 | 11.11 | 11.68 | 12.13 | 16.06 | 0 | 0.017 | 1 999 | 0 |
| **L pooled** | | 21 605 | **11.11** | **11.66** | **12.12** | 16.23 | 0 | 0.017 | 1 995 | 0 |
| F (full) | w1 | 10 772 | 11.10 | 12.07 | 13.77 | 71.66 | 1 | 0.312 | 16 442 | 6 |
| F (full) | w2 | 10 770 | 11.10 | 12.00 | 13.78 | 58.88 | 0 | 0.308 | 16 326 | 5 |
| **F pooled** | | 21 542 | **11.10** | **12.03** | **13.78** | 71.66 | **1** | **0.310** | 16 384 | 11 |

Deltas: median **−0.1 %**, p95 **+3.2 %**, **p99 +13.7 %** (1.66 ms), 1 over-budget frame in
21 542 (0.005 %), mod +0.293 ms/frame. Repeat-to-repeat spread: F p99 13.77 → 13.78 (+0.07 %),
L 12.12 → 12.13 (+0.08 %); the gap is not noise.

**Verdict: not met.** +13.7 % against ≤ +10 %; the miss is 1.66 ms of p99, 3.7 points.

### Attribution of what is left (no features removed)

In-process CSV, same windows. Frames in the 13–15 ms band that straddles the F p99 (493 frames):
mean mod **2.32 ms** there, allocated **62 KB/frame**; the control's p99 band carries 0.03 ms.
Inside that band: `bind.tick` (shortcut registry, per frame) 1.25 ms/frame, `runtime` 0.91,
`wardrobe` (SahurWardrobe assert/poll) 0.53, then cosmetics/cloth/pickers in the 0.05–0.1 range.
GC is the other half: 16.4 KB/frame allocated (vs 2.0 KB control) → 11 GC0 per 240 s vs 0.

Worst single hitches are the game-console bootstrap, not polling: `qc` = 63 frames ≥ 1 ms,
1 564 ms total, max **60.4 ms** (the `GameConsole` bridge's "ensure no live console yet" retries
walk the object table directly, untagged). It is 0.28 % of frames: excluding every `qc ≥ 1 ms`
frame moves the in-process F p99 **13.71 → 13.56 ms (+12.3 % vs control)**, so even removing that
whole diagnostic would not reach the target.

What it would cost to reach ≤ +10 %: the p99 gap is ~1.66 ms and the mean mod work is 0.31 ms, so
the number cannot be closed by shaving the mean — it needs the 2–4 ms spikes in `bind.tick` /
`runtime` / `wardrobe` flattened and the per-frame allocation (~16 KB) cut to stop the GC hits.
That is real work on the features themselves; stopping or deleting them would hit the number and
break the mod, which is not a pass.

## Build

- Before and after: `bash .temp/build.sh` (production `tracks/merged/mod`) → **0 error CS**,
  `Build succeeded`, `Ошибок: 0`, `dotnet exit=0`.
- Measurement copy rebuilt clean too (`validation/perf/2026-11-15/src`, 0 errors) with two changes,
  both in that copy only: the `BENCH` constant restored in its `.csproj` (it was lost when the
  production csproj was copied over it — that is also why the first two attempts of this pass ran at
  the 15 fps cap and were discarded), and the two `cache:` counters in `SceneCache`.

## Pin state

`C:\AlterumPinned`, pid 38484, production `AlterumModelSwap.Hot.dll` md5
`9a306bd8c11f3abe9ebb7056ef5333ae` (built 16:28 from `tracks/merged/mod`), generation 1,
`[fps] cap applied 15` ×3, scene **Lobby**. `grep -c "[Error"` = **0** (no QuantumConsole
deregister lines either), `grep -c SWAP` = **0**. `C:\AlterumB` (pid 27520) untouched. Nothing I
started is still running; the two benchmark arms were stopped by exact path.

## Caveats

- Two 120 s windows per arm in one session (the brief's ≥3 repeats did not fit the time box after
  the two discarded capped runs). Spread is quoted above and is ~0.1 %; the 3.7-point miss is far
  outside it.
- The control's absolute p99 (12.12 ms) is this session's; it is not comparable to REPORT-2's 11.91
  because this session's DXGI filter counted 21 605 frames per arm over 240 s (≈90 fps, uncapped),
  not the halved counts the previous report used for its control.
