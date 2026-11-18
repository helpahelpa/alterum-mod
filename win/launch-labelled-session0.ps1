# Session-0 (SSH) entry point for a labelled Alterum launch.
#
# It validates the target, schedules win/launch-labelled.ps1 in the interactive session (session 1)
# through Task Scheduler - session 0 has no desktop and cannot enumerate session 1's windows - and
# then waits for the TOKEN-SPECIFIC result file that the worker writes.
#
# Success is only ever the worker's exit code 0. "The task started" and "an Alterum process exists"
# are explicitly NOT success: if the result file never appears, this wrapper exits 78.
#
# Exit codes (the worker's own, propagated unchanged):
#   0  verified        - the window title matched the guard's expected title exactly
#   64 invalid target or arguments; nothing was scheduled
#   70 deployment preflight failure, or this wrapper's own deployment is incomplete
#   78 guard rejection, mismatch, timeout, premature exit, or no result file at all
#
# Do not route run-game.ps1 / run-second.ps1 / mp-test.sh / two.sh through this yet - that is a
# separate work item. This file is deliberately standalone.
param(
    [string]$GameDir = "C:\AlterumPinned",
    [string]$Target = "",
    [string]$ExtraArgs = "-screen-fullscreen 0 -screen-width 640 -screen-height 360",
    [string]$Instance = "",
    [switch]$SteamLaunch,
    [string]$WorkerPath = "",
    [string]$ResultDir = "",
    [string]$EvidenceDir = "",
    [string]$ManifestPath = "",
    [switch]$AllowManifestlessPreflight,
    [string]$RunAs = [Environment]::UserName,
    [string]$TaskPrefix = "AlterumLabelled",
    [int]$WaitSeconds = 180,
    [int]$TimeoutSeconds = 30,
    [int]$EvidenceMaxAgeSeconds = 10,
    [switch]$DryRun,
    [int]$DryRunPid = 0
)
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "instance-label-common.ps1")

function Say {
    param([string]$Message)
    Write-Output $Message
}

if ([string]::IsNullOrWhiteSpace($ResultDir)) { $ResultDir = $script:InstanceLabelDefaultResultDir }
if ([string]::IsNullOrWhiteSpace($WorkerPath)) { $WorkerPath = Join-Path $PSScriptRoot "launch-labelled.ps1" }
if ($WaitSeconds -lt 10) { $WaitSeconds = 10 }

function Complete-Wrapper {
    param([int]$Code, [string]$Reason)
    Say ("exit={0}" -f $Code)
    Say ("reason={0}" -f $Reason)
    exit $Code
}

# ---------------------------------------------------------------- 1. validate before scheduling anything

if ($SteamLaunch) {
    Complete-Wrapper 64 "steam-launch-rejected: a Steam launch cannot be labelled or verified; only the two approved installs may be launched"
}

$argsProblem = Test-InstanceLabelArgString $ExtraArgs
if ($argsProblem) { Complete-Wrapper 64 $argsProblem }

$resolution = Resolve-InstanceLabelTarget -GameDir $GameDir -Target $Target
if (-not $resolution.Ok) { Complete-Wrapper $resolution.Code $resolution.Reason }

# 'main' is reserved to the pinned install and 'b' to the B install: the same rule the in-process
# guard applies, decided here so a bad argument exits 64 before anything is scheduled.
$reservedProblem = Test-InstanceLabelReservedTag -Tag $Instance -InstallationTag $resolution.Tag
if ($reservedProblem) { Complete-Wrapper 64 $reservedProblem }

# ---------------------------------------------------------------- 2. this wrapper's own deployment

foreach ($needed in @($WorkerPath, (Join-Path (Split-Path -Parent $WorkerPath) "instance-label-common.ps1"))) {
    if (-not (Test-Path -LiteralPath $needed -PathType Leaf)) {
        Complete-Wrapper 70 ("worker-deployment-missing: '$needed' is not present; copy win/launch-labelled.ps1 and win/instance-label-common.ps1 next to each other on the Windows box")
    }
}

$token = [guid]::NewGuid().ToString("D")
$resultName = Get-InstanceLabelResultFileName $token
$resultPath = Join-Path $ResultDir ($resultName + ".json")
$logPath = Join-Path $ResultDir ($resultName + ".log")
if (Test-Path -LiteralPath $resultPath) {
    # A fresh GUID should never collide; if it does, do not let a stale file answer for this launch.
    Complete-Wrapper 78 ("stale-result-file: '$resultPath' already exists, refusing to trust it")
}

Say ("target={0}" -f $resolution.ExePath)
Say ("label='{0}'" -f $resolution.Label)
Say ("token={0}" -f $token)
Say ("worker={0}" -f $WorkerPath)

# ---------------------------------------------------------------- 3. schedule the worker in session 1

function Quote-WorkerArg {
    param([string]$Value)
    return ('"' + ($Value -replace '"', "") + '"')
}

