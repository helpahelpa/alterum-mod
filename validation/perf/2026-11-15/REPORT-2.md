# Perf re-run — scans and mirror fixed, two measurement gaps closed

**Verdict against the thresholds fixed before measuring: Lobby and Lair pass on mod CPU and on
p99-vs-control. One target is not fully met: the mirror still costs ~2.8 ms on the frames it
renders (0.5 % of Lobby frames), and the Menu scene still fails p99 because its settings/UI polls
still walk the object table. The vanilla arm has a working external frame source but is Menu-only:
the desktop is locked, so the loader-free build cannot be navigated to a Lobby.**

Production build on the pin: `AlterumModelSwap.Hot.dll` md5 `0732485fcabc0653af4b3c93d587d814`,
pid 44240, generation 2, `[fps] cap applied 15`, Lobby. No feature removed; the pin was never written by a
benchmark run.

## What changed

1. **`SceneCache` is generation-scoped, not TTL-scoped.** One scan per type per scene; the entry is
   held until an event invalidates its group. Empty results retry with backoff (1 s … 30 s) and stop
   retrying once found. Scans still happen once at a scene transition (plus `MapBody`'s first
   census), but not once a second in steady state.
2. **Events replace polling**:
   - body spawn / `SetupModel` invalidates the body group only (`MapBody.PostSetupModel`);
   - join/session spawn invalidates the player group (`JoinTrace`);
   - POV changes invalidate the camera group (`PovToggle`);
   - wardrobe/menu objects are polled only while the cursor is visible; gameplay never pays for
     them (`SceneCache.ScanPolled` + cursor-visible invalidation);
   - `SahurPreview` and the wardrobe UI scan are skipped outside the Lobby;
   - `ReelPlacements` map identity is warmed during the scene transition;
   - `LobbyStartSettings.Sync` returns immediately outside the Lobby (it used to scan the map
     object table for a readiness controller that cannot exist there).
3. **`startmap` bench verb** in `UiDriver` (the existing `ReelLogic.StartMap` test hook). The
   features-off arm now enters Lair through the same game path as the full arm. This closes the
   one-sided map comparison.
4. **Mirror**: RT 768² at ≤800 px long axis (1536² above), 15 Hz while the local player moves,
   0.1 Hz idle, and no render at all when the quad is outside the player camera frustum.

## Scan cost, before → after

| scene | before | after (steady-state windows) |
|---|---|---|
| Lobby | ~10 ms per scan, ~1 Hz: ~10 ms/s | **0 scan rows in 4×120 s** |
| Lair | 44–50 ms per type × 6–7 types at ~1 Hz: **~27 % of a wall second** | **0 scan rows in 4×120 s** |

Method: the per-frame CSV row `scan:<Type>` is written by `SceneCache` for every actual
`FindObjectsOfTypeAll`. In the eight 120 s Lobby/Lair windows there are no such rows. The only
remaining scan rows in the logs are the Menu scene's settings/UI types (see “Still over budget”)
and the first transition scans: ~3–30 ms per type, 7 types, once per scene, inside the transition.

## Mirror change and the trade-off

Before: a 1536² second camera rendered every frame, `late.mirror` **2.41 ms/frame**.

After: 768² at the benchmark's 640×360; 15 Hz while the local player is moving; 0.1 Hz when the
player is still; skipped entirely when the quad is outside the player camera. In the two Lobby
windows `late.mirror` averages **0.013 ms/frame**; render frames are ~2.8 ms each and about 0.5 %
of frames (the fixed URP `Camera.Render` overhead dominates; reducing 1536→768 did not change the
per-render cost much).

Visual trade-off taken: a moving local player updates at 15 Hz and still shows the body/hats; an
otherwise idle mirror refreshes every 10 s. Remote-player motion while the local player is idle can
therefore lag up to 10 s. Raising `IdleHz` to 1–2 Hz would restore that at +0.02–0.05 ms/frame
average but put a ~2.8 ms spike into every 0.5–1 s; I kept the lower rate because the mirror is a
static-camera lobby decoration and the benchmark/steady-state case is idle.

## Measurement method

Same method for every arm: 640×360 windowed, uncapped, 25 s warm-up inside the scene, two 120 s
windows, one arm at a time. Frame times are **external DXGI present-to-present**:

```
logman start dxgi_<tag> -p {CA11C036-0102-4A2D-A6AD-F03CFED5D3C9} 0xFFFFFFFFFFFFFFFF 5 -o t.etl -ets
... 120 s ...
logman stop dxgi_<tag> -ets
tracerpt t.etl -of CSV -o t.csv
```

Event `Microsoft-Windows-DXGI` / ID 42 (`Present` start) filtered by the arm's PID; deltas in
100 ns. This is the collector that replaced PresentMon (2.6 produced no CSV). It agrees with the
in-process `wall_ms` within 0.1 ms median / ~1 % p99 on the loaded arms. `mod_ms`, main-thread CPU,
allocation and GC come from the in-process bench CSV. Raw series:
`rerun/raw/*.csv.gz`, summary `rerun/summary.csv`.

## Results (pooled over the two 120 s windows)

| scene / arm | frames | median | p95 | p99 | worst | >66.7 ms | mod ms/frame | main CPU ms/frame | alloc B/frame | GC0 / 120 s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Menu V (vanilla) | 21 606 | 11.11 | 11.36 | **11.46** | 15.64 | 0 | — | — | — | — |
| Menu L (loaded/off) | 21 605 | 11.11 | 11.44 | 11.91 | 15.78 | 0 | 0.013 | 1.43 | 1 995 | 0 |
| Menu F (full) | 21 523 | 11.11 | 13.00 | **22.41** | 71.74 | 1 | 0.477 | 1.93 | 18 241 | 9 |
| Lobby L | 21 603 | 11.11 | 11.72 | **12.17** | 49.58 | 0 | 0.013 | 3.15 | 2 006 | 1 |
| Lobby F | 21 604 | 11.11 | 12.16 | **13.36** | 28.10 | 0 | **0.350** | 3.70 | 19 956 | 9 |
| Lair L | 21 600 | 11.12 | 12.49 | **13.19** | 58.27 | 0 | 0.014 | 5.72 | 2 195 | 1 |
| Lair F | 21 590 | 11.07 | 12.87 | **14.00** | 69.52 | 1 | **0.437** | 6.07 | 24 632 | 11 |

