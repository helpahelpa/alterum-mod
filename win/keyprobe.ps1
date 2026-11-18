$ErrorActionPreference = "SilentlyContinue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public struct RECT { public int L,T,R,B; }
  public struct POINT { public int X,Y; }
}
"@
$p = Get-Process -Name Alterum -ErrorAction SilentlyContinue
"pid=$($p.Id) hwnd=$($p.MainWindowHandle)"
$h = $p.MainWindowHandle
"fg=$([K]::GetForegroundWindow()) iconic=$([K]::IsIconic($h))"
$wr = New-Object K+RECT; [K]::GetWindowRect($h, [ref]$wr) | Out-Null
$cr = New-Object K+RECT; [K]::GetClientRect($h, [ref]$cr) | Out-Null
$pt = New-Object K+POINT; [K]::ClientToScreen($h, [ref]$pt) | Out-Null
"win=$($wr.L),$($wr.T),$($wr.R),$($wr.B) client=$($cr.R)x$($cr.B) at=$($pt.X),$($pt.Y)"
[K]::ShowWindow($h, 9) | Out-Null
Start-Sleep -Milliseconds 300
[K]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 800
"fg after=$([K]::GetForegroundWindow())"
[K]::keybd_event(0x79, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 80
[K]::keybd_event(0x79, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 300
"sent F10 (0x79)"
