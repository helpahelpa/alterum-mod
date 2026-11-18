# devbuild_unstable — harvested 2026-11-14

Candidate copy of Steam branch `devbuild_unstable` for app **4424420 (Alterum)**, downloaded into an
isolated directory. Nothing here touches `C:\AlterumPinned*` or the live Steam install.

## Steam state at harvest time (unchanged, pin still valid)

| | |
|---|---|
| Live `appmanifest_4424420.acf` buildid | **26597574** (unchanged before and after the download) |
| Staged patch | 26,874,800 / 27,565,632 B (~97.5 %), `TargetBuildID 26598292`, `StateFlags 1030` |
| Game process (pid 29384) | still running; not touched |
| Public branch now published as | buildid 26598292 (i.e. the staged patch is a *public* bump, not the dev branch) |

The staged update was **not** triggered or accepted. If it ever applies on its own, the live install
moves to 26598292 and the pin at 26597574 stays valid only as a frozen reference.

## Task 1 — SteamCMD session

- `steamcmd +app_info_print 4424420 +quit` returned full app info with **no** credential prompt, so
  the read-only probe passes and the branch listing was readable.
- However `+app_update` on its own aborted with `ERROR! Failed to request app info update, not
  online or not logged in to Steam.` and `+licenses_print` printed nothing — the stored token in
  `C:\steamcmd\config\config.vdf` (`ConnectCache`, account *[redacted]*,
  SteamID *[redacted]*) is **not** restored implicitly by a bare `+app_update`.
- Adding an explicit **`+login <steamcmd-account>`** (no password, no Guard code) restored the session from
  the cached token: `Logging in using cached credentials.` / `Logging in user '<steamcmd-account>' ... OK`.
  No interactive authentication was performed.
- A separate `steamcmd +login` console (pid 57692) belonging to the interactive session was alive
  throughout and was left alone.
- Note: a copy launched via WMI/SYSTEM context cannot use the cached credentials and fails the same
  way, so the working invocation must run as the logged-in user.

Working invocation used:

```
steamcmd +force_install_dir C:\AlterumCandidates\devbuild_unstable \
         +login <steamcmd-account> \
         +app_update 4424420 -beta devbuild_unstable validate +quit
```

## Task 2 — download

| | |
|---|---|
| Install path | `C:\AlterumCandidates\devbuild_unstable` |
| Result | `Success! App '4424420' fully installed.` |
| Files / bytes on disk | 434 files, 16,594,708,081 B |
| `SizeOnDisk` (acf) | 16,466,100,301 B |
| Transferred | 9,578,714,592 B |

All files are real copies (`link count = 1`), so this candidate cannot write through to the pin or
the Steam install.

## Task 3.1 — build identity

| | |
|---|---|
| Branch | `devbuild_unstable` |
| buildid | **26642252** |
| Depot | **4424421** |
| Manifest gid | **3300039468181698396** (fallback: `download_depot 4424420 4424421 3300039468181698396`) |
| `TargetBuildID` | 26642252 |
| Branch description | "Public devbuild with untested systems, possible critical bugs" |
| Branch timeupdated | 1794567443 |
| Unity | 6000.3.16f1 (a56f230f6470) — same as pin |
| build-guid | `dd4946d977d743c2b90092c2d2b39663` (pin: `2aae71e377394b93b6d7626d4552d658`) |
| Manifest file | `C:\AlterumCandidates\devbuild_unstable\steamapps\appmanifest_4424420.acf` |

Reference gids from `app_info_print`: public `3305259922664218926` (size 16,395,943,441),
`stable_build_previous_version` `7544362774400290007` (buildid 26572865).

## Task 3.2 — hashes vs the pin

| File | devbuild_unstable | pin (buildid 26597574) |
|---|---|---|
| `GameAssembly.dll` | `500E51AC74681C21141AB2091BC5E4B033E07F77E5C0EF558D97D50CA9CD38BB`<br>129,968,640 B | `B7C373FAD76FE9A71223620C280EA8C50225F4A5ECA3F1ECF1DDAE53E9874011`<br>125,977,600 B |
| `global-metadata.dat` | `EF7198C7579B895901DEE4AA482060E57632004B26DD02B7BBB5572349EF7332`<br>26,709,484 B | `20AA67B0A018CD3A541C34EB363E957B295F183B4AD09B7551FCB66683580716`<br>26,454,944 B |

The pin's hashes re-computed to exactly the documented baseline, so the comparison is sound.

**The dev build is a different binary.** `GameAssembly.dll` is +3,991,040 B (+3.2 %) and
`global-metadata.dat` is +254,540 B. **Interop must be regenerated for this build** — the existing
interop assemblies cannot be reused, and type/method indices will have shifted.

## Task 3.3 — symbols and debug payloads

Actual findings (recursive scan of the whole candidate tree):

| Size | Path | Note |
|---|---|---|
| 12,050,432 | `Alterum_Data\Plugins\x86_64\lib_burst_generated.pdb` | **only PDB in the build**; Burst job symbols. Not shipped in the pin. |
| 4,355,072 | `Alterum_Data\Plugins\x86_64\fmodstudioL.dll` | **FMOD logging build**. Pin ships `fmodstudio.dll` (3,248,128). |
| 3,379,200 | `Alterum_Data\Plugins\x86_64\lib_burst_generated.dll` | pin: 3,175,424 — different binary |
| 58,368 | `WinPixEventRuntime.dll` | not present in pin |
| — | `D3D12\d3d12SDKLayers.dll` | D3D12 debug layer; not present in pin (`D3D12Core.dll` is in both) |
| — | `_CommonRedist\DirectX\Jun2010\*` | DirectX redist set; not present in pin/pristine |
| — | `steamapps\appmanifest_4424420.acf`, `steamapps\appmanifest_228980.acf` | steamcmd bookkeeping |

