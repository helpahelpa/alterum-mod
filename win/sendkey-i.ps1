# Send a real key press to the clone instance (default C:\AlterumI, pid passed in / auto-detected).
# Never touches the Steam install: the pid is resolved by executable path, not by name.
param(
    [int]$ProcessId = 0,
    [string]$GameDir = "C:\AlterumI",
    [int[]]$VirtualKeys = @(0x4E),   # N
    [int]$GapMs = 250
)
$ErrorActionPreference = "Stop"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class K2 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  public static IntPtr FindFor(uint want) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == want && IsWindowVisible(h) && GetWindowTextLength(h) > 0) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

$exe = Join-Path $GameDir "Alterum.exe"
$proc = if ($ProcessId -ne 0) { Get-Process -Id $ProcessId } else { Get-Process -Name Alterum | Where-Object { $_.Path -eq $exe } | Select-Object -First 1 }
if ($null -eq $proc) { "no Alterum process for $exe"; exit 1 }
$h = $proc.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { $h = [K2]::FindFor([uint32]$proc.Id); "enumerated hwnd=$h" }
"pid=$($proc.Id) hwnd=$h path=$($proc.Path)"
if ($h -eq [IntPtr]::Zero) { "no main window handle"; exit 1 }

if ([K2]::IsIconic($h)) { [K2]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 400 }
[K2]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 700
"fg=$([K2]::GetForegroundWindow()) want=$h"

foreach ($vk in $VirtualKeys) {
    [K2]::keybd_event([byte]$vk, 0x00, 0x0000, [UIntPtr]::Zero)   # down
    Start-Sleep -Milliseconds 60
    [K2]::keybd_event([byte]$vk, 0x00, 0x0002, [UIntPtr]::Zero)   # up
    "sent vk=0x{0:X2}" -f $vk
    Start-Sleep -Milliseconds $GapMs
}
