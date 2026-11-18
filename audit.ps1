$paths = @(
 'C:\AlterumB','C:\AlterumCandidates\devbuild_unstable','C:\AlterumE','C:\AlterumI',
 'C:\AlterumSA','C:\AlterumT1','C:\AlterumT2','C:\AlterumT3',
 'C:\AlterumPinned','C:\AlterumPinned-pristine',
 'C:\Program Files (x86)\Steam\steamapps\common\Alterum'
)
foreach ($p in $paths) {
  "=== $p"
  if (-not (Test-Path $p)) { "  MISSING"; continue }
  $exe = Join-Path $p 'Alterum.exe'
  if (Test-Path $exe) {
    $fi = fsutil file queryfileid "$exe" 2>$null
    $lc = (fsutil hardlink list "$exe" 2>$null)
    "  EXE_LC_RAW: $($lc -join ' | ')"
  } else { "  NO EXE" }
}
