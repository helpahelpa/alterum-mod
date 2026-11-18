# bones track — cloth intervention lifecycle, opt-out, guard, transactions

Clean-up pass on top of `cloth-simulation.md`. Everything below is from the track build
(`module=c032bb..bedd52`, `C:\AlterumT1`), hot reload only, no A/B.

## P1.1 — registration now has a manager-scoped lifecycle

`ClothSim.cs` (rewritten) is driven per frame from `ModelSwapRuntime.Update`, not from six
sweeps. Rules, in order:

* **readiness** — `Application.isPlaying`, `MagicaManager.isPlaying`, time valid with
  `SimulationDeltaTime > 0`, team valid with `TeamCount > 0`, simulation/cloth managers non-null,
  and at least one **running** cloth. Nothing is consumed before this.
  Final cold start: the first registration happens in the Lobby (`teams=6 running=6`,
  `loopReg=0` before it), not in Bootstrap with `teams=0` as the old six-attempt version did.
* **already running** — if `SimulationStepCount > 0` is observed on two frames of a fresh
  generation, no registration is made ("no re-registration" log line).
* **one attempt per manager generation** — the generation key is the manager object identity
  (`Team/Time/Simulation/Cloth` pointers) plus a re-init epoch that fires when the cloth set
  empties and rebuilds with the same manager instances (scene load) or when `isPlaying` goes
  false→true.
* **confirmation** — 1.5 s after the attempt the observed step count is logged as CONFIRMED or
  NOT confirmed. A failed attempt is not repeated for that generation.
* **rollback** — `Rollback()` restores only the `UpdateLocation` enum and says so. The PlayerLoop
  registration has no managed removal API (`SetCustomGameLoop` / `AddPlayerLoop` / `CheckRegist`
  are private, there is no RemovePlayerLoop); restoring the enum does not stop a stepping
  simulation, measured earlier and re-stated in the log. Disposal restores the enum and leaves the
  registration in place; the next generation detects the running simulation and does not register
  again.

### What was actually measured (and what it means)

`clothmetrics.request` `count=1` installs postfixes on `TeamManager.SimulationStepTeamUpdate`,
`SimulationStepCount`'s setter, `AlwaysTeamUpdate` and `TimeManager.FrameUpdate`, and logs once a
second. Across a cold start + 7 hot reloads (`[hot] generation 2..8`) + one scene change:

| after | stepTeam | setSteps | always | frameUpdate | running | note |
|---|---|---|---|---|---|---|
| Bootstrap, before first cloth | 0 | 0 | 0 | 0 | 0 | `ready=0 loopReg=0`; the gate holds, nothing consumed |
| Menu/Lobby warm-up | 0 | 0 | 0 | 0 | 0 | `frameUpdate` hook not called at all until Lobby |
| Lobby (scene change done) | 0 | 0 | 15/s | 15/s | 9 | all manager phases run, step loop does not |
| each of 11 reloads (7 + 4) | 0 | 0 | 15/s | 15/s | 9 | one registration per module load, same result |
| explicit `recount=3` ×4 reloads | 0 | 0 | 15/s | 15/s | 9 | no step callbacks before/during/after |

PlayerLoop census (`loopdump`): **`magicaSystems=8` after every reload, and still 8 after each of
three explicit `SetUpdateLocation` calls** — repeated registration does not add PlayerLoop systems,
so the "double-step" hypothesis is not supported by system multiplicity. The step-callback
multiplicity cannot be positive here because the callbacks never fire at all.

A further fact from the final cold start: before our registration `CheckRegist` was **false** (the
game had not installed its loop at all); the registration installs it (`loopReg=1`,
`frameUpdate`/`always` start firing), yet `SimulationStepCount` remains 0.

