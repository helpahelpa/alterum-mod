param([string]$Arm='V',[string]$Keys='',[string]$Click='',[string]$Hold='',[int]$HoldMs=0,[string]$Text='')
Add-Type @"
using System;using System.Runtime.InteropServices;
public class IN {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref P r);
 [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
}
"@
$p = Get-Process -Name Alterum -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "C:\AlterumPerf\$Arm\Alterum.exe" } | Select-Object -First 1
if (-not $p) { "no process $Arm"; exit 1 }
$h = $p.MainWindowHandle
[void][IN]::ShowWindow($h,9); [void][IN]::SetForegroundWindow($h); Start-Sleep -Milliseconds 300
if ($Click) {
  $xy = $Click.Split(','); $pt = New-Object IN+P; $pt.X=[int]$xy[0]; $pt.Y=[int]$xy[1]
  [void][IN]::ClientToScreen($h, [ref]$pt)
  [void][IN]::SetCursorPos($pt.X,$pt.Y); Start-Sleep -Milliseconds 120
  [IN]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [IN]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
  "clicked $Click -> screen $($pt.X),$($pt.Y)"
}
if ($Hold) {
  $vk = [byte][char]$Hold.ToUpper()
  [IN]::keybd_event($vk,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds $HoldMs; [IN]::keybd_event($vk,0,2,[UIntPtr]::Zero)
  "held $Hold ${HoldMs}ms"
}
if ($Keys) {
  foreach ($k in $Keys.Split('+')) {
    $vk = switch ($k.ToUpper()) { 'ESC' {27} 'ENTER' {13} 'SPACE' {32} 'TAB' {9} default {[byte][char]$k.ToUpper()} }
    [IN]::keybd_event($vk,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 40; [IN]::keybd_event($vk,0,2,[UIntPtr]::Zero); Start-Sleep -Milliseconds 120
  }
  "keys $Keys"
}
"fg=" + ([IN]::GetForegroundWindow() -eq $h)