$workerArgs = @(
    "-File " + (Quote-WorkerArg $WorkerPath),
    "-GameDir " + (Quote-WorkerArg $resolution.Install),
    "-ExtraArgs " + (Quote-WorkerArg $ExtraArgs),
    "-Token " + (Quote-WorkerArg $token),
    "-ResultDir " + (Quote-WorkerArg $ResultDir),
    "-TimeoutSeconds " + $TimeoutSeconds,
    "-EvidenceMaxAgeSeconds " + $EvidenceMaxAgeSeconds,
    "-Quiet"
)
if ($Instance) { $workerArgs += "-Instance " + (Quote-WorkerArg $Instance) }
if ($EvidenceDir) { $workerArgs += "-EvidenceDir " + (Quote-WorkerArg $EvidenceDir) }
if ($ManifestPath) { $workerArgs += "-ManifestPath " + (Quote-WorkerArg $ManifestPath) }
if ($AllowManifestlessPreflight) { $workerArgs += "-AllowManifestlessPreflight" }
if ($DryRun) { $workerArgs += "-DryRun" }
if ($DryRunPid -gt 0) { $workerArgs += "-DryRunPid " + $DryRunPid }

$taskName = "$TaskPrefix-$($resolution.Kind)"
$svc = New-Object -ComObject Schedule.Service
$svc.Connect()
$root = $svc.GetFolder("\")

$task = $svc.NewTask(0)
$task.RegistrationInfo.Description = "Launch one labelled Alterum instance in the interactive session"
$task.Settings.ExecutionTimeLimit = "PT10M"
$task.Settings.DisallowStartIfOnBatteries = $false
$task.Settings.StopIfGoingOnBatteries = $false
$task.Settings.AllowHardTerminate = $true
$task.Settings.MultipleInstances = 2   # ignore new: a concurrent same-install launch fails to 78, never double-launches

$action = $task.Actions.Create(0)
$action.Path = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
$action.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " + ($workerArgs -join " ")
$action.WorkingDirectory = Split-Path -Parent $WorkerPath

$principal = $task.Principal
$principal.UserId = $RunAs
$principal.LogonType = 3               # interactive token
$principal.RunLevel = 1
$principal.GroupId = ""

try {
    $existing = $root.GetTask($taskName)
    if ($existing.State -eq 4) {
        Complete-Wrapper 78 ("worker-already-running: task '$taskName' is still running from an earlier launch; " +
            "this launch was NOT scheduled, so nothing is verified")
    }
} catch { }

try {
    $root.RegisterTaskDefinition($taskName, $task, 6, $null, $null, 3) | Out-Null
    $root.GetTask($taskName).Run($null) | Out-Null
} catch {
    Complete-Wrapper 78 ("task-schedule-failed: " + $_.Exception.Message + " (nothing verified; the game may or may not have started - do not assume either)")
}

Say ("task={0} started as {1} (interactive token)" -f $taskName, $RunAs)

# ---------------------------------------------------------------- 4. wait for THIS token's result file

$deadline = (Get-Date).AddSeconds($WaitSeconds)
$result = $null
$lastResult = $null
while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
        try {
            $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        } catch {
            $result = $null           # torn read should be impossible (atomic move), so retry
        }
        if ($result) { break }
    }
    try { $lastResult = $root.GetTask($taskName).LastTaskResult } catch { }
    Start-Sleep -Milliseconds 250
}

if (-not $result) {
    Say ("task={0} lastTaskResult={1}" -f $taskName, $lastResult)
    Say "result-file=none"
    Complete-Wrapper 78 ("no-result-file: no token-specific result appeared at '$resultPath' within $WaitSeconds s; " +
        "the launch is UNVERIFIED - do not treat a running process or a started task as success")
}

if ($null -ne $result.token -and ([string]$result.token -ine $token)) {
    Complete-Wrapper 78 ("result-token-mismatch: '" + $result.token + "' is not this launch's token '" + $token + "'")
}

$code = 78
if ($null -ne $result.exitCode) {
    $parsed = 0
    if ([int]::TryParse([string]$result.exitCode, [ref]$parsed)) { $code = $parsed }
}
if (@(0, 64, 70, 78) -notcontains $code) {
    Say ("unexpected-exit-code={0}; treating as 78" -f $result.exitCode)
    $code = 78
}

Say ("pid={0}" -f $result.pid)
Say ("workerSession={0}" -f $result.workerSession)
if ($result.effectiveTag) { Say ("effectiveTag={0}" -f $result.effectiveTag) }
if ($result.expectedTitle) { Say ("expectedTitle={0}" -f $result.expectedTitle) }
if (@($result.observedTitles).Count -gt 0) {
    Say ("observedTitles=" + ((@($result.observedTitles) | ForEach-Object { "'" + $_ + "'" }) -join ", "))
} else {
    Say "observedTitles=none"
}
if ($result.evidencePath) { Say ("evidence={0} state={1}" -f $result.evidencePath, $result.evidenceState) }
if ($result.cleanup) { Say ("cleanup={0}: {1}" -f $result.cleanup.status, $result.cleanup.detail) }
Say ("result={0}" -f $resultPath)

if ($code -ne 0 -and (Test-Path -LiteralPath $logPath -PathType Leaf)) {
    Say "--- worker log (tail) ---"
    foreach ($line in @(Get-Content -LiteralPath $logPath -Tail 8 -ErrorAction SilentlyContinue)) { Say $line }
}

Complete-Wrapper $code ([string]$result.reason)
