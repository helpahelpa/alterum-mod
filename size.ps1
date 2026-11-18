$paths = @(
 'C:\AlterumB','C:\AlterumCandidates\devbuild_unstable','C:\AlterumE','C:\AlterumI',
 'C:\AlterumSA','C:\AlterumT1','C:\AlterumT2','C:\AlterumT3',
 'C:\AlterumPinned','C:\AlterumPinned-pristine',
 'C:\Program Files (x86)\Steam\steamapps\common\Alterum'
)
foreach ($p in $paths) {
  if (-not (Test-Path $p)) { "$p`tMISSING"; continue }
  $m = Get-ChildItem -Path $p -Recurse -File -Force -ErrorAction SilentlyContinue |
       Measure-Object -Property Length -Sum
  "{0}`t{1:N0} MB`t{2} files`t{3:N0} bytes" -f $p, ($m.Sum/1MB), $m.Count, $m.Sum
}
