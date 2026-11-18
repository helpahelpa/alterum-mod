# Retired reference tools — do not use their numbers

Two tools were retired on 2026-11-12 and renamed `*_RETIRED.py`. They are kept only as the record
of the earlier pipeline. **Neither is ground truth, and neither may be used for a port-vs-LC
comparison.**

| retired file | why |
|---|---|
| `compare/scripts/ref_eval_RETIRED.py` | **Half scale.** The retail hierarchy is `Player/ScavengerModel` (localScale 2.0) -> `metarig` (localScale 0.5608), so LC's world scale is 1.1216. `fk()` multiplies the metarig-local positions by 0.5608 only and never applies the 2.0, so every position it writes is exactly 0.500x LC's world. Its arm rest pose is also wrong (the compiled bundle's metarig rest is not the retail rest), and it has no leg knee hint and no look-head. Its outputs `compare/ref/<clip>.csv`, `compare/ref/ref_meta.json` and everything derived from them (`compare/errors*.json`, `compare/ERRORS.md`) are half-scale and arm-wrong. |
| `tools/source_metrics_RETIRED.py` | Source-model frame only (no 1.1216 factor) and a pose model with no knee hint / look-head / second arm constraint, so its "source" arms and legs are not the poses LC shows. Internally consistent with the baked pack, so its *ratios* can be read, but not as LC reference numbers. |

Ground truth is now only:

* `../../lc-reference/frames/<Clip>/seq_*.png` — captured real LC frames (75 per clip);
* `compare_vs_lc/` — matched-normalised-phase RenderTexture captures of the port, the composed
  `sheets/*_vs_LC.png`, `lc_vs_port.mp4`, and the measurements in `EVIDENCE.txt` / `REPORT.md`.

`tools/ik_audit.py` and `tools/arm_rest_diagnosis.py` still import the retired module for its
quaternion/pack helpers; those diagnostics are labelled "not ground truth" in their headers.

## Retired component (2026-11-12): stage-1 extra-Animancer-layer native diagnostic

`EmoteRuntime`'s `F8` path used to play one of four hand-picked game clips on a layer added above
the game's five (`EmoteDiag`), while the game's own animancer kept running. That bypassed the
ownership lease, so the pose had no fade, no restore check and a second writer on the skeleton.
It is removed. `F8` now toggles the selected game clip through `PosePlayer.StartNative` — the same
lease (`DoingSomethingHandler` + `DisableAnimancerAndAnimator`), the same `PostAnimationUpdate`
writer and the same `RestorePoseNow` hand-back as the ported LC clips. The `native=1` request key
now means "start the selected native emote", not the old layer diagnostic. Evidence for the new
path is in `notes/native-emotes.md` and `verify/native_emotes_*.log`.
