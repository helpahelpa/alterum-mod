# Launch policy — game instances on this machine

Every game process on the desktop exists to produce evidence (logs, mod captures, reference
frames), not to be played. So every instance runs **low-res and frame-capped**, and is **closed as
soon as its task is done**. Several instances of Alterum plus Lethal Company can be up at once and
they all compete for the same GPU.

## The rule

1. **Resolution: 640x360, windowed.** Never larger, unless a capture genuinely needs the pixels
   (see "Raising it temporarily").
2. **FPS cap: 15.** Enforced inside the game, re-asserted periodically, because the game re-applies
   its own display settings a few seconds after startup and on scene loads.
3. **Close what nothing needs.** An instance whose track/capture is finished is stopped, not left
   parked. A capture harness holding a LAN session open counts as "nothing needs it".
4. **Stop by exe path, never by process name.** `Alterum.exe` runs from five installs; matching the
   name kills the wrong one. `C:\AlterumB`, `C:\AlterumT1`… are hardlinked copies of the main
   install, so a name match is indistinguishable from the instance you meant to keep.
5. **Target the pinned install.** All mod work runs against **`C:\AlterumPinned`**, not the Steam
   install (see below).

## Which install is "the game"

**`C:\AlterumPinned`**, the pinned copy — not the Steam install. Pinned 2026-11-14 on Steam
buildid `26597574` so a Steam update cannot move the files under a running experiment. Every launch
script defaults to it; the Steam path is kept only as an explicit override (`-GameDir`, `-Install`,
`-Match`, `-Src`, or `ALTERUM_GAME`) for update testing. Read `pinned/UPDATE-POLICY.md` before
pointing anything at the Steam install.

The pristine archive `C:\AlterumPinned-pristine` is the game files with no loader/mod/emulator
artifacts: a comparison baseline for update testing, never a launch target.

## Where each part lives

### Alterum

| Thing | Where |
|---|---|
| FPS cap | `FrameCap = 15` in `AlterumModelSwap.Hot/ModelSwapRuntime.cs` (`EnforceFrameCap`: `vSyncCount = 0`, `targetFrameRate = FrameCap`, re-asserted every 2 s, logs `[fps] cap applied 15 (vSync off)`) |
| Window size (authoritative) | the launch arguments `-screen-fullscreen 0 -screen-width 640 -screen-height 360` |
| Window size (config) | `[Window] QuietWidth = 640`, `QuietHeight = 360`, `ForceWindowed = true` in `<install>/BepInEx/config/com.user0.alterum.modelswap*.cfg` |
| Stop one instance | `./track.sh <bones\|emotes\|cosmetics> stop` (tracks T1/T2/T3) or `win/track-instance.ps1 -Action stop -Install C:\AlterumB` |
| Default install for every launch script | `C:\AlterumPinned` (`run-game.ps1`, `run-quiet.ps1`, `run-second.ps1`, `run-a-nokill.ps1`: `-GameDir`; `track-instance.ps1`: `-Install`; `iterate.sh`/`two.sh`/`mp-test.sh`/`clip.sh`: `ALTERUM_GAME`) |

The cfg keys are **hot-reloaded** (`[config] reloaded from disk`) but `QuietWidth/QuietHeight` are
only pushed to the display on a fullscreen → windowed transition (`Screen.SetResolution`). The live
render size comes from the launch arguments, so **cfg and launch args must agree**; changing only
the cfg affects the next launch. That is why the launch scripts below carry the numbers too.

