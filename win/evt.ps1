$ErrorActionPreference = "SilentlyContinue"
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-40)} |
  Where-Object { $_.Message -match 'Alterum' } |
  Select-Object -First 12 TimeCreated, Id, ProviderName, @{n='Msg';e={($_.Message -split "`n")[0..3] -join ' | '}} |
  Format-List
