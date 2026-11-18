# Launch one approved Alterum install IN THE INTERACTIVE SESSION and prove it is labelled.
#
# Runs inside session 1 (the desktop session) - it owns the process handle, enumerates the
# window it just created and reads the title itself. Session 0 cannot do any of that; the
# session-0 entry point is win/launch-labelled-session0.ps1.
#
# Success (exit 0) means ALL of the following agreed, not that "a process started":
#   * the target is one of the two approved executables,
#   * the deployment preflight passed (manifest hashes, or an explicit presence+hash report),
#   * in-process guard evidence exists for THIS launch: same pid, same exe path, same process
#     start time, same -alterumLabelLaunch token, protocol 1, state "verified", fresh,
#   * the guard's expected title is exactly "<label> [instance=<tag>; pid=<pid>]" with the label
#     this install must carry,
#   * the worker's OWN enumeration of that pid's window in this session returns exactly that title.
#
# Exit codes: 0 verified / 64 invalid target or arguments (nothing launched) / 70 deployment
# preflight failure (nothing launched) / 78 guard rejection, mismatch, timeout or premature exit.
# On failure after launch the worker terminates ONLY the process it started, after reconfirming
# pid + exact path + start time, and says so in the result file. It never touches other instances.
#
# The in-process guard is not deployed yet: expect 78 "no-evidence" today. That is the designed
# answer, not a bug, and it is the reason this script never falls back to "a process exists".
#
# -DryRun exercises the same gates without starting anything (it uses -DryRunPid as the simulated
# pid): the self-test uses it to prove the 78 path in session 1.
param(
    [string]$GameDir = "C:\AlterumPinned",
    [string]$Target = "",
    [string]$ExtraArgs = "-screen-fullscreen 0 -screen-width 640 -screen-height 360",
    [string]$Instance = "",
    [string]$Token = "",
    [switch]$SteamLaunch,
    [string]$ResultDir = "",
    [string]$EvidenceDir = "",
    [string]$ManifestPath = "",
    [switch]$AllowManifestlessPreflight,
    [int]$TimeoutSeconds = 30,
    [int]$EvidenceMaxAgeSeconds = 10,
    [int]$PollMilliseconds = 300,
    [switch]$DryRun,
    [int]$DryRunPid = 0,
    [switch]$Quiet
)
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "instance-label-common.ps1")

$script:Notes = New-Object System.Collections.ArrayList
# Capture the parameter before touching $script:Token: at script scope they are the same variable,
# and assigning it first would silently discard the caller's token.
$requestedToken = ""
if ($Token) { $requestedToken = $Token.Trim() }
$script:Token = ""
$script:TokenValid = $true
$script:ResultPath = $null
$script:LogPath = $null
$script:StartedUtc = (Get-Date).ToUniversalTime()

# Token first: it names the result file a session-0 caller polls for.
$parsedToken = [guid]::Empty
if ($requestedToken) {
    if ([guid]::TryParse($requestedToken, [ref]$parsedToken)) { $script:Token = $parsedToken.ToString("D") }
    else { $script:TokenValid = $false; $script:Token = $requestedToken }
} else {
    $script:Token = [guid]::NewGuid().ToString("D")
}

if ([string]::IsNullOrWhiteSpace($ResultDir)) { $ResultDir = $script:InstanceLabelDefaultResultDir }
$resultName = Get-InstanceLabelResultFileName $script:Token
$script:ResultPath = Join-Path $ResultDir ($resultName + ".json")
$script:LogPath = Join-Path $ResultDir ($resultName + ".log")

