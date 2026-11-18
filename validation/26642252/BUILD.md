# Build — 26642252

Source: `tracks/merged/work/matrix-26642252/mod` (same tree hash as the other build's copy).

```
dotnet build <Project> -c Release -p:RefsDir=/mnt/c/Interop/26642252/ -v q --nologo
```

| project | exit | `error CS` | warnings |
|---|---|---|---|
| AlterumModelSwap.Contracts | 0 | 0 | 0 |
| AlterumModelSwap (host) | 0 | 0 | 0 |
| AlterumModelSwap.Hot | 0 | 0 | 32 |

Same 32 warning classes as the other build (`CS0168`×10, `CS0169`×4, `CS0414`×6, `CS0649`×8,
`CS8073`×4). No compile error on either build — the code compiles unchanged against this build's
interop assemblies.

Deployed DLLs: `AlterumModelSwap.Hot.dll` 40,863,744 B, host 19,456 B, Contracts 6,144 B
(md5s in `logs/deployed-md5.txt`).
