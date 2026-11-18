$d = Get-PSDrive C
"freeGB={0:N3} usedGB={1:N3}" -f ($d.Free/1GB), ($d.Used/1GB)
