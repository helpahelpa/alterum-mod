"--- tar/ssh/powershell procs ---"
Get-Process -EA SilentlyContinue | Where-Object { $_.ProcessName -match 'tar|ssh|powershell|cmd|SearchProtocolHost|MsMpEng|explorer' } |
  ForEach-Object { $pp=$null; try{$pp=$_.Path}catch{}; "PROC {0} pid={1} {2}" -f $_.ProcessName,$_.Id,$pp }
"--- handle.exe available? ---"
(Get-Command handle.exe,handle64.exe -EA SilentlyContinue).Source
"--- try deleting a single file inside B ---"
$f = 'C:\AlterumB\steam_appid.txt'
if (Test-Path $f) { try { Copy-Item $f 'C:\Windows\Temp\_b_test.txt' -Force; Remove-Item 'C:\Windows\Temp\_b_test.txt' -Force; "copy-out OK" } catch { "copy failed: $_" } }
"--- retry remove (5 attempts) ---"
for ($i=1; $i -le 5; $i++) {
  try { Remove-Item C:\AlterumB -Recurse -Force -EA Stop; "REMOVED on attempt $i"; break }
  catch { "attempt $i failed: $($_.Exception.Message.Substring(0,[Math]::Min(110,$_.Exception.Message.Length)))"; Start-Sleep -Seconds 4 }
}
if (Test-Path C:\AlterumB) { "STILL EXISTS" } else { "GONE" }
