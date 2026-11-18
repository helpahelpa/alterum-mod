# Create one isolated track install (C:\AlterumT1 | T2 | T3).
#
# Like win/make-instance.ps1 the bulk of the game is NTFS-hardlinked from A, so it
# costs no disk and no copy time. The difference is what is NOT shared: every path
# the game or BepInEx writes at runtime is recreated as a real file/directory, so a
# track can never write through a hardlink into A (or into another track):
#
#   BepInEx\config            own copy (per-instance cfg name is <Instance>)
#   BepInEx\plugins           own copy - rebuilding the host here must not touch A
#   BepInEx\hot\              empty; only BepInEx\hot\<Instance>\ is used
#   BepInEx\logs, screens     empty
#   BepInEx\cache             absent (regenerated on first run)
#   BepInEx\Log*.log          fresh, empty
#   steam_settings            own copy + own gbe identity (account_name/steamid)
#
# Read-only at runtime and therefore safe to share: BepInEx\core, patchers,
# unity-libs, interop, and the whole game data tree.
param(
    [Parameter(Mandatory = $true)][string]$Dst,
    [Parameter(Mandatory = $true)][string]$Instance,      # t1 | t2 | t3
    [Parameter(Mandatory = $true)][string]$Account,       # playerT1 ..
    [Parameter(Mandatory = $true)][string]$SteamId,       # 765611980000000xx
    [string]$Track = "",
    [string]$Src = "C:\AlterumPinned"          # pinned install; see pinned/UPDATE-POLICY.md
)
$ErrorActionPreference = "Stop"
if (-not $Track) { $Track = $Instance }
if (Test-Path $Dst) { Remove-Item $Dst -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Dst | Out-Null

# --- 1. hardlink every game file except the mutable state --------------------
$skipDirs = @(
    "BepInEx\config", "BepInEx\plugins", "BepInEx\hot", "BepInEx\logs",
    "BepInEx\screens", "BepInEx\cache",
    "Alterum_Data\Plugins\x86_64\steam_settings"
)
$skipFiles = @("BepInEx\LogOutput.log", "BepInEx\ErrorLog.log")

$link = 0; $copied = 0; $skipped = 0
Get-ChildItem $Src -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($Src.Length + 1)
    $isSkipped = $false
    foreach ($d in $skipDirs) {
        if ($rel -ieq $d -or $rel.StartsWith($d + "\", [StringComparison]::OrdinalIgnoreCase)) { $isSkipped = $true; break }
    }
    if (-not $isSkipped) {
        foreach ($f in $skipFiles) { if ($rel -ieq $f) { $isSkipped = $true; break } }
    }
    if (-not $isSkipped -and $rel -match '^BepInEx\\LogOutput\..*\.log$') { $isSkipped = $true }
    if ($isSkipped) { $skipped++; return }

    $to = Join-Path $Dst $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
    cmd /c mklink /H "`"$to`"" "`"$($_.FullName)`"" | Out-Null
    if ($LASTEXITCODE -eq 0) { $link++ } else { Copy-Item $_.FullName $to -Force; $copied++ }
}
Write-Output "hardlinked=$link copied=$copied skipped=$skipped"

# --- 2. per-instance gbe steam identity -------------------------------------
$ssSrc = Join-Path $Src "Alterum_Data\Plugins\x86_64\steam_settings"
$ssDst = Join-Path $Dst "Alterum_Data\Plugins\x86_64\steam_settings"
New-Item -ItemType Directory -Force -Path $ssDst | Out-Null
Get-ChildItem $ssSrc -File -Force | Where-Object { $_.Name -ne "configs.user.ini" } | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $ssDst $_.Name) -Force
}
@"
[user::general]
account_name=$Account
account_steamid=$SteamId
language=english
"@ | Set-Content (Join-Path $ssDst "configs.user.ini") -Encoding ascii

# --- 3. own BepInEx state ----------------------------------------------------
$cfgSrc = Join-Path $Src "BepInEx\config"
$cfgDst = Join-Path $Dst "BepInEx\config"
New-Item -ItemType Directory -Force -Path $cfgDst | Out-Null
Get-ChildItem $cfgSrc -File -Force |
    Where-Object { $_.Name -notmatch '^com\.user0\.alterum\.modelswap\..+\.cfg$' } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $cfgDst $_.Name) -Force }

$plugSrc = Join-Path $Src "BepInEx\plugins"
$plugDst = Join-Path $Dst "BepInEx\plugins"
New-Item -ItemType Directory -Force -Path $plugDst | Out-Null
if (Test-Path $plugSrc) {
    Get-ChildItem $plugSrc -File -Force | ForEach-Object { Copy-Item $_.FullName (Join-Path $plugDst $_.Name) -Force }
}

foreach ($d in @("BepInEx\hot\$Instance", "BepInEx\logs", "BepInEx\screens", "BepInEx\screens\$Instance")) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Dst $d) | Out-Null
}
foreach ($f in @("BepInEx\LogOutput.log", "BepInEx\ErrorLog.log")) {
    New-Item -ItemType File -Force -Path (Join-Path $Dst $f) | Out-Null
}

# --- 4. marker so scripts can refuse to drive the wrong install --------------
@"
track=$Track
instance=$Instance
account=$Account
steamid=$SteamId
source=$Src
created=$(Get-Date -Format o)
"@ | Set-Content (Join-Path $Dst "TRACK-ISOLATION.txt") -Encoding ascii

Write-Output "instance ready at $Dst as $Account ($SteamId), track=$Track, instance-name=$Instance"
Write-Output ("hot dir: " + (Join-Path $Dst "BepInEx\hot\$Instance"))
Write-Output ("cfg: " + (Join-Path $cfgDst "com.user0.alterum.modelswap.$Instance.cfg") + " (created on first run)")
Get-Content (Join-Path $ssDst "configs.user.ini")
