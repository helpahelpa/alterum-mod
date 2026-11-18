param([Parameter(Mandatory=$true)][string]$Request)
# Minimal session-0 bridge. No fixed sleeps, persistent task, process-name kills, or game input.
$ErrorActionPreference='Stop'
$r=Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
$clock=[Diagnostics.Stopwatch]::StartNew()
$name='AlterumDiagnostics-'+[guid]::NewGuid().ToString('N')
$registered=$false
try {
    $svc=New-Object -ComObject Schedule.Service
    $svc.Connect()
    $folder=$svc.GetFolder('\')
    $task=$svc.NewTask(0)
    $task.Settings.ExecutionTimeLimit='PT'+[math]::Max(1,[math]::Ceiling($r.budgetSeconds))+'S'
    $task.Settings.AllowHardTerminate=$true
    $task.Settings.DisallowStartIfOnBatteries=$false
    $task.Settings.StopIfGoingOnBatteries=$false
    $task.Principal.UserId=[Environment]::UserName
    $task.Principal.LogonType=3
    $task.Principal.RunLevel=1
    $action=$task.Actions.Create(0)
    $action.Path="$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $worker=Join-Path $PSScriptRoot 'diagnostic-worker.ps1'
    $action.Arguments="-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$worker`" -Request `"$Request`""
    $folder.RegisterTaskDefinition($name,$task,6,$null,$null,3)|Out-Null
    $registered=$true
    $job=$folder.GetTask($name)
    $job.Run($null)|Out-Null
    while ($clock.Elapsed.TotalSeconds -lt $r.budgetSeconds) {
        if (Test-Path -LiteralPath $r.result) { Write-Output $r.result; exit 0 }
        Start-Sleep -Milliseconds 50
    }
    throw 'interactive worker exceeded stage deadline'
} finally {
    if ($registered) {
        # Only the unique task created here. Never stop an Alterum process.
        try { $folder.GetTask($name).Stop(0) } catch {}
        try { $folder.DeleteTask($name,0) } catch {}
    }
}
