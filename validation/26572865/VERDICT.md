# Verdict — buildid 26572865 (`stable_build_previous_version`)

Mod source snapshot: sha256 `b349ce4b…` (taken 01:05). `tracks/merged/mod` was edited by its owner at 01:46‑01:53, after this snapshot.

**compatible**

Reasoning, given the task's rule that "generation 1 loaded" alone is not compatible:

* Every game-build-sensitive path works: interop generation from the new metadata, a clean compile,
  host + hot assembly on generation 1 and 2, the wardrobe database (26 rows / 16 custom, adopted by
  name on reload), both custom bodies (rigid and rigged), per-model hat anchors on both, the 50-entry
  emote catalogue with a ported and a native clip plus a clean hand-back to the game's animation, the
  game's own console (109 commands), and the reel booths spawned from the saved placement store
  inside a real map run with the game's own interactable/prompt objects built.
* Two checklist items are not satisfied, and neither separates this build from any other:
  * **reel interaction** throws `NullReferenceException` in the mod's own `InitializeInteraction` /
    `OnInteractionPerformed` — identical on the dev build, i.e. a mod defect, not a version signal.
  * **map → lobby** has no unattended path (pause menu only offers *Exit to Menu*).
* One checklist item is proven only through the mod's binding action, not a physical keystroke
  (foreground lock); the console itself is live with 109 commands.

So: the build loads *and* the features work; the residual failures are mod-side or harness-side.

## Not proven / caveats

* No physical Tilde keypress reached the game (see SMOKE.md).
* Emote catalogue is 50 in the lobby and 48 in a map (`12 LC clips + 36 game clips`, line 8154) —
  the map rig exposes two fewer game clips.
* Recurring warning in a map: `'3P_ConcreteModel_Corpse_Skeleton(Clone)' is missing 22 game bone(s)`
  (620 times in a 125 s window) whenever the emote binder touches that corpse rig. A warning, not an
  exception; it stops when the binder lets go.
