$p='C:\AlterumB'
if (Test-Path $p) {
  $i = Get-Item $p -Force
  "dir mtime: $($i.LastWriteTime)  attrs: $($i.Attributes)"
  $all = Get-ChildItem $p -Recurse -Force -EA SilentlyContinue
  "items: $($all.Count)"
  $all | Select-Object -First 15 | ForEach-Object { "  $($_.Mode) $($_.Name) $($_.LastWriteTime)" }
  $m = Get-ChildItem $p -Recurse -File -Force -EA SilentlyContinue | Measure-Object Length -Sum
  "files: $($m.Count) bytes: $($m.Sum)"
} else { "B GONE" }
"--- could we create a file inside? ---"
try { New-Item -ItemType File -Path 'C:\AlterumB\_lockprobe.txt' -Force -EA Stop | Out-Null; "CREATE INSIDE OK"; Remove-Item 'C:\AlterumB\_lockprobe.txt' -Force; "probe removed" } catch { "CREATE INSIDE FAILED: $($_.Exception.Message)" }
