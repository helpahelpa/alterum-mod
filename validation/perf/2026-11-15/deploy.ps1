$ErrorActionPreference='Stop'
$hot='C:\Windows\Temp\alterum\perf\build\AlterumModelSwap.Hot.dll'
foreach ($a in 'L','F') {
  $d="C:\AlterumPerf\$a"
  Get-ChildItem "$d\BepInEx\hot" -Filter 'AlterumModelSwap.Hot.dll.*' -File | Remove-Item -Force
  Copy-Item $hot "$d\BepInEx\hot\AlterumModelSwap.Hot.dll" -Force
  New-Item -ItemType Directory -Force -Path "$d\BepInEx\perf" | Out-Null
  $feat = if ($a -eq 'L') { 'off' } else { 'on' }
  @"
# benchmark control file (see Bench.cs): this install is a measurement arm only
features=$feat
csv=$d\BepInEx\perf\frames.csv
"@ | Set-Content "$d\BepInEx\bench.cfg" -Encoding ascii
  "deployed $a features=$feat dll=$((Get-Item "$d\BepInEx\hot\AlterumModelSwap.Hot.dll").Length)"
}
