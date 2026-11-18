Get-PSDrive -PSProvider FileSystem | ForEach-Object { "{0} free={1}GB used={2}GB" -f $_.Name, [math]::Round($_.Free/1GB,2), [math]::Round($_.Used/1GB,2) }
Get-CimInstance Win32_OperatingSystem | ForEach-Object { "RAM free={0}GB total={1}GB" -f [math]::Round($_.FreePhysicalMemory/1MB,2), [math]::Round($_.TotalVisibleMemorySize/1MB,2) }
