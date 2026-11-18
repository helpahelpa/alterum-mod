# Status of the PINNED install (C:\AlterumPinned) - the canonical mod-development target.
# See pinned/UPDATE-POLICY.md. Change $g to inspect another install.
$g = "C:\AlterumPinned"
$p = Get-Process -Name Alterum -ErrorAction SilentlyContinue
if ($p) {
    Write-Output ("ALIVE pid={0} session={1} cpu={2:N0}s ws={3:N0}MB" -f $p[0].Id, $p[0].SessionId, $p[0].CPU, ($p[0].WorkingSet64/1MB))
} else { Write-Output "NOT RUNNING" }

Write-Output "=== interop ==="
$i = Get-ChildItem "$g\BepInEx\interop" -File -ErrorAction SilentlyContinue
Write-Output ("files={0} bytes={1:N0}" -f $i.Count, (($i | Measure-Object -Property Length -Sum).Sum))

Write-Output "=== LogOutput.log ==="
$l = "$g\BepInEx\LogOutput.log"
if (Test-Path $l) {
    Write-Output ("size=" + (Get-Item $l).Length)
    Get-Content $l -Tail 40
} else { Write-Output "absent" }

Write-Output "=== doorstop / stdout logs ==="
Get-ChildItem $g -File -Include "*log*.txt" -ErrorAction SilentlyContinue | Select-Object Length,Name | Format-Table -AutoSize
Write-Output "=== Player.log ==="
$pl = Join-Path $env:USERPROFILE 'AppData\LocalLow\Hot Winter\Alterum\Player.log'
if (Test-Path $pl) { Write-Output ("size=" + (Get-Item $pl).Length); Get-Content $pl -Tail 25 } else { Write-Output "absent" }
