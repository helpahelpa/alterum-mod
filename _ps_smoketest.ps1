# Task 1 step 4: prove C:\AlterumPinned runs.
#  - 640x360, its own instance id ("pinned") so its hot/config/log/screens are its own
#  - distinct gbe identity so it cannot collide with the user's running instance (playerA)
#  - stops ONLY processes whose exe path is C:\AlterumPinned\Alterum.exe
#  - restores steam_settings/configs.user.ini byte-for-byte at the end
$ErrorActionPreference = "Stop"
$pin = "C:\AlterumPinned"
$exe = Join-Path $pin "Alterum.exe"
$inst = "pinned"
$userIni = Join-Path $pin "Alterum_Data\Plugins\x86_64\steam_settings\configs.user.ini"
$userIniBak = "$userIni.pretest"
$log = Join-Path $pin "BepInEx\LogOutput.log"

function Show($k, $v) { Write-Output ("{0}={1}" -f $k, $v) }

# --- do not disturb the user's instance -----------------------------------
$user = @(Get-Process -Id 29384 -ErrorAction SilentlyContinue)
if ($user.Count -gt 0) { Show "user_pid_29384" ("alive path=" + $user[0].Path + " session=" + $user[0].SessionId) }
else { Show "user_pid_29384" "not running" }

# --- preconditions --------------------------------------------------------
$ram = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB
Show "ram_free_GB_before" ([math]::Round($ram,2))
if ($ram -lt 2.5) { Write-Output "ABORT: less than 2.5 GB free RAM; not launching"; exit 9 }

$hotSrc = Join-Path $pin "BepInEx\hot\AlterumModelSwap.Hot.dll"
$hotDstDir = Join-Path $pin "BepInEx\hot\$inst"
New-Item -ItemType Directory -Force -Path $hotDstDir | Out-Null
$hotDst = Join-Path $hotDstDir "AlterumModelSwap.Hot.dll"
if ((Test-Path $hotSrc) -and -not (Test-Path $hotDst)) { Copy-Item $hotSrc $hotDst }
Show "instance_hot_dll" ("{0} bytes={1}" -f $hotDst, (Get-Item $hotDst).Length)

# --- distinct emulator identity -------------------------------------------
$origHash = (Get-FileHash $userIni -Algorithm SHA256).Hash
Copy-Item $userIni $userIniBak -Force
@"
[user::general]
account_name=playerPinned
account_steamid=76561198000000099
language=english
"@ | Set-Content -Path $userIni -Encoding ASCII
Show "identity_swapped" ("orig=" + $origHash.Substring(0,16) + " -> playerPinned/...0099 (restored at the end)")

# --- launch through the redirected tooling --------------------------------
$offset = 0
if (Test-Path $log) { $offset = (Get-Item $log).Length }
Show "log_offset_before" $offset

$launch = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Windows\Temp\alterum\track-instance.ps1" -Action launch -Install $pin -Instance $inst -ScreenWidth 640 -ScreenHeight 360
Write-Output ("launch: " + ($launch -join " | "))

# --- wait for BepInEx + the lobby ----------------------------------------
$hotSeen = $false; $lobbySeen = $false; $secs = 0
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Seconds 1
    $secs = $i + 1
    if (-not (Test-Path $log)) { continue }
    $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    try {
        $fs.Seek($offset, 'Begin') | Out-Null
        $sr = New-Object IO.StreamReader($fs)
        $tail = $sr.ReadToEnd()
    } finally { $fs.Dispose() }
    if (-not $hotSeen -and $tail -match '\[hot\] generation \d+ active') { $hotSeen = $true; Show "hot_active_after_s" $secs }
    if (-not $lobbySeen -and $tail -match "\[scene\] -> 'Lobby'") { $lobbySeen = $true; Show "lobby_after_s" $secs }
    if ($hotSeen -and $lobbySeen) { break }
}

# --- evidence -------------------------------------------------------------
$procs = @()
foreach ($p in @(Get-Process -Name Alterum -ErrorAction SilentlyContinue)) {
    $path = $null; try { $path = $p.Path } catch { }
    $procs += [pscustomobject]@{ Id = $p.Id; Path = $path; Ws = [math]::Round($p.WorkingSet64/1MB,0); Session = $p.SessionId }
}
Write-Output "=== processes ==="
$procs | ForEach-Object { Write-Output ("  pid={0} session={1} ws={2}MB path={3}" -f $_.Id, $_.Session, $_.Ws, $_.Path) }

$mine = @($procs | Where-Object { $_.Path -and ($_.Path.TrimEnd("\") -ieq $exe) })
Show "pinned_processes" $mine.Count

Write-Output "=== log evidence (this run) ==="
$fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
try { $fs.Seek($offset, 'Begin') | Out-Null; $sr = New-Object IO.StreamReader($fs); $tail = $sr.ReadToEnd() } finally { $fs.Dispose() }
$lines = $tail -split "`r?`n"
Write-Output ("  total_new_lines=" + $lines.Count)
Write-Output "  -- BepInEx / doorstop / host --"
$lines | Where-Object { $_ -match 'BepInEx|Doorstop|doorstop|\[host\]|\[hot\]' } | Select-Object -First 12 | ForEach-Object { Write-Output ("  " + $_) }
Write-Output "  -- scene / lobby / instance --"
$lines | Where-Object { $_ -match '\[scene\]|Lobby|lobby|instance' } | Select-Object -First 8 | ForEach-Object { Write-Output ("  " + $_) }
$err = @($lines | Where-Object { $_ -match '^\[Error|^\[Fatal|Exception' })
Show "error_lines_in_run" $err.Count
if ($err.Count -gt 0) { $err | Select-Object -First 6 | ForEach-Object { Write-Output ("  ERR " + $_) } }

# --- stop, by exact exe path ---------------------------------------------
$stop = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Windows\Temp\alterum\track-instance.ps1" -Action stop -Install $pin -Instance $inst
Write-Output ("stop: " + ($stop -join " | "))
Start-Sleep -Milliseconds 800
$stillMine = @()
foreach ($p in @(Get-Process -Name Alterum -ErrorAction SilentlyContinue)) {
    $path = $null; try { $path = $p.Path } catch { }
    if ($path -and ($path.TrimEnd("\") -ieq $exe)) { $stillMine += $p.Id }
}
Show "pinned_still_running" $stillMine.Count
$stillUser = @(Get-Process -Id 29384 -ErrorAction SilentlyContinue)
Show "user_pid_29384_after" $(if ($stillUser.Count -gt 0) { "alive, untouched" } else { "GONE - investigate" })

# --- restore the manifest-covered file ------------------------------------
Copy-Item $userIniBak $userIni -Force
Remove-Item $userIniBak -Force
$nowHash = (Get-FileHash $userIni -Algorithm SHA256).Hash
Show "identity_restored" ($nowHash -eq $origHash)
Show "configs_user_ini_sha256" $nowHash

Show "ram_free_GB_after" ([math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB,2))
