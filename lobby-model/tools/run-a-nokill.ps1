# Launch Alterum in the interactive desktop session (session 1) via Task Scheduler COM.
# Session 0 (SSH) has no D3D desktop, so the game dies there.
#
# TARGET: the PINNED install C:\AlterumPinned (see pinned/UPDATE-POLICY.md). This is
# the "no kill" variant - it never stops an existing instance, it just adds one.
# Override with -GameDir 'C:\Program Files (x86)\Steam\steamapps\common\Alterum'.
param(
    [string]$ExtraArgs = "-screen-fullscreen 0 -screen-width 950 -screen-height 530",
    [switch]$SteamLaunch,
    [string]$GameDir = "C:\AlterumPinned",
    [string]$TaskName = "AlterumInteractive",
    [string]$RunAs = [Environment]::UserName
)
$ErrorActionPreference = "Stop"

# (no kill: other instances must survive)
Start-Sleep -Milliseconds 500

$svc  = New-Object -ComObject Schedule.Service
$svc.Connect()
$root = $svc.GetFolder("\")

$task = $svc.NewTask(0)
$task.RegistrationInfo.Description = "Launch Alterum in the interactive session (mod testing)"

$task.Settings.ExecutionTimeLimit      = "PT3H"
$task.Settings.DisallowStartIfOnBatteries = $false
$task.Settings.StopIfGoingOnBatteries  = $false
$task.Settings.AllowHardTerminate      = $true
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
