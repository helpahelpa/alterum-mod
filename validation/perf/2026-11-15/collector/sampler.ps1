# External process sampler: the one collector that runs for every arm. 2 Hz CPU / memory.
param([string]$Arm,[string]$Tag,[int]$Seconds=300,[int]$Hz=2)
$out="C:\Windows\Temp\alterum\perf\raw\sampler_$($Arm)_$Tag.csv"
"t_s,pid,cpu_s,ws_mb,private_mb,threads,handles" | Set-Content $out -Encoding ascii
$sw=[Diagnostics.Stopwatch]::StartNew()
$p = Get-Process -Name Alterum -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "C:\AlterumPerf\$Arm\Alterum.exe" } | Select-Object -First 1
if (-not $p) { "no process for arm $Arm"; exit 1 }
$pid0=$p.Id
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
  try {
    $q = Get-Process -Id $pid0 -ErrorAction Stop
    $cpu = $q.TotalProcessorTime.TotalSeconds
    '{0:F2},{1},{2:F3},{3:F1},{4:F1},{5},{6}' -f $sw.Elapsed.TotalSeconds,$pid0,$cpu,($q.WorkingSet64/1MB),($q.PrivateMemorySize64/1MB),$q.Threads.Count,$q.HandleCount | Add-Content $out -Encoding ascii
  } catch { break }
  Start-Sleep -Milliseconds (1000/$Hz)
}
"sampler done: $out"
