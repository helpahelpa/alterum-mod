# Runs Alterum for mod testing WITHOUT disturbing the user:
#   1. remember which window has focus (helper task in session 1)
#   2. start the game (interactive session)
#   3. the mod itself parks its window off-screen and keeps running unfocused
#   4. give focus back to the window remembered in step 1
#
# Runs from the SSH (session 0) shell; helper work is delegated to interactive tasks.
#
# TARGET: the PINNED install C:\AlterumPinned (see pinned/UPDATE-POLICY.md).
# Override with -GameDir 'C:\Program Files (x86)\Steam\steamapps\common\Alterum' to run the live
# Steam copy - only to inspect a new game build before re-pinning.
param(
    [string]$GameDir    = "C:\AlterumPinned",
    [string]$GameArgs   = "-screen-fullscreen 0 -screen-width 640 -screen-height 360",
    [float] $RestoreAfterSeconds = 10,
    [string]$RunAs      = [Environment]::UserName,
    [string]$TaskPrefix = "AlterumTest"
)
$ErrorActionPreference = "Stop"
$tmp = "C:\Windows\Temp\alterum"

Get-Process -Name Alterum -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

$svc  = New-Object -ComObject Schedule.Service
$svc.Connect()
$root = $svc.GetFolder("\")

function Invoke-InteractiveTask([string]$name, [string]$exe, [string]$arguments, [string]$workdir, [int]$waitSeconds = 0) {
    $task = $svc.NewTask(0)
    $task.Settings.ExecutionTimeLimit = "PT10M"
    $task.Settings.DisallowStartIfOnBatteries = $false
    $task.Settings.StopIfGoingOnBatteries = $false
    $task.Settings.MultipleInstances = 2
    $a = $task.Actions.Create(0)
    $a.Path = $exe
    $a.Arguments = $arguments
    if ($workdir) { $a.WorkingDirectory = $workdir }
    $p = $task.Principal
    $p.UserId = $RunAs
    $p.LogonType = 3          # interactive token
    $p.RunLevel = 1
    $p.GroupId = ""
    $root.RegisterTaskDefinition($name, $task, 6, $null, $null, 3) | Out-Null
    $root.GetTask($name).Run($null) | Out-Null
    if ($waitSeconds -gt 0) { Start-Sleep -Seconds $waitSeconds }
}

# 1) remember the focused window
Remove-Item "$tmp\fg.json" -ErrorAction SilentlyContinue
Invoke-InteractiveTask "$TaskPrefix-SaveFg" "powershell.exe" `
    "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$tmp\save-fg.ps1`"" $null 4
$fg = if (Test-Path "$tmp\fg.json") { (Get-Content "$tmp\fg.json" -Raw).Trim() } else { "<none>" }
Write-Output "foreground saved: $fg"

# 2) start the game
Invoke-InteractiveTask "$TaskPrefix-Game" "$GameDir\Alterum.exe" "$GameArgs -alterumDevMode" $GameDir 0
Write-Output "game started"

# 3) give the mod a moment to move its window away, then restore focus
Start-Sleep -Seconds $RestoreAfterSeconds
Invoke-InteractiveTask "$TaskPrefix-RestoreFg" "powershell.exe" `
    "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$tmp\restore-fg.ps1`"" $null 3

$p = Get-Process -Name Alterum -ErrorAction SilentlyContinue
if ($p) { Write-Output ("Alterum running: pid={0} session={1}" -f $p[0].Id, $p[0].SessionId) }
else    { Write-Output "WARNING: Alterum not running" }
