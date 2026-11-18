# Alterum modding workspace

Game: **`C:\AlterumPinned`** — the **pinned** working install (Unity 6000.3.16f1, IL2CPP, URP).
Frozen 2026-11-14 on Steam buildid `26597574`; see `pinned/UPDATE-POLICY.md`.
Loader: BepInEx 6 bleeding edge (be.793) IL2CPP x64.

## Where things live (read this first)

This workspace is split across **two** trees, and only the source tree is in Git:

* repository root (`alterum-mod/`) — tracked: `.cs`/`.csproj`/`.props`, all scripts, docs, plans, notes, and small
  manifests under ~1 MB.
* `~/assets/monorepo/alterum-data/` (`$HOME/assets/monorepo/alterum-data/`, 7.6 GB) — **outside the
  repo**: `installs/`, `dist/`, `refs/`, `bep/`, `models/`, `lc-reference/`, `shots/`, `mp_shots/`,
  `validation/` raw logs, `.backup/`, `scratch/`, `work/` experiment records, plus every `bin/`, `obj/`,
  `.dll`, `.apack`, `.mset`, `.rset`, image, video, archive and log.

It sits outside the repo because 7.6 GB of binary payload has no place in a checkout — a clone, a
worktree copy or a snapshot would pay for it, and one `git add -f` would poison the history. The repo
root carries no `.gitignore` entry for it and there is no symlink: one absolute root, `$(AlterumDataDir)`.

Both trees use the same relative paths, so `tracks/merged/mod/AlterumModelSwap.Hot/*.cs` is tracked while
its `.png`/`.mset`/`.rset`/`cosmetics.apack` payload sits at the mirrored path in `alterum-data/`. The
projects resolve those through `$(AlterumDataDir)` (default `$(HOME)/assets/monorepo/alterum-data/`),
`$(RefsDir)` and `$(HotAssetsDir)`; override with
`-p:AlterumDataDir=$HOME/assets/monorepo/alterum-data/` for another checkout. Never commit a binary —
see `LAYOUT.md`.

**Work against the pin, not Steam.** `C:\AlterumPinned` is a real independent copy of the game as
shipped on buildid `26597574`, taken so that a Steam update cannot change the files underneath an
experiment. `C:\AlterumPinned-pristine` is the same game files with every loader/mod/emulator
artifact removed — the baseline a future update is diffed against. Both are on C:, both have hash
manifests (`pinned/INSTALL-MANIFEST.md`, `pinned/PRISTINE-MANIFEST.md`), and all scripts here
default to the pin with the Steam path kept as an override. Full procedure: `pinned/UPDATE-POLICY.md`.

Why it matters: the Steam install's files are **hardlinked** into `C:\AlterumB`, `C:\AlterumT1`…`T3`,
`C:\AlterumI`, `C:\AlterumSA` and `C:\AlterumE` (`Alterum.exe` has 8 links, `GameAssembly.dll` 4),
so those installs share the Steam install's bytes. The pin has link count 1 on every file.

Two assemblies, so code changes never need a restart:

| Assembly | Deployed to | Rebuild? |
|---|---|---|
| `AlterumModelSwap.dll` (host) | `BepInEx/plugins/` | only if the injected pump or config keys change |
| `AlterumModelSwap.Contracts.dll` | `BepInEx/plugins/` | changing it requires a restart (both sides must agree) |
| `AlterumModelSwap.Hot.dll` (all logic) | `BepInEx/hot/` | every iteration — reloaded live |

The host owns every `ClassInjector`-registered type, because il2cpp binds method pointers at
registration and there is no unregister. `PumpBehaviour` is the only injected type; it forwards
`Update`/`LateUpdate`/`OnGUI` into the hot module. The hot assembly is loaded from a shadow copy
in `BepInEx/cache/hot/` into a collectible `AssemblyLoadContext` whose `Load` returns null so
everything resolves through the default context (that is what keeps il2cpp type identity shared).

## Running several instances at once

Verified working: two processes from the same install, both with BepInEx loaded. Give each a
name so they stop fighting over disk state:

```
Alterum.exe -alterumInstance=featureB
```

A named instance gets its own `BepInEx/hot/<name>/`, `BepInEx/config/com.user0.alterum.modelswap.<name>.cfg`,
`BepInEx/screens/<name>/` and `BepInEx/logs/<name>.log`. An unnamed instance keeps the old shared
paths, so the two coexist. Hot reload is per instance: dropping a DLL into `hot/test/` reloads only
that instance (verified: B went to generation 2, A stayed on 1).

Without a name, the second instance truncates `BepInEx/LogOutput.log` and the two fight over the
config file (`[config] reload deferred: ... used by another process`).

## Multiplayer testing (two real players, one machine)

Solved with the gbe_fork Steam emulator at `~/test/gbe`. It replaces the game's own
`Alterum_Data/Plugins/x86_64/steam_api64.dll`, so **no Steam client is needed** (vanilla DLL kept
as `steam_api64.dll.vanilla`). Do not start Steam while testing: it rewrites `steamclient*.dll`
and its presence changes how the game initialises.