Every launch script defaults to 640x360: `win/run-game.ps1`, `win/run-second.ps1`,
`win/run-quiet.ps1`, `win/track-instance.ps1` (`-ScreenWidth/-ScreenHeight`), `two.sh`, `mp-test.sh`,
`track.sh`. They are executed from `C:\Windows\Temp\alterum\`, so after editing `win/*.ps1` copy
them there (`track.sh` does this via `sync_win`, otherwise `cp -f win/*.ps1 /mnt/c/Windows/Temp/alterum/`).

Force every install's cfg back to policy (idempotent, preserves BOM/CRLF, hot-reloads):

```
python3 ~/assets/monorepo/alterum-data/.temp/set-window.py 640 360
```

Measure (or resize) the window of a running instance from session 0 — DPI-aware, matches on exe
path, writes `C:\Windows\Temp\alterum\window-rect.json`:

```
ssh windows powershell -NoProfile -ExecutionPolicy Bypass \
  -File 'C:\Windows\Temp\alterum\run-helper.ps1' -Script 'C:\Windows\Temp\alterum\window-rect.ps1'
```

A health check must look at the *client* area (`client.w x client.h`), not the window rect — the
frame border adds ~18x47 px.

### Lethal Company

| Thing | Where |
|---|---|
| FPS cap | `FrameCap = 15` in `lc-reference/plugin/Plugin.cs` (`EnforceFrameCap` called from `Awake` and re-asserted from `Tick` every 2 s, logs `[fps] cap applied 15 (vSync off)`) |
| Capture camera | `TpW = 640, TpH = 360`, and the `shot` command defaults to 640x360 |
| Window size | `lc-reference/lc-restart.sh`: `-screen-fullscreen 0 -screen-width 640 -screen-height 360` (`lc-session.sh` and `capture-all.sh` go through it) |
| Deploy | `lc-reference/lc-restart.sh` builds + deploys + relaunches (`plugin/deploy.ps1` kills the game, so it must be relaunched from the helper) |
| Quit path | `./lc.sh quit` → `Application.Quit()`; no process kill needed |

Plugin source is mirrored in two places and they must stay identical:
`lc-reference/plugin/Plugin.cs` (repo) and `C:\Windows\Temp\alterum\lc-capture\mod\LcRef\Plugin.cs`
(the copy `lc-restart.sh` builds). `cp` repo → temp before building.

## Closing instances

```powershell
# one install only, matched on the full exe path
Get-Process -Name Alterum | Where-Object { $_.Path -eq 'C:\AlterumT1\Alterum.exe' } | Stop-Process -Force
```

`UnityCrashHandler64.exe` is a child of its install's game process and exits with it — never stop it
by name, it is shared by every Unity game on the box.

Lethal Company: `./lc.sh quit` first; the plugin's `Application.Quit()` is the only clean path (it
flushes the log). Only use `Stop-Process` if the plugin stops answering (`BepInEx/lcref/hb.txt`
stops being touched).

## Raising it temporarily

The cap is a `const`, so raising it is a source edit + rebuild, not a config write:

* **Alterum, one track install** — change `FrameCap` in `tracks/<track>/mod/AlterumModelSwap.Hot/ModelSwapRuntime.cs`,
  then `./track.sh <track> build && ./track.sh <track> deploy`; the host reloads the hot dll within
  ~1 s (`[hot] generation N active`) and the new cap applies from the next re-assert. Put it back to
  15 and redeploy when the capture is done.
* **Alterum, main install (the pin)** — same edit in the merged tree, then copy the built
  `AlterumModelSwap.Hot.dll` into that install's `BepInEx/hot/`; hot reload, no restart.
* **Lethal Company** — change `FrameCap` in `lc-reference/plugin/Plugin.cs`, then `./lc-restart.sh`.

Resolution is a launch argument, so a capture that needs more pixels is a one-off override, not an
edit: `./lc-restart.sh "-screen-fullscreen 0 -screen-width 1280 -screen-height 720"`,
`win/run-game.ps1 -ExtraArgs "-screen-fullscreen 0 -screen-width 1280 -screen-height 720"`, or
`TRACK_W=1280 TRACK_H=720 ./track.sh <track> run`. Revert (and update the cfg) right after — the
640x360 cfg and a bigger window disagree on purpose only for that run.

## Known deviation

`QuietWidth/QuietHeight` in a cfg cannot shrink a window that is *already* running windowed; the
mod only calls `Screen.SetResolution` when it has to leave fullscreen. A live instance that predates
this policy therefore keeps its launch-argument size until it is relaunched, or until its client
area is resized from the outside with `win/window-rect.ps1 -Width 640 -Height 360` (which is what
the main instance got on 2026-11-12 — its `[window] WxH` log line still shows the launch size
because that line is emitted once per process).
