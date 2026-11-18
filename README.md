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

## Direct console automation

`win/console.py` executes a real QC command through the running instance's local filesystem mailbox,
without keyboard input, pausing, opening the console, or entering AEZAKMI:

```bash
python3 win/console.py --root /mnt/c/AlterumPinned 'help'
python3 win/console.py --root /mnt/c/AlterumB --instance b 'alterum.test.assert-model local tc2-sniper'
```

Use `--instance` only for a process launched with that `-alterumInstance` name. The helper atomically
publishes `exec <request-id> <command>` to that instance's `qc.request`, waits for the matching log
acknowledgement, and prints the synchronous output. Exit 0 means dispatched, 1 means a processor
error or failed synchronous assertion, and 2 means a transport/timeout error. Asynchronous game
actions still need their own subsequent observation. A timeout does not prove the command was not
executed; do not blindly retry mutations.

This is an intentionally privileged **local developer transport**: access to the install's files is
its trust boundary. Execution uses the actual QC parser/processor, with a scoped, thread-local
exception to the access gate. The exception ends even if the command throws. It does not persist an
unlock, play the unlock cue, or enable ordinary player console input. Normal `invoke` requests and
in-game console access remain gated. Do not expose the mailbox as an untrusted network endpoint.

Model join/rejoin assignment belongs to the original game. Custom rows extend its normal database;
occupied rows are not added back to availability. The mod no longer restores a saved wanted choice
or runs a second randomizer. Bodies and peer advertisements follow the game's applied row.

## Confirmed session automation (existing processes)

`win/session.py` is the generic normal-flow coordinator. Select the client to operate as `target`
and the live host of the intended lobby as `peer`; neither role assumes an A/B install or instance
name. For the current test pair:

```sh
python3 win/session.py rejoin \
  --target-root /mnt/c/AlterumB --target-instance b \
  --peer-root /mnt/c/AlterumPinned --peer-instance '' \
  --output "$HOME/assets/monorepo/alterum-data/validation/session-flow" --diagnostics
```

Operations: `status` (read-only current verdict), `leave` (stop at confirmed interactive Menu),
`join` (start from disconnected Menu), and `rejoin` (one complete leave/join cycle). `--target-pid`
and `--peer-pid` further disambiguate selectors. Omitted instance selectors allow discovery; an
explicit empty string selects the unnamed process. Host cycling, map scenes, pre-existing
confirmation dialogs, missing/ambiguous processes and busy UI automation are refused. This tool
never creates a lobby or starts, restarts, stops or moves a player. Native game model assignment
on joining is expected; the helper makes no model choice, config write or reservation override.

The merged Hot DLL must implement `SessionControl` on both selected processes. No permanent host
update is needed. The coordinator uses a separate privileged local `session.request` mailbox;
access to install files is its trust boundary, just like the direct console mailbox. It never
unlocks the player console. Requests carry a correlation ID, PID, full process start UTC, hot
MVID and generation nonce, expected lobby/client membership and a short expiry. Mutations are never automatically
retried. Full, exact scene-owned UI paths are used (not the truncated log path formatter). The
pause, exit, confirmation, code-panel, field and connect actions all use the game's native UI
paths, not a direct network join/leave API or scene loader.

Readiness is observed independently of dispatch: after leave, Menu must be loaded and interactive,
Steam membership/network listening/local player must be gone, and the host must have removed the
old client. After join, both processes must still belong to the intended lobby, the owned player
must exist, each peer must see the other's connected player, the roster and applied model row
must be observed, and membership/model observations must hold for at least three fresh samples
and one second. New client IDs are discovered, never assumed. Rendering/material health is NOT
part of connection readiness; it is collected separately with `--diagnostics` so a rendering bug
cannot disguise itself as a join timeout. Runtime probes are sequential, not an atomic peer snapshot.

Every run writes `report.json`, `summary.txt`, correlated runtime samples and OS identity evidence
under a unique output directory. OS PID/start/exe/instance is checked before and after, with runtime
identity and hot-generation guards throughout. Failure stops at the current stage and attempts a
bounded observation-only diagnostic bundle while locks are held. Preflight failures before
exclusive mailbox acquisition do not send diagnostic requests. Missing observations are not passes.
A timeout does not prove a mutation never ran: inspect the current state before any new operation.

