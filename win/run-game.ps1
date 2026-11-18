# Launch Alterum in the interactive desktop session (session 1) via Task Scheduler COM.
# Session 0 (SSH) has no D3D desktop, so the game dies there.
#
# 640x360 by default (see alterum/LAUNCH-POLICY.md): every instance runs low-res and the mod
# caps it at 15 fps. Pass -ExtraArgs to override for a one-off capture that needs more pixels.
#
# TARGET: the PINNED install C:\AlterumPinned (frozen on Steam buildid 26597574, see
# pinned/UPDATE-POLICY.md). Mod work targets the pin, never the Steam install.
# "A" (the long-running main instance) is the pin. To run the live Steam copy instead - only to
# look at a new game build - pass the old path explicitly:
#   -GameDir 'C:\Program Files (x86)\Steam\steamapps\common\Alterum'
param(
    [string]$ExtraArgs = "-screen-fullscreen 0 -screen-width 640 -screen-height 360",
    [switch]$SteamLaunch,
    [string]$GameDir = "C:\AlterumPinned",
    [string]$TaskName = "AlterumInteractive",
    [string]$RunAs = [Environment]::UserName
)
$ErrorActionPreference = "Stop"

Get-Process -Name Alterum -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq (Join-Path $GameDir "Alterum.exe") } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$svc  = New-Object -ComObject Schedule.Service
$svc.Connect()
$root = $svc.GetFolder("\")

$task = $svc.NewTask(0)
$task.RegistrationInfo.Description = "Launch Alterum in the interactive session (mod testing)"

# No execution time limit: the default PT3H silently TERMINATES the game exactly
# three hours after launch (Task Scheduler kills the process; Unity gets no
# shutdown callback, leaving no Application-quit log line). That looked like a
# spontaneous crash four times. PT0S = unlimited, and hard-terminate off.
$task.Settings.ExecutionTimeLimit      = "PT0S"
$task.Settings.DisallowStartIfOnBatteries = $false
$task.Settings.StopIfGoingOnBatteries  = $false
$task.Settings.AllowHardTerminate      = $false
$task.Settings.MultipleInstances       = 2   # TASK_INSTANCES_IGNORE_NEW

if ($SteamLaunch) {
    $exe = "C:\Program Files (x86)\Steam\steam.exe"
    $args = "steam://rungameid/4424420"
    $wd  = "C:\Program Files (x86)\Steam"
} else {
    $exe = Join-Path $GameDir "Alterum.exe"
    $args = $ExtraArgs
    $wd  = $GameDir
}

$action = $task.Actions.Create(0)   # TASK_ACTION_EXEC
$action.Path            = $exe
$action.Arguments       = $args
$action.WorkingDirectory = $wd

$principal            = $task.Principal
$principal.UserId     = $RunAs
$principal.LogonType  = 3   # TASK_LOGON_INTERACTIVE_TOKEN
$principal.RunLevel   = 1   # highest available
$principal.GroupId    = ""

# 6 = TASK_CREATE_OR_UPDATE ; logonType 3 = interactive token
$root.RegisterTaskDefinition($TaskName, $task, 6, $null, $null, 3) | Out-Null
$t = $root.GetTask($TaskName)
$t.Run($null) | Out-Null
Write-Output "task '$TaskName' started as $RunAs (interactive token)"

for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Name Alterum -ErrorAction SilentlyContinue
    if ($p) {
        Write-Output ("Alterum running: pid={0} session={1}" -f $p[0].Id, $p[0].SessionId)
        exit 0
    }
    Start-Sleep -Milliseconds 700
}
Write-Output "WARNING: Alterum.exe not observed after ~21s"
