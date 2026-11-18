$out = Join-Path $env:TEMP 'alterum-audit'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$paths = @(
 @{n='Pinned';        p='C:\AlterumPinned'},
 @{n='B';             p='C:\AlterumB'},
 @{n='E';             p='C:\AlterumE'},
 @{n='I';             p='C:\AlterumI'},
 @{n='SA';            p='C:\AlterumSA'},
 @{n='T1';            p='C:\AlterumT1'},
 @{n='T2';            p='C:\AlterumT2'},
 @{n='T3';            p='C:\AlterumT3'},
 @{n='Pristine';      p='C:\AlterumPinned-pristine'}
)
# subdirs of interest: evidence + config + logs + plugins + hot
$subs = @('BepInEx\screens','BepInEx\config','BepInEx\logs','BepInEx\plugins','BepInEx\hot')
foreach ($e in $paths) {
  $rows = New-Object System.Collections.Generic.List[string]
  foreach ($s in $subs) {
    $d = Join-Path $e.p $s
    if (-not (Test-Path $d)) { continue }
    Get-ChildItem $d -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
      $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
      $rel = $_.FullName.Substring($e.p.Length+1)
      $rows.Add(("{0}`t{1}`t{2}`t{3}" -f $e.n, $rel, $_.Length, $h))
    }
  }
  foreach ($f in @('TRACK-ISOLATION.txt','BepInEx\LogOutput.log','BepInEx\LogOutput.1.log','BepInEx\ErrorLog.log')) {
    $fp = Join-Path $e.p $f
    if (Test-Path $fp) {
      $it = Get-Item $fp
      $h = (Get-FileHash $fp -Algorithm SHA256).Hash
      $rows.Add(("{0}`t{1}`t{2}`t{3}" -f $e.n, $f, $it.Length, $h))
    }
  }
  $dest = Join-Path $out "$($e.n).tsv"
  Set-Content -Path $dest -Value $rows -Encoding UTF8
  "{0}: {1} rows -> {2}" -f $e.n, $rows.Count, $dest
}
