param([Parameter(Mandatory=$true)][string]$Request)
$ErrorActionPreference='Stop'
$r=Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
$clock=[Diagnostics.Stopwatch]::StartNew()
$result=[ordered]@{runId=$r.runId;stage=$r.stage;utc=[DateTime]::UtcNow.ToString('o');targets=@();error=$null}
try {
    . (Join-Path $PSScriptRoot 'instance-label-common.ps1')
    if ((Get-InstanceLabelSessionId) -ne 1) {throw 'worker is not in interactive session 1'}
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DiagWindow {
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint f,bool inherit,uint access);
 [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h,int n,System.Text.StringBuilder s,int size,out int need);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string s,out int count);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
 public struct Rect {public int L,T,R,B;}
 public static string[] Args(string command) {
  int n; IntPtr p=CommandLineToArgvW(command,out n);
  if(p==IntPtr.Zero) throw new Exception("cannot parse command line");
  try {var a=new string[n];for(int i=0;i<n;i++)a[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(p,i*IntPtr.Size));return a;}
  finally {LocalFree(p);}
 }
}
'@
    [void][DiagWindow]::SetProcessDPIAware()
    function Identity($c) {
        $p=Get-Process -Id $c.ProcessId -ErrorAction Stop
        $args=[DiagWindow]::Args($c.CommandLine)
        $tags=@()
        for($i=1;$i -lt $args.Length;$i++) {
            if($args[$i] -imatch '^-alterumInstance=(.*)$') {$tags+= $Matches[1].Trim()}
            elseif($args[$i] -ieq '-alterumInstance' -and $i+1 -lt $args.Length) {$i++;$tags+= $args[$i].Trim()}
        }
        if($tags.Count -gt 1) {throw 'duplicate instance arguments'}
        $instance='';if($tags.Count -eq 1){$instance=$tags[0]}
        if($instance -and $instance -notmatch '^[A-Za-z0-9_-]+$'){throw 'unsupported unsafe instance argument'}
        $windows=Get-InstanceLabelWindowReport -TargetPid $p.Id
        $handles=@($windows.Windows | Where-Object {$_.ClassName -eq 'UnityWndClass'})
        $hwnd=$null;if($windows.Ok -and $handles.Count -eq 1){$hwnd=$handles[0].HwndValue}
        return [pscustomobject]@{pid=$p.Id;exe=(ConvertTo-InstanceLabelFullPath $c.ExecutablePath);startUtc=$p.StartTime.ToUniversalTime().ToString('o');session=$p.SessionId;instance=$instance;hwnd=$hwnd;windowCount=$handles.Count;windowReason=$windows.Reason}
    }
    function SameIdentity($a,$b) {
        return $a.pid -eq $b.pid -and $a.exe -ieq $b.exe -and $a.startUtc -eq $b.startUtc -and $a.session -eq $b.session -and $a.instance -ceq $b.instance
    }
    function Capture($identity,$path) {
        $cap=[ordered]@{status='unavailable';reason=$null;backend='PrintWindow/PW_RENDERFULLCONTENT';utc=[DateTime]::UtcNow.ToString('o');identity=$identity;path=$path;focusChanged=$false;freshnessLimit='window pixels may be cached by DWM; not proof of a fresh render or visible body'}
        $bitmap=$null;$graphics=$null;$dc=[IntPtr]::Zero
        $foreground=[DiagWindow]::GetForegroundWindow()
        try {
            if($identity.session -ne 1 -or $null -eq $identity.hwnd){throw 'no unique session-1 game window'}
            $desktop=[DiagWindow]::OpenInputDesktop(0,$false,1)
            if($desktop -eq [IntPtr]::Zero){throw 'input desktop unavailable or locked'}
            try {
                $text=New-Object System.Text.StringBuilder 256;$needed=0
                if(-not [DiagWindow]::GetUserObjectInformation($desktop,2,$text,512,[ref]$needed) -or $text.ToString() -ne 'Default'){throw 'desktop locked or not the default input desktop'}
            } finally {[void][DiagWindow]::CloseDesktop($desktop)}
            $handle=[IntPtr]([long]$identity.hwnd)
            if([DiagWindow]::IsIconic($handle)){throw 'window minimized; current pixels not reliable'}
            $rect=New-Object DiagWindow+Rect
            if(-not [DiagWindow]::GetWindowRect($handle,[ref]$rect)){throw 'window rectangle unavailable'}
            $w=$rect.R-$rect.L;$h=$rect.B-$rect.T
            if($w -lt 16 -or $h -lt 16 -or $w -gt 4096 -or $h -gt 4096){throw 'unsupported capture dimensions'}
            $bitmap=New-Object Drawing.Bitmap $w,$h
            $graphics=[Drawing.Graphics]::FromImage($bitmap);$dc=$graphics.GetHdc()
            $ok=[DiagWindow]::PrintWindow($handle,$dc,2)
            $graphics.ReleaseHdc($dc);$dc=[IntPtr]::Zero
            if(-not $ok){throw 'PrintWindow failed'}
            $cap.width=$w;$cap.height=$h
            # Detect suspicious blank/near-uniform captures, not visibility or framing acceptance.
            $colors=New-Object 'System.Collections.Generic.HashSet[int]'
            for($y=0;$y -lt $h;$y+= [math]::Max(1,[int]($h/24))) {
                for($x=0;$x -lt $w;$x+= [math]::Max(1,[int]($w/32))){[void]$colors.Add($bitmap.GetPixel($x,$y).ToArgb())}
            }
            $cap.sampleColors=$colors.Count
            $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
            $cap.sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            $cap.status='observed'
            if($colors.Count -lt 8){$cap.status='suspect';$cap.reason='suspicious blank or near-uniform window image'}
        } catch {$cap.reason=$_.Exception.Message}
        finally {
            if($dc -ne [IntPtr]::Zero -and $graphics){$graphics.ReleaseHdc($dc)}
            if($graphics){$graphics.Dispose()};if($bitmap){$bitmap.Dispose()}
            $cap.focusChanged=([DiagWindow]::GetForegroundWindow() -ne $foreground)
        }
        return [pscustomobject]$cap
    }
    $all=@(Get-CimInstance Win32_Process -Filter "Name='Alterum.exe'")
    foreach($target in $r.targets) {
        $entry=[ordered]@{role=$target.role;status='unavailable';reason=$null;candidates=@();process=$null;capture=$null}
        try {
            if($clock.Elapsed.TotalSeconds -ge $r.budgetSeconds){throw 'worker deadline reached'}
            $want=ConvertTo-InstanceLabelFullPath (Join-Path $target.root 'Alterum.exe')
            foreach($c in $all) {
                if((ConvertTo-InstanceLabelFullPath $c.ExecutablePath) -ine $want){continue}
                $fact=Identity $c
                if($null -ne $target.instance -and $fact.instance -cne $target.instance){continue}
                if($target.pid -and $fact.pid -ne $target.pid){continue}
                $entry.candidates+= $fact
            }
            if($entry.candidates.Count -eq 0){throw 'target absent or selector matched no process'}
            if($entry.candidates.Count -ne 1){throw 'ambiguous process candidates; supply instance or PID selector'}
            $identity=$entry.candidates[0]
            $entry.process=$identity
            if($target.expected -and -not (SameIdentity $identity $target.expected)){throw 'process identity changed since preflight'}
            if($identity.session -ne 1){throw 'target not in interactive session 1'}
            $entry.status='observed'
            if($r.stage -eq 'capture') {
                $entry.capture=Capture $identity (Join-Path (Split-Path $Request) ($target.role+'.png'))
                $c=Get-CimInstance Win32_Process -Filter "ProcessId=$($identity.pid)"
                if(-not $c -or -not (SameIdentity (Identity $c) $identity)){throw 'process replaced during capture'}
            }
        } catch {$entry.status='unavailable';$entry.reason=$_.Exception.Message}
        $result.targets+= [pscustomobject]$entry
        # Keep already collected targets if a later window backend blocks until the deadline.
        $result.elapsedSeconds=$clock.Elapsed.TotalSeconds
        $result | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath ($r.result+'.partial.tmp') -Encoding UTF8
        Move-Item -LiteralPath ($r.result+'.partial.tmp') -Destination ($r.result+'.partial') -Force
    }
} catch {$result.error=$_.Exception.Message}
$result.elapsedSeconds=$clock.Elapsed.TotalSeconds
$result | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath ($r.result+'.tmp') -Encoding UTF8
Move-Item -LiteralPath ($r.result+'.tmp') -Destination $r.result