The full arm wore a custom body with its hat/bat in both scenes: Sahur (Classic) in the Lobby,
and the persisted rigged TC2 Medic in Lair (`[mapbody] rig[on=1] bodyGo=1`); the native body was
not the one on screen. Repeat-to-repeat p99 spread: Lobby F 13.32→13.44 (+0.9 %), Lair F
13.95→14.03 (+0.6 %), L 12.14→12.19 (+0.4 %); nothing here is inside noise.

Deltas against the features-off control:

- **Lobby:** median −0.03 %, p95 +3.8 %, **p99 +9.8 %** (≤10 % target), 0 over-budget frames,
  mod +0.337 ms/frame.
- **Lair:** median −0.5 %, p95 +3.0 %, **p99 +6.1 %**, 1/21 590 frames over 66.7 ms (0.005 %),
  mod +0.423 ms/frame.
- **Menu:** median flat, p95 +13.6 %, **p99 +88 %** — still fails. This is the UI/settings polling
  described below, not the gameplay path.

## Remaining attribution

F Lobby, 0.350 ms/frame: runtime 0.26 (wardrobe 0.030, cosmetics 0.033, sahur 0.026, emote 0.020,
clothsim 0.016, cosmetic.bind 0.014, pickers 0.012), late.bounds 0.062, late.mirror 0.013.
F Lair, 0.437 ms/frame: runtime 0.32, late.sahurrig 0.087, late.jigsolve 0.064, reelplace 0.035,
cosmetics 0.030, sahur 0.028, prop 0.025, emote 0.021, late.bounds 0.021, cosmetic.bind 0.016.
Nested rows are not additive.

Still over the stated spike target, quantified:

- **Mirror render:** ~2.8 ms on ~0.5 % of Lobby frames (idle refresh). To remove it entirely the
  fixed per-render cost has to come down (the 1536→768 change barely moved it) or the mirror must
  freeze when idle. Freezing is a visible behaviour change; I kept the 10 s heartbeat.
- **Body refit:** 6–12 ms on a wardrobe/body change (`cosmetic.bind`), 1–5 frames per 120 s. It is
  event-driven, not periodic.
- **Map `late.sahurrig`:** 2–4 ms on 7–8 frames per 120 s while the rigged custom body is bound
  (0.06 % of frames). The native body path does not pay it.
- **Menu:** `PauseMenuView`, `RebindActionUI`, `Pauser` and `TMP_InputField` are still polled at
  0.5–1 s while the cursor is visible. In the Menu scene that is ~160 scans/120 s and p99 22 ms.
  Lobby/Lair windows contain none of these because the cursor is hidden or the Menu-only settings
  block does not run. A full fix is to event-drive the settings/controls panels (open/close) rather
  than poll them; that was not worth the remaining time and does not affect gameplay scenes.

Allocation/GC is still above control (19.9 KB vs 2.0 KB per frame Lobby; GC0 9 vs 1 per 120 s).
It is 2.3× lower than the previous full-mod 45 KB/frame, but the remaining allocation is not zero.

## The two measurement gaps

1. **Vanilla frame source — fixed.** PresentMon still produces nothing on this box (target,
   process-id and system-wide; no CSV, no rows). The `logman` DXGI session above works, is external,
   and was cross-checked against the in-process series on L/F. Vanilla V was run at 640×360
   windowed by editing the game's own `settings/game/graphics.json` (`FullScreenMode=3`,
   `VSync=0`); no loader, no BepInEx log. V Menu: p99 11.46 ms, 0 over-budget frames.
   **Not delivered: V Lobby/Lair.** The interactive desktop is locked (`LockApp`,
   `Windows Default Lock Screen`, foreground 0x79903A6); keyboard/mouse events cannot reach the
   game, and vanilla has no `ui.request` automation by definition. The V arm is therefore a
   loader-free **Menu** baseline, not a Lobby/Lair baseline. To close it materially: unlock the
   desktop and click V through the menu, or provide a pre-navigated vanilla save. This is a
   navigation limitation, not a frame-time-source limitation.
2. **Control arm in maps — fixed.** `ui.request startmap` runs through `UiDriver` in the
   features-off arm and uses the game's `GameSceneStarter`/readiness path. L Lair was captured
   2×120 s; the map comparison is now two-sided. Log proof: L `startmap requested` →
   `GameSceneStarter objects=1` → `StartTransitionToGameScene invoked` → Lair.

## Reproduce / where things are

- New measurement build source: `src/` (contains the BENCH-only instrumentation).
- Production source: `tracks/merged/mod/`; hot dll md5 above.
- Harness: `run-arm-2.ps1` (lobby/map, DXGI + bench CSV), `run-menu.ps1`, `run-v.ps1`,
  `enumwin.ps1`, `stop-all-alterum.ps1` in `C:\Windows\Temp\alterum\perf\`.
- Raw series this report quotes: `rerun/raw/*.csv.gz`; table: `rerun/summary.csv`.
- Arms: `C:\AlterumPerf\{V,L,F}`. V has the loader moved aside; L has `features=off`; F
  `features=on`. The pin is running the production build with its 15 fps cap; the benchmark arms
  are separate copies.
