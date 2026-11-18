$ErrorActionPreference='Stop'
$src='C:\AlterumPinned'; $root='C:\AlterumPerf'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$mutable = @('BepInEx\config','BepInEx\hot','BepInEx\logs','BepInEx\screens','BepInEx\cache',
             'Alterum_Data\Plugins\x86_64\steam_settings')

function MakeArm([string]$dst,[string]$acct,[string]$sid,[bool]$vanilla) {
  New-Item -ItemType Directory -Force -Path $dst | Out-Null
  $link=0
  Get-ChildItem $src -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length + 1)
    $skip = $false
    foreach ($m in $mutable) { if ($rel.StartsWith($m, [StringComparison]::OrdinalIgnoreCase)) { $skip=$true; break } }
    if ($rel -eq 'BepInEx\LogOutput.log' -or $rel -eq 'BepInEx\ErrorLog.log') { $skip=$true }
    if ($skip) { return }
    $to = Join-Path $dst $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
    cmd /c mklink /H "`"$to`"" "`"$($_.FullName)`"" | Out-Null
    if ($LASTEXITCODE -eq 0) { $script:link++ } else { Copy-Item $_.FullName $to -Force }
  }
  foreach ($m in @('BepInEx\config','BepInEx\hot','BepInEx\logs','BepInEx\cache','BepInEx\screens')) {
    $d = Join-Path $dst $m
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    if ($m -in @('BepInEx\config','BepInEx\hot','BepInEx\cache')) {
      Get-ChildItem (Join-Path $src $m) -File -Force | ForEach-Object { Copy-Item $_.FullName (Join-Path $d $_.Name) -Force }
    }
  }
  Set-Content (Join-Path $dst 'BepInEx\LogOutput.log') '' -NoNewline
  Set-Content (Join-Path $dst 'BepInEx\ErrorLog.log') '' -NoNewline
  $settings = Join-Path $dst 'Alterum_Data\Plugins\x86_64\steam_settings'
  New-Item -ItemType Directory -Force -Path $settings | Out-Null
  foreach ($f in @('configs.main.ini','steam_appid.txt','steam_interfaces.txt')) {
    $s = Join-Path $src "Alterum_Data\Plugins\x86_64\steam_settings\$f"
    if (Test-Path $s) { Copy-Item $s (Join-Path $settings $f) -Force }
  }
  @"
[user::general]
account_name=$acct
account_steamid=$sid
language=english
"@ | Set-Content (Join-Path $settings 'configs.user.ini') -Encoding ascii
  if ($vanilla) {
    Move-Item (Join-Path $dst 'winhttp.dll') (Join-Path $dst 'winhttp.dll.off') -Force
    if (Test-Path (Join-Path $dst 'doorstop_config.ini')) { Move-Item (Join-Path $dst 'doorstop_config.ini') (Join-Path $dst 'doorstop_config.ini.off') -Force }
  }
  "arm $dst links=$script:link"
}

$script:link=0; MakeArm "$root\V" playerV 76561198000000011 $true
$script:link=0; MakeArm "$root\L" playerL 76561198000000012 $false
$script:link=0; MakeArm "$root\F" playerF 76561198000000013 $false
"--- V root ---"
Get-ChildItem "$root\V" -File | Select-Object Name | Format-Table -AutoSize | Out-String
"--- hot V ---"
Get-ChildItem "$root\V\BepInEx\hot" | Select-Object Name | Format-Table -AutoSize | Out-String
"--- disk ---"
foreach ($a in 'V','L','F') { "$a : " + [math]::Round((Get-ChildItem "$root\$a" -Recurse -File | Measure-Object Length -Sum).Sum/1GB,2) + " GB" }
Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}} | Format-Table -AutoSize | Out-String
