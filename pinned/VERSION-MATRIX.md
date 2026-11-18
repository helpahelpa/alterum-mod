# Alterum — Version / Interop Matrix

App `4424420` (Alterum), single Windows depot `4424421`. Unity **6000.3.16f1** on every build.
SteamCMD account *[redacted]* (cached login only; no credentials in this repo). Acquisition + inventory only: no interop
regeneration, no mod build, no launch.

## 1. Acquisition (per branch)

| Branch | Dir | Result | buildid | depot 4424421 manifest | SizeOnDisk (acf) | BytesToDownload (log) |
|---|---|---|---|---|---|---|
| `public` (default) | `C:\Program Files (x86)\Steam\steamapps\common\Alterum` (pre-existing) | not downloaded — already installed | `26598292` | `3305259922664218926` | 16,395,943,441 | 27,565,632 (last update, already applied) |
| `stable_build_previous_version` | `C:\AlterumCandidates\stable_prev` | **downloaded** (`Success! App '4424420' fully installed.`) | `26572865` | `7544362774400290007` | 16,395,797,528 | 9,425,935,520 (compressed) |
| `devbuild_unstable` | `C:\AlterumCandidates\devbuild_unstable` (pre-existing) | **verified intact** (full `validate`, 0 bytes re-fetched) | `26642252` | `3300039468181698396` | 16,466,100,301 | 0 |
| `devbuild_closed` | `C:\AlterumCandidates\devbuild_closed` (empty) | **NOT obtained — password-gated** | (`25773974`, client-cached) | (`6145293522663467285`) | (16,371,686,215) | — |

`devbuild_closed` verbatim output (exit code 8), `_logs/devbuild_closed.txt`:

```
ERROR! Failed to set beta 'devbuild_closed'
```

No password attempted or guessed. Corroborating evidence from the Steam client's
`appmanifest_4424420.acf` — the app carries `"privatebranches" "1"` and lists:

```
"devbuild_closed" { "buildid" "25773974" "pwdrequired" "1" "timeupdated" "1791365793" }
"4424421" { "manifests" { "devbuild_closed" { "gid" "6145293522663467285" "size" "16371686215" "download" "9293053280" } } }
```

Those `devbuild_closed` numbers come from the client's cache (last touched 1791365793), so
buildid/manifest may have advanced; they are *not* verified against the CDN.

`public` default-branch confirmation via `+app_info_print 4424420` (`_logs/appinfo.txt`):
`buildid 26598292` → `4424421` manifest `3305259922664218926`, size 16,395,943,441,
download 9,425,915,952. Matches the installed acf exactly.

Validated against `app_find`-style branch table:

```
"public"                         buildid 26598292
"devbuild_unstable"              buildid 26642252
"stable_build_previous_version"  buildid 26572865
```

## 2. Inventory (builds on disk)

`shares interop` = identical `GameAssembly.dll` **and** `global-metadata.dat` as the pin.
Symbols column: `*.pdb` / `*.debug` / `Alterum_Data\Managed`.

| Build | Dir | buildid | du size (bytes) | files | GameAssembly.dll | global-metadata.dat | Symbols | Verdict |
|---|---|---|---|---|---|---|---|---|
| Pin (modded) | `C:\AlterumPinned` | 26597574 (no acf; manual copy) | 19,503,075,837 | 5,950 | 125,977,600 / `b7c373fa…` | 26,454,944 / `20aa67b0…` | no pdb/debug/Managed (BepInEx+dotnet+doorstop present) | **same binary as pin** (baseline) |
| Public / Steam | `…\Steam\steamapps\common\Alterum` | 26598292 | 23,573,635,549 | 6,025 | 125,977,600 / `b7c373fa…` | 26,454,944 / `20aa67b0…` | no pdb/debug/Managed (BepInEx 6.7G + doorstop present) | **same binary as pin** — shares interop with pin |
| `stable_build_previous_version` | `C:\AlterumCandidates\stable_prev` | 26572865 | 16,524,405,332 | 431 | 125,901,824 / `8cb6e087…` | 26,434,464 / `130a6e13…` | none (clean) | **needs new interop** |
| `devbuild_unstable` | `C:\AlterumCandidates\devbuild_unstable` | 26642252 | 16,594,708,081 | 434 | 129,968,640 / `500e51ac…` | 26,709,484 / `ef7198c7…` | only `Alterum_Data\Plugins\x86_64\lib_burst_generated.pdb` (burst, shipped); no Managed | **needs new interop** |
| Pin pristine (ref) | `C:\AlterumPinned-pristine` | 26597574 | 16,396,059,973 | 266 | 125,977,600 / `b7c373fa…` | 26,454,944 / `20aa67b0…` | none (clean) | **same binary as pin** (read-only reference) |

Full SHA-256:

