"--- processes with path under AlterumB ---"
Get-Process -EA SilentlyContinue | ForEach-Object {
  $pp = $null; try { $pp = $_.Path } catch {}
  if ($pp -like 'C:\AlterumB*') { "PROC $($_.ProcessName) pid=$($_.Id) $pp" }
}
"--- any process cmdline mentioning AlterumB ---"
Get-CimInstance Win32_Process -EA SilentlyContinue | Where-Object { $_.CommandLine -like '*AlterumB*' -or $_.ExecutablePath -like 'C:\AlterumB*' } |
  Select-Object ProcessId,Name,ExecutablePath,CommandLine | Format-List
"--- rename test ---"
try { Rename-Item C:\AlterumB C:\AlterumB_locktest -EA Stop; "RENAME OK"; Rename-Item C:\AlterumB_locktest C:\AlterumB -EA Stop; "RENAMED BACK" }
catch { "RENAME FAILED: $($_.Exception.Message)" }
