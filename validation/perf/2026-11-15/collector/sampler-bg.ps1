param([string]$Arm,[string]$Tag,[int]$Seconds=500)
Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','C:\Windows\Temp\alterum\perf\sampler.ps1','-Arm',$Arm,'-Tag',$Tag,'-Seconds',"$Seconds")
"sampler started for $Arm $Tag ${Seconds}s"