$script:Result = [ordered]@{
    protocol                  = $script:InstanceLabelProtocol
    kind                      = "alterum-instance-label-launch"
    token                     = $script:Token
    tokenValid                = $script:TokenValid
    pid                       = $null
    exitCode                  = $null
    reason                    = ""
    dryRun                    = [bool]$DryRun
    target                    = $null
    install                   = $null
    label                     = $null
    instanceTag               = $null
    effectiveTag              = $null
    expectedTitle             = $null
    observedTitles            = @()
    arguments                 = $null
    startedUtc                = $script:StartedUtc.ToString("o")
    finishedUtc               = $null
    processStartUtc           = $null
    evidencePath              = $null
    evidenceState             = $null
    evidenceLastVerifiedUtc   = $null
    guardAssemblySha256       = $null
    hotDllSha256              = $null
    hotDllPreflightSha256     = $null
    exeSha256                 = $null
    guardAssemblyVersion      = $null
    preflight                 = $null
    checks                    = @()
    cleanup                   = [ordered]@{ status = "not-needed"; detail = "nothing launched"; pid = $null; ok = $true; killed = $false }
    preExistingPids           = @()
    leftAlonePids             = @()
    workerPid                 = $PID
    workerSession             = (Get-InstanceLabelSessionId)
    resultPath                = $script:ResultPath
    notes                     = @()
}

function Write-WorkerNote {
    param([string]$Message)
    $line = ("[{0}] {1}" -f (Get-Date).ToUniversalTime().ToString("HH:mm:ss.fff"), $Message)
    [void]$script:Notes.Add($line)
    if (-not $Quiet) { Write-Output $line }
}

function Complete-Worker {
    <# The single exit path: stamp the result, write log + JSON, exit with the code. #>
    param([int]$Code, [string]$Reason)
    $script:Result.exitCode = $Code
    $script:Result.reason = $Reason
    $script:Result.finishedUtc = (Get-Date).ToUniversalTime().ToString("o")
    Write-WorkerNote ("exit {0}: {1}" -f $Code, $Reason)
    $script:Result.notes = @($script:Notes)

    $dir = Split-Path -Parent $script:ResultPath
    if ($dir -and -not (Test-Path -LiteralPath $dir -PathType Container)) {
        try { [void](New-Item -ItemType Directory -Path $dir -Force) } catch {
            Write-Output ("result-dir-create-failed: " + $_.Exception.Message)
        }
    }
    if ($script:LogPath) {
        try {
            [IO.File]::WriteAllLines($script:LogPath, [string[]]$script:Notes, (New-Object System.Text.UTF8Encoding($false)))
        } catch { Write-Output ("log-write-failed: " + $_.Exception.Message) }
    }
    try {
        Write-InstanceLabelJson -Path $script:ResultPath -Value $script:Result
    } catch {
        Write-Output ("result-file-write-failed: " + $_.Exception.Message)
    }
    exit $Code
}

Write-WorkerNote ("worker pid={0} session={1} dryRun={2}" -f $PID, $script:Result.workerSession, $DryRun)

# ---------------------------------------------------------------- 1. validate (no launch, no target writes)

if (-not $script:TokenValid) {
    $script:Result.tokenValid = $false
    Complete-Worker 64 ("invalid-token: '" + $requestedToken + "' is not a GUID")
}

$argsProblem = Test-InstanceLabelArgString $ExtraArgs
if ($argsProblem) {
    $script:Result.arguments = $ExtraArgs
    Complete-Worker 64 $argsProblem
}
# -Instance is the same argument the guard reads, so it gets the same grammar check up front.
if ($Instance) {
    $instanceProblem = Test-InstanceLabelInstanceTag $Instance
    if ($instanceProblem) {
        $script:Result.arguments = $ExtraArgs
        Complete-Worker 64 $instanceProblem
    }
    $argsTag = Get-InstanceLabelArgTag $ExtraArgs
    if ($argsTag -and $argsTag -ine $Instance) {
        $script:Result.arguments = $ExtraArgs
        Complete-Worker 64 ("arguments-conflict: -Instance=" + $Instance +
            " but -ExtraArgs carries -alterumInstance=" + $argsTag)
    }
}

if ($SteamLaunch) {
    Complete-Worker 64 "steam-launch-rejected: a Steam launch cannot be labelled or verified; only the two approved installs may be launched"
}

