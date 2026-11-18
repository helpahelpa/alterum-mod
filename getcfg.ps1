foreach ($p in @('C:\AlterumB','C:\AlterumPinned','C:\AlterumDev')) {
  $f = Join-Path $p 'Alterum_Data\Plugins\x86_64\steam_settings\configs.user.ini'
  "=== $p"
  if (Test-Path $f) { Get-Content $f } else { "  (no configs.user.ini)" }
}
