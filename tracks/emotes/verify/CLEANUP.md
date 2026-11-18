# Emotes track — single clean-up pass (P1.4, P1.3, P1.5, P2.7, P2.6)

Source worked on: `tracks/emotes/mod/` only. Driven only with
`./track.sh emotes build|deploy|run|log|stop|status`. Install `C:\AlterumT2`, hot reload only.
Desktop locked: evidence is log + RenderTexture only.

Final binary of every log below: `AlterumModelSwap.Hot.dll` md5
`3bd3cd0b555bf9acd9df2d5fd246bdfb` (source copies: `EmoteRuntime.pre_cleanup.cs`,
`PosePlayer.pre_cleanup.cs` are the "before").

Invariants re-checked after the pass:

| file | md5 |
|---|---|
| `mod/AlterumModelSwap.Contracts/Contracts.cs` | `ee94408c227debe84a93c3c4f2f6f852` (baseline) |
| `mod/AlterumModelSwap.Hot/Mirror.cs` | `08b77a17c997ed1b8cb0998d35ec8cb6` (baseline) |
| `mod/AlterumModelSwap.Hot/MirrorPlayerVisibility.cs` | `3f33c8244d41dde1901e8a9d5f48c179` (baseline) |
| `emotes/emote.bin` | `9231ee4dcff1846b38d44ef2ffe6e9f1` (unchanged) |
| `emotes/manifest.txt` | `472f6c69cefc9195b8a598dc4ca58ccd` (unchanged) |
| `emotes/manifest.json` / `coverage.json` | unchanged |

Pack not touched. 12 clips, 51 mappings, 406225 floats, fps 60 on every load.

## P1.4 — self-test is default-off and armed per run

`EmoteRuntime.cs:32` (`_selfTest` field), `:169` `ConfigureSelfTest()`, `:237` request key
`selftest=`, `:381` driver guard.

* Default is off; arming only via `emote.request` `selftest=1` or `ALTERUM_EMOTE_SELFTEST=1` at
  launch. A bare diagnostic request no longer enters manual mode, so the sweep can still run.
* `final_coldstart_and_idle_reload.log`:
  - cold start: `[emote:selftest] off (default)...`, `resolved model ... layers=5`, no
    `[emote] START`, no `suspended=True`, no `nudged owner`;
  - reload with no request: `[emote:teardown] id=1 writes=0 leaseHeld=False->False selftest=off
    layers=5->5 verifyCams=0 verifyRts=0`.
  The same sequence is at the top of `final_five_reloads_and_pelvis.log` (final binary: cold start
  line 20, idle-reload teardown line 159).
* `final_armed_sweep.log`: `[emote:request] self-test ARMED for this run` → 12 steps,
  `RESTORE CHECK ... 2.24deg`, `sequence complete`. `emote.request` with `clip=`/`repeats=` works
  unchanged (recording path used for every pelvis run below).
* Defect reproduced on the pre-cleanup build (`coldstart_oldbuild_autosweep.log`): cold start
  ran `selftest#1..12` and `nudged owner (0.00,0.00,0.00) -> (0.60,0.00,0.00)` unattended.

## P1.3 — transactional lease, retained release obligations, fail-closed writes

`PosePlayer.cs:745` `AcquireLease()`, `:793` `ReleaseLease(reason)`, `:734` `Stop()` release in
`finally`, `:844` `AbortPlayback()`, `:865`/`:880` write-failure trap, `:229` unbind teardown.

* Acquisition records each step before invoking it; `StartDoingSomething` failure or a disable that
  does not take effect aborts before any bind state is committed (`Start` clears `_clip`/`Active`
  and returns false). No pose write happens without the full lease.
* `ReleaseLease` keeps `_lease` + the owning handler + per-step obligations until a call returns;
  a throw logs `EMOTE_RELEASE_FAILED` and the key is retried by the next Stop/Dispose.
* A write/fade-out exception logs `EMOTE_WRITE_FAILED`, clears playback without fading, restores
  the pose and releases the lease. A looping clip cannot keep writing.
* Log-proven: every `START` has a matching `STOP ... released=True` (8/8 in the final run) and the
  mid-emote teardown shows `leaseHeld=True->False`. Zero `EMOTE_OWNERSHIP_FAILED`,
  `EMOTE_RELEASE_FAILED`, `EMOTE_WRITE_FAILED` lines. The failure branches themselves are not
  exercised by the game and are statically reasoned only.

## P1.5 — pelvis translation fades in, fades out and is restored

`PosePlayer.cs:956` entry (`* fade`), `:1121` `TickFadeOut` (position blended, `:1140` target
selection), `:988` `RestorePoseNow`, `:650` pre-start restore, `:229` unbind restore, `:1162`
fade-complete restore.

`final_five_reloads_and_pelvis.log`, Prisyadka (peak pelvis delta ≈ -0.50 m at full fade):

