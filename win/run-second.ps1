# Launch an ADDITIONAL Alterum instance without touching the one already running.
# 640x360 by default (see alterum/LAUNCH-POLICY.md).
# TARGET: the PINNED install C:\AlterumPinned (see pinned/UPDATE-POLICY.md); pass
# -GameDir 'C:\Program Files (x86)\Steam\steamapps\common\Alterum' to run the live Steam copy.
param(
    [string]$GameArgs = "-screen-fullscreen 0 -screen-width 640 -screen-height 360",
    [string]$GameDir  = "C:\AlterumPinned",
    [string]$RunAs    = [Environment]::UserName,
    [string]$TaskName = ""
)
$ErrorActionPreference = "Stop"
if (-not $TaskName) { $TaskName = "AlterumSecond_" + (Get-Date -Format "HHmmss") }
$before = @(Get-Process -Name Alterum -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

$svc  = New-Object -ComObject Schedule.Service
$svc.Connect()
$root = $svc.GetFolder("\")
$task = $svc.NewTask(0)
# PT0S = unlimited. The old PT3H silently killed the client at exactly 3 h and
# looked like a spontaneous crash; see run-game.ps1 for the same fix.
$task.Settings.ExecutionTimeLimit = "PT0S"
$task.Settings.DisallowStartIfOnBatteries = $false
$task.Settings.StopIfGoingOnBatteries = $false
$task.Settings.AllowHardTerminate = $false
$task.Settings.MultipleInstances = 2
$a = $task.Actions.Create(0)
$a.Path = "$GameDir\Alterum.exe"
$a.Arguments = "$GameArgs -alterumDevMode"
$a.WorkingDirectory = $GameDir
$p = $task.Principal
$p.UserId = $RunAs; $p.LogonType = 3; $p.RunLevel = 1; $p.GroupId = ""
$root.RegisterTaskDefinition($TaskName, $task, 6, $null, $null, 3) | Out-Null
$root.GetTask($TaskName).Run($null) | Out-Null

for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 700
    $now = @(Get-Process -Name Alterum -ErrorAction SilentlyContinue)
    $new = @($now | Where-Object { $before -notcontains $_.Id })
    if ($new.Count -gt 0) {
        Write-Output ("new instance pid={0} session={1}; total running={2}" -f $new[0].Id, $new[0].SessionId, $now.Count)
        exit 0
    }
}
Write-Output "WARNING: no new instance appeared"
