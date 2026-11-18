# Re-verify every file listed in INSTALL-MANIFEST.md still matches (the smoke test ran after the freeze).
$ErrorActionPreference = "Stop"
function Show($k, $v) { Write-Output ("{0}={1}" -f $k, $v) }
$pin = "C:\AlterumPinned"
$man = "C:\Windows\Temp\alterum-pinned-manifest.md"

$rows = @()
foreach ($line in Get-Content $man) {
    # table rows look like: | 667648 | `HASH` | `Alterum.exe` |
    if ($line -match '^\|\s*(\d+)\s*\|\s*`([0-9A-F]{64})`\s*\|\s*`([^`]+)`\s*\|$') {
        $rows += [pscustomobject]@{ Bytes = [int64]$matches[1]; Sha256 = $matches[2]; Path = $matches[3] }
    }
}
Show "manifest_rows_parsed" $rows.Count

$bad = 0; $missing = 0
foreach ($r in $rows) {
    $p = Join-Path $pin $r.Path
    if (-not (Test-Path $p)) { Show "MISSING" $r.Path; $missing++; continue }
    $h = (Get-FileHash $p -Algorithm SHA256).Hash
    $len = (Get-Item $p).Length
    if ($h -ne $r.Sha256 -or $len -ne $r.Bytes) { Show ("MISMATCH " + $r.Path) ("expected=" + $r.Sha256.Substring(0,16) + " got=" + $h.Substring(0,16) + " len=" + $len); $bad++ }
}
Show "verified_after_smoketest" $rows.Count
Show "hash_mismatches" $bad
Show "missing" $missing

# What changed in the pin between freeze and now: BepInEx only (the loader clears BepInEx/cache/hot).
$all = @(Get-ChildItem $pin -Recurse -File -Force -ErrorAction SilentlyContinue)
Show "pin_files_now" $all.Count
Show "pin_bytes_now" (($all | Measure-Object -Property Length -Sum).Sum)
Show "pin_cache_hot_files" (@(Get-ChildItem (Join-Path $pin "BepInEx\cache\hot") -Recurse -File -Force -ErrorAction SilentlyContinue)).Count
Show "pin_instance_dirs" ((Get-ChildItem (Join-Path $pin "BepInEx\hot") -Directory -Force -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join ",")