$resolution = Resolve-InstanceLabelTarget -GameDir $GameDir -Target $Target
if (-not $resolution.Ok) {
    Write-WorkerNote ("target rejected: " + $resolution.Reason)
    Complete-Worker $resolution.Code $resolution.Reason
}
$script:Result.target = $resolution.ExePath
$script:Result.install = $resolution.Install
$script:Result.label = $resolution.Label
$script:Result.arguments = $ExtraArgs

$tag = $Instance
if (-not $tag) { $tag = Get-InstanceLabelArgTag $ExtraArgs }

# 'main' is reserved to the pinned install, 'b' to the B install (InstanceLabelIdentity). Same rule,
# same code, and now judged before anything is launched.
$reservedProblem = Test-InstanceLabelReservedTag -Tag $tag -InstallationTag $resolution.Tag
if ($reservedProblem) {
    $script:Result.instanceTag = $tag
    Complete-Worker 64 $reservedProblem
}
$script:Result.instanceTag = $tag

Write-WorkerNote ("target={0} label='{1}' tag='{2}'" -f $resolution.ExePath, $resolution.Label, $tag)

# ---------------------------------------------------------------- 2. deployment preflight

$preflight = Invoke-InstanceLabelPreflight -Install $resolution.Install -ManifestPath $ManifestPath `
    -InstanceTag $tag -AllowManifestlessPreflight:$AllowManifestlessPreflight
$script:Result.preflight = [ordered]@{
    ok              = $preflight.Ok
    degraded        = $preflight.Degraded
    reason          = $preflight.Reason
    manifestPath    = $preflight.ManifestPath
    manifestPresent = $preflight.ManifestPresent
    files           = @($preflight.Files)
    notes           = @($preflight.Notes)
}
$script:Result.exeSha256 = $preflight.ExeSha256
$script:Result.hotDllPreflightSha256 = $preflight.HotDllSha256
foreach ($n in @($preflight.Notes)) { Write-WorkerNote ("preflight note: " + $n) }
if (-not $preflight.Ok) {
    Write-WorkerNote ("preflight failed: " + $preflight.Reason)
    Complete-Worker 70 $preflight.Reason
}
if ($preflight.Degraded) {
    Write-WorkerNote "preflight DEGRADED: presence checked, hashes reported but not verified (no manifest)"
}
Write-WorkerNote ("preflight ok: " + $preflight.Reason)
foreach ($f in @($preflight.Files)) {
    Write-WorkerNote ("  {0,-10} {1}  {2}" -f $f.Role, $f.Sha256, $f.Path)
}

# ---------------------------------------------------------------- 3. session gate

$session = Get-InstanceLabelSessionId
$script:Result.workerSession = $session
if ($session -le 0) {
    Complete-Worker 78 ("session-0-cannot-verify: this worker needs the interactive session to start and inspect the game; " +
        "an empty window enumeration in session 0 is blindness, never proof. Use launch-labelled-session0.ps1.")
}

# ---------------------------------------------------------------- 4. launch (or dry run)

$launchStartedUtc = (Get-Date).ToUniversalTime()
$preExisting = @(Get-InstanceLabelProcessesForPath $resolution.ExePath)
$script:Result.preExistingPids = @($preExisting | ForEach-Object { $_.Pid })
if ($script:Result.preExistingPids.Count -gt 0) {
    Write-WorkerNote ("other instances of this install already running (left alone): " + (($script:Result.preExistingPids) -join ","))
}

$targetPid = 0
$processStartUtc = $null
$proc = $null
$launched = $false

if ($DryRun) {
    $targetPid = $DryRunPid
    $processStartUtc = $launchStartedUtc
    $script:Result.pid = $(if ($DryRunPid -gt 0) { $DryRunPid } else { $null })
    $script:Result.cleanup = [ordered]@{ status = "skipped"; detail = "dry run: nothing was launched, nothing will be killed"; pid = $null; ok = $true; killed = $false }
    Write-WorkerNote ("dry run: no process started; simulating pid {0}" -f $DryRunPid)
} else {
    $launchArgs = ""
    if ($ExtraArgs) { $launchArgs = $ExtraArgs.Trim() }
    # The process has to select the instance the preflight resolved, or the preflight proves something
    # about a hot directory the game will not load. Appended only when -ExtraArgs does not already
    # carry it (a duplicate is refused above).
    if ($Instance) {
        if ($launchArgs) { $launchArgs = $launchArgs + " " }
        $launchArgs = $launchArgs + "-alterumInstance=" + $Instance
    }
    if ($launchArgs) { $launchArgs = $launchArgs + " " }
    $launchArgs = $launchArgs + "-alterumLabelLaunch=" + $script:Token + " -alterumDevMode"
    $script:Result.arguments = $launchArgs

    try {
        $proc = Start-Process -FilePath $resolution.ExePath -ArgumentList $launchArgs `
            -WorkingDirectory $resolution.Install -PassThru -ErrorAction Stop
    } catch {
        Complete-Worker 78 ("launch-failed: " + $_.Exception.Message)
    }
    $launched = $true
    $targetPid = $proc.Id
    $script:Result.pid = $targetPid
    Write-WorkerNote ("started pid={0} exe='{1}' args='{2}'" -f $targetPid, $resolution.ExePath, $launchArgs)

    for ($i = 0; $i -lt 40 -and $null -eq $processStartUtc; $i++) {
        $fact = Get-InstanceLabelProcessFact -TargetPid $targetPid
        if ($fact.Found -and $fact.StartUtc) { $processStartUtc = $fact.StartUtc; break }
        Start-Sleep -Milliseconds 50
    }
    if ($null -eq $processStartUtc) {
        $fact = Get-InstanceLabelProcessFact -TargetPid $targetPid
        if (-not $fact.Found) {
            $script:Result.cleanup = [ordered]@{ status = "not-needed"; detail = "process exited before its start time could be read"; pid = $targetPid; ok = $true; killed = $false }
        } else {
            $script:Result.cleanup = [ordered]@{ status = "refused"; detail = "start time of pid $targetPid unreadable; refusing to kill a process whose identity cannot be re-confirmed"; pid = $targetPid; ok = $false; killed = $false }
            Write-WorkerNote ("CLEANUP NOT COMPLETED for pid {0}: start time unreadable, refusing to kill" -f $targetPid)
        }
        Complete-Worker 78 "process-start-unknown: started pid $targetPid but could not read its start time, so its identity cannot be re-confirmed"
    }
    $script:Result.processStartUtc = ([DateTime]$processStartUtc).ToString("o")
    Write-WorkerNote ("process start utc={0:o} token={1}" -f $processStartUtc, $script:Token)
}