Per-instance identity lives in `Alterum_Data/Plugins/x86_64/steam_settings/configs.user.ini`:

```
[user::general]
account_name=playerA
account_steamid=76561198000000001
```

That folder sits beside the DLL, so a second identity needs a second install: `win/make-instance.ps1`
builds `C:\AlterumB` where every file is an NTFS hardlink to the real one (free) and only
`steam_settings`, `BepInEx/config`, `BepInEx/hot` are independent. It is `playerB / ...0002`.

`./mp-test.sh` (quiet) or `MODE=headed ./mp-test.sh` drives the whole thing: A creates a private
lobby, A's code is read from the log via the `@LobbyCode` directive, B opens the join panel, fills
the field and connects. Verified: A ends up `server=True`, B `server=False client=True`, both see
the same networked player objects, and B's log reports `OnLobbyEntered <A's id>`.

Known issues while doing this:
- `CurrentPlayerProvider` does not resolve on a joining client — the mirror falls back to finding
  `Owner: 0` by name there.
- The joining client gets dropped from the lobby after roughly 1-2 minutes (no crash, no error, it
  just exits). Restart it with `mp-test.sh`. Not yet diagnosed; likely the emulated relay.
- Keep Steam closed for the whole session. Starting it rewrites nothing (gbe is per-game) but the
  game behaves differently with a real client present.
- To revert to real Steam: copy `steam_api64.dll.vanilla` back over `steam_api64.dll` and delete
  `steam_settings/`.

`win/run-second.ps1` launches an extra instance without killing the first.

## Iterating

```
./iterate.sh hot      # rebuild + copy the hot dll only: live in ~1s, game keeps running
./iterate.sh quiet    # full restart, off-screen (unattended screenshots)
./iterate.sh headed   # full restart, visible + private lobby (leave it up for the user)
```

Config edits need no rebuild at all: the host polls the cfg mtime and reloads it live.

## Standing rule

**When work on this game is finished, launch it headed (visible window) and let the automation
create a Private lobby, so the user can check the result himself.**

That means the config must end in this state:

```
[Mirror] AutoPlace = true, LobbyOnly = true, ShowLocalPlayer = false
[Window] Quiet = false                      <- headed, not parked off-screen
[Automation] ClickSequence = Create Lobby Menu Button|@LobbyPrivate|Lobby Creator/Vertical Sectors/Buttons/Gothic
[Debug] Screenshots = false
```

then `win/run-game.ps1` (interactive session 1 launch).

`@LobbyPrivate` walks the lobby-type dropdown and picks the option labelled **Private**; if it
cannot find one it aborts and refuses to create a lobby. Never leave that step out.

## While iterating (unattended) use the quiet harness instead

- `win/run-quiet.ps1` — launches in session 1, parks the window off-screen (config `Quiet = true`),
  hands focus back to whatever the user was using. Screenshots land in `BepInEx/screens/`.
- `win/save-fg.ps1` / `win/restore-fg.ps1` — foreground record/restore helpers.
- `win/status.ps1`, `win/stop.ps1` — health check / kill.

Session 0 (SSH) cannot host a D3D window; all launches go through a Task Scheduler task with an
interactive token, never `Start-Process` from the SSH shell.

## Rebuild after a game update

Steam updating the game invalidates `BepInEx/interop`. BepInEx regenerates it on next launch, but
both assemblies must be recompiled because Il2CppInterop binds methods by token:

```
rm -rf refs && mkdir refs
cp "$GAME/BepInEx/core/"*.dll "$GAME/BepInEx/interop/"*.dll refs/
cd mod && dotnet build AlterumModelSwap -c Release && dotnet build AlterumModelSwap.Hot -c Release
```

## Known IL2CPP gotchas hit so far

- Generic engine methods (`Object.Instantiate<T>`, any overload) crash with `AccessViolation`.
- The game's UI is `UI.Elements.Button` / `UI.Elements.Tab`, not `UnityEngine.UI.Button`.
  Click via `Button.Click(PointerEventData)` then `OnClickedSafe()`; tabs via `Tab.Activate()`.
- `Resources.FindObjectsOfTypeAll(Il2CppType.Of<Button>())` is unreliable — scan `MonoBehaviour`.
- `SkinnedMeshRenderer` is a `Renderer`, not a `MonoBehaviour`.
- Several enabled cameras are tagged `MainCamera` (menu + lobby) — `Camera.main` is not safe here.
- The game re-applies its own fullscreen setting ~6 s into startup, overriding `-screen-fullscreen 0`.
- The owner's third-person body is never drawn (first-view proxy), so the mirror cannot show you.

## Proof images

`proof/` — `mirror_small.jpg`, `mirror_montage.jpg`, `montage.jpg` (Diana → Freya before/after),
`private_panel.jpg` (lobby type Private), plus `BEFORE/` and `AFTER/` wardrobe renders.
