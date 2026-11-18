# Records the window currently in the foreground (session 1) so it can be restored later.
$ErrorActionPreference = "SilentlyContinue"
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(System.IntPtr h, out int pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(System.IntPtr h, System.Text.StringBuilder s, int n);
'@

$h = [W.U]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 512
[void][W.U]::GetWindowTextW($h, $sb, 512)
$pid_ = 0
[void][W.U]::GetWindowThreadProcessId($h, [ref]$pid_)
$pname = ""
try { $pname = (Get-Process -Id $pid_).ProcessName } catch {}

$out = @{
    hwnd  = $h.ToInt64()
    pid   = $pid_
    proc  = $pname
    title = $sb.ToString()
} | ConvertTo-Json -Compress

$dir = "C:\Windows\Temp\alterum"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Set-Content -Path "$dir\fg.json" -Value $out -Encoding utf8
Write-Output "saved foreground: $out"
