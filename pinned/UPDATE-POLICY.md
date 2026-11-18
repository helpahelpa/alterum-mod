# Update policy — the pinned build is the target

## What is pinned

| | |
|---|---|
| Pinned working install | `C:\AlterumPinned` |
| Pristine game archive | `C:\AlterumPinned-pristine` |
| Frozen on | Steam buildid **26597574**, Unity **6000.3.16f1 (a56f230f6470)** |
| `Alterum.exe` SHA256 | `3FCCA364ACFF91980EF9C995C573A0C5AD1B0B96728A504C74F8BBFEB0B0B2D2` |
| Manifests | `pinned/INSTALL-MANIFEST.md`, `pinned/PRISTINE-MANIFEST.md` |
| Source of truth for the freeze | the Steam install, `C:\Program Files (x86)\Steam\steamapps\common\Alterum`, as it was on 2026-11-14 |

`C:\AlterumPinned` is a **real, independent copy** (robocopy `/COPY:DAT`, every file link count 1).
It is not a hardlink farm. That matters: the Steam install's files are hardlinked into
`C:\AlterumB`, `C:\AlterumT1`…`T3`, `C:\AlterumI`, `C:\AlterumSA` and `C:\AlterumE` — `Alterum.exe`
alone has 8 links, `GameAssembly.dll` has 4 — so an update or a write-through on the Steam side can
silently change those installs. The pin is immune to that.

`C:\AlterumPinned-pristine` is the same game files with every loader/mod/emulator artifact removed
(see `pinned/PRISTINE-MANIFEST.md` for the exclusion list and why). It is **not runnable as a mod
install** — no loader, no mod, genuine Steam DLL. It exists to be diffed against.

## The rule

**Day-to-day mod work targets `C:\AlterumPinned`, never the live Steam install.**

- Scripts and docs default to the pin. Every launch path keeps the old path available as an
  override (`-GameDir`, `-Install`, `-Match`, `-Src`, or `ALTERUM_GAME` for the shell scripts) —
  use it only for the update test below, never for normal work.
- Deploying a hot DLL, rebuilding, capturing, or "just checking something quickly" against the
  Steam install is what this policy exists to prevent: after a Steam update the game files move
  underneath you and every result becomes unattributable.
- The Steam install is left alone. Pinning is additive; nothing was deleted or moved.

## Testing a new game version against the pin

Run these in order. Do not skip the compare step — it is the only thing that tells you *what*
changed and therefore whether a mod break is your code or the game's.

### 1. Snapshot the current state (already done)

```
C:\AlterumPinned          <- working install, frozen
C:\AlterumPinned-pristine <- game files only, frozen
```

Both have hash manifests. Re-verify the pin at any time with the method in
`pinned/INSTALL-MANIFEST.md` (SHA256 per file, compared against the table).

### 2. Let Steam update the live install

Update only the Steam install. The pin and pristine are untouched by Steam.

After the update, read the new version anchors before anything else:

```powershell
Select-String -Path 'C:\Program Files (x86)\Steam\steamapps\appmanifest_4424420.acf' -Pattern 'buildid|LastUpdated|SizeOnDisk'
(Get-Item 'C:\Program Files (x86)\Steam\steamapps\common\Alterum\Alterum.exe').VersionInfo | Format-List
(Get-FileHash 'C:\Program Files (x86)\Steam\steamapps\common\Alterum\Alterum.exe' -Algorithm SHA256).Hash
```

### 3. Compare manifests — *what actually changed*

The comparison is pristine vs the updated Steam install, **excluding** the same mod artifacts the
pristine already excludes (otherwise every result is just "BepInEx differs"):

```powershell
$old = 'C:\AlterumPinned-pristine'
$new = 'C:\Program Files (x86)\Steam\steamapps\common\Alterum'
$xd  = @((Join-Path $new 'BepInEx'), (Join-Path $new 'dotnet'),
         (Join-Path $new 'Alterum_Data\Plugins\x86_64\steam_settings'))
$xf  = @('winhttp.dll','doorstop_config.ini','.doorstop_version','changelog.txt','steam_appid.txt')
robocopy $old $new /L /E /NJH /NJS /NDL /FP /XD $xd /XF $xf
```

