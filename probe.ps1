$d = Get-PSDrive C
"DiskUsedGB : {0:N1}" -f ($d.Used/1GB)
"DiskFreeGB : {0:N1}" -f ($d.Free/1GB)
$os = Get-CimInstance Win32_OperatingSystem
"MemTotalGB : {0:N1}" -f ($os.TotalVisibleMemorySize/1MB)
"MemFreeGB  : {0:N1}" -f ($os.FreePhysicalMemory/1MB)
