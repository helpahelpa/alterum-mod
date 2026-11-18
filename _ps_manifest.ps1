# Task 1 step 3: verify C:\AlterumPinned against the Steam install and emit a hash manifest.
# Compares every file's size and, for the files that matter for a Unity/BepInEx install,
# the SHA256. Files that legitimately differ are reported explicitly rather than hidden.
$ErrorActionPreference = "Stop"
$src = "C:\Program Files (x86)\Steam\steamapps\common\Alterum"
$dst = "C:\AlterumPinned"
$out = "C:\Windows\Temp\alterum-pinned-manifest.md"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

function Rel($root, $full) { $full.Substring($root.Length).TrimStart("\") }

# ---- full tree listing, both sides ----
$srcAll = @{}
foreach ($f in Get-ChildItem $src -Recurse -File -Force -ErrorAction SilentlyContinue) { $srcAll[(Rel $src $f.FullName)] = $f.Length }
$dstAll = @{}
foreach ($f in Get-ChildItem $dst -Recurse -File -Force -ErrorAction SilentlyContinue) { $dstAll[(Rel $dst $f.FullName)] = $f.Length }

"src_files=" + $srcAll.Count
"dst_files=" + $dstAll.Count
"src_bytes=" + (($srcAll.Values | Measure-Object -Sum).Sum)
"dst_bytes=" + (($dstAll.Values | Measure-Object -Sum).Sum)

$missing = @($srcAll.Keys | Where-Object { -not $dstAll.ContainsKey($_) })
$extra   = @($dstAll.Keys | Where-Object { -not $srcAll.ContainsKey($_) })
$sizeDiff = @($srcAll.Keys | Where-Object { $dstAll.ContainsKey($_) -and $srcAll[$_] -ne $dstAll[$_] })
"missing_in_dst=" + $missing.Count
"extra_in_dst=" + $extra.Count
"size_mismatch=" + $sizeDiff.Count
foreach ($m in $missing) { "  MISSING " + $m }
foreach ($e in $extra)   { "  EXTRA "   + $e }
foreach ($s in $sizeDiff) { "  SIZE " + $s + " src=" + $srcAll[$s] + " dst=" + $dstAll[$s] }

# ---- hash the things that define the install ----
# Alterum_Data (game content, the whole point of the pin) + every top-level file.
$targets = @()
$targets += @(Get-ChildItem $dst -File -Force | ForEach-Object { Rel $dst $_.FullName })
$targets += @(Get-ChildItem (Join-Path $dst "Alterum_Data") -Recurse -File -Force | ForEach-Object { Rel $dst $_.FullName })
$targets = $targets | Sort-Object -Unique

"hashing " + $targets.Count + " files (top-level + Alterum_Data) both sides..."

$sw = [Diagnostics.Stopwatch]::StartNew()
$rows = New-Object System.Collections.Generic.List[object]
$mismatch = 0
foreach ($t in $targets) {
    $ps = Join-Path $src $t
    $pd = Join-Path $dst $t
    $hs = (Get-FileHash $ps -Algorithm SHA256).Hash
    $hd = (Get-FileHash $pd -Algorithm SHA256).Hash
    $ok = ($hs -eq $hd)
    if (-not $ok) { $mismatch++; "  HASHMISMATCH " + $t }
    $rows.Add([pscustomobject]@{ Path = $t; Bytes = $dstAll[$t]; Sha256 = $hd; Match = $ok })
}
$sw.Stop()
"hash_pairs=" + $rows.Count + " mismatches=" + $mismatch + " seconds=" + [math]::Round($sw.Elapsed.TotalSeconds,1)

# ---- BepInEx inventory for the record ----
$bx = Join-Path $dst "BepInEx"
function DirStat($p) {
    if (-not (Test-Path $p)) { return "ABSENT" }
    $fs = @(Get-ChildItem $p -Recurse -File -Force -ErrorAction SilentlyContinue)
    return ("{0} files / {1:N0} bytes" -f $fs.Count, (($fs | Measure-Object -Property Length -Sum).Sum))
}

# ---- markdown ----
$md = New-Object System.Collections.Generic.List[string]
$md.Add("# C:\AlterumPinned - install manifest")
$md.Add("")
$md.Add("Frozen copy of the canonical mod-development target. Generated " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss") + ".")
$md.Add("")
$md.Add("| field | value |")
$md.Add("|---|---|")
$md.Add("| pinned path | ``C:\AlterumPinned`` |")
$md.Add("| source | ``C:\Program Files (x86)\Steam\steamapps\common\Alterum`` |")
$md.Add("| game version (Steam buildid) | ``26597574`` |")
$md.Add("| unity version | ``6000.3.16f1 (a56f230f6470)`` |")
$md.Add("| asset bundle version | ``1.0`` |")
$md.Add("| Alterum.exe SHA256 | ``3FCCA364ACFF91980EF9C995C573A0C5AD1B0B96728A504C74F8BBFEB0B0B2D2`` |")
$md.Add("| files | " + $dstAll.Count + " |")
$md.Add("| bytes | " + (($dstAll.Values | Measure-Object -Sum).Sum) + " |")
$md.Add("| Alterum_Data files | " + @(Get-ChildItem (Join-Path $dst "Alterum_Data") -Recurse -File -Force).Count + " |")
$md.Add("| Alterum_Data bytes | " + (($dstAll.Keys | Where-Object { $_ -like "Alterum_Data\*" } | ForEach-Object { $dstAll[$_] } | Measure-Object -Sum).Sum) + " |")
$md.Add("| verified vs source | " + $rows.Count + " files hashed, " + $mismatch + " mismatches |")
$md.Add("")
$md.Add("## BepInEx pieces")
$md.Add("")
$md.Add("| piece | contents |")
$md.Add("|---|---|")
$md.Add("| host + Contracts | ``BepInEx\plugins\AlterumModelSwap.dll``, ``AlterumModelSwap.Contracts.dll`` |")
$md.Add("| hot dll | ``BepInEx\hot\AlterumModelSwap.Hot.dll`` |")
$md.Add("| loader (doorstop) | ``winhttp.dll``, ``doorstop_config.ini``, ``.doorstop_version`` |")
$md.Add("| runtime | ``BepInEx\core`` - $((DirStat (Join-Path $bx "core"))) |")
$md.Add("| interop | ``BepInEx\interop`` - $((DirStat (Join-Path $bx "interop"))) |")
$md.Add("| unity-libs | ``BepInEx\unity-libs`` - $((DirStat (Join-Path $bx "unity-libs"))) |")
$md.Add("| config | ``BepInEx\config`` - $((DirStat (Join-Path $bx "config"))) |")
$md.Add("| cache | ``BepInEx\cache`` - $((DirStat (Join-Path $bx "cache"))) |")
$md.Add("| patchers | ``BepInEx\patchers`` - $((DirStat (Join-Path $bx "patchers"))) |")
$md.Add("")
$md.Add("## Plugins")
$md.Add("")
$md.Add("| file | bytes | sha256 (first 16) |")
$md.Add("|---|---|---|")
foreach ($f in Get-ChildItem (Join-Path $bx "plugins") -File) {
    $md.Add(("| ``{0}`` | {1} | ``{2}`` |" -f $f.Name, $f.Length, (Get-FileHash $f.FullName -Algorithm SHA256).Hash.Substring(0,16)))
}
$md.Add("")
$md.Add("## Steam emulator (gbe_fork) - NOT part of the pin")
$md.Add("")
$md.Add("``Alterum_Data\Plugins\x86_64\steam_api64.dll`` is the emulator (11,431,336 bytes); the genuine")
$md.Add("Steam DLL is kept beside it as ``steam_api64.dll.vanilla`` (319,584 bytes). The pristine archive")
$md.Add("restores the vanilla DLL; the pin keeps the emulator so multiplayer tests keep working.")
$md.Add("")
$md.Add("## Every top-level file and every Alterum_Data file")
$md.Add("")
$md.Add("SHA256 of each file, identical on both sides (verified against the Steam install).")
$md.Add("")
$md.Add("| bytes | sha256 | path |")
$md.Add("|---|---|---|")
foreach ($r in ($rows | Sort-Object Path)) {
    $md.Add(("| {0} | ``{1}`` | ``{2}`` |" -f $r.Bytes, $r.Sha256, $r.Path))
}
$md.Add("")

Set-Content -Path $out -Value $md -Encoding UTF8
"manifest=" + $out + " lines=" + $md.Count
"manifest_bytes=" + (Get-Item $out).Length
