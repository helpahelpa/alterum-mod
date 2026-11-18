# Task 2: pristine archive of the GAME files only -> C:\AlterumPinned-pristine
# Derived from the pinned copy by excluding every BepInEx / doorstop / gbe artifact, and by
# restoring the genuine Steam steam_api64.dll from the .vanilla backup.
$ErrorActionPreference = "Stop"
$src = "C:\AlterumPinned"
$dst = "C:\AlterumPinned-pristine"

function Show($k, $v) { Write-Output ("{0}={1}" -f $k, $v) }

# ---- exclusions -----------------------------------------------------------
# Directories (full path; robocopy /XD matches dirs exactly)
$xd = @(
    (Join-Path $src "BepInEx"),                                            # loader + mod + interop + caches
    (Join-Path $src "dotnet"),                                             # BepInEx's bundled .NET runtime
    (Join-Path $src "Alterum_Data\Plugins\x86_64\steam_settings")          # gbe_fork identity/config
)
# Files (full path; robocopy /XF)
$xf = @(
    (Join-Path $src "winhttp.dll"),                                        # UnityDoorstop proxy loader
    (Join-Path $src "doorstop_config.ini"),                                # doorstop config
    (Join-Path $src ".doorstop_version"),                                  # doorstop version marker
    (Join-Path $src "changelog.txt"),                                      # BepInEx 6's own changelog
    (Join-Path $src "steam_appid.txt"),                                    # gbe input, not shipped by Steam
    (Join-Path $src "Alterum_Data\Plugins\x86_64\steam_api64.dll"),        # the emulator itself
    (Join-Path $src "Alterum_Data\Plugins\x86_64\steam_api64.dll.vanilla") # backup, restored below
)