`/L` lists only. Files reported as "newer"/"changed" are the real delta. Anything under
`Alterum_Data` that moved is game content (assets, levels, metadata); `GameAssembly.dll` and
`Alterum_Data/il2cpp_data/Metadata/global-metadata.dat` moving means the IL2CPP side changed and
interop is now stale (next step). A delta of only `Alterum.exe`/`UnityPlayer.dll` with identical
`GameAssembly.dll` and metadata is usually a Unity patch — interop may survive, but recompile
anyway.

### 4. Regenerate interop

`BepInEx/interop` is generated from `GameAssembly.dll` + `global-metadata.dat`. When either moves it
is invalid: Il2CppInterop binds methods by token, so old interop against new metadata is undefined
behaviour, not a clean failure.

Easiest path — let BepInEx do it. `UpdateInteropAssemblies = true` is already set in
`BepInEx/config/BepInEx.cfg`, so:

```powershell
# on the install being updated
Remove-Item '<install>\BepInEx\interop' -Recurse -Force
Remove-Item '<install>\BepInEx\cache\hot' -Recurse -Force   # shadow copies of the old hot dll
# then start the game once and let it sit at the menu; first launch is slow (interop generation)
```

Confirm it finished: `BepInEx\interop` is repopulated (~226 files, ~155 MB at the pinned version)
and the log shows the chainloader reaching `[host] ready`.

### 5. Rebuild

Both assemblies must be recompiled — the host too, not just the hot DLL — because the references
move with the interop:

```bash
cd alterum
GAME=/path/to/the/updated/install
rm -rf refs && mkdir refs
cp "$GAME/BepInEx/core/"*.dll "$GAME/BepInEx/interop/"*.dll refs/
cd tracks/merged/mod
dotnet build AlterumModelSwap -c Release
dotnet build AlterumModelSwap.Contracts -c Release
dotnet build AlterumModelSwap.Hot -c Release
```

### 6. Smoke test

Launch at 640x360 on its own instance id and check the two markers that matter:

```bash
ALTERUM_GAME=<updated install> ./iterate.sh headed
```

Pass criteria:

- log shows `Chainloader startup complete` (BepInEx loaded)
- `[hot] generation 1 active` (host found and loaded the hot assembly)
- `[scene] -> 'Lobby'` (the game actually reached a playable scene)
- no `MissingFieldException` / `MissingMethodException` bursts — those mean interop no longer
  matches and step 4 did not take effect

`win/status.ps1` prints interop size, the live log tail and the running process in one shot.

### 7. Re-pin (only after the new version passes)

Decide deliberately, do not drift into it:

- **Keep the old pin** (default when the update is not required): do nothing. The Steam install now
  differs from the pin; work continues on the pin. Delete nothing.
- **Move the pin** to the new version: re-run the pin + pristine procedure
  (`_ps_pin_copy.ps1` → `_ps_manifest.ps1` → `_ps_pristine.ps1` with `$src` pointed at the updated
  Steam install, and rename the old pin to `C:\AlterumPinned-<oldbuildid>` rather than deleting it,
  so captures that reference it still resolve). Update the tables above, the manifests, and the
  `buildid` in `pinned/INSTALL-MANIFEST.md`.

## Cost of the two copies

| path | files | bytes |
|---|---|---|
| `C:\AlterumPinned` | see `INSTALL-MANIFEST.md` | ~17.9 GB now (was 23.4 GB at freeze; the loader clears `BepInEx/cache/hot` on start) |
| `C:\AlterumPinned-pristine` | 266 | 16,396,059,973 (= Steam's own `SizeOnDisk` for the app, an independent confirmation the archive is complete) |

Budget ~18 GB per extra pinned install if you keep an old one for comparison. Everything is on C:.

## Maintenance

- `BepInEx/cache/hot` grows every launch and is cleared by the loader at the next start. Not worth
  backing up.
- `BepInEx/LogOutput.log` is truncated on every launch but grows fast (it reached 142 MB once).
  If disk pressure appears, delete it — it is a log.
- The pin is a working install: its `BepInEx/hot/<instance>/`, `config/*.cfg`, `screens/` and
  `logs/` change as you work. The manifest covers the executable and `Alterum_Data` only, precisely
  so that day-to-day work does not invalidate it.
