$pin = 'C:\AlterumPinned'
$pinSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
Get-ChildItem $pin -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
  [void]$pinSet.Add($_.FullName.Substring($pin.Length+1))
}
"PIN file count: $($pinSet.Count)"
$paths = @('C:\AlterumB','C:\AlterumCandidates\devbuild_unstable','C:\AlterumE','C:\AlterumI',
 'C:\AlterumSA','C:\AlterumT1','C:\AlterumT2','C:\AlterumT3','C:\AlterumPinned-pristine',
 'C:\Program Files (x86)\Steam\steamapps\common\Alterum')
foreach ($p in $paths) {
  if (-not (Test-Path $p)) { continue }
  $extras = @()
  Get-ChildItem $p -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
    if (-not $pinSet.Contains($_.FullName.Substring($p.Length+1))) {
      $extras += [pscustomobject]@{ rel = $_.FullName.Substring($p.Length+1); size = $_.Length }
    }
  }
  "=== $p  extras=$(($extras).Count)  extraMB=$([math]::Round((($extras | Measure-Object size -Sum).Sum)/1MB,1))"
  $extras | Group-Object { ($_.rel -split '\\')[0..1] -join '\' } |
    Sort-Object Count -Descending | Select-Object -First 25 |
    ForEach-Object { "    {0}  x{1}" -f $_.Name, $_.Count }
}
