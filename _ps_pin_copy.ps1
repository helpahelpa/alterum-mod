# Task 1 step 2: copy the working Steam install to C:\AlterumPinned (plain recursive copy).
# Source install stays untouched; the user's running instance holds read-only handles, which is fine.
$ErrorActionPreference = "Stop"
$src = "C:\Program Files (x86)\Steam\steamapps\common\Alterum"
$dst = "C:\AlterumPinned"

Get-PSDrive C | ForEach-Object { "C.free_before_GB=" + [math]::Round($_.Free/1GB,2) }

if (Test-Path $dst) {
    "destination already exists: $dst"
    $existing = @(Get-ChildItem $dst -Recurse -File -ErrorAction SilentlyContinue)
    "existing_files=" + $existing.Count
    "existing_bytes=" + (($existing | Measure-Object -Property Length -Sum).Sum)
} else {
    "destination absent, starting copy"
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$rc = Start-Process -FilePath "robocopy.exe" -ArgumentList @(
    "`"$src`"", "`"$dst`"", "/E", "/COPY:DAT", "/DCOPY:T", "/R:1", "/W:1",
    "/NFL", "/NDL", "/NP", "/MT:16", "/XJ"
) -Wait -PassThru -NoNewWindow
$sw.Stop()
"robocopy_exit=" + $rc.ExitCode + " (0..7 = success classes; 8+ = failure)"
"elapsed_seconds=" + [math]::Round($sw.Elapsed.TotalSeconds,1)

Get-PSDrive C | ForEach-Object { "C.free_after_GB=" + [math]::Round($_.Free/1GB,2) }

$all = @(Get-ChildItem $dst -Recurse -File -Force -ErrorAction SilentlyContinue)
"dst_files=" + $all.Count
"dst_bytes=" + (($all | Measure-Object -Property Length -Sum).Sum)
$data = @(Get-ChildItem (Join-Path $dst "Alterum_Data") -Recurse -File -Force -ErrorAction SilentlyContinue)
"dst_data_files=" + $data.Count
"dst_data_bytes=" + (($data | Measure-Object -Property Length -Sum).Sum)
"dst_exe_sha256=" + (Get-FileHash (Join-Path $dst "Alterum.exe") -Algorithm SHA256).Hash
