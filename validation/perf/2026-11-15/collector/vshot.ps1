param([string]$Out='C:\Windows\Temp\alterum\perf\raw\win.png')
$ErrorActionPreference='Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W2 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$p = Get-Process -Name Alterum -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "no Alterum window"; exit 1 }
$h = $p.MainWindowHandle
$r = New-Object W2+RECT; [void][W2]::GetWindowRect($h, [ref]$r)
$c = New-Object W2+RECT; [void][W2]::GetClientRect($h, [ref]$c)
"pid=$($p.Id) win=$($r.R-$r.L)x$($r.B-$r.T) client=$($c.R-$c.L)x$($c.B-$c.T) title=$($p.MainWindowTitle)"
$bmp = New-Object System.Drawing.Bitmap ($r.R-$r.L), ($r.B-$r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc(); $ok = [W2]::PrintWindow($h, $dc, 3); $g.ReleaseHdc($dc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"printwindow=$ok $Out $((Get-Item $Out).Length)"
