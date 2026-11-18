$p = 'C:\Program Files (x86)\Steam\steamapps\common\Alterum'
"Exists: " + (Test-Path $p)
Get-ChildItem $p | ForEach-Object { "{0}`t{1}`t{2}" -f $_.Mode, $_.Name, $(if ($_.PSIsContainer) { '<DIR>' } else { $_.Length }) }
"--- exe ---"
$exe = Join-Path $p 'Alterum.exe'
if (Test-Path $exe) {
  $i = Get-Item $exe
  "Length: " + $i.Length
  "FileVersion: " + $i.VersionInfo.FileVersion
  "ProductVersion: " + $i.VersionInfo.ProductVersion
  "ProductName: " + $i.VersionInfo.ProductName
  "LastWrite: " + $i.LastWriteTime
}
"--- sizes ---"
$d = Join-Path $p 'Alterum_Data'
if (Test-Path $d) {
  $files = Get-ChildItem $d -Recurse -File
  "Alterum_Data files: " + $files.Count
  "Alterum_Data bytes: " + ($files | Measure-Object Length -Sum).Sum
}
$all = Get-ChildItem $p -Recurse -File
"Total files: " + $all.Count
"Total bytes: " + ($all | Measure-Object Length -Sum).Sum
"--- bepinex ---"
Get-ChildItem $p -Recurse -File -Include 'BepInEx*','*.dll','*.ini','*.cfg' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'BepInEx|winhttp|doorstop|steam_api64' } | ForEach-Object { "{0}`t{1}`t{2}" -f $_.FullName.Substring($p.Length+1), $_.Length, $_.LastWriteTime }