Write-Output "=== exclusions ==="
foreach ($d in $xd) {
    $n = 0; $b = 0
    if (Test-Path $d) { $f = @(Get-ChildItem $d -Recurse -File -Force -ErrorAction SilentlyContinue); $n = $f.Count; $b = ($f | Measure-Object -Property Length -Sum).Sum }
    Write-Output ("  DIR  {0}  files={1} bytes={2}" -f $d.Substring($src.Length).TrimStart("\"), $n, $b)
}
foreach ($f in $xf) {
    $n = "ABSENT"; $b = ""
    if (Test-Path $f) { $n = 1; $b = (Get-Item $f).Length }
    Write-Output ("  FILE {0}  bytes={1}" -f $f.Substring($src.Length).TrimStart("\"), $b)
}

Get-PSDrive C | ForEach-Object { Show "C.free_before_GB" ([math]::Round($_.Free/1GB,2)) }
if (Test-Path $dst) { Write-Output "destination exists already: $dst"; exit 2 }

$sw = [Diagnostics.Stopwatch]::StartNew()
$args = @("`"$src`"", "`"$dst`"", "/E", "/COPY:DAT", "/DCOPY:T", "/R:1", "/W:1", "/NFL", "/NDL", "/NP", "/MT:16", "/XJ")
$args += "/XD"
foreach ($d in $xd) { $args += "`"$d`"" }
$args += "/XF"
foreach ($f in $xf) { $args += "`"$f`"" }

$rc = Start-Process -FilePath "robocopy.exe" -ArgumentList $args -Wait -PassThru -NoNewWindow
$sw.Stop()
Show "robocopy_exit" $rc.ExitCode
Show "copy_seconds" ([math]::Round($sw.Elapsed.TotalSeconds,1))

# ---- restore the genuine Steam DLL --------------------------------------
$genuineFrom = Join-Path $src "Alterum_Data\Plugins\x86_64\steam_api64.dll.vanilla"
$genuineTo   = Join-Path $dst "Alterum_Data\Plugins\x86_64\steam_api64.dll"
if (Test-Path $genuineFrom) {
    Copy-Item $genuineFrom $genuineTo -Force
    Show "restored_steam_api64_dll_bytes" (Get-Item $genuineTo).Length
    Show "restored_steam_api64_dll_sha256" (Get-FileHash $genuineTo -Algorithm SHA256).Hash
} else { Write-Output "WARNING: no .vanilla backup found; pristine has no steam_api64.dll" }

Get-PSDrive C | ForEach-Object { Show "C.free_after_GB" ([math]::Round($_.Free/1GB,2)) }

# ---- counts --------------------------------------------------------------
$srcAll = @(Get-ChildItem $src -Recurse -File -Force -ErrorAction SilentlyContinue)
$dstAll = @(Get-ChildItem $dst -Recurse -File -Force -ErrorAction SilentlyContinue)
Show "pin_files" $srcAll.Count
Show "pristine_files" $dstAll.Count
Show "pristine_bytes" (($dstAll | Measure-Object -Property Length -Sum).Sum)
$d = @(Get-ChildItem (Join-Path $dst "Alterum_Data") -Recurse -File -Force -ErrorAction SilentlyContinue)
Show "pristine_Alterum_Data_files" $d.Count
Show "pristine_Alterum_Data_bytes" (($d | Measure-Object -Property Length -Sum).Sum)

# ---- cross-check: every pristine file must be byte-identical to the pin --
# (except steam_api64.dll, which is deliberately the vanilla one)
$pinMap = @{}
foreach ($f in $srcAll) { $pinMap[$f.FullName.Substring($src.Length).TrimStart("\")] = $f.FullName }
$mismatch = 0; $hashed = 0
$rows = New-Object System.Collections.Generic.List[object]
foreach ($f in $dstAll) {
    $rel = $f.FullName.Substring($dst.Length).TrimStart("\")
    $hd = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    if ($rel -ieq "Alterum_Data\Plugins\x86_64\steam_api64.dll") {
        $rows.Add([pscustomobject]@{ Path = $rel; Bytes = $f.Length; Sha256 = $hd; Note = "vanilla (restored from .vanilla)" })
        continue
    }
    if (-not $pinMap.ContainsKey($rel)) { Show "UNEXPECTED_IN_PRISTINE" $rel; $mismatch++; continue }
    $hs = (Get-FileHash $pinMap[$rel] -Algorithm SHA256).Hash
    if ($hs -ne $hd) { Show "HASHMISMATCH_vs_pin" $rel; $mismatch++ }
    $hashed++
    $rows.Add([pscustomobject]@{ Path = $rel; Bytes = $f.Length; Sha256 = $hd; Note = "matches pin" })
}
Show "hashed_vs_pin" $hashed
Show "mismatches" $mismatch

$out = "C:\Windows\Temp\alterum-pristine-manifest.md"
$md = New-Object System.Collections.Generic.List[string]
$md.Add("# C:\AlterumPinned-pristine - pristine game archive")
$md.Add("")
$md.Add("The game files exactly as shipped (Steam buildid 26597574), with every BepInEx / UnityDoorstop")
$md.Add("/ gbe_fork artifact removed. This is the baseline a future Steam update is compared against;")
$md.Add("it is NOT runnable as a mod install (no loader, no mod, genuine Steam DLL).")
$md.Add("")
$md.Add("Generated " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss") + ".")
$md.Add("")
$md.Add("| field | value |")
$md.Add("|---|---|")
$md.Add("| pristine path | ``C:\AlterumPinned-pristine`` |")
$md.Add("| derived from | ``C:\AlterumPinned`` |")
$md.Add("| game version (Steam buildid) | ``26597574`` |")
$md.Add("| unity version | ``6000.3.16f1 (a56f230f6470)`` |")
$md.Add("| Alterum.exe SHA256 | ``3FCCA364ACFF91980EF9C995C573A0C5AD1B0B96728A504C74F8BBFEB0B0B2D2`` |")
$md.Add("| files | " + $dstAll.Count + " |")
$md.Add("| bytes | " + (($dstAll | Measure-Object -Property Length -Sum).Sum) + " |")
$md.Add("| verified vs pin | " + $hashed + " files hashed, " + $mismatch + " mismatches |")
$md.Add("")
$md.Add("## What was excluded, and why")
$md.Add("")
$md.Add("| excluded | size | why |")
$md.Add("|---|---|---|")
$md.Add("| ``BepInEx/`` | ~6.5 GB | the whole loader + mod tree: core runtime, plugins, hot dll, per-instance config, generated interop, unity-libs, caches, logs, screenshots. None of it ships with the game. |")
$md.Add("| ``dotnet/`` | 67 MB | BepInEx 6's bundled .NET runtime, extracted by the BepInEx installer next to the game. |")
$md.Add("| ``winhttp.dll`` | 26,624 B | UnityDoorstop proxy; the game loads it as a DLL search-path hijack. This is how the loader gets in. |")
$md.Add("| ``doorstop_config.ini`` | 1,637 B | UnityDoorstop config, points at ``BepInEx\core\BepInEx.Unity.IL2CPP.dll``. |")
$md.Add("| ``.doorstop_version`` | 5 B | UnityDoorstop version marker (``4.6.0``). |")
$md.Add("| ``changelog.txt`` | 6,154 B | BepInEx 6's own changelog, extracted into the game root by its installer. |")
$md.Add("| ``steam_appid.txt`` | 7 B | gbe_fork input (``4424420``). Steam does not ship this file. |")
$md.Add("| ``Alterum_Data\Plugins\x86_64\steam_api64.dll`` | 11,431,336 B | the gbe_fork Steam emulator that replaced the genuine DLL. Replaced in the pristine by the genuine one (below). |")
$md.Add("| ``Alterum_Data\Plugins\x86_64\steam_api64.dll.vanilla`` | 319,584 B | backup of the genuine Steam DLL. Used to restore ``steam_api64.dll``, then dropped. |")
$md.Add("| ``Alterum_Data\Plugins\x86_64\steam_settings/`` | 4 files | gbe_fork identity + interface list (``account_name=playerA``, / ``steam_interfaces.txt``). |")
$md.Add("")
$md.Add("## Steam emulator restored to genuine")
$md.Add("")
$md.Add("| file | bytes | sha256 |")
$md.Add("|---|---|---|")
$genuine = Join-Path $dst "Alterum_Data\Plugins\x86_64\steam_api64.dll"
if (Test-Path $genuine) { $md.Add(("| ``steam_api64.dll`` | {0} | ``{1}`` |" -f (Get-Item $genuine).Length, (Get-FileHash $genuine -Algorithm SHA256).Hash)) }
$md.Add("")
$md.Add("## Every file in the pristine archive")
$md.Add("")
$md.Add("| bytes | sha256 | path | vs pin |")
$md.Add("|---|---|---|---|")
foreach ($r in ($rows | Sort-Object Path)) {
    $md.Add(("| {0} | ``{1}`` | ``{2}`` | {3} |" -f $r.Bytes, $r.Sha256, $r.Path, $r.Note))
}
$md.Add("")
Set-Content -Path $out -Value $md -Encoding UTF8
Show "manifest" $out
Show "manifest_lines" $md.Count
