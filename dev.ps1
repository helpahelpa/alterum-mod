"=== C:\AlterumDev"
if (Test-Path C:\AlterumDev) {
  $i = Get-Item C:\AlterumDev -Force
  "attributes: $($i.Attributes)"
  "target: $($i.Target)"
  "mtime: $($i.LastWriteTime)"
  $m = Get-ChildItem C:\AlterumDev -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum
  "files: $($m.Count)  {0:N0} MB" -f ($m.Sum/1MB)
  Get-ChildItem C:\AlterumDev -Force | Select-Object -First 30 Mode,Name,Length | ForEach-Object { "  $($_.Mode) $($_.Name)" }
  "--- exe? ---"
  if (Test-Path C:\AlterumDev\Alterum.exe) { "exe EXISTS" } else { "exe MISSING (dangling link?)" }
} else { "C:\AlterumDev does not exist" }
