$r = Get-ChildItem -Path C:\ -Filter Alterum.exe -Recurse -File -ErrorAction SilentlyContinue -Force |
  Select-Object -ExpandProperty FullName
$r | ForEach-Object { "EXE: $_" }
"----"
# also find by directory name pattern in case exe renamed
Get-ChildItem -Path C:\ -Directory -ErrorAction SilentlyContinue -Force |
  Where-Object { $_.Name -match 'Alterum' } | Select-Object -ExpandProperty FullName | ForEach-Object { "DIR: $_" }
