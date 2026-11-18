param([string]$Arm,[string]$ExtraArgs="-screen-fullscreen 0 -screen-width 640 -screen-height 360")
$ErrorActionPreference='Stop'
$g = "C:\AlterumPerf\$Arm"
& 'C:\Windows\Temp\alterum\run-game.ps1' -GameDir $g -TaskName "AlterumPerf_$Arm" -ExtraArgs $ExtraArgs | Out-String