| Binary | SHA-256 |
|---|---|
| `GameAssembly.dll` — pin / public 26598292 / pristine | `b7c373fad76fe9a71223620c280ea8c50225f4a5eca3f1ecf1ddae53e9874011` |
| `global-metadata.dat` — pin / public 26598292 / pristine | `20aa67b0a018cd3a541c34eb363e957b295f183b4ad09b7551fcb66683580716` |
| `GameAssembly.dll` — stable_prev 26572865 | `8cb6e08767554cd113af5161a5aec3a5c75d5c591773a892843a60da240ad69d` |
| `global-metadata.dat` — stable_prev 26572865 | `130a6e1351e6243806425fb4989f59235a5aa0d4633dcce9e1bc99e392238c99` |
| `GameAssembly.dll` — devbuild_unstable 26642252 | `500e51ac74681c21141ab2091bc5e4b033e07f77e5c0ef558d97d50ca9cd38bb` |
| `global-metadata.dat` — devbuild_unstable 26642252 | `ef7198c7579b895901dee4aa482060e57632004b26dd02b7bbb5572349ef7332` |

### Interop groups (by `GameAssembly.dll` hash)

| Group | Builds | Interop artifacts needed |
|---|---|---|
| A — `b7c373fa…` | pin 26597574, public 26598292, pristine | 1 (already exists), covers **two** branches |
| B — `8cb6e087…` | stable_prev 26572865 | 1 new |
| C — `500e51ac…` | devbuild_unstable 26642252 | 1 new |
| — | devbuild_closed 25773974 | unknown, not obtainable yet |

**Pin and the just-updated Steam build are the same code binary** (26598292 only staged 27.5 MB of
data/asset changes). `global-metadata.dat` matches too, so the pin's interop is valid for the
current public build as well. Total new interop generations required: **2** (groups B and C).

Existing group-A interop artifacts live at `C:\AlterumPinned\BepInEx\interop` (226 files,
`Assembly-CSharp.dll` 16,077,312 B) — generated against `b7c373fa…`, therefore reusable for the
public 26598292 build without regeneration.

Cross-check: the pre-existing `DEBBUILD-UNSTABLE.md` records the same `devbuild_unstable` hashes
(`500E51AC…` / 129,968,640 B) and the same pin hash (`B7C373FA…`), so this inventory is consistent
with the earlier harvest rather than a second, divergent download.

`UnityPlayer.dll` note: identical on pin/public/pristine/stable_prev (36,519,336 B,
`bef601ad70832ed4…`), but `devbuild_unstable` ships 84,018,088 B (`3bb8dfa7875a4d11…`) — a
development/validation player. Same Unity version string (6000.3.16f1), different player binary;
not an interop determinant but relevant when comparing runtime behaviour.

## 3. Disk

| | Free on C: |
|---|---|
| Before | 633 GiB |
| After (stable_prev installed, devbuild_unstable already present) | 618 GiB |
| Net consumed | ~15 GiB |

Peak headroom needed for one more full candidate: ~16.5 GiB (devbuild_closed, if unlocked).

## 4. State of the tree (what next phase inherits)

- Pin `C:\AlterumPinned` untouched; the running game (pid 96760,
  `"C:\AlterumPinned\Alterum.exe" -screen-fullscreen 0 -screen-width 640 -screen-height 360`)
  was not interrupted.
- `C:\AlterumPinned-pristine` untouched (read-only).
- `C:\AlterumCandidates\stable_prev` and `\devbuild_unstable` are **clean** installations: no
  `winhttp.dll`, no `doorstop_config.ini`, no `BepInEx`, no `dotnet`, no `steam_appid.txt`.
- The Steam install is **not clean**: it carries the loader and a 6.7 GiB `BepInEx` tree. Its
  `GameAssembly.dll` and `global-metadata.dat` are unmodified (hashes equal the pristine archive),
  so binary identity is trustworthy, but it cannot serve as an unmodded run without first
  disabling/removing `winhttp.dll` + `doorstop_config.ini` + `BepInEx`.
- `C:\AlterumCandidates\devbuild_closed` exists but contains only an empty `steamapps\`.

## 5. Next phase — per build

| Build | What must happen |
|---|---|
| pin 26597574 | Nothing new. Reference run for current interop artifacts. |
| public 26598292 | Reuse the pin's interop (same binary). Only needs a clean loader path if an unmodded baseline is required — the installed dir is mod-contaminated. |
| stable_prev 26572865 | Generate interop for `8cb6e087…` / `130a6e13…`, build the mod against it, install into `C:\AlterumCandidates\stable_prev`, test. |
| devbuild_unstable 26642252 | Generate interop for `500e51ac…` / `ef7198c7…`, build, install, test. Note the dev UnityPlayer — expect different runtime/debug surface. |
| devbuild_closed 25773974 | **Blocked**: beta password required (`ERROR! Failed to set beta 'devbuild_closed'`). Needs the password supplied by the owner, then `+app_update 4424420 -beta devbuild_closed <pwd>`, ~16.5 GiB. Until then its interop group is unknown. |

## 6. Evidence files

- `C:\AlterumCandidates\_logs\appinfo.txt` — `+app_info_print 4424420` (branch + depot tables)
- `C:\AlterumCandidates\_logs\stable_prev.out.txt` — full download/verify log
- `C:\AlterumCandidates\_logs\devbuild_unstable_validate.out.txt` — validate log (100% verified, 0 B downloaded)
- `C:\AlterumCandidates\_logs\devbuild_closed.txt` — password-gate error, verbatim
- `alterum/pinned/inventory.sh` — inventory script used for every build
