$targets = @('C:\AlterumE','C:\AlterumI','C:\AlterumSA','C:\AlterumT1','C:\AlterumT2','C:\AlterumT3')
# safety: never touch these
$forbidden = @('C:\Program Files (x86)\Steam\steamapps\common\Alterum','C:\AlterumPinned','C:\AlterumPinned-pristine','C:\AlterumB','C:\AlterumDev','C:\AlterumCandidates')
foreach ($t in $targets) {
  if ($forbidden -contains $t) { "SKIP forbidden $t"; continue }
  $running = Get-Process -Name Alterum -EA SilentlyContinue | Where-Object { $_.Path -like "$t\*" }
  if ($running) { "SKIP running $t pid=$($running.Id)"; continue }
  if (-not (Test-Path $t)) { "already gone $t"; continue }
  try {
    Remove-Item $t -Recurse -Force -EA Stop
    "deleted $t"
  } catch {
    "ERROR $t : $($_.Exception.Message)"
  }
}
"--- remaining Alterum dirs ---"
Get-ChildItem C:\ -Directory -Force -EA SilentlyContinue | Where-Object { $_.Name -match 'Alterum' } | ForEach-Object { "  $($_.FullName)" }
"--- integrity: Steam / Pinned / B / Dev exe ---"
foreach ($p in @('C:\Program Files (x86)\Steam\steamapps\common\Alterum','C:\AlterumPinned','C:\AlterumB','C:\AlterumDev')) {
  $e = Join-Path $p 'Alterum.exe'
  "{0} -> {1}" -f $p, (if (Test-Path $e) { "exe OK $([math]::Round((Get-Item $e).Length/1KB,0))KB" } else { "EXE MISSING" })
}
