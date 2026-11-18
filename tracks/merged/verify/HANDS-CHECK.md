# Hands check — both blockers measured and fixed; deploy to A

Final build `8e379af586069453a24aad5016f87d71` (40,795,136 B) from `tracks/merged/mod`
(the hands-fix build before it was `89d6fdb5de1564ef866e4069023c79fb`, 40,787,456 B). Every build in this
session: `dotnet build -c Release -p:RefsDir=…/refs/`, **exit status read from `$?`**, never a script
that swallows `error CS`. Final full rebuild: exit 0, 0 errors, 15 warnings (CS0168 ×5, CS0649 ×4,
CS0414 ×3, CS0169 ×2, CS8073 ×1) — the manifest's `MSB3245` is gone because `RefsDir` resolves.

## Blocker 1 — `item renderers=0 → grip=n/a` (FIXED)

Cause, measured on the clone in the Lair (census gen 10):
`.../Inventory Items | Owner: 0/Axe_00_woodsman Weapon Interactable Handheld | Owner: 0/Handheld` is the
item's **logic** holder — 0 renderers, always. The visible model is parented by the game under the active
view's **hand bone**:

```
[hands:census] item state=ReadyToUse readyToUse=True holdingHands='IReadOnlyHands'
               major='.../Axe_00_woodsman Weapon Interactable Handheld | Owner: 0'
[hands:census] item modelTransform='First Person Concrete Model Parent/1P_ConcreteModel_Female(Clone)/root/pelvis/
               spine_01/spine_02/spine_03/clavicle_r/upperarm_r/lowerarm_r/hand_r/First Person Item Parent/Model'
               modelRenderers=2 ['Plane.001'(enabled=True) 'Plane.002'(enabled=True)]
```

The item is genuinely equipped (`State=ReadyToUse`, `IsReadyToUse=True`), so nothing had to be forced in
hand. The fix reads the model through the game's own property component —
`held.GetProperties<HandheldModelProperties>(false).ModelTransform` — and measures renderers and the grip
bounds on that. The holder's count is still printed beside it. Result:

```
[hands:census] item renderers=2 visible=2 source='model' holderRenderers=0 path='…/hand_r/First Person Item Parent/Model'
[hands:census] grip=1 arms=1 visible=1 contact=1 pov=FirstPerson mode=Forearm itemFrom='model'
              itemBounds=c=(-30.072,2.281,-25.670) ['Hands_08_Mesh'[vis=True hit=True d=0.090]]
```

## Blocker 2 — `third model=none` (FIXED)

Cause, measured (`[hands:census] scan3 …`): the scene-scan route required
`PathOf(...) contains "Owner: 0"` **and** `HasNativeBody(...)`. The map's player 3P body is one
`Body_Optimized` mesh (plus Boots/Head/Hair/…, layer 23) with **no `_arm_` segments**, so
`HasNativeBody` rejected it: `scan3 inScene=True owner0=True native=False active=True`. The lobby's
mannequin bodies *do* have arms, which is why the test looked right there.

Fix: resolve through the local player's own children first (`GetComponentsInChildren<ThirdViewPlayerModel>`,
active preferred) — authoritative in both scenes and independent of the hierarchy's names; the scan stays
as the fallback and `HasNativeBody` is a preference, not a filter. Measured after:

```
[hands:census] search playerChildren third=1 first=1
[hands:census] why=request pov=FirstPerson … third='GamePlayer_Separated | Owner: 0/GamePlayer_Moving Part/…
               /Third Person Concrete Model Parent/3P_ConcreteModel_Female_01(Clone)' …
[hands:census] third model renderers=11
```

The same census on **A's** live run (gen 113) resolves it too, so the fix holds in a different map instance.

## Captures (clone `C:\AlterumI`, Lair, item = `Axe_00_Woodsman`, 640×360)

`tracks/merged/shots/hands-check/`

| # | state | file | census witness |
|---|---|---|---|
| 1 | native model, 1P, item | `shot_main_cap-native-1p-b_…_06.png` | `custom=(classic=False,rigged=False)`, `grip=1` |
| 2 | rigged custom, 1P, item | `shot_main_cap-rigged-1p-b_…_08.png` | `rigged=True slug=sahur-roblox`, `grip=1`, lease took `Hands_08_Mesh` |
| 3 | rigid custom, 1P, item | `shot_main_cap-rigid-1p_…_10.png` | `classic=True slug=sahur-classic`, `grip=1` |
| 4 | rigid custom, 1P, **empty-handed** | `shot_main_cap-rigid-empty-1p_…_12.png` | `held='-'`, lease `released 1 renderer(s)`, `grip=n/a` |
| 5 | native, 3P | `c3p-native.png` | body layer 23 not in the showcase mask → body not drawn |
| 6 | rigid custom, 3P | `c3p-rigid.png` | body visible, item floats at the native hand bone |
| 7 | rigged custom, 3P | `c3p-rigged.png` | rig build had not completed at capture time |
| 8 | rigid custom, 3P, empty-handed | `shot_main_cap-rigid-empty-3p_…_13.png` | `held='-'` |

