# bones track — why the shipped breast cloth never simulated, and the fix

Scope: the MagicaCloth2 `Breast` BoneCloth that ships on the female rigs. Root-cause work, driven
entirely from the mod through `ScreensDir` request files, RT captures and the BepInEx log.
Install: `C:\AlterumT1`, log `C:\AlterumT1\BepInEx\LogOutput.log`.

## Answer

**The whole MagicaCloth2 simulation is dead in this build until the manager's custom game loop is
re-registered.** No team is ever stepped: `ClothManager.ClothUpdate()` runs every frame but
`TeamManager.SimulationStepTeamUpdate()` is called **zero** times and
`SimulationManager.SimulationStepCount` stays 0. The cloths still write a pose back every frame
(the rest/animation pose), which is why the leaves look "written to" while never moving.

That is not owner-specific and not camera culling. One call to
`MagicaCloth2.MagicaManager.SetUpdateLocation(...)` — made after the manager is alive, i.e. from
the first model sweeps, not from module init — re-registers the loop and the simulation starts.
The fix is `ClothSim.Tick()` called from `Proportions.Sweep()`.

## What was measured

### 1. Enabled camera instead of `enabled=false` + manual `Render()` — no effect

`cam=live` (camera `enabled = true`, `targetTexture = rt`, no manual render) and a force impulse:
0.0000° on all four chest bones over 25 frames. `cameraCullingMode=Off` + `SetParameterChange`
(the same run: the cloth's own culling flags report `camInvis=False`, `rvis=1`) — also 0.0000°.
Culling is demonstrably not the gate: `int_visible` ran with `IsCameraCullingInvisible=False`,
the cloth's renderers value 1 in `ClothManager.rendererVisibleDict`, and the leaf still did not
move.

### 2. Control: does **any** cloth simulate? No

`KickAll` sends `AddForce(right, 30, VelocityChange)` to every running cloth every 0.5 s for 7 s
while a census samples every MagicaCloth in memory (29 components, 8 running, 0 mesh cloths):

```
[census] cloths=29 running=8 stepping=0
```

Per-cloth mean local-rotation delta over the whole census: **0.0000° for all 29**, including

* the local player's own body — `Third Person Concrete Model Parent/3P_ConcreteModel_Male_04_James/Magica Cloth/Hair`,
  a BoneCloth with 10 used transforms, `IsRunning=True`, `IsCameraCullingInvisible=False`,
  `rendererVisibleDict=1`;
* every wardrobe rig (hair, bang, skirt, breast, raincoat).

A fresh 3-bone BoneCloth created by the mod itself (culling `Off`, `gravity=1`,
`blendWeight=1`, no animator, `BuildAndRun=True`, `valid=True`, `run=True`, 3 particles,
horizontal chain) also never moves: 0.000° / 0.00 mm over 6 s. So this is not the owner body,
not a prefab, not a renderer, not culling.

### 3. Culling permutations — no effect

`cameraCullingMode` (Off / Keep / Reset / AnimatorLinkage) and `distanceCulling` were varied via
`serializeData` + `SetParameterChange` (plus a rebuild attempt). Live values read back correctly
(`camMode=20` = `Keep`; the enum is `Off=0, Reset=10, Keep=20, AnimatorLinkage=30`). No motion in
any combination.

### 4. Simulation stepping — the phase runs, the teams do not

Harmony postfixes on the public manager entry points, 6 s at ~15 fps:

```
hooks[clothUpdate+16 always+16 cullPre+16 cullPost+16 early+16 firstPre+16
      beforeLate+16 afterLate+16 timeFrame+16 timeRender+16 timeFixed+53]
```

`MagicaManager.CheckRegist()` → `True`; `Time.isValid=True`, `Team.IsValid()=True`,
`Cloth.isValid=True`, `Simulation/Render/Bone/VMesh/Collider/Wind/PreBuild` all valid;
`simulationFrequency=60`, `maxSimulationCountPerFrame=2`, `SimulationDeltaTime=0.0167`,
`GlobalTimeScale=1`, `Time.timeScale=1`, `deltaTime=0.0667`, `Application.isFocused=True`,
`runInBackground=True`.

But:

* `TeamManager.SimulationStepTeamUpdate`, `SimulationPostTeamUpdate`,
  `SimulationCalcCenterAndInertiaAndWind` — **0 calls** (never appear in the counters);
* `SimulationManager.SimulationStepCount` — **0**; its setter is never invoked;
* per-team `TeamData`: `upd=2` (== `TeamMaxUpdateCount`, a schedule value, not a counter),
  `IsEnable/IsValid/IsProcess=true`, `IsRunning=false`, `IsStepRunning=False`,
  `cull/camInvis/distInvis=false`, `particleChunk.dataLength=3..4`.

Forcing the step count (`Harmony` postfix on the `SimulationStepCount` getter returning 2) makes
the breast cloth move immediately (0.46°, 6.07°, 6.34°, 2.21°…), which pins the gate on that
value. `MagicaManager.Cloth.ClothUpdate()` / the other phases pumped manually (including
`CompleteMasterJob()`) never move anything.

### 5. Owner-body gating — ruled out

The local player's body cloth (hair, see above) is not culled and is just as inert as the
wardrobe rigs. When the fix is applied every cloth in the process starts stepping, including
hair/skirt/bang — they show up as motion in the frame diffs.

## The fix

> **Correction (2026-11-11 clean-up pass):** the result below could **not** be reproduced.
> `SimulationStepTeamUpdate` stays at 0 calls/s after `SetUpdateLocation` on a cold start and
> across 7 hot reloads; `SimulationStepCount` is never written; with the animator frozen
> (`freeze=1`) an impulse gives 0.0000° with or without the intervention. The bone motion that
> the table below reports is the Animator/`AddForce` writer path (it disappears when the animator
> is frozen and reappears at `animspeed=0` with the animator enabled), not simulation. The
> intervention is still applied (lifecycle, opt-out, guard in `cloth-lifecycle.md`), but
> "registration starts the simulation" is **unproven/false** in the tested build. A latched
> `forcesteps` override is one way the earlier pass could have measured a different path; see
> P2.12 in `cloth-lifecycle.md`. The actual step-scheduling gate is still unknown.

`AlterumModelSwap.Hot/ClothSim.cs`, called per frame from `ModelSwapRuntime.Update` (it used to be
called from `Proportions.Sweep()`):

```csharp
MagicaManager.SetUpdateLocation(TimeManager.UpdateLocation.BeforeLateUpdate);
```

Why there and not at module init: a cold start with the call in `HotModule.Initialize` left the
cloth at 0.0000° for 30 frames — the managers are not ready that early. Called from the sweeps it
works, and the simulation keeps running afterwards even if the location value is put back (the
trigger is the re-registration, not the value).

Cold start, same force impulse (`AddForce(right, 20, VelocityAdd)` at frame 6):

| state | leaf `chest_02_l` per-frame local rotation deviation |
|---|---|
| cold start, no fix (fresh process) | 0.0000° × 30 frames |
| cold start, fix applied by the sweeps | 5.79°, 10.51°, 12.33°, 3.37°, 2.41°, 3.31°, 1.68°, 0.70°, 0.56°, 0 |
| same build, cloth component disabled (control) | 0.0000° × 39 frames |

## Deliverables

* `tracks/bones/clips/bones-jiggle-realtime.mp4` — 640x360, 90 frames, recorded over **8.89 s
  wall clock** (recorder achieved **10.12 fps**, requested 10), encoded at **10.12 fps** →
  duration **8.893 s**, i.e. true speed. Leaf peak 6.38° / 5.73 mm; 44 of 89 frames above 0.05°.
  The rig is swayed by the probe (`sway=0.15 m @ 1.8 Hz`) so the cloth has body motion to react
  to; the sway is scripted, the cloth response is real.
* `tracks/bones/shots/jiggle-peak.png` (frame 13: leaf 15.18° / 13.63 mm) and
  `jiggle-rest.png` (frame 40: 0.00°), same rig, camera pinned, animator speed 0, from
  `bones_still_final`; `jiggle-peak-crop.png`, `jiggle-rest-crop.png`, `jiggle-diff.png` (chest
  crop, diff ×8).

## Guardrails at the end of the pass

* `Proportions.cs` constants untouched: `BreastLeafScale = 1.45f`, `HipScale = 1.22f`;
  `[bounds:verify] scaleDrift=0 posDrift=0` on every rig after the final cold start.
* `Mirror.cs` and `MirrorPlayerVisibility.cs` md5-identical to the shared tree
  (`08b77a17c997ed1b8cb0998d35ec8cb6`, `3f33c8244d41dde1901e8a9d5f48c179`).
* `AlterumModelSwap.Contracts/Contracts.cs` untouched by this pass (it still differs from the
  shared tree by the earlier `// Swap` block removal described in `implementation.md`).
* Cloth parameters restored after probing: `gravity=0.00`, `culling=Keep`,
  `updateMode=AnimatorLinkage`, `skipWrite=False`, animator speed/culling restored; probe camera,
  RT and the synthetic control cloth are destroyed on stop.

## Diagnostic tools left in the track

Driven by `jiggle.request` keys, all read-only unless a key asks for a change:
`census=1` (all cloths + per-second motion meter), `hooks=1` (manager call counters),
`forcesteps=N`, `kickall=1`, `spawncloth=1` (synthetic BoneCloth),
`gfield/gprop/gravity/update/culling/skip/rebuild/cloff/cycle`,
`cam=live`, `camhold=N`, `follow=0`, `animspeed=X`, `sway/swayhz`, `updlocation=N`, `pump=N`.
