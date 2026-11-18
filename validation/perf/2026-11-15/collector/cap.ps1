param([string]$Out,[int]$Seconds=20)
$pm='C:\Windows\Temp\alterum\perf\bin\PresentMon.exe'
$dir='C:\Windows\Temp\alterum\perf\raw'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$pmargs = @('--process_name','Alterum.exe','--output_file',"$dir\$Out",'--timed',"$Seconds",'--terminate_after_timed','--no_console_stats','--date_time','--stop_existing_session')
& $pm @pmargs 2>&1 | Out-String
"exit=$LASTEXITCODE"
Get-ChildItem "$dir\$Out" -ErrorAction SilentlyContinue | Select-Object Name,Length | Out-String
