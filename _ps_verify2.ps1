$ErrorActionPreference = "Stop"
function Show($k, $v) { Write-Output ("{0}={1}" -f $k, $v) }
$pin = "C:\AlterumPinned"; $pri = "C:\AlterumPinned-pristine"

foreach ($pair in @(@("pin", $pin), @("pristine", $pri))) {
    $n = $pair[0]; $p = $pair[1]
    $all = @(Get-ChildItem $p -Recurse -File -Force -ErrorAction SilentlyContinue)
    Show ("{0}_files" -f $n) $all.Count
    Show ("{0}_bytes" -f $n) (($all | Measure-Object -Property Length -Sum).Sum)
    Show ("{0}_GB" -f $n) ([math]::Round((($all | Measure-Object -Property Length -Sum).Sum)/1GB,2))
    Show ("{0}_exe_sha256" -f $n) (Get-FileHash (Join-Path $p "Alterum.exe") -Algorithm SHA256).Hash
    $d = @(Get-ChildItem (Join-Path $p "Alterum_Data") -Recurse -File -Force -ErrorAction SilentlyContinue)
    Show ("{0}_data_files" -f $n) $d.Count
    Show ("{0}_data_bytes" -f $n) (($d | Measure-Object -Property Length -Sum).Sum)
    # top-level breakdown so the totals reconcile
    foreach ($sub in (Get-ChildItem $p -Directory -Force | Select-Object -ExpandProperty Name)) {
        $f = @(Get-ChildItem (Join-Path $p $sub) -Recurse -File -Force -ErrorAction SilentlyContinue)
        Write-Output ("    {0}\{1}  files={2} bytes={3}" -f $n, $sub, $f.Count, (($f | Measure-Object -Property Length -Sum).Sum))
    }
    Write-Output ("    {0}\<root files> = {1}" -f $n, @(Get-ChildItem $p -File -Force).Count)
}

Write-Output "=== pristine: what is present at the top level ==="
Get-ChildItem $pri -Force | ForEach-Object { Write-Output ("  " + $(if ($_.PSIsContainer) { "DIR  " } else { "FILE " }) + $_.Name) }
Write-Output "=== pristine: BepInEx / doorstop remnants (expect none) ==="
foreach ($probe in "BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "steam_appid.txt", "changelog.txt",
                   "Alterum_Data\Plugins\x86_64\steam_settings", "Alterum_Data\Plugins\x86_64\steam_api64.dll.vanilla") {
    $p = Join-Path $pri $probe
    Write-Output ("  {0,-58} {1}" -f $probe, $(if (Test-Path $p) { "PRESENT (unexpected)" } else { "absent" }))
}
Write-Output "=== pristine: genuine Steam DLL ==="
$g = Join-Path $pri "Alterum_Data\Plugins\x86_64\steam_api64.dll"
if (Test-Path $g) { Write-Output ("  bytes={0} sha256={1}" -f (Get-Item $g).Length, (Get-FileHash $g -Algorithm SHA256).Hash) } else { Write-Output "  MISSING" }

Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Name -match "^[CD]$" } | ForEach-Object {
    Show ("{0}.free_GB" -f $_.Name) ([math]::Round($_.Free/1GB,2))
}
Show "manifest_pin" $(if (Test-Path "C:\Windows\Temp\alterum-pinned-manifest.md") { (Get-Item "C:\Windows\Temp\alterum-pinned-manifest.md").Length } else { "ABSENT" })
Show "manifest_pristine" $(if (Test-Path "C:\Windows\Temp\alterum-pristine-manifest.md") { (Get-Item "C:\Windows\Temp\alterum-pristine-manifest.md").Length } else { "ABSENT" })