Captures 1–4 are the requested matrix and they read correctly: item + native hands visible while a
custom body is worn (the lease), and both gone after the game's own drop (`[hands:drop] ok=True
heldAfter=-` → `[hands:lease] released 1 renderer(s) (why=state-change)`).

**3P is the honest gap.** In a map the game's third-person lens is the first-person camera (the frame is
the inside of the face: `shot_main_cap-*3p-b`), `ThirdPersonCamera` is documented lobby-only, and the
clip-recorder showcase camera draws the custom body but not layer 23 where the map's native body lives.
So there is no picture of *native 3P hands around the item in a map*; the census proves the 3P half
(`third model renderers=11`, item model parented under `…/3P_ConcreteModel_Female_01(Clone)/root/…/hand_r/`),
and `grip=0 arms=0` in 3P because that map body has no separable arm renderers to grip with.

## Deploy to A (pid 29384, never restarted, `win/run-game.ps1` never run)

`C:\Program Files (x86)\Steam\steamapps\common\Alterum\BepInEx\hot\AlterumModelSwap.Hot.dll`

| step | result |
|---|---|
| backup | `AlterumModelSwap.Hot.dll.prev`, md5 `eadea60e55537ec705c895089f07eb13` (40,680,448 B) |
| first deploy (10:29:57) | `77-…`→`047c9716e9e99c0b0b9b19032d524dfa` (40,787,456 B), copy→`.tmp`→`mv -f`; `[hot] generation 109 active` |
| hands fix (11:03) | `89d6fdb5…`; `[hot] generation 112 active` |
| marker fix (11:07) | `8e379af5…` (the tree's current build, byte-identical to `bin/Release`); `[hot] generation 113 active` |

Gate after generation 113, window from the `generation 113 active` line: `[Error]` **0**, `SWAP` **0**,
`built 34 rows … resolved=34/34 failed=0`, wardrobe `16 of 26`, reel `cells=64 blank=22 item=21 damage=10
debuff=6 double=5`. Undisturbed: same scene before and after (`'Lair Of Occultists Map Scene'`, single
loaded scene, `mapRun=True`), same pose and position
`pos=(-30.23,0.58,-4.72) fwd=(0.70,0.00,-0.72)`, `owner=True connected=True`, and the identical inventory
in the identical slots (FA: `ss_exit`,`rune`; Const: `lantern`; Exit Spell: `ss_exit_A…F`; Altered:
`ritualdagger`; `components=4 items=10 held=False`).

Frame rate in the map, measured from the module's own frame counter over wall clock:

| window | frames | wall | fps |
|---|---|---|---|
| post-fix, 90-sample window | 8,485 → 13,110 | 348.4 s | **13.3** |
| after gen 112 | 685 → 1,063 | 25.8 s | **14.7** |
| after gen 113 | 1,945 → 2,322 | 30.7 s | **12.3** |

(before the scan-cache fix: 1.9–3.4 fps.)

## Placement restore — and the defect that blocked it

The machine was **not** persisted on A: `[prop] placed at player why=F4 … at=(-34.35,0.582,-2.48)`
never wrote a store, and `BepInEx/config/alterum-reel-placements.json` did not exist, so the first reload
disposed the prop (`[prop:disposed] parts=76`) with nothing to restore from. Two real defects:

1. **F4 placements are never saved.** `PlacePropAtPlayer` (the F4/`place=here` path) does not call
   `ReelPlacements.Save`; only `save=<name>` persists.
2. **The restore marker outlives the prop it stands for.** `alterum-reel-restored.txt` keeps
   `<sceneHandle>|<mapKey>`, and `Dispose` destroys the booth on every hot reload, so every later reload
   logged `restore skipped: … (marker)` and left the machine missing until the marker was deleted by hand.

Fixed (defect 2): the marker is now treated as stale when nothing is live —
`marker matched but no live booth (hot reload): restoring instead of skipping`. Verified on the clone
(gen 14) and on A (gen 113):

```
[reel:place] marker matched but no live booth (hot reload): restoring instead of skipping
[reel:place] restored default 'lair-1' into a new booth map='LairOfOccultists_GameMapData|Lair Of Occultists Map Scene'
             pos=(-34.35,0.582,-2.48) yaw=89.1 generation=-2175220|…
[prop:measure] worldBounds centre=(-34.33, 2.06, -2.48) floorY=0.582 baseY=0.582 topY=3.532
```

The store now holds `lair-1` (copied from the clone's seeded file, byte-identical anchor: the exact spot
the F4 placement logged). Defect 1 is **not** fixed — reported, not claimed.

## Still open

* No 3P picture of native hands around the item in a map (see above); the 3P census data is there.
* 3P `grip` cannot be measured on a map body: no `_arm_*` renderers exist (`arms=0`).
* F4 placements are not persisted, so a placement made by hand is only as durable as the session.
* The clone (pid 31940) reached 30.7 GB private after this many reloads; host free memory was 1.28 GB at
  the end. It was left running (instructed not to kill it).