**Contradiction with the premise.** In this environment the registration does not start the
simulation: `SimulationStepTeamUpdate` is invoked 0 times/s, `SimulationStepCount` is never
written (monitored immediately before/after `FrameUpdate`, `AfterFixedUpdate`,
`OnEarlyClothUpdate`, `OnFirstPreUpdate`, `ClothUpdate`, `AlwaysTeamUpdate`, `OnBeforeLateUpdate`,
`OnAfterLateUpdate`), and the cloth set's `TeamData.IsRunning` stays false. A `forcesteps=2`
getter override did **not** produce motion with the animator frozen either. The only bone motion
observed comes from (a) the Animator and (b) `MagicaCloth.AddForce()` writes through the
AnimatorLinkage writer; within `freeze=1` (animator disabled) an impulse produces **0.0000°**
with or without the intervention, and `animspeed=0` with the animator enabled reproduces the
9–15° writer response that the earlier pass attributed to simulation.

Consequence: the earlier "5.79/10.51/12.33/3.37°" cold-start table is **not reproducible as
cloth simulation**. It was most likely the Animator/AddForce writer path, exactly the
contaminated evidence that P2.12's latched `ForceSteps` describes. Enabling actual simulation is
now a separate root-cause problem (step scheduling above the registration), not a lifecycle bug.

## P1.2 — opt-out, guard, cost

* **Opt-out** (persistent `clothsim.request`, `enabled=0|1`, state logged). `enabled=0`:
  rollback of the enum, no registration, running cloths untouched; `enabled=1`: a new attempt.
  Verified both transitions in the log.
* **Guard** (`guard=1`, default on): once a second, for every running bone cloth, samples the
  cloth root's world position and the local positions of its `GetUsedTransform()` set (≤64
  cloths, one shared HashSet). Logs a warning when the root moves >3 m/s (teleport/culling
  transition) or a simulated bone moves >0.5 m/s (instability). Verified: `place=3` moved the
  wardrobe rig and produced `cloth root jumped 23.72m in 1s` lines; normal frames only log a
  once-a-minute summary (`cloths=8 transforms=62`).
* **Cost** (`clothmetrics.request cost=1`, 15 s windows, 8 running cloths, `kick=1`, 15 fps cap
  lifted for the window; `cloff=1` disables every running cloth component and restores it):

  | window | frames | wall mean | p50 | p95 | p99 | max | process CPU |
  |---|---|---|---|---|---|---|---|
  | cloths enabled (9 running) | 1431 | 8.4 ms | 7.8 | 12.2 | 17.1 | 25.3 | 5.36 cores |
  | cloths disabled (`cloff`, 9) | 1468 | 8.2 ms | 7.6 | 11.4 | 16.7 | 23.3 | 5.40 cores |
  | first pair, 8 running | 1709 / 1815 | 8.8 / 8.3 | — | 12.0 / 11.3 | 17.3 / 16.8 | — | 5.12 / 5.19 |

  Delta ≈ **0.2–0.5 ms/frame mean** and **no CPU delta**, which is the cost of the manager's
  per-frame bookkeeping path with 8 registered cloths — **not** of simulation, because no team
  ever steps. `ProfilerRecorder` is not generated into the game's interop assemblies (only
  `ProfilerUnsafeUtility` exists), so MagicaCloth2's `startClothUpdate*Profiler` markers are not
  reachable; process CPU (`TotalProcessorTime`, all threads) is the closest available figure.

## P1.13 — apply is now a transaction

`Proportions.Apply` registers the rig in `_rigs` **before the first write**, wraps the whole
apply in `try/catch`, and on failure restores every recorded node from `Touch()`'s original and
forgets the rig (`[bounds:apply-failed]`). Verified with `proportions.request` one-shot injections
on the live Lobby rigs:

