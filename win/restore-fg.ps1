# Restores foreground focus to the window saved by save-fg.ps1 (session 1 helper).
$ErrorActionPreference = "SilentlyContinue"
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
[DllImport("user32.dll")] public static extern bool IsWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(System.IntPtr h);
'@

$path = "C:\Windows\Temp\alterum\fg.json"
if (-not (Test-Path $path)) { Write-Output "no saved foreground"; exit 0 }
$json = Get-Content $path -Raw | ConvertFrom-Json
$h = [System.IntPtr]$json.hwnd

if (-not [W.U]::IsWindow($h)) { Write-Output "saved window no longer exists (hwnd=$($json.hwnd))"; exit 0 }

$cur = [W.U]::GetForegroundWindow()
if ($cur -eq $h) { Write-Output "already foreground: $($json.title)"; exit 0 }

# Classic foreground-unlock: tap ALT, then SetForegroundWindow, then release ALT.
[W.U]::keybd_event(0x12, 0, 0, [System.UIntPtr]::Zero)
[W.U]::ShowWindow($h, 9)   # SW_RESTORE
$ok = [W.U]::SetForegroundWindow($h)
[W.U]::keybd_event(0x12, 0, 2, [System.UIntPtr]::Zero)

Write-Output "restore to '$($json.title)' (proc=$($json.proc)) -> $ok"
