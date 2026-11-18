# Start one Alterum instance from a named install without touching other processes.
# Used for the qc console/overlay work against C:\AlterumDev. Never stops pid 29384 (the user's run).
param(
    [string]$GameDir = "C:\AlterumDev",
    [string]$Instance = "qc",
    [string]$Args = "-screen-fullscreen 0 -screen-width 1280 -screen-height 720"
)
$ErrorActionPreference = "Stop"
$exe = Join-Path $GameDir "Alterum.exe"
if (-not (Test-Path $exe)) { throw "no exe at $exe" }
$running = Get-Process -Name Alterum -ErrorAction SilentlyContinue
if ($running) { Write-Output ("existing Alterum pids: " + (($running | ForEach-Object { $_.Id }) -join ",")) }
$p = Start-Process -FilePath $exe -ArgumentList ("-alterumInstance=$Instance " + $Args) -WorkingDirectory $GameDir -PassThru
Write-Output ("started pid={0} instance={1} dir={2}" -f $p.Id, $Instance, $GameDir)
