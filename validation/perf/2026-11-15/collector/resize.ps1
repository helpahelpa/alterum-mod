# Resize a game window's CLIENT area to 640x360 from the outside (same call for every arm).
param([string]$Arm='V',[int]$W=640,[int]$H=360)
Add-Type @"
using System;using System.Runtime.InteropServices;
public class RZ {
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$p = Get-Process -Name Alterum -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "C:\AlterumPerf\$Arm\Alterum.exe" } | Select-Object -First 1
if (-not $p) { "no process for arm $Arm"; exit 1 }
$h = $p.MainWindowHandle
$wr = New-Object RZ+RECT; [void][RZ]::GetWindowRect($h, [ref]$wr)
$cr = New-Object RZ+RECT; [void][RZ]::GetClientRect($h, [ref]$cr)
$bw = ($wr.R-$wr.L) - ($cr.R-$cr.L); $bh = ($wr.B-$wr.T) - ($cr.B-$cr.T)
[void][RZ]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, $W+$bw, $H+$bh, 0x0004 -bor 0x0010)
Start-Sleep -Milliseconds 800
$cr2 = New-Object RZ+RECT; [void][RZ]::GetClientRect($h, [ref]$cr2)
"arm=$Arm pid=$($p.Id) client=$($cr2.R-$cr2.L)x$($cr2.B-$cr2.T) (was $($cr.R-$cr.L)x$($cr.B-$cr.T))"
