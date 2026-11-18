# Feature tracks — parallel, isolated development

Three agents can work at the same time, each on its own feature, without touching each
other's source or each other's running game.

**The one rule: a track agent only ever writes inside its own `tracks/<track>/` directory
and inside its own `C:\AlterumT<n>` install.** A and B (`C:\AlterumB`) belong to the user
and to the mainline work; they are never built, deployed to, or stopped from here.

| track | source copy | install | instance name | gbe identity | hot dir | log |
|---|---|---|---|---|---|---|
| `bones` | `tracks/bones/mod/` | `C:\AlterumT1` | `t1` | `playerT1` / 76561198000000011 | `C:\AlterumT1\BepInEx\hot\t1\` | `C:\AlterumT1\BepInEx\LogOutput.log` |
| `emotes` | `tracks/emotes/mod/` | `C:\AlterumT2` | `t2` | `playerT2` / 76561198000000012 | `C:\AlterumT2\BepInEx\hot\t2\` | `C:\AlterumT2\BepInEx\LogOutput.log` |
| `cosmetics` | `tracks/cosmetics/mod/` | `C:\AlterumT3` | `t3` | `playerT3` / 76561198000000013 | `C:\AlterumT3\BepInEx\hot\t3\` | `C:\AlterumT3\BepInEx\LogOutput.log` |

Untouched reference setups: A = `C:\Program Files (x86)\Steam\steamapps\common\Alterum`
(unnamed instance, `playerA`), B = `C:\AlterumB` (instance `b`, `playerB`).

## Commands

Run from the repository root:

```bash
./track.sh bones build      # build that track's hot assembly only
./track.sh bones deploy     # copy the dll into C:\AlterumT1\BepInEx\hot\t1\ (atomic replace)
./track.sh bones run        # launch C:\AlterumT1 windowed + tiled (interactive session)
./track.sh bones log        # tail LogOutput.log (last 60 lines)
./track.sh bones log 200    # last 200 lines
./track.sh bones log follow # live tail (Ctrl-C to stop)
./track.sh bones shot       # desktop screenshot -> tracks/bones/shots/<timestamp>.png
./track.sh bones stop       # stop ONLY C:\AlterumT1\Alterum.exe
```

Extras: `status` (running? pid, hot dll age, last `[hot]` lines), `cfg` (print/create the
per-instance cfg), `build-all` (Contracts + host + hot, when you change shared code).

Typical loop:

```bash
./track.sh bones build && ./track.sh bones deploy && ./track.sh bones log 30
```

`deploy` while the game is running is the normal case: the host reloads the dll within
about a second and logs `[hot] generation N active`. No restart needed.

Environment: `LINES=<n>` changes the default tail length, `TRACK_DESKTOP=1920x1080`
overrides the tile geometry if the desktop-size probe fails.

## What is isolated, what is shared

Per install (private): `BepInEx/config` (own cfg file `com.user0.alterum.modelswap.<inst>.cfg`,
auto-created from the base cfg on first run), `BepInEx/plugins` (real copies — rebuilding
the host here cannot write through to A), `BepInEx/hot/<inst>`, `BepInEx/logs/<inst>.log`,
`BepInEx/screens/<inst>`, `LogOutput.log`, and the gbe `steam_settings` identity.

Shared read-only via NTFS hardlinks (no disk cost): the game data tree, `BepInEx/core`,
`patchers`, `unity-libs`, `interop`. `BepInEx/cache` is deliberately absent and regenerated.

Each install also carries `C:\AlterumT<n>\TRACK-ISOLATION.txt` with `track=`/`instance=`;
`track.sh` refuses to deploy to, run, or stop an install whose marker does not match the
track you named.

## Rules

1. Only edit `tracks/<your-track>/mod/`. Never `mod/` at the workspace root, never another
   track's directory.
2. Only build/deploy/run/stop your own install. Never `Get-Process -Name Alterum | Stop-Process`
   — that kills A, B and the other tracks. `./track.sh <track> stop` matches the install's
   own exe path, nothing else.
3. One instance per install. `run` refuses if that install is already up; `stop` first.
4. Do not change `AlterumModelSwap.Contracts` semantics unilaterally: the host and the
   Contracts dll in your install's `plugins/` are shared code. If you do change it, copy your
   rebuilt `AlterumModelSwap*.dll` into **your** `plugins/` and restart that install.
5. Screenshots are opt-in per install: `[Debug] Screenshots = true` in your cfg, or grab a
   full-desktop shot with `./track.sh <track> shot`.

## How the isolation was built

* Source: `mod/` was copied with `tar` into each `tracks/<track>/mod/` (real files, no
  symlinks, without `bin/`, `obj/`, `.nuget/`). The copies needed one path fix, because the
  shared refs folder now lives in the untracked payload tree next to the source tree:
  * `Alterum.References.props`: `RefsDir` was `$(MSBuildThisFileDirectory)../refs/` and now defaults
    to `$(AlterumDataDir)refs/`, where `$(AlterumDataDir)` defaults to `$(HOME)/assets/monorepo/alterum-data/` (override with `-p:AlterumDataDir=.../`)
    (overridable, plus an `AlterumRefsCheck` target that fails loudly if `$(RefsDir)BepInEx.Core.dll`
    is missing). See `../LAYOUT.md` for the tracked/payload split.
  * `AlterumModelSwap.Hot.csproj`: the `Il2CppSystem.Core` hint path now uses `$(RefsDir)`
    instead of a second hardcoded `../../refs/`.
  The original `mod/` tree is unchanged.
* Installs: created by `win/make-track-instance.ps1` (hardlink copy like `win/make-instance.ps1`,
  but every mutable BepInEx path and the steam identity are recreated as real files). To redo one:

  ```bash
  cp win/*.ps1 /mnt/c/Windows/Temp/alterum/
  ssh windows powershell -NoProfile -ExecutionPolicy Bypass \
    -File 'C:\Windows\Temp\alterum\make-track-instance.ps1' \
    -Dst C:\\AlterumT1 -Instance t1 -Account playerT1 -SteamId 76561198000000011 -Track bones
  ```

* Launching: see `win/track-instance.ps1`. GUI processes must start from the interactive
  session (session 0 over SSH has no D3D desktop), so it registers a Task Scheduler task with
  an interactive token and waits for the process. `win/desktop-size.ps1` (via
  `win/run-helper.ps1`) reads the real desktop geometry so the 2×2 tiling is computed from the
  actual work area (slots: bones top-left, emotes top-right, cosmetics bottom-left).

## Notes

* `LogOutput.log` is truncated on every launch; `BepInEx/logs/<inst>.log` keeps the same
  lines for that instance.
* Windows can be locked. The game still runs and logs normally, but `shot` captures the lock
  screen — check the log rather than the picture when the desktop is locked.
* `tracks/<track>/` may already hold that track's own recon and scratch material (`notes/`,
  `notes.md`, `ref/`, `decomp/`, `dl/`, `unpack/`, helper scripts, ...). It belongs to that
  track. `track.sh` itself only touches `tracks/<track>/mod/`, `tracks/<track>/shots/` and the
  track's install.
* A/B keep using the mainline `mod/` tree and `iterate.sh` / `two.sh` / `mp-test.sh`. Those
  scripts still kill every `Alterum.exe`; do not run them while tracks are up.
