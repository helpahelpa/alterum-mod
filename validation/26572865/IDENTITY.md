# Identity — buildid 26572865 (`stable_build_previous_version`)

| field | value |
|---|---|
| candidate (read-only source) | `C:\AlterumCandidates\stable_prev` |
| test copy | `C:\AlterumTests\26572865` (plain recursive copy, `robocopy /E /COPY:DAT`) |
| source files / bytes | 431 / 16,524,405,332 (copy = 431 files, 16,524,405,332 B; 0 files with >1 hardlink) |
| differential check | file set + sizes identical to the candidate except the deliberate emulator swap (`steam_api64.dll` → gbe_fork) and the added `steam_settings/` |
| `buildid` (acf) | 26572865 |
| `Alterum.exe` (667,648 B) | `3FCCA364ACFF91980EF9C995C573A0C5AD1B0B96728A504C74F8BBFEB0B0B2D2` (same exe as the pin) |
| `GameAssembly.dll` (125,901,824 B) | `8CB6E08767554CD113AF5161A5AEC3A5C75D5C591773A892843A60DA240AD69D` |
| `global-metadata.dat` (26,434,464 B) | `130A6E1351E6243806425FB4989F59235A5AA0D4633DCCE9E1BC99E392238C99` |
| `UnityPlayer.dll` (36,519,336 B) | `BEF601AD70832ED45DA76262346D90BD8136D97F2D338DB743F02D9B2F314712` |
| `Alterum_Data\boot.config` (156 B) | `24191182BD4CE5883F38EA515ED1DE80CEE9323897EFDB6604690D31D42E435B` |
| Unity | 6000.3.16f1 (a56f230f6470), FileVersion 6000.3.16.10841891 |
| mod source tree (sha256 of all .cs/.csproj/.props) | `b349ce4b3384cfb0d8df5f4a62326dc3ef0c0cc98a6b1d66e417273d8ae03289` |
| built from | `tracks/merged/work/matrix-26572865/mod` (separate copy of `tracks/merged/mod`, no bin/obj) |
