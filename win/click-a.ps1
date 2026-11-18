# Click the C:\AlterumB instance at a Unity screen position (origin bottom-left).
# Driven from the interactive session via run-helper.ps1; parameters come from click-a.cfg
# because a scheduled-task -File launch cannot carry arguments.
#   x=132 y=81 clicks=1
$cfg = @{}
Get-Content "C:\Windows\Temp\alterum\click-a.cfg" -ErrorAction SilentlyContinue | ForEach-Object {
  foreach ($tok in ($_ -split '\s+')) { if ($tok -match '^([a-z]+)=(.+)$') { $cfg[$matches[1]] = $matches[2] } }
}
[int]$X = 0; [int]$Y = 0; [int]$Clicks = 1
if ($cfg['x']) { [int]::TryParse($cfg['x'], [ref]$X) | Out-Null }
if ($cfg['y']) { [int]::TryParse($cfg['y'], [ref]$Y) | Out-Null }
if ($cfg['clicks']) { [int]::TryParse($cfg['clicks'], [ref]$Clicks) | Out-Null }
$ErrorActionPreference = "SilentlyContinue"
$log = "C:\Windows\Temp\alterum\click-a.out.txt"
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class C {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
  public static List<IntPtr> For(uint target) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pid == target) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
}
"@
"start $(Get-Date -Format HH:mm:ss) target=($X,$Y) clicks=$Clicks" | Out-File -Encoding utf8 $log
# DPI-unaware helpers work in virtualized 0.8x coordinates on this 125% desktop, which makes a
# 640x360 client read as 512x288; every number here must be physical pixels.
[C]::SetProcessDPIAware() | Out-Null
# "A" is the PINNED install C:\AlterumPinned (see pinned/UPDATE-POLICY.md).
$p = Get-Process -Name Alterum | Where-Object { $_.Path -like "*AlterumPinned\Alterum.exe*" } | Select-Object -First 1
if (-not $p) { "no B instance" | Out-File -Append -Encoding utf8 $log; exit 1 }
"pid=$($p.Id) path=$($p.Path)" | Out-File -Append -Encoding utf8 $log
$best = [IntPtr]::Zero
foreach ($h in [C]::For([uint32]$p.Id)) {
  $sb = New-Object System.Text.StringBuilder 256
  [void][C]::GetWindowText($h, $sb, 256)
  $t = $sb.ToString()
  if ([C]::IsWindowVisible($h) -and $t -like "*Alterum*") { "hwnd=$h title='$t'" | Out-File -Append -Encoding utf8 $log; if ($best -eq [IntPtr]::Zero) { $best = $h } }
}
if ($best -eq [IntPtr]::Zero) { "no visible B window" | Out-File -Append -Encoding utf8 $log; exit 1 }
[C]::ShowWindow($best, 9) | Out-Null
[C]::BringWindowToTop($best) | Out-Null
$fgThread = 0
[void][C]::GetWindowThreadProcessId([C]::GetForegroundWindow(), [ref]$fgThread)
$myThread = [C]::GetCurrentThreadId()
[void][C]::AttachThreadInput($myThread, [uint32]$fgThread, $true)
[void][C]::SetForegroundWindow($best)
[void][C]::SetFocus($best)
[void][C]::AttachThreadInput($myThread, [uint32]$fgThread, $false)
Start-Sleep -Milliseconds 700
"foreground=$([C]::GetForegroundWindow()) target=$best" | Out-File -Append -Encoding utf8 $log
$r = New-Object C+RECT
[void][C]::GetClientRect($best, [ref]$r)
$origin = New-Object C+POINT
$origin.x = 0; $origin.y = 0
[void][C]::ClientToScreen($best, [ref]$origin)
$cw = $r.r - $r.l; $ch = $r.b - $r.t
"client=${cw}x${ch} origin=($($origin.x),$($origin.y))" | Out-File -Append -Encoding utf8 $log
# Unity screen y grows upwards, Windows y downwards.
$px = $origin.x + $X
$py = $origin.y + ($ch - $Y)
[C]::SetCursorPos($px, $py) | Out-Null
Start-Sleep -Milliseconds 400
$now = New-Object C+POINT
[void][C]::GetCursorPos([ref]$now)
"cursor=($($now.x),$($now.y)) want=($px,$py)" | Out-File -Append -Encoding utf8 $log
# small move then click: the Input System needs motion to update the pointer position
[C]::mouse_event(0x0001, 1, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 120
[C]::mouse_event(0x0001, -1, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 120
for ($i = 0; $i -lt $Clicks; $i++) {
  [C]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # left down
  Start-Sleep -Milliseconds 90
  [C]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # left up
  Start-Sleep -Milliseconds 250
}
"clicked $Clicks at ($px,$py)" | Out-File -Append -Encoding utf8 $log
"done $(Get-Date -Format HH:mm:ss)" | Out-File -Append -Encoding utf8 $log
