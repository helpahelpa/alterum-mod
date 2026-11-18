"--- exe presence ---"
foreach ($p in @('C:\Program Files (x86)\Steam\steamapps\common\Alterum','C:\AlterumPinned','C:\AlterumPinned-pristine','C:\AlterumB','C:\AlterumDev','C:\AlterumCandidates\devbuild_unstable')) {
  $e = Join-Path $p 'Alterum.exe'
  if (Test-Path $e) { $s=(Get-Item $e).Length; "OK   {0}  {1} bytes  links={2}" -f $p, $s, ((fsutil hardlink list $e | Measure-Object).Count) }
  else { "MISS {0}" -f $p }
}
"--- Steam live pid ---"
$p = Get-Process -Id 29384 -EA SilentlyContinue
if ($p) { "pid 29384 alive: $($p.Path)" } else { "pid 29384 NOT RUNNING" }
"--- free space ---"
$d = Get-PSDrive C
"freeGB={0:N3} usedGB={1:N3}" -f ($d.Free/1GB), ($d.Used/1GB)
