# Parse-check every .ps1 that was retargeted to C:\AlterumPinned, and prove the new defaults.
$ErrorActionPreference = "Stop"
$files = @(
    "C:\Windows\Temp\alterum\run-game.ps1",
    "C:\Windows\Temp\alterum\run-quiet.ps1",
    "C:\Windows\Temp\alterum\run-second.ps1",
    "C:\Windows\Temp\alterum\track-instance.ps1",
    "C:\Windows\Temp\alterum\status.ps1",
    "C:\Windows\Temp\alterum\window-rect.ps1",
    "C:\Windows\Temp\alterum\click-a.ps1",
    "C:\Windows\Temp\alterum\label-instances.ps1",
    "C:\Windows\Temp\alterum\make-instance.ps1",
    "C:\Windows\Temp\alterum\make-track-instance.ps1",
    "C:\Windows\Temp\alterum\run-a-nokill.ps1"
)
$bad = 0
foreach ($f in $files) {
    if (-not (Test-Path $f)) { Write-Output ("MISSING " + $f); $bad++; continue }
    $tokens = $null; $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        Write-Output ("PARSE-FAIL " + (Split-Path $f -Leaf) + " :: " + ($errors[0].Message))
        $bad++
    } else {
        Write-Output ("parse-ok   " + (Split-Path $f -Leaf))
    }
}
Write-Output ("parse_failures=" + $bad)

# Show the effective default GameDir/Install of each retargeted launcher by reading its AST default.
Write-Output "=== effective defaults ==="
foreach ($f in $files) {
    if (-not (Test-Path $f)) { continue }
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$null)
    $param = $ast.ParamBlock
    if (-not $param) { Write-Output ("  " + (Split-Path $f -Leaf) + ": (no param block)"); continue }
    $found = $false
    foreach ($p in $param.Parameters) {
        if ($p.Name.VariablePath.UserPath -in @("GameDir","Install","Match","Src") -and $p.DefaultValue) {
            $v = $p.DefaultValue.Extent.Text.Trim("'",'"')
            Write-Output ("  {0,-24} {1,-10} = {2}" -f (Split-Path $f -Leaf), $p.Name.VariablePath.UserPath, $v)
            $found = $true
        }
    }
    if (-not $found) { Write-Output ("  " + (Split-Path $f -Leaf) + ": no path parameter (hardcoded or none)") }
}
