Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'Alterum' } |
  ForEach-Object { "RUNNING {0} pid={1} {2}" -f $_.ProcessName, $_.Id, $_.Path }
"--- AlterumDev exe id/links ---"
fsutil hardlink list 'C:\AlterumDev\Alterum.exe'
"--- AlterumDev vs Pinned equality sample ---"
foreach ($f in @('Alterum.exe','GameAssembly.dll','UnityPlayer.dll','winhttp.dll')) {
  $a=(Get-FileHash "C:\AlterumDev\$f" -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
  $b=(Get-FileHash "C:\AlterumPinned\$f" -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
  "{0}: dev={1} pin={2} same={3}" -f $f, $a.Substring(0,12), $b.Substring(0,12), ($a -eq $b)
}
"--- AlterumDev extra files (not in pin) ---"
$pin='C:\AlterumPinned'
$ps=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
Get-ChildItem $pin -Recurse -File -Force -EA SilentlyContinue | ForEach-Object { [void]$ps.Add($_.FullName.Substring($pin.Length+1)) }
$c=0
Get-ChildItem 'C:\AlterumDev' -Recurse -File -Force -EA SilentlyContinue | ForEach-Object {
  $rel=$_.FullName.Substring('C:\AlterumDev'.Length+1)
  if (-not $ps.Contains($rel)) { "  EXTRA: $rel $([math]::Round($_.Length/1MB,1))MB $($_.LastWriteTime)"; $c++ }
}
"extras: $c"
