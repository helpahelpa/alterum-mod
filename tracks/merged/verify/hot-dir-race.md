# What overwrote install A's hot dir at 15:21, and what stops it happening again

> **Moved 2026-11-16:** the `alterum/mod/` tree named below no longer exists — the source is now `alterum/tracks/merged/mod/` and the payload lives outside the repo at `~/assets/monorepo/alterum-data/`; kept as written.

Install A = `C:\Program Files (x86)\Steam\steamapps\common\Alterum`. Process 10752, running headed
at 640×360 in a private lobby, never restarted during this investigation.

## What was loaded

`BepInEx/cache/hot/10752-16-02eb8758915d40aaad0e0da17af2f5fc/AlterumModelSwap.Hot.dll`
(mtime **15:21:48**, 95 232 B, md5 **fd6a3af17428d6b470c6767bde8ddb95**) is byte-identical to

```
alterum/mod/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll   95,232 B, 2026-11-11 16:41:33
```

i.e. the **old shared tree** build (swap era: `Contracts.ConfigSnapshot.SwapEnabled`,
`ModelSwapRuntime.DumpAndSwap`), not the merged tree. The generation-16 log is 824 consecutive lines of

```
[Error] Update threw: System.MissingFieldException: Field not found:
        'AlterumModelSwap.Contracts.ConfigSnapshot.SwapEnabled'.
   at AlterumModelSwap.Hot.ModelSwapRuntime.DumpAndSwap()
   at AlterumModelSwap.Hot.ModelSwapRuntime.Update(Single dt)
```

A's `BepInEx/plugins/AlterumModelSwap.Contracts.dll` is the merged one
(md5 `3b8b784ed59002b45c2be098595722ed`, identical to the merged tree's build), which no longer has
that field — so any swap-era hot dll fails on the first `Update`.

Timeline from `BepInEx/cache/hot/*` (one directory per generation, named with the generation number):

| time | generation | content |
|---|---|---|
| 15:21:48 | 16 | **the stale `mod/` build** (95 232 B, fd6a3af1) — throws every frame |
| 15:22:45 | 17 | `BadImageFormatException` — a **partial** read of a merged dll (3 670 016 B) |
| 15:22:47 | 18 | merged build (21 429 760 B, 7bade499) — recovered |
| 15:25…16:07 | 19–36 | nine more partial/complete **pairs** from non-atomic `cp`s of the merged dll |

So the wrong build was live for ~57 s (824 frames at 15 fps).

## Who could have written it — ruled out one by one

| candidate | finding |
|---|---|
| stale scheduled task | **none.** 364 tasks enumerated through `Schedule.Service`; no task action mentions `BepInEx`, a hot dir, a dll copy, or a deploy script. The 46 enabled `AlterumHelper_*` / `AlterumSecond_*` / `AlterumTrack*` tasks are one-shot launchers (`Alterum.exe`, `run-game.ps1`, `desktop-size.ps1`, `window-rect.ps1`, `save-fg.ps1`). Nothing with a repeating trigger touches the install. |
| cron / systemd timer | none for Alterum (`crontab -l` empty; `systemctl --user` and system lists have no game tasks). |
| old watcher | none running. No `inotifywait`/`entr`/`watchexec`/`fswatch`/`dotnet watch` in WSL, nothing in the Windows process table with a hot-dir command line. `/tmp/watch.sh` is a one-shot poller, `/tmp/hotdeploy.sh` already deploys the **merged** tree atomically. |
| another track's deploy script | no. `track.sh` maps bones/emotes/cosmetics → `C:\AlterumT1|T2|T3` with per-instance hot dirs; the only files under `tracks/*` that mention A's path are two `notes/*.md` and one `notes/explore_shared.py` (a reader). `grep -rln "steamapps/common/Alterum" tracks/bones tracks/emotes tracks/cosmetics` returns no deploy script. |
| `iterate.sh hot` | **no build ran**: nothing under `alterum/mod/` has a 2026-11-12 mtime (newest file 2026-11-11 16:41), so the copy deployed the stale artifact as-is. `iterate.sh` builds before it copies, so as written it would have refreshed `bin/`/`obj/`. |
| `mp-test.sh` | excluded: it kills every `Alterum` process first; A's log is continuous across 15:21 (generation 15→16→17→18, same pid 10752). |
| `clip.sh --no-build` | excluded: it writes `clip.request` and the recorder logs `[clip] start`; there is no clip run and no clip output between 15:21 and 15:23 (only the mirror's own `mirror_static_g…` PNGs). |
| a script copying `alterum/mod` | none exists (`grep -rn "alterum/mod" /tmp/*.sh` and every `tracks/*` script: no hit). |

**Verdict: a plain `cp` of the shared tree's stale hot dll** into A's `BepInEx/hot/` at 15:21:48,
almost certainly by hand — `alterum/mod/…` and `alterum/tracks/merged/mod/…` differ by the
`tracks/merged/` prefix only. The nine later partial reads are the same hand-deploy habit with the
correct file: a 21 MB `cp` into the watched directory is seen half-written by the host's reloader,
which shadows the file and then fails to load it (`BadImageFormatException`), producing a wasted
generation per deploy.

## What was changed

* `iterate.sh`, `clip.sh`, `mp-test.sh` — the three scripts whose hot-dll source was the stale
  `alterum/mod/` — now build/source `tracks/merged/mod/` (the only tree that matches the deployed
  Contracts) and deploy with `cp` to a temp name + `mv -f`, so a reload can never see a partial file.
  Their launch/config/lobby behaviour is unchanged.
* `mod/STALE-DO-NOT-DEPLOY.md` — written into the stale tree itself: what it is, the exact
  exception it causes, which md5 it is, and where the live tree is.
* `tracks/merged/tools/deploy-hot.sh` was already atomic and correct; unchanged.

## Current state (verified)

* A's `BepInEx/hot/AlterumModelSwap.Hot.dll` = **980955cbda13279ae34685c56cda119a** (21 431 808 B),
  the build of `tracks/merged/mod` deployed by `tools/deploy-hot.sh`; the same md5 is in B's
  `BepInEx/hot/b/`. Generation 42 is live, no `[Error]` line after it.
* `tracks/merged/mod/AlterumModelSwap.Contracts` → `BepInEx/plugins/AlterumModelSwap.Contracts.dll`
  (3b8b78…) is the merged Contracts; A was not restarted.
* Re-verified the repaired path end to end: `./iterate.sh hot` rebuilt the merged tree and deployed
  atomically → `[hot] generation 42 active`, `[chest:restore] … clone destroyed` for the previous
  generation, no `BadImageFormatException` (the last 10 partial reads all belong to generations ≤ 35,
  before the atomic deploys).
