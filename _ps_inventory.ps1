# Inventory the canonical Alterum install: version, exe hash, sizes, BepInEx pieces, processes.
$ErrorActionPreference = "Stop"
$g = "C:\Program Files (x86)\Steam\steamapps\common\Alterum"

function Show($k, $v) { Write-Output ("{0}={1}" -f $k, $v) }

# --- processes ---
Write-Output "=== processes ==="
foreach ($p in @(Get-Process -Name Alterum -ErrorAction SilentlyContinue)) {
    $path = $null
    try { $path = $p.Path } catch { }
    Show ("pid{0}" -f $p.Id) ("path={0} session={1} ws={2}MB" -f $path, $p.SessionId, [math]::Round($p.WorkingSet64/1MB,0))
}

# --- version ---
Write-Output "=== version ==="
$exe = Join-Path $g "Alterum.exe"
$vi = (Get-Item $exe).VersionInfo
foreach ($n in "FileVersion","ProductVersion","FileDescription","CompanyName","ProductName") {
    $val = $vi.$n
    if ($val) { Show $n $val }
}
# Unity version out of the data folder
$gv = Join-Path $g "Alterum_Data\globalgamemanagers"
if (Test-Path $gv) {
    $bytes = [IO.File]::ReadAllBytes($gv)
    $txt = [Text.Encoding]::ASCII.GetString($bytes[0..([Math]::Min(4000,$bytes.Length-1))])
    $m = [Regex]::Match($txt, "\d+\.\d+\.\d+[a-z]\d+")
    if ($m.Success) { Show "unityVersion(globalgamemanagers)" $m.Value }
}
$appInfo = Join-Path $g "Alterum_Data\app.info"
if (Test-Path $appInfo) { Show "app.info" ((Get-Content $appInfo -Raw) -replace "`r`n"," | ") }
# Unity build report / data
$bp = Join-Path $g "Alterum_Data\data.unity3d"
if (Test-Path $bp) { Show "data.unity3d bytes" (Get-Item $bp).Length }
# changelog head gives a human version marker
$cl = Join-Path $g "changelog.txt"
if (Test-Path $cl) {
    Write-Output "--- changelog.txt (first 12 lines) ---"
    Get-Content $cl -TotalCount 12 | ForEach-Object { Write-Output $_ }
    Write-Output "--- end changelog ---"
}

# --- hashes ---
Write-Output "=== hashes ==="
Show "exe.sha256" (Get-FileHash $exe -Algorithm SHA256).Hash
foreach ($f in "GameAssembly.dll","UnityPlayer.dll","winhttp.dll","doorstop_config.ini","baselib.dll",".doorstop_version") {
    $pth = Join-Path $g $f
    if (Test-Path $pth) { Show ("{0}.sha256" -f $f) (Get-FileHash $pth -Algorithm SHA256).Hash }
}

# --- sizes / counts ---
Write-Output "=== sizes ==="
$data = Join-Path $g "Alterum_Data"
$df = @(Get-ChildItem $data -Recurse -File -ErrorAction SilentlyContinue)
Show "Alterum_Data files" $df.Count
Show "Alterum_Data bytes" (($df | Measure-Object -Property Length -Sum).Sum)
$all = @(Get-ChildItem $g -Recurse -File -ErrorAction SilentlyContinue)
Show "install files(incl BepInEx)" $all.Count
Show "install bytes(incl BepInEx)" (($all | Measure-Object -Property Length -Sum).Sum)

# --- BepInEx pieces ---
Write-Output "=== bepinex pieces ==="
$bx = Join-Path $g "BepInEx"
foreach ($sub in "core","patchers","plugins","hot","config","interop","unity-libs","cache") {
    $d = Join-Path $bx $sub
    if (Test-Path $d) {
        $fs = @(Get-ChildItem $d -Recurse -File -ErrorAction SilentlyContinue)
        Show ("bepinex/{0} files" -f $sub) $fs.Count
        Show ("bepinex/{0} bytes" -f $sub) (($fs | Measure-Object -Property Length -Sum).Sum)
    } else { Show ("bepinex/{0}" -f $sub) "ABSENT" }
}
Write-Output "--- plugins ---"
Get-ChildItem (Join-Path $bx "plugins") -File -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output ("  {0}  {1}  {2}  {3}" -f $_.Length, $_.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0,16), $_.Name)
}
Write-Output "--- core (dll+name) ---"
Get-ChildItem (Join-Path $bx "core") -File -Filter *.dll -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output ("  {0}  {1}" -f $_.Name, $_.Length)
}
Write-Output "--- hot tree ---"
Get-ChildItem (Join-Path $bx "hot") -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output ("  {0}  {1}" -f $_.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), $_.FullName.Substring($bx.Length+6))
}
Write-Output "--- config ---"
Get-ChildItem (Join-Path $bx "config") -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output ("  {0}  {1}  {2}" -f $_.Length, $_.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), $_.FullName.Substring($bx.Length+8))
}
Write-Output "--- doorstop_config.ini ---"
Get-Content (Join-Path $g "doorstop_config.ini") -TotalCount 30 | ForEach-Object { Write-Output ("  " + $_) }

# --- steam emulator files ---
Write-Output "=== steam_api64 emulator state ==="
$ad = Join-Path $g "Alterum_Data\Plugins\x86_64"
if (Test-Path $ad) {
    Get-ChildItem $ad -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "steam" } | ForEach-Object {
        Write-Output ("  {0}  {1}  {2}" -f $_.Length, $_.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), $_.FullName.Substring($ad.Length+1))
    }
}

# --- disk ---
Write-Output "=== disk ==="
Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Name -match "^[CD]$" } | ForEach-Object {
    Show ("{0}.freeGB" -f $_.Name) ([math]::Round($_.Free/1GB,2))
}
Get-CimInstance Win32_OperatingSystem | ForEach-Object {
    Show "RAM.freeGB" ([math]::Round($_.FreePhysicalMemory/1MB,2))
    Show "RAM.totalGB" ([math]::Round($_.TotalVisibleMemorySize/1MB,2))
}