# ---------------------------------------------------------------- 5. verification loop

$deadline = (Get-Date).ToUniversalTime().AddSeconds([Math]::Max(1, $TimeoutSeconds))
$lastVerdict = $null
$timedOut = $false
$prematureExit = $false
$exitCodeOfProcess = $null

while ($true) {
    if ($launched) {
        $alive = Get-InstanceLabelProcessFact -TargetPid $targetPid
        if (-not $alive.Found) {
            $prematureExit = $true
            try { $proc.Refresh(); $exitCodeOfProcess = $proc.ExitCode } catch { }
            break
        }
    }

    $lookup = Get-InstanceLabelEvidenceRecords -Install $resolution.Install -EvidenceDir $EvidenceDir -SinceUtc $launchStartedUtc
    $record = Select-InstanceLabelEvidenceRecord -Records $lookup.Records -TargetPid $targetPid

    $windowReport = $null
    if ($record -and -not $DryRun) {
        $windowReport = Get-InstanceLabelWindowReport -TargetPid $targetPid
    }

    $verdict = Resolve-InstanceLabelVerdict -Context @{
        Record                 = $record
        ProcessId              = $targetPid
        ExePath                = $resolution.ExePath
        ProcessStartUtc        = $processStartUtc
        Token                  = $script:Token
        ExpectedLabel          = $resolution.Label
        InstanceTag            = $tag
        WindowReport           = $windowReport
        LaunchStartedUtc       = $launchStartedUtc
        MaxEvidenceAgeSeconds  = $EvidenceMaxAgeSeconds
        HotDllSha256           = $preflight.HotDllSha256
    }

    if (-not $lastVerdict -or $lastVerdict.Reason -ne $verdict.Reason) {
        Write-WorkerNote ("verify: " + $verdict.Reason)
    }
    $lastVerdict = $verdict

    if ($verdict.Ok) { break }
    if (-not $verdict.Retryable) { break }
    if ((Get-Date).ToUniversalTime() -ge $deadline) { $timedOut = $true; break }
    Start-Sleep -Milliseconds ([Math]::Max(50, $PollMilliseconds))
}

