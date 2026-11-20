# Results-screen custom body — reproduction, fix, evidence

The user reported: at the Results screen, a player wearing a custom model sees the default model.

## Mechanism (from the game, confirmed live)

`Gameplay.Results.ModelsCreatorResultsPostloadable.InstantiatePlayerModel` instantiates a fresh
`ThirdViewPlayerModel` prefab clone per `ResultPlayer` from
`PlayerModelDatabase._models[resultPlayer.ModelIndex]`. `ModelIndex` comes from
`PlayersAppearanceManager.GetBaseModelIndex(clientId, fallback)` — the stored base row, which the
wire guard keeps a native donor — so the outcome screen always shows donor bodies and every mod
body from the map is gone there. Results loads **additively** (`NetworkSceneManager.LoadScene`,
`OnLoadEventCompleted: scene=Results, mode=Additive`): the active scene stays the map, so any
scene-name gate must check the model's own scene, not the active one.

## Reproduction

A (host, client 0) wore tc2-demoman through Lobby → Lair → forced result
(`gamecycle.start-resulting true`, the verified alias of `LocationsCommandsWrapper.StartResulting`).
`SpawnPlayerModelOnServer dictionaryIndex=8` lines and the window capture show A's Results model as
the donor `Male_4_James`.
Evidence: `/home/user0/session-evidence/20261120T034735Z-results-model-donor/`.

## Fix

`tracks/merged/mod/AlterumModelSwap.Hot/ResultsBodies.cs`:

- postfix on `InstantiatePlayerModel(ResultPlayer, PlayerModelData, Transform)` records
  (model, `FirstOwnerId`) pairs — no Unity work in the game's call stack;
- per-frame tick (wired into `ModelSwapRuntime.Update` after `RemoteModels.Tick`, late pose in
  `ModelSwapRuntime.LateUpdate`) resolves the owner's slug — peers via `RemoteModels.RemoteSlug`,
  local via the new `RemoteModels.LocalPublishedSlug` accessor (which survives scene changes,
  unlike `SahurWardrobe.Snapshot`, whose local sources degrade in Results);
- attaches with the existing owner-agnostic machinery: rigid `SahurModel.BuildPreviewBody`,
  rigged `SahurRig.AttachExternal` + `PoseExternal`; native renderers leased hidden like
  `RemoteModels` does; body layer = host model's own layer (Results camera policy belongs to the
  scene);
- pointer-keyed teardown on model destroyed / scene left / slug empty or changed / dispose;
  installed from `HotModule.Initialize` with a per-generation Harmony id, disposed next to
  `MapBody`.
- Diagnostics-only processes never install it (`HotModule.Initialize` returns early), so B stays
  stock-visual — by design.

Body only; cosmetics (hats/chest/hips) stay map-scene features.

## Verification

Deployed hot sha `ba44cf0eabe5b878051b3b145fea27385db5881f716c98c7e22e1b443dab320e`
(built with `-p:ContractsPath` against the running hosts' contracts; A generation 4, B generation 5,
hot reload, no restarts). Same flow re-run:

```
[results:body] attached 'tc2-demoman' (rigged skeleton) to
'.../Explorers Win Positions/Altered/Altered Position #1/3P_ConcreteModel_Male_04_James(Clone)'
owner=0 layer=2 hiddenNative=0
```

A's log carries exactly three `[results:body]` lines (install + lease + attach), no errors; B sees
the donor for the same player (diagnostics-only), which is the intended divergence. The visual on
A's 640×360 capture is fog-obscured (A drew Altered and the camera centers the winning explorer),
so identification rests on the attach line plus the A-vs-B contrast.
Evidence: `/home/user0/session-evidence/20261120T041204Z-results-body-live-verify/`,
bundle `/home/user0/assets/monorepo/alterum-data/validation/diagnostics/20261120T043138Z-1367686d7179427ea7fe98f97d4b9b7b/`.

## Limits

- A hot reload while Results is already open cannot adopt the existing models (no postfix event
  until the next `Postload`); they keep donor bodies until the next result run.
- Cosmetic attachments are not re-applied on the Results screen.
- `hiddenNative=0` in the attach line is expected on the rigged path: `SahurRig.AttachExternal`
  hides and restores the host renderers itself.

## RE feedback

`alterum-re` commit `82ba9d1c313a8b56040b560ccaf863ac540cd844`:
`docs/UI.md` §6.1 + §6 wrapper table (verified `gamecycle.start-resulting explorersWin`; the old
`AlteredWon`/`ExplorersWon` placeholders never existed at runtime), `docs/GAMEPLAY.md` §4
(verified alias; additive Results load; per-peer model build from stored base rows),
`docs/OPEN-QUESTIONS.md` §23 (the `Player+0xE8` lookup-key twin in `GameResultsPlayersGetter`,
with the `isDead`/`role` decompiler losses), and one `docs/DISCREPANCIES.md` row.
`python3 alterum-re/tools/verify.py` passes; `git -C alterum-re status --short` empty.

Doc drift noted outside RE: `tracks/merged/shots/reel-lair/README.md` step 3 relies on the removed
synthetic readiness pulse — `req=1` alone no longer starts a one-player map; the readiness pad
(`standat=`) is required.
