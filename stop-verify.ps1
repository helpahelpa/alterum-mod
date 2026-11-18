$BEXE = 'C:\AlterumB\Alterum.exe'
"--- hot marker ---"
Select-String -Path 'C:\AlterumB\BepInEx\LogOutput.log' -Pattern '\[hot\] generation' -EA SilentlyContinue |
  Select-Object -First 3 | ForEach-Object { "  " + $_.Line }
"--- stopping B by exact path ---"
$p = Get-Process -Name Alterum -EA SilentlyContinue | Where-Object { $_.Path -eq $BEXE }
if ($p) { $p | ForEach-Object { "  stopping pid=$($_.Id) $($_.Path)"; Stop-Process -Id $_.Id -Force } } else { "  no B process" }
Start-Sleep -Seconds 3
$still = Get-Process -Name Alterum -EA SilentlyContinue
"--- Alterum processes now ---"
$still | ForEach-Object { "  pid=$($_.Id) $($_.Path)" }
"--- Steam live pid 29384 ---"
$s = Get-Process -Id 29384 -EA SilentlyContinue
if ($s) { "  ALIVE: $($s.Path)" } else { "  NOT RUNNING (was it closed?)" }
"--- final free space ---"
$d = Get-PSDrive C
"freeGB={0:N3}" -f ($d.Free/1GB)
"--- pin integrity: exe id/links unchanged ---"
$lb=0
"B exe links: " + ((fsutil hardlink list $BEXE) -join ' | ')
"pin LogOutput mtime: " + (Get-Item 'C:\AlterumPinned\BepInEx\LogOutput.log' -EA SilentlyContinue).LastWriteTime
"B  LogOutput mtime: " + (Get-Item 'C:\AlterumB\BepInEx\LogOutput.log').LastWriteTime
"pin exe mtime: " + (Get-Item 'C:\AlterumPinned\Alterum.exe').LastWriteTime
"--- B screens/logs after run ---"
foreach ($d2 in @('BepInEx\screens','BepInEx\logs','BepInEx\config')) {
  $m = Get-ChildItem "C:\AlterumB\$d2" -Recurse -File -Force -EA SilentlyContinue | Measure-Object Length -Sum
  "  {0,-16} {1,6} files {2,8:N1} MB" -f $d2,$m.Count,($m.Sum/1MB)
}
