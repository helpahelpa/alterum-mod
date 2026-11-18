# Report (and optionally resize) a game window from the interactive session.
#
# SSH runs in session 0, which cannot touch a session-1 window, so this has to be started through
# win/run-helper.ps1. Processes are matched on the full exe path, never on the process name, so
# it can never hit the wrong install.
#
#   window-rect.ps1 [-Match <exe-path substring>] [-Width 640 -Height 360]
#
# TARGET: the PINNED install (C:\AlterumPinned) by default - see
# pinned/UPDATE-POLICY.md. Pass -Match 'steamapps\common\Alterum\Alterum.exe'
# to measure the live Steam copy instead.
#
# Writes C:\Windows\Temp\alterum\window-rect.json:
#   [{ pid, path, hwnd, win {x,y,w,h}, client {w,h}, applied }]
param(
    [string]$Match = "AlterumPinned\Alterum.exe",
    [int]$Width = 0,
    [int]$Height = 0
)
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class Win {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    // Without this the helper is DPI-unaware and Windows reports (and accepts) virtualized
    // coordinates: a 950x530 client area reads as 760x424 on a 125% desktop.
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern bool AdjustWindowRectEx(ref RECT r, int style, bool menu, int exStyle);
}
"@

[Win]::SetProcessDPIAware() | Out-Null

$out = @()
foreach ($p in @(Get-Process -Name Alterum -ErrorAction SilentlyContinue)) {
    $path = $null
    try { $path = $p.Path } catch { }
    if (-not $path -or ($path -notlike "*$Match*")) { continue }

    $hwnd = $p.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { $p.Refresh(); $hwnd = $p.MainWindowHandle }
    $rec = [ordered]@{ pid = $p.Id; path = $path; hwnd = $hwnd.ToInt64(); minimized = $false; win = $null; client = $null; applied = $false }
    if ($hwnd -ne [IntPtr]::Zero) {
        $rec.minimized = [Win]::IsIconic($hwnd)
        $r = New-Object RECT
        if ([Win]::GetWindowRect($hwnd, [ref]$r)) {
            $rec.win = @{ x = $r.Left; y = $r.Top; w = $r.Right - $r.Left; h = $r.Bottom - $r.Top }
        }
        if ($Width -gt 0 -and $Height -gt 0) {
            # Size the *client* area: adjust the requested size for the border/title bar, so a
            # request of 640x360 gives a 640x360 client, which is what Unity renders into
            # (verified: client 950x530 == Screen 950x530 on this install).
            $style = [Win]::GetWindowLong($hwnd, -16)   # GWL_STYLE
            $exStyle = [Win]::GetWindowLong($hwnd, -20) # GWL_EXSTYLE
            $want = New-Object RECT
            $want.Right = $Width; $want.Bottom = $Height
            [Win]::AdjustWindowRectEx([ref]$want, $style, $false, $exStyle) | Out-Null
            $cw = $want.Right - $want.Left; $ch = $want.Bottom - $want.Top
            # SWP_NOZORDER | SWP_NOACTIVATE, size included on purpose
            $rec.applied = [Win]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $cw, $ch, 0x0004 -bor 0x0010)
            Start-Sleep -Milliseconds 700
            if ([Win]::GetWindowRect($hwnd, [ref]$r)) {
                $rec.win = @{ x = $r.Left; y = $r.Top; w = $r.Right - $r.Left; h = $r.Bottom - $r.Top }
            }
        }
        $c = New-Object RECT
        if ([Win]::GetClientRect($hwnd, [ref]$c)) {
            $rec.client = @{ w = $c.Right - $c.Left; h = $c.Bottom - $c.Top }
        }
    }
    $out += $rec
}

$json = $out | ConvertTo-Json -Depth 5 -Compress
Set-Content -Path "C:\Windows\Temp\alterum\window-rect.json" -Value $json -Encoding utf8
Write-Output $json
