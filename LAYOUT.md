# alterum-mod/ (repository root) ↔ ~/assets/monorepo/alterum-data/ — the two-tree contract

This project is split in two so that source is reviewable in Git while the payload never is.
The two trees are no longer siblings: **the payload lives outside the repository**, under `$HOME`.

| tree | in Git | holds |
| --- | --- | --- |
| `alterum-mod/` (repository root) | **yes** | `.cs`, `.csproj`, `.props`, `.sln`, `*.sh`, `win/*.ps1`, `*.py`, `*.md`, plans/notes, and small structured data (`*.json`/`*.csv` under ~1 MB) |
| `~/assets/monorepo/alterum-data/` (`$HOME/assets/monorepo/alterum-data/`, 7.6 GB, ~21.5 k files) | **no — outside the repo** | `installs/`, `dist/`, `refs/`, `bep/`, `models/`, `lc-reference/`, `shots/`, `mp_shots/`, `validation/` (raw logs), `.backup/`, `scratch/`, `work/` (experiment records), `mirrordecor/`, `lobby-model/` — plus every `bin/`, `obj/`, `*.dll`, `*.apack`, `*.mset`, `*.rset`, image, video, archive and log |

## Why it is outside the repo

Git ignores large files badly and a 7.6 GB payload has no business in a checkout: a clone, a CI
run, a worktree copy or a snapshot would all pay for it, and a single `git add -f` would poison the
history permanently. Ignoring it (the old `/alterum-data/` line) kept it out of commits but still
left it in the working tree, where every stray `tar`, backup or disk-space complaint found it.

So the payload was moved to `~/assets/monorepo/alterum-data/` with a **same-filesystem `mv`** (the
repo lives under the same mount), the repo-root `.gitignore` entry was dropped, and there is **no
symlink** back: a symlink would be a second path to the same bytes and would have to be ignored as
an untracked file. Everything resolves through one absolute root instead.

## Same relative paths, absolute root

Both trees keep the **same relative paths**. `tracks/merged/mod/AlterumModelSwap.Hot/` holds
the `.cs` + `.csproj`; its `.png`/`.mset`/`.rset`/`cosmetics.apack` payload lives at
`~/assets/monorepo/alterum-data/tracks/merged/mod/AlterumModelSwap.Hot/`. That is why the project
files compute their inputs from properties instead of from their own directory:

* `tracks/<track>/mod/Alterum.References.props` → `$(AlterumDataDir)` defaults to the **absolute**
  `$(HOME)/assets/monorepo/alterum-data/`, and `$(RefsDir)` to `$(AlterumDataDir)refs/` (the game
  interop DLLs). Absolute on purpose: a workspace-relative default would now resolve to nothing.
* `AlterumModelSwap.Hot.csproj` → `$(HotAssetsDir)` defaults to
  `$(AlterumDataDir)tracks/<track>/mod/AlterumModelSwap.Hot/`, and every `<EmbeddedResource>` is read
  from there. The embedded resource **names** (`LogicalName`) are unchanged, so the built DLL carries
  the same payload as the shipped one.
* Scripts use the same root: `$HOME/assets/monorepo/alterum-data` in shell,
  `os.path.expanduser("~/assets/monorepo/alterum-data")` in Python.

Override when working from another checkout (absolute, keep the trailing slash):

```sh
dotnet build -c Release -p:AlterumDataDir=$HOME/assets/monorepo/alterum-data/
```

Write `$HOME`, not `~`: the shell does not expand `~` inside `-p:Name=~/...`, and a `-p:` value is a
global MSBuild property that the props file cannot rewrite. A wrong value fails loudly —
`AlterumRefsCheck` names the resolved path instead of silently embedding no assets.

## Rules for the next change

1. Never commit a binary. If a new asset is needed, put it under `~/assets/monorepo/alterum-data/` at
   the mirrored path and reference it from the project through `$(HotAssetsDir)` (or a new property in
   `Alterum.References.props`).
2. `.cs`/`.csproj`/docs/scripts go to the repository root (this repo). `.gitignore` blocks `bin/`, `obj/` and common
   binary extensions as a backstop, so an asset written here by mistake shows up as an error, not a commit.
3. Screenshots, captures and logs are always written to
   `~/assets/monorepo/alterum-data/tracks/<track>/shots/`; scripts derive that from the absolute root above.
4. `LAUNCH-POLICY.md`, `pinned/` evidence and `validation/*.md` reports are the deployed-mod evidence
   base; their raw logs/screens live in `~/assets/monorepo/alterum-data/validation/`.
5. The deploy target (`C:\AlterumPinned`, the running game, the pinned DLL) is never written by a build
   or by this layout; moving the data tree changes only where the *inputs* are read from.
