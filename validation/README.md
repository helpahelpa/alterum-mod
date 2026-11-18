# Version matrix — mod vs two unseen game builds

Built, installed and smoke-tested in isolation against the two candidate builds. The pin
(`C:\AlterumPinned`), `C:\AlterumPinned-pristine`, the Steam install, `C:\AlterumCandidates\*` and
`tracks/merged/mod` were not written to; the two candidate directories were only read.

| | 26572865 (`stable_build_previous_version`) | 26642252 (`devbuild_unstable`) |
|---|---|---|
| test copy | `C:\AlterumTests\26572865` (431 files, 16,524,405,332 B) | `C:\AlterumTests\26642252` (434 files, 16,594,708,079 B) |
| `GameAssembly.dll` | `8cb6e087…` (125,901,824 B) | `500e51ac…` (129,968,640 B) |
| `global-metadata.dat` | `130a6e13…` (26,434,464 B) | `ef7198c7…` (26,709,484 B) |
| interop generation | **ok** — 226 asm / 154,929,051 B, Cpp2IL 46.6 s | **ok** — 226 asm / 157,009,651 B, Cpp2IL 29.3 s |
| references | `C:\Interop\26572865` (255 files) | `C:\Interop\26642252` (255 files) |
| build | **0 errors**, 32 warnings | **0 errors**, 32 warnings |
| generations | 1 → 2 | 1 → 2 |
| wardrobe | 26 rows / 16 custom, adopted on reload | 26 rows / 16 custom, adopted on reload |
| model swap (rigid + rigged) | pass | pass (after the game reverted the first selection) |
| hat on custom model | pass (`sahur-classic`, `sahur-roblox` anchors) | pass (same anchors) |
| reel spawn + native INTERACT prompt | pass | pass |
| reel interaction | **fail** (NRE in mod path) | **fail** (same NRE) |
| emotes 50 / ported / native / resume | pass | pass |
| console open, 109 commands | pass (binding action; physical Tilde blocked) | pass (same) |
| lobby → map | pass | pass (solo map admitted by this build) |
| map → lobby | blocked (no unattended path) | blocked |
| 120 s settled, no mod exceptions | pass (0) | pass (0) |
| **verdict** | **compatible** | **compatible** (2 build-specific behavioural differences) |

Loader for both: BepInEx **6.0.0-be.793** / Il2CppInterop **1.5.3** / Cpp2IL
**2022.1.0-development.1452**; Unity **6000.3.16f1** on both.

## Snapshot

Both builds were compiled from separate copies taken at 01:05 JST, sha256
`b349ce4b3384cfb0d8df5f4a62326dc3ef0c0cc98a6b1d66e417273d8ae03289` (all `.cs`/`.csproj`/`.props`).
`tracks/merged/mod` is owned by another agent and **moved while this ran** (`GameConsole.cs` 01:46,
`ReelHud.cs`/`ReelHudWorld.cs` 01:47, rebuild 01:53). Every result below applies to the 01:05
snapshot, not to that newer tree.

Both test instances were stopped afterwards (`Stop-Process` on the exact exe paths only); the user's own instance (pid 96760, `C:\AlterumPinned`) was never touched. Test copies (~16.5 GB each) and `C:\Interop\<buildid>` are left in place for re-runs.

Evidence paths: `alterum/validation/<buildid>/` — `IDENTITY.md`, `INTEROP.md`, `BUILD.md`,
`CONFIG.md`, `SMOKE.md`, `VERDICT.md`, `logs/` (raw runtime log, interop excerpt, compiler output,
observation window, applied cfg), `captures/` (in-game PNGs).

## What this says about tolerance to a genuine game update

* The interop step is fully automatic and clean: both unseen builds generated from scratch on a
  clean process, and the mod's source compiled **unchanged** against both — no `error CS`, so no
  API removal/rename touched any symbol the mod uses.
* The brittle part is not compilation but the *game's own behaviour around the mod's hooks*:
  * the wardrobe selection re-assert now happens later/independently on 26642252, so a selection made
    in the first seconds after the lobby appears is overwritten (the mod does not re-apply it);
  * the reel's `InitializeInteraction`/`OnInteractionPerformed` path throws a managed NRE on **both**
    builds — that is a latent mod defect that a version bump did not cause but also did not surface
    before, and it means the reel is only communicative (spawns, prompts, highlights), not operable;
  * map min-player counts moved (`min=4` → `min=1`), which silently changes the lobby automation.
* Both builds tolerate the *mod* (no crash, no missing types, hot reload intact) and the mod tolerates
  both builds' metadata. A real Steam update that ships *only* metadata/IL2CPP drift should therefore
  cost an interop regeneration and a rebuild, not code changes — provided the selection-timing race
  and the reel interaction NRE are fixed, since those are what would present as "the update broke it".
