param([string]$Arm='F',[string]$Prefix='F_lobby',[string]$Seq='Create Lobby Menu Button|@Peers',[int]$Warm=30,[int]$Win=120,[int]$Count=2)
$ErrorActionPreference='Continue'
$g="C:\AlterumPerf\$Arm"; $log="$g\BepInEx\LogOutput.log"
Get-Process -Name Alterum -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$g\Alterum.exe" } | Stop-Process -Force
Start-Sleep -Seconds 2
Set-Content $log '' 
& 'C:\Windows\Temp\alterum\perf\launch.ps1' -Arm $Arm | Out-String
$sw=[Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 90) { if ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match "scene\] -> 'Menu'") { break }; Start-Sleep -Seconds 2 }
"menu seen"
Start-Sleep -Seconds 2
Set-Content "$g\BepInEx\screens\ui.request" "seq=$Seq" -Encoding ascii
$sw.Restart()
while ($sw.Elapsed.TotalSeconds -lt 180) { if ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match "scene\] -> 'Lobby'") { break }; Start-Sleep -Seconds 2 }
"lobby seen after $([int]$sw.Elapsed.TotalSeconds)s"
& 'C:\Windows\Temp\alterum\perf\sampler-bg.ps1' -Arm $Arm -Tag lobby2 -Seconds ($Warm+$Win*$Count+40) | Out-String
& 'C:\Windows\Temp\alterum\perf\windows.ps1' -Arm $Arm -Prefix $Prefix -Warm $Warm -Win $Win -Count $Count | Out-String
& 'C:\Windows\Temp\alterum\perf\vshot.ps1' -Out "C:\Windows\Temp\alterum\perf\raw\$($Prefix).png" | Out-String
"done $Arm $(Get-Date -Format HH:mm:ss)"
