# Run an arbitrary PowerShell script in the interactive session and wait for it.
param(
    [Parameter(Mandatory=$true)][string]$Script,
    [string]$RunAs = [Environment]::UserName,
    [string]$TaskName = ""
)
if (-not $TaskName) { $TaskName = "AlterumHelper_" + (Get-Date -Format "HHmmssfff") }
$svc = New-Object -ComObject Schedule.Service; $svc.Connect(); $root = $svc.GetFolder("\")
$task = $svc.NewTask(0)
$task.Settings.ExecutionTimeLimit = "PT5M"
$task.Settings.DisallowStartIfOnBatteries = $false
$task.Settings.StopIfGoingOnBatteries = $false
$task.Settings.MultipleInstances = 2
$a = $task.Actions.Create(0)
$a.Path = "powershell.exe"
$a.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$Script`""
$p = $task.Principal; $p.UserId = $RunAs; $p.LogonType = 3; $p.RunLevel = 1; $p.GroupId = ""
$root.RegisterTaskDefinition($TaskName, $task, 6, $null, $null, 3) | Out-Null
$root.GetTask($TaskName).Run($null) | Out-Null
Write-Output "started $TaskName"
