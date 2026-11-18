# Perf re-run 4 — the random-on-join explicit choice, `bind.tick` made cheap, Menu arm re-measured

Scope: two items — (1) the roll must defer to a persisted **explicit** model choice; (2) `bind.tick`
(1.25 ms/frame in the Menu p99 band) made cheap. Then the **Menu arm only**, same method as
REPORT-3.

**Verdict on the target: met.** Menu p99 pooled **13.02 ms vs 12.29 ms control = +5.9 %**
(target ≤ +10 %; last pass +13.7 %). p95 +4.1 %, median 0.0 %, **0** frames over 66.7 ms, mod
**0.282 ms/frame** (was 0.310), `bind.tick` **0.0021 ms/frame** (was 0.033).

## Item 1 — an explicit choice is not overwritten by the roll

`SahurWardrobe` gained one accessor, `ExplicitWanted(log, out entry, out label, out applied)`: it
reads the wanted cfg if this generation has not (same `EnsureWanted` the wardrobe's own sync uses)
and reports `0` = no explicit choice, `1` = explicit roster entry, `2` = explicit native row, plus
whether the game's applied row already *is* that entry. `RandomModelOnJoin.Tick` asks it at the
roll point — after the game's own join apply, after the 3 s grace, roster ready — and if it answers
non-zero it **skips the roll** and, when the join path did not already land it,
`SahurWardrobe.ApplyEntry(...)` re-applies the entry through `PlayersAppearanceManager
.SetBaseModelIndex` (the game's own path). Nothing else changed: one roll per lobby membership, the
3 s grace, and a same-session manual pick still wins (checked before the roster/grace gates).

Log line (both cases, pin `C:\AlterumPinned`, live Lobby, hot reload = fresh module generation):

```
[random:join] no roll: reason=an explicit model choice is persisted ('Peter Griffin' entry=15, explicit=1),
               and it outranks the roll on a fresh join - re-applied=yes (pool=16, lobby=109775242831090703,
               client=0, pid=32592)
[sahur:wardrobe] apply 'Peter Griffin': SetBaseModelIndex(client=0, 25) on 'Lobby Global/Network/PlayersAppearance'
[sahur:map] wanted entry restored from '...\alterum-wanted-model.cfg': 15 'Peter Griffin' (peter-griffin) [explicit]
```

Both cases, same session, same lobby:

| case | config | result |
|---|---|---|
| explicit pick persisted | `slug=peter-griffin`, `explicit=1`, no membership record | **no roll**, restored + re-applied (`SetBaseModelIndex(…,25)`, rig built, hat seated) |
| explicit pick persisted (original record) | `slug=tc2-scout`, `explicit=1` | **no roll** (`'TC2 Scout' entry=5 … re-applied=yes`) |
| no explicit pick (wanted cfg deleted = fresh config) | no wanted cfg, no membership record | **ROLL … pool=16 … chosenEntry=14 chosenDbIndex=24 chosenLabel='TC2 Saxton Hale'** |

No new state is written on the skip path (a skip is not a roll), so the membership record stays
absent and the wardrobe keeps owning the choice.

## Item 2 — what `bind.tick` was doing, and what it costs now

`ModBindings.Tick` = `Attach` (a bool check after the first call) + `ModControlsUI.Tick` +
`ModPauseSettings.Tick` + `MenuProof.Tick`. The per-frame cost was already µs; the cost was the
**0.5 s audits**, and they all landed in one frame: in `rerun2/bench_F_menu5_w1.csv` **246 frames had
`bind.tick > 0.5 ms`, exactly 500 ms apart, and exactly the same 246 frames carried `wardrobe >
0.3`**. Mean over the whole window was only 0.033 ms/frame; in the 13–15 ms band it was **1.27 ms** —
i.e. the entire Menu p99 band was one periodically-audited frame, and `bind.tick` was first in line
in it.

The audit itself: `ModControlsUI.Mirror` called `RebindActionAndBinding` **35 times per sweep** and,
in this game, that call fails for every one of our rows (`[bind:rows] row id=… resolved=False
readOnly=True` on every row, also on the pin) — 35 native row/asset walks for zero effect, 1.2–3.9 ms
each sweep. `EffectivePathOf` also re-read `action.bindings` (a full marshalled copy of the binding
list) three times per row.

Changes (all in `tracks/merged/mod/AlterumModelSwap.Hot`, no feature removed, no key path
re-routed):

- **Row handles are resolved once and cached per generation** (`RowState`: action + binding index).
  A native rebind changes the *binding path* on that same action, so the mirror still sees it; the
  cache is cleared in `Build` (new rows / new menu generation).
- **A row that failed to resolve is not retried every sweep** — 5 s round-robin, and the last
  mirrored path is compared before `SetFromNative` (no parse when nothing changed).
- **`EffectivePathOf` reads `action.bindings` once** per row instead of three times.
- **Sweep rate follows the UI**: 0.5 s while the pause menu is open (the only place a native row can
  be rebound) and while the rows are unbuilt, 2 s otherwise.
- **`ModBindings`: held modifiers are memoised per engine frame** (`Time.frameCount`), so N shortcut
  polls cost N key reads instead of N × 7. Rebinds are untouched: `Pressed/Held` still read
  `action.Current`, which `SetOverride`/`SetFromNative` write synchronously.
- **`MenuProof.Poll` caches its request path** (it `Path.Combine`d, i.e. allocated, every 0.35 s).

Measured, same build, in-process CSV:

| row | before (`F_menu5`, 2×120 s) | after (`F_menu7`, 2×120 s) |
|---|---:|---:|
| `bind.tick` mean | 0.033 ms/frame | **0.0021** |
| `bind.tick` max | 3.88 ms | **0.34 ms** |
| `bind.tick` in the 13–15 ms band | **1.27 ms** (493 band frames) | **0.002 ms** (79 band frames) |
| frames with `bind.tick > 0.5 ms` | 246 / 22 350 | **0** |

All 35 actions still register (`[bind] 35 actions registered (world=15 picker=20)`, `[keys] registry`
logged), the native Controls rows still build (`[bind:rows] built … resolved=0/35`, i.e. the
pre-existing read-only listing), and the arm logs contain 0 `[Error]` lines.

## Menu arm re-measured

Method identical to REPORT-3: `logman` DXGI provider `{CA11C036-…}`, ID 42, filtered by the arm's
PID, present-to-present deltas in 100 ns; 640×360 windowed, **uncapped** (`BENCH` compiles
`EnforceFrameCap` out), **one arm at a time** (the other arm's process stopped and verified gone
before each run), 10 s warm-up, two 120 s windows per arm, L 19:04:56–19:09:00 and F
19:14:44–19:18:48 (same session, back to back). Two earlier attempts (`L_menu6`, `F_menu6`) were
**discarded**: the previous arm's process was still up and its presents doubled the trace (156 MB
vs 84 MB) — the reported windows are the ones where `Get-Process` showed only the arm under test,
the pin and `C:\AlterumB`. Raw series: `rerun3/raw/dxgi_*_menu7_*.csv`,
in-process `rerun3/*_menu7_*.csv`, analyser `rerun3/menu.py`.

| arm | window | frames | median | p95 | p99 | worst | >66.7 ms | mod ms/f | alloc B/f | GC0 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| L (control) | w1 | 10 802 | 11.10 | 11.57 | 12.31 | 16.95 | 0 | — | — | — |
| L (control) | w2 | 10 802 | 11.10 | 11.61 | 12.26 | 16.57 | 0 | — | — | — |
| **L pooled** | | 21 604 | **11.10** | **11.59** | **12.29** | 16.95 | 0 | 0.016 | 1 998 | 0 |
| F (full) | w1 | 10 773 | 11.09 | 12.11 | 13.15 | 44.08 | 0 | 0.283 | 15 654 | 5 |
| F (full) | w2 | 10 773 | 11.10 | 12.04 | 12.90 | 51.90 | 0 | 0.281 | 15 857 | 6 |
| **F pooled** | | 21 546 | **11.10** | **12.07** | **13.02** | 51.90 | **0** | **0.282** | 15 756 | 11 |

Deltas: median **0.0 %**, p95 **+4.1 %**, **p99 +5.9 %** (0.73 ms) — inside the ≤ +10 % target.
Repeat-to-repeat p99 spread: F 13.15 → 12.90 (−1.9 %), L 12.31 → 12.26 (−0.4 %).

### What is left in the p99 band (no features removed)

Pooled F frames in the 13–15 ms band: **79** (was 493), mean mod **0.56 ms** (was 2.32):
`runtime` 0.465, `wardrobe` 0.150, `cosmetics` 0.046, `clothsim` 0.046, `late.bounds` 0.030,
`mapbody` 0.025, `pickers` 0.020 — `bind.tick` 0.002. Zero `scan:`/`cache:` rows in either window
(one `scan:CurrentPlayerProvider` row in F w1, the startup ladder).

Worst hitches are unchanged and unrelated to polling: **41 frames > 33.3 ms, 38 of them `qc`**
(1 188 ms total, ~30 ms each) — the GameConsole bridge's "ensure no live console yet" retries. None
exceeds 66.7 ms, so the over-budget count is 0; excluding those frames would move F p99 down
further, but they sit beyond p99 (42/21 546 = 0.19 %) and do not set it.

## Build

`bash .temp/build.sh` (production `tracks/merged/mod`) → **0 error CS**, `Build succeeded`,
`Ошибок: 0`, `dotnet exit=0`. Production `AlterumModelSwap.Hot.dll` md5
`2037ecc3c802f0dd155fc2671009822e`. Measurement copy (`validation/perf/2026-11-15/src`, `BENCH` +
`Bench.cs`, `RefsDir` override) rebuilt clean too, 0 errors, md5 `2f42058ed54a4376118ffd9551025ac2`.
No bench-only constant was added to the production tree (`grep -c BENCH` in its csproj = 0,
`FrameCap = 15` untouched).

## Pin state

`C:\AlterumPinned`, pid 32592, **generation 6**, `[fps] cap applied 15 (vSync off)`, scene
**Lobby**, `BepInEx/hot/AlterumModelSwap.Hot.dll` md5 `2037ecc3c802f0dd155fc2671009822e` (the
production build). `[Error]` lines since generation 6: **0**; `grep -c SWAP` = **0**. Wanted cfg is
back to the original record (`slug=tc2-scout`, `explicit=1`); no membership record. Peer
`C:\AlterumB` (pid 92856) untouched. The perf arms (`C:\AlterumPerf\L`, `\F`) are stopped.

Not re-exercised this pass: the reel/`cells=64` line (the pin stayed in the Lobby, no reel strip was
generated; no reel/deck file is in the diff) and a live keystroke dispatch test (key injection from
the ssh session-0 shell cannot reach the game in session 1 — the arm/pin runs use request files).