```
phase=in  t=0.067 fade=0.000 offset=0.0000  pelvisY= 0.0043
phase=in  t=0.133 fade=0.333 offset=-0.1432 pelvisY=-0.1389
phase=in  t=0.200 fade=0.667 offset=-0.2807 pelvisY=-0.2764
phase=in  t=0.267 fade=1.000 offset=-0.4554 pelvisY=-0.4512
phase=out fade=0.000 pelvisY=-0.4792 base=0.0043 live=-0.0149 target=animator
phase=out fade=0.333 pelvisY=-0.3236 live=-0.0126
phase=out fade=0.667 pelvisY=-0.1673 live=-0.0114
phase=out fade=1.000 pelvisY=-0.0113 live=-0.0113   -> restored 0 position(s)
```

Case "returning animator does not write pelvis translation" (branch forced with request
`baseline=1`, same code path, since the game re-drives the pelvis every frame):

```
baseline fade target forced ...
phase=out fade=0.000 pelvisY=-0.5061 base=-0.0033 target=baseline
phase=out fade=0.333 pelvisY=-0.3385 target=baseline
phase=out fade=1.000 pelvisY=-0.0033 target=baseline   -> restored 0 position(s)
```

Immediate teardown, reload while the clip is still running:

```
STOP reason=unbind clip='Prisyadka' t=3.09s released=True
[emote:pos] restored 1 position(s) to the pre-emote baseline reason=unbind
[emote:teardown] writes=45 leaseHeld=True->False layers=5->5 verifyCams=1 verifyRts=1
```

Entries are 3-4 frames at this frame rate, and the last fade-out tick is now forced to `a==1`
(previously a 15 fps fade ended at `a=0.667` and the restore added a ~0.17 m jump of its own).

## P2.7 — layer, camera and RenderTexture are retired

`EmoteRuntime.cs:985` `AcquireLayer` (reuse before Add), `:1028` `RetireLayer` (`DestroyStates`,
`Layers.Count = index` when it is the top layer), `:1086` `NativeStop`, `:1138` `Dispose`,
`:808` `DestroyVerifyResources` + `CountNamed<>`.

Five consecutive hot reloads, each generation requesting `clip=Salute` + `native=1`
(`final_five_reloads_and_pelvis.log`), final binary:

| gen | layer acquired | retire | teardown |
|---|---|---|---|
| 3 | layer 5 (layers 6) | 6->5 trimmed=True | layers 6->5, verifyCams 1, verifyRts 1 |
| 4 | layer 5 (layers 6) | 6->5 trimmed=True | layers 6->5, verifyCams 1, verifyRts 1 |
| 5 | layer 5 (layers 6) | 6->5 trimmed=True | layers 6->5, verifyCams 1, verifyRts 1 |
| 6 | layer 5 (layers 6) | 6->5 trimmed=True | layers 6->5, verifyCams 1, verifyRts 1 |
| 7 | layer 5 (layers 6) | 6->5 trimmed=True | layers 6->5, verifyCams 1, verifyRts 1 |

No growth in any of the three counters. `verifyCams/Rts` read 1 because `Object.Destroy` is
deferred to the end of the frame and the census runs in the teardown frame itself; the constant 1
across five reloads (and 0 before the first capture) proves the previous generation's objects are
gone. Before the fix the same session reached 29 layers, 31 cameras, 31 RTs
(`warm_before_coldstart.log`, teardown line `layers=29->29 verifyCams=31 verifyRts=31`).

## P2.6 — generation detection compares identity

`EmoteRuntime.cs:479` `CheckGeneration`, `:526` `Rebind`, `:538` `SamePointer`, `:707` fixup of
`_nextGenerationCheck` on resolve, `:695` bind-then-commit in `Resolve`.

* `swapped` now requires `SamePointer(current, _controller)` and `SamePointer(person, _person)`
  (native pointer identity) in addition to the old null tests.
* The check runs before the `_player_owner == null` early return, so an owner that disappears
  without a replacement stops/unbinds instead of returning.
* `_refs == null` no longer returns silently; it rebinds (stopping first when a lease/pose exists).
* `Resolve` commits `_player_owner/_refs/_controller/_person/_animator` only after `Bind` returns
  true, so a failed bind cannot suppress the next resolve.
* No natural model swap occurred in this session (`grep -c "model changed"` = 0), so this fix is
  statically reasoned; the normal path (resolve once, `Rebind` never taken) is log-proven.

## Port regression check (same rig, same session)

Pre-cleanup build vs cleanup build on `3P_ConcreteModel_Male_03`: all 46 clip/strip keys and all
12 durations identical; see `metrics_same_rig_compare.txt`. Every delta tracks the sample time
offset (`dt`). Where clip time is identical the metrics are identical except a 1-2 mm pelvis-Y
baseline shift, which is the P1.5 fix itself (the old build captured a leftover offset as the new
baseline), plus one in-fade sample (Salute strip 0) that differs because the pelvis now ramps
during entry. No rotation/IK retarget code changed.

## Known, deferred (not touched)

* P2.8 LateUpdate ordering (still relies on `PostAnimationUpdate` running before the mirror tick).
* P2.10 pack validation hardening.
* P2.11 elbow continuity through singularities.
* P2.14 fail-closed retargeting.
* P2.15 tooling loop-period mismatch.