# ---------------------------------------------------------------- 6. verdict + result

if ($lastVerdict) {
    $script:Result.checks = @($lastVerdict.Checks)
    $script:Result.expectedTitle = $lastVerdict.ExpectedTitle
    $script:Result.observedTitles = @($lastVerdict.ObservedTitles)
    $rec = $lastVerdict.Record
    if ($rec) {
        $script:Result.evidencePath = $rec.Path
        $script:Result.evidenceState = $rec.State
        if ($rec.LastVerifiedUtc) { $script:Result.evidenceLastVerifiedUtc = ([DateTime]$rec.LastVerifiedUtc).ToString("o") }
        $script:Result.effectiveTag = $rec.EffectiveTag
        $script:Result.guardAssemblySha256 = $rec.GuardAssemblySha256
        $script:Result.hotDllSha256 = $rec.HotDllSha256
    }
}

# Who is still running from this install, so the caller can see we killed nothing else.
$left = @(Get-InstanceLabelProcessesForPath $resolution.ExePath)
$script:Result.leftAlonePids = @($left | Where-Object { $_.Pid -ne $targetPid } | ForEach-Object { $_.Pid })

if ($lastVerdict -and $lastVerdict.Ok) {
    $script:Result.cleanup = [ordered]@{ status = "none"; detail = "instance left running: verification succeeded"; pid = $targetPid; ok = $true; killed = $false }
    Complete-Worker 0 $lastVerdict.Reason
}

$reason = "unknown-verification-failure"
if ($prematureExit) {
    $reason = ("premature-exit: pid {0} exited before the label could be verified" -f $targetPid)
    if ($null -ne $exitCodeOfProcess) { $reason = $reason + (" (exit code " + $exitCodeOfProcess + ")") }
} elseif ($timedOut) {
    $reason = ("timeout after {0}s: {1}" -f $TimeoutSeconds, $lastVerdict.Reason)
} elseif ($lastVerdict) {
    $reason = $lastVerdict.Reason
}

if ($launched) {
    $cleanup = Stop-InstanceLabelProcess -TargetPid $targetPid -ExpectedExePath $resolution.ExePath -ExpectedStartUtc $processStartUtc
    $cleanupStatus = "failed"
    if ($cleanup.Killed) { $cleanupStatus = "killed" }
    elseif ($cleanup.Ok) { $cleanupStatus = "not-needed" }
    $script:Result.cleanup = [ordered]@{ status = $cleanupStatus; detail = $cleanup.Reason; pid = $targetPid; ok = [bool]$cleanup.Ok; killed = [bool]$cleanup.Killed }
    if ($cleanup.Ok -and $cleanup.Killed) {
        Write-WorkerNote ("cleanup: killed the process this worker started, pid {0} ({1})" -f $targetPid, $resolution.ExePath)
    } elseif ($cleanup.Ok) {
        Write-WorkerNote ("cleanup: nothing to kill for pid {0} ({1})" -f $targetPid, $cleanup.Reason)
    } else {
        Write-WorkerNote ("cleanup FAILED for pid {0}: {1}" -f $targetPid, $cleanup.Reason)
        $reason = $reason + "; cleanup not completed for pid $targetPid ($($cleanup.Reason))"
    }
}

Complete-Worker 78 $reason