Defaults: 150-second total flow deadline, 30 seconds per stage; optional/failure diagnostics have
a separate 30-second bound. Exit 0 = requested state ready, 1 = flow/readiness failure, 2 = invocation
or preflight failure, 3 = connection ready but diagnostic findings/incomplete observations. Existing
permanent hosts currently lack verified active-config-path observations, so `--diagnostics` can
legitimately return 3 after a successful cycle; inspect its findings rather than treating it as a
join failure. Without `--diagnostics`, success reports health as `not-requested`, not healthy.

The helper holds cooperative exclusive locks in both selected mailboxes. Other raw UI/QC writers
and human input do not honor them: do not operate those instances concurrently. One cycle only;
there is no blind retry/repeat mode or automatic recovery that could conceal a regression.

Tests:

```sh
python3 -m unittest discover -s tests/session-flow -v
dotnet run --project tests/session-flow/UiPathProbe.csproj -c Release
```

## Live lobby music and Reel regression probes

`win/music-reel-probe.py` runs against two existing peers. Use `status` to collect fresh scene,
transport, and booth evidence, `eof` for a short track that ends naturally, and `map` for a long
track playing on both peers as they enter the map via the native readiness pad. The `reel` case
requires a loaded map; it grants and equips an eligible stake, uses explicit test poses, and
checks host consumption and settlement against the client result. Its default action calls the
game's `Interact` method, not the physical hold-E input path; `--callback-only` tests only the
completion callback and downstream logic.

Run `python3 win/music-reel-probe.py --help` for the selectors, poses, and source options. The
probe holds the session mailbox locks, does not restart either game, and never retries a mutation.
Keep other mailbox writers and player input idle while it runs. Each run saves fresh, bounded log
windows alongside identity observations and a verdict in its own output directory. Use
`win/focus-logs.py` to group repeated errors without changing the original logs.

## Diagnostic bundle

For one bounded, observation-only collection of the current A/B processes:

```sh
python3 win/collect-diagnostics.py --main-root /mnt/c/AlterumPinned \
  --secondary-root /mnt/c/AlterumB \
  --output "$HOME/assets/monorepo/alterum-data/validation/diagnostics" --timeout 30
```

This stages its own minimal interactive Windows worker via `ssh windows` and emits absolute paths
to a versioned JSON report and findings-first summary, fresh runtime manifests, hashes, targeted
logs and current-window PNGs. It does not deploy, launch/stop, change models/configuration/POV,
operate UI, or persist an unlock. Missing/stale observations are not passes. Exit 0 = complete and
healthy; 1 = findings or incomplete; 2 = invocation failure. Full schema, limitations, selectors,
runtime deployment boundary, tested invocation and tests: `notes/diagnostic-bundle.md`.

## Iterating

```
./iterate.sh hot      # rebuild + copy the hot dll only: live in ~1s, game keeps running
./iterate.sh quiet    # full restart, off-screen (unattended screenshots)
./iterate.sh headed   # full restart, visible + private lobby (leave it up for the user)
```

Config edits need no rebuild at all: the host polls the cfg mtime and reloads it live.

## Mirror self-reflection

`[Mirror] ReflectLocalPlayer` (default `true`) makes the mirror show your own body. Set it to
`false` to opt out. It replaces the pre-fix `[Mirror] ShowLocalPlayer`, which defaulted to `false`
and was labelled "does not work". On upgrade the old key is removed and its value is **not**
carried over, because a generated `false` cannot be distinguished from a deliberate pre-fix
opt-out; set `ReflectLocalPlayer = false` after upgrading if you want it off.

## Standing rule

**When work on this game is finished, launch it headed (visible window) and let the automation
create a Private lobby, so the user can check the result himself.**

That means the config must end in this state:

```
[Mirror] AutoPlace = true, LobbyOnly = true, ReflectLocalPlayer = true
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
- The owner's third-person body is hidden by the game's first-view proxy; the mirror temporarily
  binds the native body renderers and adds the body layer to its camera mask, so it can show you.
  `[Mirror] ReflectLocalPlayer = false` opts out.

## Proof images

`proof/` — `mirror_small.jpg`, `mirror_montage.jpg`, `montage.jpg` (Diana → Freya before/after),
`private_panel.jpg` (lobby type Private), plus `BEFORE/` and `AFTER/` wardrobe renders.
