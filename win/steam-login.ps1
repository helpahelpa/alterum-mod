# Open an INTERACTIVE SteamCMD login console in the user's desktop session (session 1).
#
# SSH runs in session 0, which has no desktop: a console started from here is invisible to the
# user and cannot be typed into. So this uses the same Task Scheduler / interactive-token trick as
# win/run-game.ps1 and win/run-helper.ps1, except the task is started WITHOUT -WindowStyle Hidden
# so the console window is actually visible on the user's desktop.
#
#   ssh windows 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\Temp\steam-login.ps1'
#
# The window runs `steamcmd.exe +login` with no arguments, so it prompts for the account name and
# password. It is never given a password, never logs one, and never stores one. The account name
# is read from the installed Steam client's loginusers.vdf and printed only as a hint.
#
# Writes C:\Windows\Temp\alterum\steam-login.json from inside session 1 (session 0 cannot see
# session-1 windows, so the window's own pid/title/session has to be reported by the window).
param(
    [switch]$InSession,                 # set by the scheduled task; means "we are in session 1"
    [string]$RunAs = [Environment]::UserName,
    [string]$TaskName = 'AlterumSteamLogin',
    [string]$SteamCmd = 'C:\steamcmd\steamcmd.exe',
    [string]$HelperCopy = 'C:\Temp\steam-login.ps1',
    [string]$ReportPath = 'C:\Windows\Temp\alterum\steam-login.json',
    [int]$WaitSeconds = 60,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'

$Vdf = 'C:\Program Files (x86)\Steam\config\loginusers.vdf'
$Title = 'SteamCMD login - Alterum'

# --- account-name hint, read from the installed client (never a password) -------------------
function Get-SteamAccountName([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $txt = Get-Content -Raw -LiteralPath $path
    $best = $null
    foreach ($m in [regex]::Matches($txt, '"(\d{17})"\s*\{(?<body>[^}]*)\}')) {
        $body = $m.Groups['body'].Value
        $acct = [regex]::Match($body, '"AccountName"\s*"([^"]*)"').Groups[1].Value
        if (-not $acct) { continue }
        $ts   = [regex]::Match($body, '"timestamp"\s*"(\d+)"').Groups[1].Value
        $auto = [regex]::Match($body, '"AutoLogin"\s*"(\d)"').Groups[1].Value
        $rec  = [pscustomobject]@{
            AccountName = $acct
            Timestamp   = if ($ts) { [int64]$ts } else { 0 }
            AutoLogin   = ($auto -eq '1')
        }
        # prefer AutoLogin, then the most recent login
        if (-not $best -or ($rec.AutoLogin -and -not $best.AutoLogin) -or
            (($rec.AutoLogin -eq $best.AutoLogin) -and $rec.Timestamp -gt $best.Timestamp)) {
            $best = $rec
        }
    }
    return $best
}

# --- session 0 (SSH): re-launch ourselves in session 1 with a visible console ---------------
if (-not $InSession) {
    $sid = (Get-Process -Id $PID).SessionId
    if ($sid -ne 0) {
        # Already on a desktop session - just run the interactive part in place.
        $InSession = $true
    } else {
        if (-not (Test-Path -LiteralPath $SteamCmd)) { throw "SteamCMD not found at $SteamCmd" }
        if (-not (Test-Path 'C:\Temp')) { New-Item -ItemType Directory -Path 'C:\Temp' -Force | Out-Null }
        if ($PSCommandPath -and ($PSCommandPath -ne $HelperCopy)) {
            Copy-Item -LiteralPath $PSCommandPath -Destination $HelperCopy -Force
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null
        if (Test-Path -LiteralPath $ReportPath) { Remove-Item -LiteralPath $ReportPath -Force }

        $svc  = New-Object -ComObject Schedule.Service
        $svc.Connect()
        $root = $svc.GetFolder('\')

        $task = $svc.NewTask(0)
        $task.RegistrationInfo.Description = 'Interactive SteamCMD login console (visible window)'
        # Unlimited: this window waits for a human to type credentials, so it must not be killed.
        $task.Settings.ExecutionTimeLimit         = 'PT0S'
        $task.Settings.DisallowStartIfOnBatteries = $false
        $task.Settings.StopIfGoingOnBatteries     = $false
        $task.Settings.AllowHardTerminate         = $false
        $task.Settings.MultipleInstances          = 2      # IGNORE_NEW
        $task.Settings.Hidden                     = $false  # keep the console window VISIBLE

        $action = $task.Actions.Create(0)
        $action.Path             = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
        # No -WindowStyle Hidden, and -NoExit so the window survives steamcmd exiting.
        $action.Arguments        = "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$HelperCopy`" -InSession"
        $action.WorkingDirectory = 'C:\Temp'

        $principal           = $task.Principal
        $principal.UserId    = $RunAs
        $principal.LogonType = 3    # TASK_LOGON_INTERACTIVE_TOKEN
        $principal.RunLevel  = 1
        $principal.GroupId   = ''

        # 6 = CREATE_OR_UPDATE, logonType 3 = interactive token (runs inside session 1)
        $root.RegisterTaskDefinition($TaskName, $task, 6, $null, $null, 3) | Out-Null
        $root.GetTask($TaskName).Run($null) | Out-Null
        Write-Output "task '$TaskName' started as $RunAs (interactive token, visible window)"

        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        $rep = $null
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400
            if (Test-Path -LiteralPath $ReportPath) {
                try { $rep = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json } catch { $rep = $null }
                if ($rep -and $rep.steamcmdPid) { break }
            }
        }
        if (-not $rep) { Write-Output "WARNING: no report from session 1 within ${WaitSeconds}s"; exit 1 }
        Write-Output ($rep | ConvertTo-Json -Depth 5)
        exit 0
    }
}

# --- session 1: the actual visible console --------------------------------------------------
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Con {
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern uint GetConsoleProcessList(uint[] list, uint count);
}
"@

try { [Console]::Title = $Title } catch { }
try { $Host.UI.RawUI.WindowTitle = $Title } catch { }

$acct = Get-SteamAccountName $Vdf

Write-Host ''
Write-Host '  ============================================================' -ForegroundColor DarkGray
Write-Host '   SteamCMD interactive login' -ForegroundColor White
Write-Host '  ============================================================' -ForegroundColor DarkGray
Write-Host ''
if ($acct) {
    Write-Host ("   Your account appears to be: {0}" -f $acct.AccountName) -ForegroundColor Cyan
    Write-Host '   (read from the Steam client loginusers.vdf - hint only)' -ForegroundColor DarkGray
} else {
    Write-Host '   Could not read a saved account name from loginusers.vdf.' -ForegroundColor Yellow
}
Write-Host ''
Write-Host '   Type the password, and a Steam Guard code if prompted,' -ForegroundColor Gray
Write-Host '   directly into THIS window. Nothing is logged or stored.' -ForegroundColor Gray
Write-Host ''

if (-not (Test-Path -LiteralPath $SteamCmd)) {
    Write-Host ("   ERROR: {0} not found." -f $SteamCmd) -ForegroundColor Red
    if (-not $NoPause) { [void](Read-Host 'Press Enter to close') }
    exit 1
}

$started = Get-Date
$proc = Start-Process -FilePath $SteamCmd -ArgumentList '+login' `
        -WorkingDirectory (Split-Path -Parent $SteamCmd) -NoNewWindow -PassThru
Start-Sleep -Milliseconds 1200

# Report the window from inside session 1 - session 0 cannot enumerate session-1 windows.
$hwnd = [Con]::GetConsoleWindow()
$sb = New-Object System.Text.StringBuilder 512
[void][Con]::GetWindowText($hwnd, $sb, $sb.Capacity)
$consoleTitle = $sb.ToString()
$buf = New-Object uint32[] 64
$n = [Con]::GetConsoleProcessList($buf, 64)
$consolePids = @()
for ($i = 0; $i -lt $n; $i++) { $consolePids += [int]$buf[$i] }

$sp = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
$report = [ordered]@{
    generatedAt    = (Get-Date).ToString('o')
    accountHint    = if ($acct) { $acct.AccountName } else { $null }
    steamcmdPid    = $proc.Id
    steamcmdExe    = $SteamCmd
    steamcmdArgs   = '+login'
    steamcmdSession= if ($sp) { $sp.SessionId } else { $null }
    helperPid      = $PID
    helperSession  = (Get-Process -Id $PID).SessionId
    consoleHwnd    = $hwnd.ToInt64()
    consoleTitle   = $consoleTitle
    consoleVisible = [Con]::IsWindowVisible($hwnd)
    consolePids    = $consolePids
}
New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8

$proc.WaitForExit()

$report.exitedAt = (Get-Date).ToString('o')
$report.exitCode = $proc.ExitCode
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8

Write-Host ''
Write-Host ("   steamcmd exited (code {0}) after {1:N0}s." -f $proc.ExitCode, ((Get-Date) - $started).TotalSeconds) -ForegroundColor Yellow
if (-not $NoPause) {
    Write-Host '   Window stays open. Press Enter to close it, or just close the window.' -ForegroundColor DarkGray
    [void](Read-Host)
}
