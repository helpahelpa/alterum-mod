$pin='C:\AlterumPinned'
foreach ($d in @('BepInEx\screens','BepInEx\config','BepInEx\hot','BepInEx\logs','BepInEx\plugins','BepInEx\cache')) {
  $p = Join-Path $pin $d
  if (Test-Path $p) {
    $m = Get-ChildItem $p -Recurse -File -Force | Measure-Object -Property Length -Sum
    "PIN {0}: {1} files {2:N1} MB" -f $d, $m.Count, ($m.Sum/1MB)
  } else { "PIN $d : MISSING" }
}
"--- pin root ---"
Get-ChildItem $pin -Force | Select-Object Mode,Name | ForEach-Object { "  $($_.Mode) $($_.Name)" }
"--- pin version ---"
$exe = Join-Path $pin 'Alterum.exe'
(Get-Item $exe).VersionInfo | Select-Object FileVersion,ProductVersion,FileDescription | Format-List
"exe mtime: $((Get-Item $exe).LastWriteTime)  size: $((Get-Item $exe).Length)"
