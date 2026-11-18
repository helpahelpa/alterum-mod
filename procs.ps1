"--- Alterum processes ---"
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'Alterum|steam' } |
  ForEach-Object { "{0}`tpid={1}`t{2}" -f $_.ProcessName, $_.Id, $_.Path }
"--- pids of interest ---"
foreach ($id in 29384) {
  $p = Get-Process -Id $id -ErrorAction SilentlyContinue
  if ($p) { "pid $id RUNNING: $($p.ProcessName) $($p.Path)" } else { "pid $id not running" }
}
