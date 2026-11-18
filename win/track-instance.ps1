# Drive exactly ONE Alterum install (default: the PINNED install C:\AlterumPinned).
#   launch : start <Install>\Alterum.exe through a Task Scheduler task with an
#            interactive token (session 0 has no desktop, so a direct start dies).
#            Never touches an instance that is already running.
#   stop   : kill ONLY processes whose executable path is <Install>\Alterum.exe.
#            Never matches by process name - A, B and the other tracks must survive.
#   status : report this install's processes and how many other instances exist.
#
# All parameters are space-free so this can be called over ssh without quoting games.
#
# TARGET: -Install defaults to the PINNED install C:\AlterumPinned (frozen on Steam buildid
# 26597574, see pinned/UPDATE-POLICY.md). Pass -Install explicitly for a track
# install (C:\AlterumT1|T2|T3), C:\AlterumB, or the old Steam path
# 'C:\Program Files (x86)\Steam\steamapps\common\Alterum'.
param(
    [Parameter(Mandatory = $true)][ValidateSet("launch", "stop", "status")][string]$Action,
    [string]$Install = "C:\AlterumPinned",
    [string]$Instance = "",
    [int]$ScreenWidth = 640,
    [int]$ScreenHeight = 360,
    [string]$RunAs = [Environment]::UserName,
    [string]$TaskName = ""
)
$ErrorActionPreference = "Stop"

$Install = [IO.Path]::GetFullPath($Install).TrimEnd("\")
$exe = Join-Path $Install "Alterum.exe"
if (-not (Test-Path $exe)) { throw "no Alterum.exe at $exe" }
if (-not $TaskName) { $TaskName = "AlterumTrack" + ($(if ($Instance) { $Instance.ToUpper() } else { "Default" })) }

# Processes belonging to THIS install only, matched on the full executable path.
function Get-TrackProcs {
    $found = @()
    foreach ($p in @(Get-Process -Name Alterum -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { }
        if (-not $path) {
            try { $path = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").ExecutablePath } catch { }
        }
        if ($path -and ([IO.Path]::GetFullPath($path).TrimEnd("\") -ieq $exe)) { $found += $p }
    }
    return $found
}

function Get-AllAlterumCount {
    return @(Get-Process -Name Alterum -ErrorAction SilentlyContinue).Count
}

$svc = $null
function Get-TaskRoot {
    if (-not $script:svc) {
        $script:svc = New-Object -ComObject Schedule.Service
        $script:svc.Connect()
    }
    return $script:svc.GetFolder("\")
}

switch ($Action) {
    "launch" {
        $running = @(Get-TrackProcs)
        if ($running.Count -gt 0) {
            Write-Output ("already-running pid=" + ($running.Id -join ","))
            exit 3
        }

        $gameArgs = "-screen-fullscreen 0 -screen-width $ScreenWidth -screen-height $ScreenHeight -alterumDevMode"
        if ($Instance) { $gameArgs += " -alterumInstance=$Instance" }

        $root = Get-TaskRoot
        try { $old = $root.GetTask($TaskName); if ($old.State -eq 4) { $old.Stop() }; $root.DeleteTask($TaskName, 0) } catch { }

        $task = $svc.NewTask(0)
        $task.RegistrationInfo.Description = "Alterum track instance $Instance ($Install)"
        $task.Settings.ExecutionTimeLimit = "PT6H"
        $task.Settings.DisallowStartIfOnBatteries = $false
        $task.Settings.StopIfGoingOnBatteries = $false
        $task.Settings.AllowHardTerminate = $true
        $task.Settings.MultipleInstances = 2          # ignore new while this action runs

        $a = $task.Actions.Create(0)                  # TASK_ACTION_EXEC
        $a.Path = $exe
        $a.Arguments = $gameArgs
        $a.WorkingDirectory = $Install

        $principal = $task.Principal
        $principal.UserId = $RunAs
        $principal.LogonType = 3                      # TASK_LOGON_INTERACTIVE_TOKEN
        $principal.RunLevel = 1
        $principal.GroupId = ""

        $root.RegisterTaskDefinition($TaskName, $task, 6, $null, $null, 3) | Out-Null
        $root.GetTask($TaskName).Run($null) | Out-Null

        for ($i = 0; $i -lt 40; $i++) {
            $p = @(Get-TrackProcs)
            if ($p.Count -gt 0) {
                Write-Output ("started pid={0} session={1} task={2} args='{3}'" -f $p[0].Id, $p[0].SessionId, $TaskName, $gameArgs)
                exit 0
            }
            Start-Sleep -Milliseconds 700
        }
        Write-Output "WARNING: no process appeared for $exe after ~28s"
        exit 4
    }

    "stop" {
        $p = @(Get-TrackProcs)
        if ($p.Count -eq 0) {
            Write-Output "not-running"
            $others = (Get-AllAlterumCount) 
            if ($others -gt 0) { Write-Output "left-alone=$others" }
            exit 0
        }
        $ids = @($p.Id)
        foreach ($proc in $p) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        $gone = $false
        for ($i = 0; $i -lt 30; $i++) {
            Start-Sleep -Milliseconds 500
            if (@(Get-TrackProcs).Count -eq 0) { $gone = $true; break }
        }
        if ($gone) {
            try { $root = Get-TaskRoot; $root.DeleteTask($TaskName, 0) } catch { }
            Write-Output ("stopped pid=" + ($ids -join ","))
            $others = (Get-AllAlterumCount)
            if ($others -gt 0) { Write-Output "left-alone=$others" }
            exit 0
        }
        Write-Output ("still-running pid=" + ((@(Get-TrackProcs).Id) -join ","))
        exit 5
    }

    "status" {
        $p = @(Get-TrackProcs)
        if ($p.Count -gt 0) {
            Write-Output ("running pid={0} session={1} install={2}" -f ($p.Id -join ","), $p[0].SessionId, $Install)
        } else {
            Write-Output "not-running install=$Install"
        }
        $others = (Get-AllAlterumCount) - $p.Count
        Write-Output "others-running=$others"
        exit 0
    }
}