`Alterum_Data\boot.config` in the dev build additionally enables Unity's **Player Connection**:

```
player-connection-mode=Listen
player-connection-guid=88160013
player-connection-project-name=Alterum
player-connection-ip=<lan-address-1>
player-connection-ip=<vpn-address-1>
player-connection-ip=<lan-address-2>
```

The pin's `boot.config` has none of these lines — this is an attachable profiler/debug endpoint
compiled into the build.

**Not present** (checked, absent): `*.debug`, `*.map`, `*.sym`, `*.mdb`, `*.ilk`, `*.lib`, `*.exp`,
`Alterum_Data\Managed`, IL2CPP generated-C++ / backup folders,
`*_BurstDebugInformation_DoNotShip`, any `_BackUpThisFolder_ButDontShipItWithYourGame`.
`Alterum_Data` is otherwise a normal layout (`il2cpp_data`, `Plugins`, `Resources`,
`StreamingAssets` + standard asset files). `Alterum_Data\Resources` contains only
`unity default resources` and `unity_builtin_extra` in both builds.

## Task 3.4 — console and dev-log code

### Console (QFSW.QC / Quantum Console)

| check | dev | pin |
|---|---|---|
| `QFSW.QC` string occurrences in metadata | 228 | 228 |
| `QuantumConsole` in metadata / `GameAssembly.dll` | 8 / 8 | 8 / 8 |
| `ConsoleInstaller` | 3 | 3 |
| Source path in build | `Assets\Plugins\QFSW\Quantum Console\Source\Scripts\...` | same |
| `ConsoleInstaller` source | `Assets\_Scripts\Zenject\Scene Installers\ConsoleInstaller.cs`, ns `Zenject.Installers`, field `_console` | same |
| Serialized `QuantumConsole` MonoBehaviour in `globalgamemanagers.assets` | 6 | 6 |
| `ScriptingAssemblies.json` assembly list | identical (QFSW.QC in both) | identical |
| `QFSW.QC::QuantumRegistry.ResetRegistry` runtime init | present | present |

**Conclusion: the console is present in both builds, in the same version, wired the same way
through the Zenject `ConsoleInstaller`, and it is instantiated in both** (a serialized
`QuantumConsole` object exists in `globalgamemanagers.assets` in both builds — not merely compiled
in). The dev build does **not** add, enable or gate the console; there is no console-specific
prefab in `Alterum_Data\Resources` and no settings asset by name in either build.
No console difference to exploit.

### Debug / log overlay

| type | dev | pin | notes |
|---|---|---|---|
| `DebugCanvas` | 6 | 3 | type exists in both at `Assets\_Scripts\Debug\DebugCanvas.cs`; the 3 extra hits are string literals, see below |
| `DebugFrameOverlay` | 3 | 3 | `Assets\_Scripts\Test\DebugFrameOverlay.cs`, identical |
| `DebugGUI` / `DebugGUISettings` / `DebugIsTalking` | 2 / 4 / 3 | 2 / 4 / 3 | pre-existing, unchanged |
| `[Debug Updater]`, `[DebugGUI] Graph export failed`, `[DebugGUI] Wrote graph`, `[DebugFromPlayer]`, `[Debug Canvas - Persistent]` | present | present | pre-existing |
| `Resources/DebugCanvas` | **1** | **0** | **new literal — a `Resources.Load` path added in the dev build** |
| `BannerOverlay` | 3 | 0 | new |

A `DebugCanvas` asset itself is serialized inside `resources.assets` **in both builds**, so the dev
build's new literal now loads it by path. The overlay subsystem is not new; what is new is that the
dev build resolves it through `Resources`.

### New code in the dev build (C# source-path inventory from metadata)

5826 `.cs` paths in dev vs 5817 in pin — **9 additions, 0 removals**:

```
Assets\_Scripts\BannerOverlay\BannerData.cs
Assets\_Scripts\BannerOverlay\BannerStack.cs
Assets\_Scripts\BannerOverlay\BannerView.cs
Assets\_Scripts\Gameplay\Players\Diagnostics\PlayerInitLog.cs
Assets\_Scripts\Steam\LobbyDoubleCreateTest.cs
Assets\_Scripts\Steam\LobbyMembershipProbe.cs
Assets\_Scripts\Steam\LobbyOwnerWatcher.cs
Assets\_Scripts\Steam\SteamRelayMonitor.cs
Assets\_Scripts\Steam\View\SteamRelayStatusUI.cs
```

`RuntimeInitializeOnLoads.json`: 97 entries (dev) vs 95 (pin); the two additions are
`Assembly-CSharp::LobbyOwnerWatcher::Bootstrap` and
`Kybernetik.Animancer::Animancer.Validate::InitializePermanentlyDisabledWarnings`.

## What differs from the pin, in short

1. Different IL2CPP binaries — interop regeneration required.
2. Ships a Burst PDB, an FMOD **logging** DLL and a rebuilt Burst DLL.
3. Enables Unity Player Connection (profiler attach) in `boot.config`.
4. Adds DirectX redist, `WinPixEventRuntime.dll` and the D3D12 debug layer.
5. Nine new C# files (BannerOverlay ×3, PlayerInitLog, five Steam/lobby diagnostics types);
   nothing removed, assembly list unchanged.
6. Console and debug-overlay wiring are otherwise identical to the pin. The only console-adjacent
   change is the new `Resources/DebugCanvas` load path.
