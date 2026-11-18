$src = 'C:\AlterumPinned\BepInEx\hot'
$dst = 'C:\AlterumB\BepInEx\hot'
Get-ChildItem $src -Recurse -File -Force | ForEach-Object {
  $rel = $_.FullName.Substring($src.Length+1)
  $to  = Join-Path $dst $rel
  $tmp = "$to.tmpcopy"
  New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
  try {
    Copy-Item $_.FullName $tmp -Force -EA Stop
    Move-Item $tmp $to -Force -EA Stop
    "  broke link: $rel"
  } catch {
    "  FAILED $rel : $($_.Exception.Message.Substring(0,[Math]::Min(90,$_.Exception.Message.Length)))"
    if (Test-Path $tmp) { Remove-Item $tmp -Force -EA SilentlyContinue }
  }
}
