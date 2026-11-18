param([string]$Arm='L',[string]$Prefix='L_lobby',[string]$Warm=60,[int]$Win=120,[int]$Count=2)
$marker="C:\AlterumPerf\$Arm\BepInEx\bench.window"
if ($Warm -gt 0) { "warmup ${Warm}s from $(Get-Date -Format HH:mm:ss)"; Start-Sleep -Seconds $Warm }
for ($i=1; $i -le $Count; $i++) {
  $tag = "{0}_w{1}" -f $Prefix,$i
  Set-Content $marker $tag -Encoding ascii
  "window $tag start $(Get-Date -Format HH:mm:ss)"
  Start-Sleep -Seconds $Win
  "window $tag end   $(Get-Date -Format HH:mm:ss)"
}
Set-Content $marker 'end' -Encoding ascii
"done"