```
[bounds:inject] armed fail='afterBreast'
[bounds:restore] rig='...Sarah(Clone)' bones=1 reason='apply failed' scaleNow->original: chest_02_l:1.45->1.00
[bounds:apply-failed] ... (InvalidOperationException: injected failure at 'afterBreast')
    rolled back 1 node(s); rig forgotten, next sweep retries from the true originals

[restore=1 + fail='midHips']
[bounds:restore] rig='...Sarah(Clone)' bones=9 reason='apply failed'
    scaleNow->original: chest_02_l:1.45->1.00 chest_02_r:1.45->1.00 pelvis:1.22->1.00
[bounds:apply-failed] ... rolled back 9 node(s)
```

After both retries `proportions.request dump=1` shows the recorded originals as **unit**
(`chest_02_l orig=(1.000,1.000,1.000)`, pelvis unit) and the applied values exactly 1.45/1.22,
i.e. no compounding. Teardown uses the same `Restore` path; the final generation's verifies are
`scaleDrift=0`, `scaledLeavesInCloth=2/2`.

## P2.12 — diagnostic cleanup

`JiggleProbe` now captures the animator's enabled state at arm time for `freeze=1` in every mode
and cleans up only if it actually disabled it; `GravityPropRest` / `GravityFieldRest` are
restored; the prior `updateLocation` and `simulationFrequency` are saved and restored;
`ClothHooks.Uninstall` resets `ForceSteps` (and logs the reset). `JiggleProbe.Cleanup` ends with a
field-by-field `[jiggle:cleanup]` line read back from the live objects.

Verified:

* normal probe with `gprop=0.5 gfield=0.5 gravity=0.5 updlocation=0 simfreq=30 culling=off skip=1
  forcesteps=2`: cleanup line `gPropNow=0.000 gFieldNow=0.000 gravityNow=0.000 updateModeNow=10
  skipWriteNow=False cullNow=20 updLocNow=1 simFreqNow=60 forceStepsNow=0 hooksInstalled=False`;
  `[hooks] uninstall reset forcesteps 2 -> 0`.
* abort during the upright warmup (`freeze=1 upright=99`, hot reload 2 s in): `animCaptured=1
  animWas=on animTouched=0 animNow=on`, so the animator the warmup never reached was not touched;
  `forceStepsNow=0`.

## P1.14 — decision: the intervention is default OFF, jiggle is our own solver

After the retraction above there is nothing left for the loop registration to buy: no team steps, so
the measured 0.2–0.5 ms/frame is pure bookkeeping. `ClothSim.DefaultEnabled` is now **false** and the
module logs one line per generation saying so (`[bounds:cloth-sim] DEFAULT OFF …`). Consequences:

* no registration attempt, no `SetUpdateLocation` churn, nothing consumed before readiness;
* `clothsim.request enabled=1` still re-enables the whole path (lifecycle, confirmation, guard) for a
  re-test on a future build, and removing the file now falls back to the **default (off)**, not `on`;
* the guard and the counters stay available and unchanged.

The jiggle feature itself no longer depends on cloth at all: it is a self-contained Verlet solver on
`chest_02_l/r` — design, parameters, the frozen-animator proof, bounds and the video are in
`jiggle-solver.md`. One thing from this note is still load-bearing there: the shipped Breast
BoneCloth keeps writing its build pose onto the leaves every frame after LateUpdate, so the solver
switches that write-back off with `SetSkipWriting(true)` for the rig's breast cloths (restored on
release) while the component keeps running.

## Deferred / known

* **P2.9** — pelvis child positions are an animation-phase snapshot: animated rigs still show
  `posDrift=3` (final Lobby verify, `posDelta=0.0045 m`) and the census backs off to 8 s / 60 s.
  Not attempted here.
* The swap-removal follow-ups.
* The actual step-scheduling root cause (why `SimulationStepCount` is never written after the
  registration): measured, not fixed. The intervention is kept and now honest/reversible at the
  level it can be; it cannot be shown to enable simulation.

## Request files (test-only, persistent)

`clothsim.request` (enabled/guard), `clothmetrics.request` (count/watch/recount/loopdump/cost), and
`proportions.request` (fail/dump/restore). None are required for the feature; absent files mean
the defaults (intervention on, guard on, no counters).
