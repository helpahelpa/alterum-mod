$ErrorActionPreference = "Continue"
& "C:\Windows\Temp\run-in-session1.ps1" -Script "C:\Windows\Temp\qc-launch.ps1" -TaskName "QcConsoleLaunch" -WaitSeconds 8
Get-Process -Name Alterum -ErrorAction SilentlyContinue | Select-Object Id,SessionId,StartTime | Format-Table -Auto
