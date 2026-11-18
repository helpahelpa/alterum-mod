# Build — 26572865

Source: `tracks/merged/work/matrix-26572865/mod` (byte-identical copy of `tracks/merged/mod`, sha256 above).
Command (each project, isolated bin/obj in that copy):

```
dotnet build <Project> -c Release -p:RefsDir=/mnt/c/Interop/26572865/ -v q --nologo
```

| project | exit | `error CS` | warnings |
|---|---|---|---|
| AlterumModelSwap.Contracts | 0 | 0 | 0 |
| AlterumModelSwap (host) | 0 | 0 | 0 |
| AlterumModelSwap.Hot | 0 | 0 | 32 |

Warning codes (all pre-existing classes; no unresolved-reference warning this time):
`CS0168` ×10, `CS0169` ×4, `CS0414` ×6, `CS0649` ×8, `CS8073` ×4.

Outputs: `AlterumModelSwap.Hot.dll` 40,863,744 B (md5 `B2A524A533676251D774D18CD26529CD`),
`AlterumModelSwap.dll` 19,456 B (md5 `1025D48AFC92EE028F9E013E551AB4BC`),
`AlterumModelSwap.Contracts.dll` 6,144 B (md5 `E0F221E88998A3ACE23D786CE3D072E4`).

Full compiler output: `buildlogs/` (contracts.log, host.log, hot.log).
