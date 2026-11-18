# Writes the real interactive-desktop geometry (session 1) to a JSON file.
# SSH runs in session 0, which reports a 1024x768 placeholder, so this has to run
# in the interactive session - invoke it through win/run-helper.ps1.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms | Out-Null
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$w = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$dir = "C:\Windows\Temp\alterum"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$json = @{
    x      = $b.X; y = $b.Y; width = $b.Width; height = $b.Height
    workX  = $w.X; workY = $w.Y; workWidth = $w.Width; workHeight = $w.Height
    at     = (Get-Date).ToString("o")
} | ConvertTo-Json -Compress
Set-Content -Path "$dir\desktop-size.json" -Value $json -Encoding utf8
Write-Output $json
