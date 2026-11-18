# Identity — buildid 26642252 (`devbuild_unstable`)

| field | value |
|---|---|
| candidate (read-only source) | `C:\AlterumCandidates\devbuild_unstable` |
| test copy | `C:\AlterumTests\26642252` (plain recursive copy, `robocopy /E /COPY:DAT`) |
| source files / bytes | 434 / 16,594,708,079 (copy = 434 files, 16,594,708,079 B; 0 files with >1 hardlink). `VERSION-MATRIX.md` lists 16,594,708,081 — 2 B more; the copy matches the candidate exactly (per-file sizes compared), so the matrix figure is the stale one |
| differential check | file set + sizes identical to the candidate except the deliberate emulator swap and added `steam_settings/` |
| `buildid` (acf) | 26642252 |
| `Alterum.exe` (667,648 B) | `84D02DC372C9AB95811801DDFFD15FD87E1ECC47FB6DDF2E72F4FC0D606AD0AB` (different from the pin/stable) |
| `GameAssembly.dll` (129,968,640 B) | `500E51AC74681C21141AB2091BC5E4B033E07F77E5C0EF558D97D50CA9CD38BB` |
| `global-metadata.dat` (26,709,484 B) | `EF7198C7579B895901DEE4AA482060E57632004B26DD02B7BBB5572349EF7332` |
| `UnityPlayer.dll` (84,018,088 B) | `3BB8DFA7875A4D1173A1D980F8DB8FCAFA8D514BF89C6EED875FB5B01A4781AE` |
| `Alterum_Data\boot.config` (387 B) | `5FD401034F722B8DA5A3A55E0C7319AC8D8C6D3F069D3040AD0814168F2653FA` (carries the `player-connection-*` keys) |
| Unity | 6000.3.16f1 (a56f230f6470), FileVersion 6000.3.16.10841891 |
| mod source tree (sha256 of all .cs/.csproj/.props) | `b349ce4b3384cfb0d8df5f4a62326dc3ef0c0cc98a6b1d66e417273d8ae03289` (identical to the stable_prev tree) |
| built from | `tracks/merged/work/matrix-26642252/mod` |
