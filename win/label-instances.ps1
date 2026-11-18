# Label each running Alterum window with a human-readable instance name.
# Must run inside the interactive session (session 1) - session 0 cannot see
# or rename another session's windows. Use win/run-in-session1.ps1 for that.
$ErrorActionPreference = "Stop"
$log = "C:\Windows\Temp\alterum\label-instances.log"
function Say($m) { $m | Tee-Object -FilePath $log -Append | Out-Null }

Remove-Item $log -ErrorAction SilentlyContinue

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinLabel {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool SetWindowTextW(IntPtr h, string s);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  public static List<IntPtr> TopLevelFor(uint target) {
    var found = new List<IntPtr>();
    EnumWindows((h,l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target && GetParent(h) == IntPtr.Zero) found.Add(h);
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

# instance label by exe path
function LabelFor([string]$path) {
  if ($path -like "*\AlterumB\*") { return "Alterum B - SAHUR TEST" }
  if ($path -like "*\AlterumPinned\*") { return "Alterum - MAIN (all features) [pinned]" }
  if ($path -like "*steamapps\common\Alterum\*") { return "Alterum - STEAM (live, unpinned)" }
  return $null
}

$procs = Get-Process -Name Alterum -ErrorAction SilentlyContinue
if (-not $procs) { Say "no Alterum processes found"; exit }

$summary = @()
foreach ($p in $procs) {
  $label = LabelFor $p.Path
  if (-not $label) { Say ("pid={0} path='{1}' -> no label rule, skipped" -f $p.Id, $p.Path); continue }
  $wins = [WinLabel]::TopLevelFor([uint32]$p.Id)
  Say ("pid={0} exe='{1}' windows={2}" -f $p.Id, $p.Path, $wins.Count)
  foreach ($h in $wins) {
    $sb = New-Object System.Text.StringBuilder 512
    [void][WinLabel]::GetWindowTextW($h, $sb, 512)
    $before = $sb.ToString()
    $visible = [WinLabel]::IsWindowVisible($h)
    $ok = [WinLabel]::SetWindowTextW($h, $label)
    $sb2 = New-Object System.Text.StringBuilder 512
    [void][WinLabel]::GetWindowTextW($h, $sb2, 512)
    Say ("  hwnd={0} visible={1} '{2}' -> '{3}' set={4} readback='{5}'" -f $h, $visible, $before, $label, $ok, $sb2.ToString())
    $summary += [pscustomobject]@{ pid = $p.Id; label = $label; hwnd = "$h"; ok = $ok; readback = $sb2.ToString() }
  }
}

$summary | ConvertTo-Json | Set-Content "C:\Windows\Temp\alterum\instance-labels.json" -Encoding UTF8
Say ("done: {0} window(s) labelled" -f $summary.Count)
