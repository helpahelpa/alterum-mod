# Interop generation — 26642252

Same clean-process protocol as the other build: loader installed with an empty `BepInEx/plugins`,
no `BepInEx/hot`, no inherited `interop`/`cache`/`config`.

| field | value |
|---|---|
| generator | BepInEx **6.0.0-be.793** (commit `dbc82f05b8ea34833de5b6a723eb60dd8398e0b1`), .NET 6.0.7 |
| Il2CppInterop | **1.5.3+dbda1cb353b0f4253345dc45136d170b9e50a5a0** |
| Cpp2IL | **2022.1.0-development.1452+558ddd9** |
| metadata version | IL2CPP 39 |
| Cpp2IL run | `Cpp2IL finished in 00:00:29.2683366` |
| IL unstrip | 13616 successful, 2498 failed |
| output | `BepInEx/interop`: **226 assemblies, 157,009,651 B** |
| references copied for the build | `C:\Interop\26642252`: **255 files, 113,376,256 B** |
| clean-process confirmation | `0 patcher plugins loaded`, `0 assemblies discovered`, `0 plugins to load`, `Chainloader startup complete` |

Log excerpt: `logs/interop-LogOutput.log`.
