# Self-test for the labelled-launch worker. Nothing here starts, stops or restarts the game.
#
#   -IncludeCli      run win/launch-labelled.ps1 as a child process for the argument, preflight and
#                    session gates, and assert exit 64 / 70 / 78 plus the token result file.
#                    Safe by construction: every case fails before Start-Process. The two cases that
#                    WOULD reach a launch (correct manifest, manifestless) only run when this script
#                    itself is in session 0, where the worker refuses to verify windows.
#   -IncludeKillTest prove the exact-path terminator against a throwaway cmd.exe staged as
#                    Alterum.exe in a scratch dir, and prove it refuses to kill this PowerShell when
#                    the path disagrees and refuses a staged process whose start time is off by one
#                    tick (there is no drift window and no bypass switch any more).
#
# Default: pure-function tests only (approved targets, argument hygiene, evidence parsing,
# freshness/token selection, the full verification gate, result-file naming).
[CmdletBinding()]
param(
    [switch]$IncludeCli,
    [switch]$IncludeKillTest,
    [string]$WorkerPath = "",
    [string]$CommonPath = ""
)
$ErrorActionPreference = "Stop"

if (-not $CommonPath) {
    $CommonPath = Join-Path $PSScriptRoot "instance-label-common.ps1"
    if (-not (Test-Path -LiteralPath $CommonPath -PathType Leaf)) {
        $CommonPath = Join-Path $PSScriptRoot "win\instance-label-common.ps1"
    }
}
. $CommonPath
if (-not $WorkerPath) {
    $WorkerPath = Join-Path (Split-Path -Parent $CommonPath) "launch-labelled.ps1"
}

$script:Pass = 0
$script:Fail = 0
$script:Failures = New-Object System.Collections.ArrayList

function Test-That {
    param([string]$Name, [bool]$Condition, [string]$Detail = "")
    if ($Condition) {
        $script:Pass++
        Write-Output ("  PASS  " + $Name)
    } else {
        $script:Fail++
        [void]$script:Failures.Add($Name + $(if ($Detail) { " :: $Detail" } else { "" }))
        Write-Output ("  FAIL  " + $Name + $(if ($Detail) { " :: $Detail" } else { "" }))
    }
}

