# Create a lightweight second install: every file is a hardlink to the real one
# (same NTFS volume, so ~zero disk cost), except the per-instance state.
#
# $Src is the PINNED install C:\AlterumPinned: new instances hardlink from the pin, so a Steam
# update can never change an existing test install. Pass
# -Src 'C:\Program Files (x86)\Steam\steamapps\common\Alterum' to clone the live Steam copy.
param(
    [string]$Src = "C:\AlterumPinned",
    [string]$Dst = "C:\AlterumB",
    [string]$Account = "playerB",
    [string]$SteamId = "76561198000000002"
)
$ErrorActionPreference = "Stop"
if (Test-Path $Dst) { Remove-Item $Dst -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Dst | Out-Null

$link = 0; $skip = 0
Get-ChildItem $Src -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($Src.Length + 1)
    $to  = Join-Path $Dst $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
    cmd /c mklink /H "`"$to`"" "`"$($_.FullName)`"" | Out-Null
    if ($LASTEXITCODE -eq 0) { $link++ } else { Copy-Item $_.FullName $to -Force; $skip++ }
}
Write-Output "hardlinked=$link copied=$skip"

# --- per-instance state: recreate as real files so writes do not leak into the original ---
function Fresh([string]$rel) {
    $p = Join-Path $Dst $rel
    if (Test-Path $p) { Remove-Item $p -Force }
    return $p
}

$settings = Join-Path $Dst "Alterum_Data\Plugins\x86_64\steam_settings"
New-Item -ItemType Directory -Force -Path $settings | Out-Null
foreach ($f in @("configs.user.ini","configs.main.ini","steam_appid.txt","steam_interfaces.txt")) {
    $n = Fresh "Alterum_Data\Plugins\x86_64\steam_settings\$f"
    Copy-Item (Join-Path $Src "Alterum_Data\Plugins\x86_64\steam_settings\$f") $n -Force
}
@"
[user::general]
account_name=$Account
account_steamid=$SteamId
language=english
"@ | Set-Content (Join-Path $settings "configs.user.ini") -Encoding ascii

# own BepInEx log + config + hot dir
Fresh "BepInEx\LogOutput.log" | Out-Null
Fresh "BepInEx\ErrorLog.log" | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $Dst "BepInEx\LogOutput.log") | Out-Null
Copy-Item (Join-Path $Src "BepInEx\config") (Join-Path $Dst "BepInEx\config") -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $Dst "BepInEx\hot") | Out-Null
Copy-Item (Join-Path $Src "BepInEx\hot\*") (Join-Path $Dst "BepInEx\hot") -Recurse -Force
Write-Output "instance ready at $Dst as $Account ($SteamId)"
Get-Content (Join-Path $settings "configs.user.ini")
