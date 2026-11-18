$log = 'C:\AlterumB\BepInEx\LogOutput.log'
for ($i=1; $i -le 40; $i++) {
  $procs = Get-Process -Name Alterum -EA SilentlyContinue | Where-Object { $_.Path -eq 'C:\AlterumB\Alterum.exe' }
  $pl = if ($procs) { ($procs | ForEach-Object { "pid=$($_.Id) session=$($_.SessionId)" }) -join ',' } else { 'none' }
  $c = if (Test-Path $log) { Get-Content $log -Raw -EA SilentlyContinue } else { '' }
  $lobby = $c -match "\[scene\] -> 'Lobby'"
  $hot = $c -match '\[hot\] generation'
  "t+{0,2}s Bprocs={1,-22} logBytes={2,-8} lobby={3,-5} hot={4}" -f $i,$pl,$c.Length,$lobby,$hot
  if ($lobby -and $hot) { "READY"; break }
  Start-Sleep -Seconds 1
}
"--- markers ---"
Select-String -Path $log -Pattern '\[hot\] generation|\[scene\] -> ''Lobby''|Private|error CS|Exception' -EA SilentlyContinue |
  Select-Object -Last 12 | ForEach-Object { "  " + $_.Line }