function New-TestScratchDir {
    param([string]$Name)
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("alterum-label-selftest-" + $Name + "-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    [void](New-Item -ItemType Directory -Path $dir -Force)
    return $dir
}

$approvedLabelPinned = "Alterum - MAIN (all features) [pinned]"
$approvedLabelB = "Alterum B - SAHUR TEST"
$approvedExePinned = "C:\AlterumPinned\Alterum.exe"
# The gate requires a loaded-hot hash on BOTH sides (a missing one is a failure, not a pass), so the
# fixtures carry one by default.
$script:TestHotSha = ("D" * 64)
$currentSession = Get-InstanceLabelSessionId
$powershellExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

Write-Output ("session={0} powershell='{1}'" -f $currentSession, $powershellExe)
Write-Output ("common='{0}'" -f $CommonPath)
Write-Output ("worker='{0}'" -f $WorkerPath)

# ------------------------------------------------------------------ approved targets

Write-Output "== target validation =="
$t = Resolve-InstanceLabelTarget -GameDir "C:\AlterumPinned"
Test-That "approved: pinned dir" ($t.Ok -and $t.ExePath -eq $approvedExePinned -and $t.Label -eq $approvedLabelPinned -and $t.Kind -eq "pinned") $t.Reason
$t = Resolve-InstanceLabelTarget -GameDir "C:\AlterumB"
Test-That "approved: B dir" ($t.Ok -and $t.ExePath -eq "C:\AlterumB\Alterum.exe" -and $t.Label -eq $approvedLabelB -and $t.Kind -eq "b") $t.Reason
$t = Resolve-InstanceLabelTarget -Target "c:\alterumpinned\ALTERUM.EXE"
Test-That "approved: explicit exe, odd case" ($t.Ok -and $t.ExePath -eq $approvedExePinned) $t.Reason
$t = Resolve-InstanceLabelTarget -GameDir "C:\AlterumPinned\"
Test-That "approved: trailing backslash" ($t.Ok) $t.Reason
$t = Resolve-InstanceLabelTarget -GameDir "C:/AlterumPinned"
Test-That "approved: forward slashes" ($t.Ok) $t.Reason

$t = Resolve-InstanceLabelTarget -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Alterum"
Test-That "reject: Steam install -> 64" ((-not $t.Ok) -and $t.Code -eq 64 -and $t.Reason -match "protected|not-approved") $t.Reason
$t = Resolve-InstanceLabelTarget -GameDir "C:\AlterumPinned-pristine"
Test-That "reject: pristine archive -> 64" ((-not $t.Ok) -and $t.Code -eq 64) $t.Reason
$t = Resolve-InstanceLabelTarget -GameDir "C:\AlterumT1"
Test-That "reject: track install -> 64" ((-not $t.Ok) -and $t.Code -eq 64 -and $t.Reason -match "not-approved") $t.Reason
$t = Resolve-InstanceLabelTarget -GameDir "C:\Windows\System32"
Test-That "reject: system dir -> 64" ((-not $t.Ok) -and $t.Code -eq 64 -and $t.Reason -match "protected") $t.Reason
$t = Resolve-InstanceLabelTarget -Target "\\server\share\Alterum.exe"
Test-That "reject: UNC -> 64" ((-not $t.Ok) -and $t.Code -eq 64) $t.Reason
$t = Resolve-InstanceLabelTarget -Target "C:\AlterumPinned\notthegame.exe"
Test-That "reject: wrong exe name -> 64" ((-not $t.Ok) -and $t.Code -eq 64) $t.Reason
$t = Resolve-InstanceLabelTarget -GameDir "C:\AlterumPinned" -SteamLaunch
Test-That "reject: -SteamLaunch -> 64" ((-not $t.Ok) -and $t.Code -eq 64 -and $t.Reason -match "steam-launch-rejected") $t.Reason
$t = Resolve-InstanceLabelTarget -GameDir ""
Test-That "reject: no target -> 64" ((-not $t.Ok) -and $t.Code -eq 64) $t.Reason

# ------------------------------------------------------------------ argument hygiene

Write-Output "== argument hygiene =="
Test-That "args: plain policy args accepted" ($null -eq (Test-InstanceLabelArgString "-screen-fullscreen 0 -screen-width 640 -screen-height 360 -alterumInstance=b"))
Test-That "args: caller token rejected" ((Test-InstanceLabelArgString "-alterumInstance=b -alterumLabelLaunch=dead") -match "arguments-rejected")
Test-That "args: quote rejected" ((Test-InstanceLabelArgString "-name `"x`"") -match "malformed")
Test-That "args: newline rejected" ((Test-InstanceLabelArgString "-a`r`n-b") -match "malformed")
Test-That "tag parsed from args" ((Get-InstanceLabelArgTag "-screen-fullscreen 0 -alterumInstance=b") -eq "b")
Test-That "tag: absent -> null" ($null -eq (Get-InstanceLabelArgTag "-screen-width 640"))
Test-That "args: valid inline instance tag accepted" ($null -eq (Test-InstanceLabelArgString "-alterumInstance=b"))
Test-That "args: valid separate-value instance tag accepted" ($null -eq (Test-InstanceLabelArgString "-alterumInstance b"))
# The guard refuses a second -alterumInstance (arg-duplicate); it used to be "last occurrence wins",
# which launched a process the guard then killed with exit 78. It is now a prelaunch 64.
Test-That "args: duplicate -alterumInstance rejected (arg-duplicate)" ((Test-InstanceLabelArgString "-alterumInstance=a -alterumInstance=b2") -match "arg-duplicate")
Test-That "args: malformed instance tag rejected (arg-malformed)" ((Test-InstanceLabelArgString "-alterumInstance=bad!") -match "arg-malformed")
Test-That "args: over-long instance tag rejected" ((Test-InstanceLabelArgString ("-alterumInstance=" + ("a" * 33))) -match "arg-malformed")
Test-That "args: leading '-' instance tag rejected" ((Test-InstanceLabelArgString "-alterumInstance=-b") -match "malformed|missing-value")
Test-That "args: bare -alterumInstance rejected (arg-missing-value)" ((Test-InstanceLabelArgString "-screen-width 640 -alterumInstance") -match "arg-missing-value")
Test-That "args: 'main' reserved to the pinned install" ((Test-InstanceLabelArgString "-alterumInstance=main" -InstallationTag "b") -match "reserved-tag")
Test-That "args: 'b' reserved to the B install" ((Test-InstanceLabelArgString "-alterumInstance=b" -InstallationTag "main") -match "reserved-tag")
Test-That "args: 'main' accepted on the pinned install" ($null -eq (Test-InstanceLabelArgString "-alterumInstance=main" -InstallationTag "main"))
Test-That "args: 'b' accepted on the B install" ($null -eq (Test-InstanceLabelArgString "-alterumInstance=b" -InstallationTag "b"))
$tMain = Resolve-InstanceLabelTarget -GameDir "C:\AlterumPinned"
Test-That "target carries the install tag" ($tMain.Tag -eq "main") $tMain.Tag
$tB = Resolve-InstanceLabelTarget -GameDir "C:\AlterumB"
Test-That "B target carries the install tag" ($tB.Tag -eq "b") $tB.Tag

# ------------------------------------------------------------------ evidence parsing

Write-Output "== evidence parsing / freshness / selection =="
$scratch = New-TestScratchDir "evidence"
$nowUtc = (Get-Date).ToUniversalTime()
$tokA = [guid]::NewGuid().ToString("D")
$tokB = [guid]::NewGuid().ToString("D")
$startA = $nowUtc.AddSeconds(-6)
$startB = $nowUtc.AddSeconds(-3)

$recA = [ordered]@{
    protocol = 1
    pid = 4242
    processStartUtc = $startA.ToString("o")
    sessionId = 1
    exePath = $approvedExePinned
    rawInstanceArg = "pinned"
    installationIdentity = "pinned"
    effectiveTag = "pinned"
    expectedTitle = "$approvedLabelPinned [instance=pinned; pid=4242]"
    observedTitle = "$approvedLabelPinned [instance=pinned; pid=4242]"
    hwnd = "0x100C"
    windowClass = "UnityWndClass"
    guardAssemblySha256 = ("A" * 64)
    launchToken = $tokA
    state = "verified"
    lastVerifiedUtc = $nowUtc.ToString("o")
}
$freshPath = Join-Path $scratch ("4242-" + $startA.Ticks + ".json")
Write-InstanceLabelJson -Path $freshPath -Value $recA

$staleRecord = [ordered]@{
    protocol = 1; pid = 4243; processStartUtc = $nowUtc.AddMinutes(-10).ToString("o"); state = "verified"
    launchToken = $tokB; expectedTitle = "x"; observedTitle = "x"; lastVerifiedUtc = $nowUtc.AddMinutes(-10).ToString("o")
}
$stalePath = Join-Path $scratch ("4243-" + $nowUtc.AddMinutes(-10).Ticks + ".json")
Write-InstanceLabelJson -Path $stalePath -Value $staleRecord
(Get-Item -LiteralPath $stalePath).LastWriteTimeUtc = $nowUtc.AddMinutes(-10)

$jsonlPath = Join-Path $scratch "instance-labels.jsonl"
$line = (@{ pid = 4242; launchToken = $tokA; state = "verified"; expectedTitle = $recA.expectedTitle; lastVerifiedUtc = $nowUtc.ToString("o") } | ConvertTo-Json -Compress)
[IO.File]::WriteAllLines($jsonlPath, [string[]]@($line), (New-Object System.Text.UTF8Encoding($false)))

$lookup = Get-InstanceLabelEvidenceRecords -Install "C:\AlterumPinned" -EvidenceDir $scratch -SinceUtc $nowUtc.AddSeconds(-60)
Test-That "evidence: dir exists" ($lookup.Exists)
Test-That "evidence: stale record filtered by freshness" (@($lookup.Records | Where-Object { $_.Pid -eq 4243 }).Count -eq 0)
Test-That "evidence: fresh json parsed" (@($lookup.Records | Where-Object { $_.Pid -eq 4242 -and $_.Path -eq $freshPath }).Count -eq 1)
Test-That "evidence: jsonl line parsed" (@($lookup.Records | Where-Object { $_.Path -like "*jsonl*" }).Count -ge 1)
$freshRec = $lookup.Records | Where-Object { $_.Pid -eq 4242 -and $_.Path -eq $freshPath } | Select-Object -First 1
Test-That "evidence: token parsed" ($freshRec.LaunchToken -eq $tokA)
Test-That "evidence: expected title parsed" ($freshRec.ExpectedTitle -eq $recA.expectedTitle)
Test-That "evidence: hwnd parsed" ($freshRec.Hwnd -eq 4108)
Test-That "evidence: start parsed (ticks/ISO agree)" ([Math]::Abs((([DateTime]$freshRec.ProcessStartUtc) - $startA).TotalSeconds) -lt 2)
$selected = Select-InstanceLabelEvidenceRecord -Records $lookup.Records -TargetPid 4242
Test-That "evidence: selection by pid" ($null -ne $selected -and [int]$selected.Pid -eq 4242)

# The reproduced B1 case: the guard writes the status record, then appends an event line, so the
# .jsonl file is the newest thing in the directory. The event carries the pid, the state and the
# expected title but no exe/start/token, and it used to be selected - which read as
# evidence-exe-mismatch on a perfectly healthy launch.
$eventDir = New-TestScratchDir "event-vs-status"
$evStart = (Get-Date).ToUniversalTime().AddSeconds(-4)
$evToken = [guid]::NewGuid().ToString("D")
$statusRecord = [ordered]@{
    protocolVersion = 1
    pid = 5151
    processStartUtc = $evStart.ToString("o")
    sessionId = "11111111-2222-3333-4444-555555555555"
    exe = $approvedExePinned
    effectiveTag = "pinned"
    expectedTitle = "$approvedLabelPinned [instance=pinned; pid=5151]"
    observedTitle = "$approvedLabelPinned [instance=pinned; pid=5151]"
    hwnd = 4108
    windowClass = "UnityWndClass"
    guardAssemblySha256 = ("A" * 64)
    hotDllSha256 = $script:TestHotSha
    launchToken = $evToken
    state = "verified"
    lastVerifiedUtc = (Get-Date).ToUniversalTime().ToString("o")
}
$statusPath2 = Join-Path $eventDir ("5151-" + $evStart.Ticks + ".json")
Write-InstanceLabelJson -Path $statusPath2 -Value $statusRecord
# ... and then the events, appended after the status write (so the .jsonl mtime is newer).
$eventLines = @(
    (@{ utc = (Get-Date).ToUniversalTime().ToString("o"); sessionId = "x"; pid = 5151; event = "initializing"; detail = "exe=..."; state = "initializing"; hwnd = "0x0"; expectedTitle = $statusRecord.expectedTitle } | ConvertTo-Json -Compress),
    (@{ utc = (Get-Date).ToUniversalTime().ToString("o"); sessionId = "x"; pid = 5151; event = "verified"; detail = "hwnd=0x100C"; state = "verified"; hwnd = "0x100C"; expectedTitle = $statusRecord.expectedTitle } | ConvertTo-Json -Compress)
)
$eventPath = Join-Path $eventDir ("5151-" + $evStart.Ticks + ".jsonl")
[IO.File]::WriteAllLines($eventPath, [string[]]$eventLines, (New-Object System.Text.UTF8Encoding($false)))
(Get-Item -LiteralPath $eventPath).LastWriteTimeUtc = (Get-Date).ToUniversalTime()
(Get-Item -LiteralPath $statusPath2).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddSeconds(-3)

$evLookup = Get-InstanceLabelEvidenceRecords -Install "C:\AlterumPinned" -EvidenceDir $eventDir -SinceUtc (Get-Date).ToUniversalTime().AddSeconds(-60)
Test-That "evidence: event lines are parsed" (@($evLookup.Records | Where-Object { [string]$_.Source -eq "event" }).Count -ge 2)
Test-That "evidence: event lines are not status records" (-not (Test-InstanceLabelStatusFields (@($evLookup.Records | Where-Object { [string]$_.Source -eq "event" })[0])))
$evSelected = Select-InstanceLabelEvidenceRecord -Records $evLookup.Records -TargetPid 5151
Test-That "evidence: a newer event does not displace the status record" ($null -ne $evSelected -and [string]$evSelected.Source -eq "status" -and $evSelected.Path -eq $statusPath2) ("selected=" + $(if ($evSelected) { $evSelected.Source + " " + $evSelected.Path } else { "none" }))
Test-That "evidence: selected record keeps the launch token" ($null -ne $evSelected -and $evSelected.LaunchToken -eq $evToken)
$evRecord = ConvertTo-InstanceLabelEvidenceRecord -Bag (New-Object hashtable) -Path "test://none" -FileWriteUtc (Get-Date).ToUniversalTime()
Test-That "evidence: a status-null record has no pid" ($null -eq $evRecord.Pid)
Test-That "evidence: a pid-less record is never selected" ($null -eq (Select-InstanceLabelEvidenceRecord -Records @($evRecord) -TargetPid 5151))


$noneSelected = Select-InstanceLabelEvidenceRecord -Records $lookup.Records -TargetPid 9999
Test-That "evidence: other pid never selected" ($null -eq $noneSelected)
$missing = Get-InstanceLabelEvidenceRecords -Install "C:\AlterumPinned" -EvidenceDir (Join-Path $scratch "nope") -SinceUtc $nowUtc
Test-That "evidence: missing dir is reported, not silently empty" ((-not $missing.Exists) -and (@($missing.Notes) -join " ") -match "evidence-dir-missing")

Test-That "utc: .NET ticks" ([Math]::Abs((((ConvertTo-InstanceLabelUtc ([string]$startA.Ticks)) - $startA).TotalSeconds)) -lt 1)
$epochBase = [DateTime]::SpecifyKind((New-Object System.DateTime -ArgumentList 1970, 1, 1), [DateTimeKind]::Utc)
$epochSecondsText = [string][int]$nowUtc.Subtract($epochBase).TotalSeconds
Test-That "utc: epoch seconds" ([Math]::Abs(((ConvertTo-InstanceLabelUtc $epochSecondsText) - $nowUtc).TotalSeconds) -lt 2)
Test-That "utc: garbage -> null" ($null -eq (ConvertTo-InstanceLabelUtc "not-a-time"))
Test-That "result filename: token sanitised" ((Get-InstanceLabelResultFileName "a/b c") -eq "a_b_c")

# ------------------------------------------------------------------ the verification gate

Write-Output "== verification gate (pure) =="

function New-TestWindowReport {
    param([string[]]$Titles, [long]$Hwnd = 4108, [int]$Session = 1, [bool]$Ok = $true, [string]$Reason = "")
    $wins = @()
    foreach ($title in @($Titles)) {
        $wins += [pscustomobject]@{ Hwnd = ("0x{0:X}" -f $Hwnd); HwndValue = $Hwnd; Title = $title; ClassName = "UnityWndClass"; Visible = $true }
    }
    return [pscustomobject]@{ Ok = $Ok; Reason = $Reason; CurrentSession = $Session; TargetSession = $Session; Windows = $wins }
}

function New-TestRecord {
    param(
        [string]$Token = $tokA, [int]$ProcId = 4242, [string]$ExePath = $approvedExePinned,
        [DateTime]$StartUtc = $startA, [string]$Title = "$approvedLabelPinned [instance=pinned; pid=4242]",
        [string]$State = "verified", [DateTime]$VerifiedUtc = (Get-Date).ToUniversalTime(),
        [string]$Tag = "pinned", $Hwnd = 4108, $Protocol = 1, [string]$ObservedTitle = "",
        [string]$HotSha = $script:TestHotSha
    )
    if (-not $ObservedTitle) { $ObservedTitle = $Title }
    return [pscustomobject]@{
        Path = "test://record"; FileWriteUtc = $VerifiedUtc; Protocol = $Protocol; Pid = $ProcId
        ProcessStartUtc = $StartUtc; SessionId = 1; ExePath = $ExePath; RawInstanceArg = "pinned"
        InstallationIdentity = "pinned"; EffectiveTag = $Tag; ExpectedTitle = $Title; ObservedTitle = $ObservedTitle
        Hwnd = $Hwnd; WindowClass = "UnityWndClass"; GuardAssemblySha256 = ("A" * 64); HotDllSha256 = $HotSha
        LaunchToken = $Token; State = $State; LastVerifiedUtc = $VerifiedUtc; Detail = $null
    }
}

function New-TestContext {
    param($Record, $WindowReport, [string]$Token = $tokA, [DateTime]$StartUtc = $startA, $Tag = "pinned", $HotSha = $script:TestHotSha, [int]$MaxAge = 60)
    return @{
        Record = $Record; ProcessId = 4242; ExePath = $approvedExePinned; ProcessStartUtc = $StartUtc
        Token = $Token; ExpectedLabel = $approvedLabelPinned; InstanceTag = $Tag
        WindowReport = $WindowReport; LaunchStartedUtc = (Get-Date).ToUniversalTime().AddSeconds(-30)
        MaxEvidenceAgeSeconds = $MaxAge; HotDllSha256 = $HotSha
    }
}

$goodTitle = "$approvedLabelPinned [instance=pinned; pid=4242]"
$baseRecord = New-TestRecord
$baseWindows = New-TestWindowReport @($goodTitle)
$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record $baseRecord -WindowReport $baseWindows)
Test-That "gate: matching record + window -> 0" ($v.Ok -and $v.Code -eq 0) $v.Reason
Test-That "gate: expected title carried through" ($v.ExpectedTitle -eq $goodTitle) $v.ExpectedTitle
Test-That "gate: observed title read from the window, not the record" ((@($v.ObservedTitles) -contains $goodTitle))

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record $null -WindowReport $baseWindows)
Test-That "gate: no evidence -> 78 no-evidence (retryable)" ((-not $v.Ok) -and $v.Code -eq 78 -and $v.Reason -eq "no-evidence" -and $v.Retryable) $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record $baseRecord -WindowReport (New-TestWindowReport @("Something else")))
Test-That "gate: wrong window title -> 78" ((-not $v.Ok) -and $v.Reason -match "window-title-mismatch") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record $baseRecord -WindowReport (New-TestWindowReport @()))
Test-That "gate: zero windows -> 78 no-window (never success)" ((-not $v.Ok) -and $v.Reason -match "no-window") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record $baseRecord -WindowReport (New-TestWindowReport @($goodTitle) -Ok $false -Reason "session-0-cannot-enumerate-windows"))
Test-That "gate: session-0 window blindness -> 78" ((-not $v.Ok) -and $v.Reason -match "window-query-failed") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -ProcId 999) -WindowReport $baseWindows)
Test-That "gate: record from another pid -> 78" ((-not $v.Ok) -and $v.Reason -match "evidence-pid-mismatch" -and (-not $v.Retryable)) $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -Token ([guid]::NewGuid().ToString("D"))) -WindowReport $baseWindows)
Test-That "gate: other launch's token -> 78" ((-not $v.Ok) -and $v.Reason -match "evidence-token-mismatch") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -StartUtc $startA.AddSeconds(-120)) -WindowReport $baseWindows)
Test-That "gate: different process start -> 78" ((-not $v.Ok) -and $v.Reason -match "evidence-start-mismatch") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -ExePath "C:\AlterumB\Alterum.exe") -WindowReport $baseWindows)
Test-That "gate: record from another install -> 78" ((-not $v.Ok) -and $v.Reason -match "evidence-exe-mismatch") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -State "fatal") -WindowReport $baseWindows)
Test-That "gate: guard fatal -> 78" ((-not $v.Ok) -and $v.Reason -match "guard-fatal") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -State "initializing") -WindowReport $baseWindows)
Test-That "gate: guard still initializing -> 78 retryable" ((-not $v.Ok) -and $v.Reason -match "guard-state-not-verified" -and $v.Retryable) $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -Title "$approvedLabelB [instance=b; pid=4242]" -Tag "b") -WindowReport (New-TestWindowReport @("$approvedLabelB [instance=b; pid=4242]")))
Test-That "gate: B label on the pinned install -> 78" ((-not $v.Ok) -and $v.Reason -match "expected-title-label-mismatch") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -Title $approvedLabelPinned) -WindowReport (New-TestWindowReport @($approvedLabelPinned)))
Test-That "gate: title without pid suffix -> 78" ((-not $v.Ok) -and $v.Reason -match "expected-title-format") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -Title "$approvedLabelPinned [instance=pinned; pid=999]") -WindowReport $baseWindows)
Test-That "gate: title with another pid -> 78" ((-not $v.Ok) -and $v.Reason -match "expected-title-pid-mismatch") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record $baseRecord -WindowReport $baseWindows -Tag "b")
Test-That "gate: requested tag vs title tag -> 78" ((-not $v.Ok) -and $v.Reason -match "expected-title-tag-mismatch") $v.Reason

$stale = New-TestRecord -VerifiedUtc (Get-Date).ToUniversalTime().AddMinutes(-5)
$stale.FileWriteUtc = (Get-Date).ToUniversalTime()   # written now, but the guard stopped verifying
$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record $stale -WindowReport $baseWindows -MaxAge 60)
Test-That "gate: stale verification -> 78" ((-not $v.Ok) -and $v.Reason -match "evidence-stale") $v.Reason

$good = New-TestRecord
$good.ObservedTitle = "Alterum - MAIN (all features) [pinned] [instance=pinned; pid=4242] "
$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record $good -WindowReport $baseWindows)
Test-That "gate: guard readback differs -> 78" ((-not $v.Ok) -and $v.Reason -match "guard-observed-title-mismatch") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -Protocol 2) -WindowReport $baseWindows)
Test-That "gate: unknown protocol -> 78" ((-not $v.Ok) -and $v.Reason -match "evidence-protocol-unsupported") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -Protocol $null) -WindowReport $baseWindows)
Test-That "gate: protocol absent -> 78 (format drift fails closed)" ((-not $v.Ok) -and $v.Reason -match "evidence-protocol-missing") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -Hwnd 2000) -WindowReport $baseWindows)
Test-That "gate: hwnd not one of the pid's windows -> 78" ((-not $v.Ok) -and $v.Reason -match "window-hwnd-mismatch") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -HotSha ("B" * 64)) -WindowReport $baseWindows -HotSha ("C" * 64))
Test-That "gate: loaded hot DLL differs from preflight -> 78" ((-not $v.Ok) -and $v.Reason -match "hot-dll-mismatch") $v.Reason

$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -HotSha ("B" * 64)) -WindowReport $baseWindows -HotSha ("B" * 64))
Test-That "gate: loaded hot DLL matches preflight -> 0" ($v.Ok) $v.Reason

# F2: a hash that cannot be compared on both sides is a failure, never a pass.
$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record (New-TestRecord -HotSha "") -WindowReport $baseWindows)
Test-That "gate: guard reported no loaded hot hash -> 78 (not a pass)" ((-not $v.Ok) -and $v.Reason -match "hot-dll-record-hash-missing") $v.Reason
$v = Resolve-InstanceLabelVerdict -Context (New-TestContext -Record $baseRecord -WindowReport $baseWindows -HotSha $null)
Test-That "gate: preflight produced no hot hash -> 78 (not a pass)" ((-not $v.Ok) -and $v.Reason -match "hot-dll-preflight-hash-missing") $v.Reason

# B1 repro: the status record selected above (with the newer event line beside it) verifies.
$evVerdict = Resolve-InstanceLabelVerdict -Context @{
    Record = $evSelected; ProcessId = 5151; ExePath = $approvedExePinned; ProcessStartUtc = $evStart
    Token = $evToken; ExpectedLabel = $approvedLabelPinned; InstanceTag = "pinned"
    WindowReport = (New-TestWindowReport @($statusRecord.expectedTitle))
    LaunchStartedUtc = (Get-Date).ToUniversalTime().AddSeconds(-30)
    MaxEvidenceAgeSeconds = 60; HotDllSha256 = $script:TestHotSha
}
Test-That "gate: status record + newer event line -> 0 (B1 repro)" ($evVerdict.Ok -and $evVerdict.Code -eq 0) $evVerdict.Reason

# ------------------------------------------------------------------ exact-path terminator

Write-Output "== exact-path termination =="
$refuse = Stop-InstanceLabelProcess -TargetPid 999999 -ExpectedExePath $approvedExePinned -ExpectedStartUtc $nowUtc
Test-That "kill: absent pid -> ok, nothing killed" ($refuse.Ok -and (-not $refuse.Killed) -and $refuse.Reason -eq "process-not-running") $refuse.Reason

# The bypass switch is gone for good: it skipped both the executable-name and the protected-path
# check, which is a kill-safety hole, not a convenience.
Test-That "kill: no -AllowNonAlterumTarget bypass exists" ($null -eq (Get-Command Stop-InstanceLabelProcess).Parameters["AllowNonAlterumTarget"])

$selfFact = Get-InstanceLabelProcessFact -TargetPid $PID
$refuse = Stop-InstanceLabelProcess -TargetPid $PID -ExpectedExePath $approvedExePinned -ExpectedStartUtc $selfFact.StartUtc
Test-That "kill: path mismatch refused (this test process survived)" ((-not $refuse.Ok) -and $refuse.Reason -match "refused-path-mismatch") $refuse.Reason

$refuse = Stop-InstanceLabelProcess -TargetPid $PID -ExpectedExePath $selfFact.Path -ExpectedStartUtc $selfFact.StartUtc
Test-That "kill: non-Alterum target refused (no bypass)" ((-not $refuse.Ok) -and $refuse.Reason -match "refused-not-an-alterum-executable") $refuse.Reason

$refuse = Stop-InstanceLabelProcess -TargetPid $PID -ExpectedExePath $approvedExePinned -ExpectedStartUtc ([DateTime]::MinValue)
Test-That "kill: missing expected start refused" ((-not $refuse.Ok) -and $refuse.Reason -match "refused-start-missing") $refuse.Reason

$refuse = Stop-InstanceLabelProcess -TargetPid $PID -ExpectedExePath "C:\Program Files (x86)\Steam\steamapps\common\Alterum\Alterum.exe" -ExpectedStartUtc $nowUtc
Test-That "kill: protected path refused" ((-not $refuse.Ok) -and $refuse.Reason -match "refused-protected-path") $refuse.Reason

if ($IncludeKillTest) {
    # The target has to be an Alterum.exe outside the protected paths, so stage a throwaway copy of
    # cmd.exe under that name in a scratch dir. It is not the game and it is not an installed build.
    $killDir = New-TestScratchDir "kill"
    $stagedExe = Join-Path $killDir "Alterum.exe"
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot "System32\cmd.exe") -Destination $stagedExe -Force
    $scratchProc = Start-Process -FilePath $stagedExe `
        -ArgumentList "/c ping -n 8 127.0.0.1 > nul" -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 600
    $fact = Get-InstanceLabelProcessFact -TargetPid $scratchProc.Id
    if (-not $fact.Found) {
        Test-That "kill: scratch process started" $false "the staged Alterum.exe exited immediately"
    } else {
        # Exact start time, no drift window: one tick off is a mismatch. The process must survive it.
        $refuse = Stop-InstanceLabelProcess -TargetPid $scratchProc.Id -ExpectedExePath $fact.Path -ExpectedStartUtc $fact.StartUtc.AddTicks(1)
        Test-That "kill: start-time off by one tick refused (no drift window)" ((-not $refuse.Ok) -and $refuse.Reason -match "refused-start-mismatch") $refuse.Reason
        Test-That "kill: refused process still runs" ($null -ne (Get-Process -Id $scratchProc.Id -ErrorAction SilentlyContinue))
        $refuse = Stop-InstanceLabelProcess -TargetPid $scratchProc.Id -ExpectedExePath $fact.Path -ExpectedStartUtc $fact.StartUtc.AddMinutes(-5)
        Test-That "kill: start-time mismatch refused" ((-not $refuse.Ok) -and $refuse.Reason -match "refused-start-mismatch") $refuse.Reason

        $killed = Stop-InstanceLabelProcess -TargetPid $scratchProc.Id -ExpectedExePath $fact.Path -ExpectedStartUtc $fact.StartUtc
        Test-That "kill: exact identity match kills that one process" ($killed.Ok -and $killed.Killed) $killed.Reason
        Start-Sleep -Milliseconds 300
        Test-That "kill: scratch pid is gone" (-not (Get-InstanceLabelProcessFact -TargetPid $scratchProc.Id).Found)
        Test-That "kill: this test process is still alive" ($null -ne (Get-Process -Id $PID -ErrorAction SilentlyContinue))
    }
    try { Remove-Item -LiteralPath $killDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
} else {
    Write-Output "  SKIP  kill success path (-IncludeKillTest not given)"
}

# ------------------------------------------------------------------ child-process exit codes

if ($IncludeCli) {
    Write-Output "== worker exit codes (child process) =="
    if (-not (Test-Path -LiteralPath $WorkerPath -PathType Leaf)) {
        Test-That "cli: worker present" $false $WorkerPath
    } else {
        $cliRoot = New-TestScratchDir "cli"

        function Invoke-WorkerCli {
            param([string]$Name, [hashtable]$Settings)
            $tk = [guid]::NewGuid().ToString("D")
            $rd = Join-Path $cliRoot $Name
            $workerArgList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $WorkerPath,
                "-Token", $tk, "-ResultDir", $rd, "-TimeoutSeconds", "3")
            foreach ($key in @($Settings.Keys | Sort-Object)) {
                $value = $Settings[$key]
                if ($value -is [switch] -or $value -eq $true) { $workerArgList += ("-" + $key) }
                else { $workerArgList += @(("-" + $key), [string]$value) }
            }
            $output = & $powershellExe @workerArgList 2>&1 | Out-String
            $code = $LASTEXITCODE
            $resultFile = Join-Path $rd ($tk + ".json")
            $parsedResult = $null
            if (Test-Path -LiteralPath $resultFile -PathType Leaf) {
                try { $parsedResult = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json } catch { }
            }
            return [pscustomobject]@{ Name = $Name; Code = $code; Output = $output; Result = $parsedResult; ResultFile = $resultFile; Token = $tk }
        }

        $badManifestDir = Join-Path $cliRoot "manifests"
        [void](New-Item -ItemType Directory -Path $badManifestDir -Force)
        $holdDir = "C:\AlterumPinned"

        $c = Invoke-WorkerCli "c64-steam" @{ GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Alterum" }
        Test-That "cli: Steam target -> 64" ($c.Code -eq 64) $c.Output
        Test-That "cli: 64 result file written, pid null" ($null -ne $c.Result -and $c.Result.exitCode -eq 64 -and $null -eq $c.Result.pid) $c.Output

        $c = Invoke-WorkerCli "c64-pristine" @{ GameDir = "C:\AlterumPinned-pristine" }
        Test-That "cli: pristine target -> 64" ($c.Code -eq 64) $c.Output
        Test-That "cli: 64 reason names the problem" ($null -ne $c.Result -and $c.Result.reason -match "protected|not-approved|missing") $c.Result.reason

        $c = Invoke-WorkerCli "c64-args" @{ GameDir = $holdDir; ExtraArgs = "-alterumInstance=b -alterumLabelLaunch=fake" }
        Test-That "cli: caller-supplied token -> 64" ($c.Code -eq 64 -and $c.Result.reason -match "arguments-rejected") $c.Output

        # F4: a bad instance argument is a PRELAUNCH 64, not a guard rejection after the process is up.
        $c = Invoke-WorkerCli "c64-instance-bad" @{ GameDir = $holdDir; ExtraArgs = "-alterumInstance=bad!" }
        Test-That "cli: malformed instance argument -> 64 (arg-malformed)" ($c.Code -eq 64 -and $c.Result.reason -match "arg-malformed") $c.Output
        $c = Invoke-WorkerCli "c64-instance-dup" @{ GameDir = $holdDir; ExtraArgs = "-alterumInstance=a -alterumInstance=mine" }
        Test-That "cli: duplicate instance argument -> 64 (arg-duplicate)" ($c.Code -eq 64 -and $c.Result.reason -match "arg-duplicate") $c.Output
        $c = Invoke-WorkerCli "c64-instance-reserved" @{ GameDir = $holdDir; ExtraArgs = "-alterumInstance=b" }
        Test-That "cli: reserved tag on the pinned install -> 64 (reserved-tag)" ($c.Code -eq 64 -and $c.Result.reason -match "reserved-tag") $c.Output

        $c = Invoke-WorkerCli "c70-nomanifest" @{ GameDir = $holdDir }
        Test-That "cli: no deployment manifest -> 70" ($c.Code -eq 70) $c.Output
        Test-That "cli: 70 result file written, pid null" ($null -ne $c.Result -and $c.Result.exitCode -eq 70 -and $null -eq $c.Result.pid) $c.Output
        Test-That "cli: 70 reason says manifest-missing" ($null -ne $c.Result -and $c.Result.reason -match "manifest-missing") $c.Result.reason

        $globs = @()
        foreach ($rel in @("BepInEx\plugins\AlterumModelSwap.dll", "BepInEx\plugins\AlterumModelSwap.Contracts.dll", "BepInEx\hot\AlterumModelSwap.Hot.dll")) {
            $full = Join-Path $holdDir $rel
            $globs += [pscustomobject]@{ role = $(if ($rel -match "Contracts") { "contracts" } elseif ($rel -match "Hot") { "hot" } else { "permanent" }); path = $rel; sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
        }
        $badManifest = Join-Path $badManifestDir "bad-hash.json"
        Write-InstanceLabelJson -Path $badManifest -Value ([ordered]@{
            protocol = 1; install = $holdDir
            files = @($globs | ForEach-Object { [ordered]@{ role = $_.role; path = $_.path; sha256 = ("0" * 64) } })
        })
        $c = Invoke-WorkerCli "c70-badhash" @{ GameDir = $holdDir; ManifestPath = $badManifest }
        Test-That "cli: manifest hash mismatch -> 70" ($c.Code -eq 70 -and $c.Result.reason -match "manifest-hash-mismatch") $c.Output

        $missingManifest = Join-Path $badManifestDir "missing-file.json"
        Write-InstanceLabelJson -Path $missingManifest -Value ([ordered]@{
            protocol = 1; install = $holdDir
            files = @(
                [ordered]@{ role = "permanent"; path = "BepInEx\plugins\AlterumModelSwap.dll"; sha256 = ("0" * 64) },
                [ordered]@{ role = "contracts"; path = "BepInEx\plugins\AlterumModelSwap.Contracts.dll"; sha256 = ("0" * 64) },
                [ordered]@{ role = "hot"; path = "BepInEx\hot\does-not-exist.dll"; sha256 = ("0" * 64) }
            )
        })
        $c = Invoke-WorkerCli "c70-manifestfile" @{ GameDir = $holdDir; ManifestPath = $missingManifest }
        Test-That "cli: manifest names an absent file -> 70" ($c.Code -eq 70) $c.Output

        $goodManifest = Join-Path $badManifestDir "good.json"
        Write-InstanceLabelJson -Path $goodManifest -Value ([ordered]@{
            protocol = 1; install = $holdDir
            files = @($globs | ForEach-Object { [ordered]@{ role = $_.role; path = $_.path; sha256 = $_.sha256 } })
        })

        if ($currentSession -eq 0) {
            $c = Invoke-WorkerCli "c78-session0" @{ GameDir = $holdDir; AllowManifestlessPreflight = $true }
            Test-That "cli: session-0 verification refused -> 78" ($c.Code -eq 78) $c.Output
            Test-That "cli: 78 reason is session-0-cannot-verify" ($null -ne $c.Result -and $c.Result.reason -match "session-0-cannot-verify") $c.Result.reason
            Test-That "cli: 78 result has no pid (nothing launched)" ($null -ne $c.Result -and $null -eq $c.Result.pid) $c.Output
            Test-That "cli: 78 result records preflight ok" ($null -ne $c.Result -and $c.Result.preflight.ok -eq $true -and $c.Result.preflight.degraded -eq $true) $c.Output

            $c = Invoke-WorkerCli "c78-goodmanifest" @{ GameDir = $holdDir; ManifestPath = $goodManifest }
            Test-That "cli: manifest-verified preflight then session-0 refusal -> 78" ($c.Code -eq 78 -and $c.Result.preflight.degraded -eq $false -and $c.Result.preflight.ok -eq $true) $c.Output
        } else {
            Write-Output "  SKIP  session-0 refusal cases: this shell is in session $currentSession, where those cases would launch"
        }

        Write-Output ("  cli scratch: " + $cliRoot)
    }
} else {
    Write-Output "  SKIP  worker exit codes (-IncludeCli not given)"
}

# ------------------------------------------------------------------ summary

try { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue } catch { }

Write-Output ""
Write-Output ("passed={0} failed={1}" -f $script:Pass, $script:Fail)
if ($script:Fail -gt 0) {
    Write-Output "failures:"
    foreach ($f in $script:Failures) { Write-Output ("  - " + $f) }
    exit 1
}
Write-Output "selftest: OK"
exit 0
