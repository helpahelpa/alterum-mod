# Run an arbitrary PowerShell script inside the interactive desktop session (session 1).
# Session 0 (SSH) has no desktop, so window APIs fail there. Same scheduled-task
# trick win/run-game.ps1 uses for the game itself.
param(
    [Parameter(Mandatory=$true)][string]$Script,
    [string]$TaskName = "AlterumHelper_session1",
    [string]$RunAs = [Environment]::UserName,
    [int]$WaitSeconds = 20
)
$ErrorActionPreference = "Stop"

$svc  = New-Object -ComObject Schedule.Service
$svc.Connect()
$root = $svc.GetFolder("\")

$task = $svc.NewTask(0)
$task.RegistrationInfo.Description = "Run a script in the interactive session"
$task.Settings.ExecutionTimeLimit      = "PT5M"
$task.Settings.DisallowStartIfOnBatteries = $false
$task.Settings.StopIfGoingOnBatteries  = $false
$task.Settings.AllowHardTerminate      = $true
$task.Settings.MultipleInstances       = 2

$action = $task.Actions.Create(0)
$action.Path            = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
$action.Arguments       = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Script`""
$action.WorkingDirectory = Split-Path -Parent $Script

$principal            = $task.Principal
$principal.UserId     = $RunAs
$principal.LogonType  = 3
$principal.RunLevel   = 1
$principal.GroupId    = ""

$root.RegisterTaskDefinition($TaskName, $task, 6, $null, $null, 3) | Out-Null
$t = $root.GetTask($TaskName)
$t.Run($null) | Out-Null
Write-Output "task '$TaskName' started as $RunAs"

Start-Sleep -Seconds $WaitSeconds
$t2 = $root.GetTask($TaskName)   # re-fetch; the running instance exposes no Refresh()
Write-Output ("last result: {0}" -f $t2.LastTaskResult)
