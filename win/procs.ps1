$ErrorActionPreference = "SilentlyContinue"
$p = Get-Process -Name Alterum
Write-Output ("count=" + ($p | Measure-Object).Count)
foreach ($x in $p) {
    $path = $null
    try { $path = $x.Path } catch { }
    $cmd = $null
    try { $cmd = (Get-CimInstance Win32_Process -Filter ("ProcessId=" + $x.Id)).CommandLine } catch { }
    Write-Output ("pid={0} session={1} cpu={2:N0}s ws={3:N0}MB start={4:HH:mm:ss} path='{5}' cmd='{6}'" -f `
        $x.Id, $x.SessionId, $x.CPU, ($x.WorkingSet64/1MB), $x.StartTime, $path, $cmd)
}
