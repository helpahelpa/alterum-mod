# Shared helpers for the labelled-launch worker (win/launch-labelled.ps1), its session-0
# wrapper (win/launch-labelled-session0.ps1) and their self-test (win/test-instance-label.ps1).
#
# Dot-sourceable, no side effects except one Add-Type of a tiny user32 wrapper.
# PowerShell 5.1 only, no external modules.
#
# Design notes
# ------------
# * Approved installs are the ONLY launch targets (see alterum/LAUNCH-POLICY.md). An unapproved
#   path - the Steam install, a track install, the pristine archive - is rejected, never coerced.
# * Windows are enumerated and their titles read IN THE SESSION THAT OWNS THE PROCESS. Session 0
#   (SSH) cannot see session 1's windows, so an empty enumeration there is reported as
#   "cannot verify", never as "no window". That distinction is the whole point of this module.
# * Termination takes pid + exact exe path + exact process start time, all three. Nothing in this
#   file matches processes by name.
# * Guard evidence is parsed leniently (several plausible key spellings) but judged strictly:
#   token, pid, exe path, start time, protocol, state, label, tag and title must all agree.
#
# The guard that writes the evidence is not deployed yet (work item 4 assumes its format only):
#   <install>\BepInEx\instance-labels\<pid>-<startUtcTicks>.json   (+ a .jsonl sibling)
# Fields: protocol version, pid, process start UTC, session id, exe path, raw instance arg,
# installation identity, effective tag, expected title, observed title, hwnd, window class,
# guard assembly SHA-256, loaded hot-DLL hash, launch token, state, last verified UTC.

$ErrorActionPreference = "Stop"

$script:InstanceLabelProtocol        = 1
$script:InstanceLabelEvidenceSubdir  = "BepInEx\instance-labels"
$script:InstanceLabelDefaultResultDir = "C:\Windows\Temp\alterum\instance-label\results"
$script:InstanceLabelSkewSeconds     = 2
# Evidence only: the guard writes its own start time and this process reads it back, so the gate
# allows a small skew. Termination never uses it - a kill needs the exact start ticks.
$script:InstanceLabelStartToleranceSeconds = 2

# The two approved installs and the exact window label each one must end up with.
$script:InstanceLabelApproved = @(
    [pscustomobject]@{
        Kind    = "pinned"
        Install = "C:\AlterumPinned"
        Exe     = "C:\AlterumPinned\Alterum.exe"
        Label   = "Alterum - MAIN (all features) [pinned]"
        Tag     = "main"     # the in-process identity's reserved tag for this install
    },
    [pscustomobject]@{
        Kind    = "b"
        Install = "C:\AlterumB"
        Exe     = "C:\AlterumB\Alterum.exe"
        Label   = "Alterum B - SAHUR TEST"
        Tag     = "b"
    }
)

# Never a launch target, whatever the approved list says: Steam, system dirs, the baseline.
$script:InstanceLabelProtectedFragments = @(
    "\steam\", "steamlibrary", "steamapps", "program files", "\windows\", "\system32",
    "alterumpinned-pristine", "alterum-pristine", "windowsapps"
)

$script:InstanceLabelDefaultRoles = [ordered]@{
    permanent = "BepInEx\plugins\AlterumModelSwap.dll"
    contracts = "BepInEx\plugins\AlterumModelSwap.Contracts.dll"
    hot       = "BepInEx\hot\AlterumModelSwap.Hot.dll"
}

# ---------------------------------------------------------------- small utilities

function ConvertTo-InstanceLabelKeyName {
    param([string]$Name)
    if (-not $Name) { return "" }
    return ($Name -replace "[^A-Za-z0-9]", "").ToLowerInvariant()
}

function ConvertTo-InstanceLabelFullPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try {
        return [IO.Path]::GetFullPath($Path.Trim().Trim('"')).TrimEnd("\")
    } catch {
        return $null
    }
}

function ConvertTo-InstanceLabelUtc {
    <# Accepts DateTime, ISO-8601 string, .NET ticks, epoch seconds or epoch milliseconds. #>
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTime]) {
        if ($Value.Kind -eq [DateTimeKind]::Utc) { return $Value }
        return $Value.ToUniversalTime()
    }
    $s = [string]$Value
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $ticks = 0L
    if ([long]::TryParse($s.Trim(), [ref]$ticks)) {
        $epoch = [DateTime]::SpecifyKind((New-Object System.DateTime -ArgumentList 1970, 1, 1), [DateTimeKind]::Utc)
        if ($ticks -ge 4000000000000000000) { return $null }                     # beyond .NET ticks: nonsense
        if ($ticks -ge 1000000000000000) {                                       # .NET ticks (~2026 = 6.4e17)
            return [DateTime]::SpecifyKind((New-Object System.DateTime -ArgumentList $ticks), [DateTimeKind]::Utc)
        }
        if ($ticks -ge 100000000000) { return $epoch.AddMilliseconds([double]$ticks) }   # epoch milliseconds
        if ($ticks -gt 0) { return $epoch.AddSeconds([double]$ticks) }                   # epoch seconds
        return $null
    }
    $styles = [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal
    try { return [DateTime]::Parse($s, [Globalization.CultureInfo]::InvariantCulture, $styles) } catch { return $null }
}

function ConvertTo-InstanceLabelHwnd {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [int] -or $Value -is [long]) { return [long]$Value }
    $s = ([string]$Value).Trim()
    if ($s -match "^0[xX](?<h>[0-9A-Fa-f]+)$") {
        try { return [long]([Convert]::ToInt64($Matches["h"], 16)) } catch { return $null }
    }
    $n = 0L
    if ([long]::TryParse($s, [ref]$n)) { return $n }
    return $null
}

function Get-InstanceLabelApprovedTargets {
    <# Fresh copies, so a caller cannot mutate the module's approved list. #>
    $out = @()
    foreach ($t in $script:InstanceLabelApproved) {
        $out += [pscustomobject]@{ Kind = $t.Kind; Install = $t.Install; Exe = $t.Exe; Label = $t.Label }
    }
    return $out
}

# ---------------------------------------------------------------- target validation

function Get-InstanceLabelProtectedPathReason {
    param([string]$FullPath)
    if ([string]::IsNullOrWhiteSpace($FullPath)) { return "empty path" }
    if ($FullPath.StartsWith("\\")) { return "UNC path" }
    if ($FullPath -notmatch "^[A-Za-z]:\\") { return "not a local absolute path" }
    foreach ($frag in $script:InstanceLabelProtectedFragments) {
        if ($FullPath.ToLowerInvariant().Contains($frag)) { return "protected location ($frag)" }
    }
    if ([IO.Path]::GetFileName($FullPath) -ine "Alterum.exe") { return "executable name is not Alterum.exe" }
    return $null
}

function Resolve-InstanceLabelTarget {
    <#
      Validates a launch target against the approved list. Returns
        { Ok; Code (0|64); Reason; ExePath; Install; Label; Kind; Tag }
      `Tag` is the installation's own tag ("main" for the pinned install, "b" for the B install) -
      the value the in-process identity reserves to that executable. It is what the instance-argument
      check compares against, so the reserved-tag rule cannot drift from InstanceLabelIdentity.
      Code 64 means "invalid target/arguments, nothing launched" - the caller must exit with it
      BEFORE touching the filesystem or starting anything.
    #>
    param(
        [string]$GameDir = "",
        [string]$Target = "",
        [switch]$SteamLaunch
    )
    $r = [ordered]@{ Ok = $false; Code = 64; Reason = ""; ExePath = $null; Install = $null; Label = $null; Kind = $null; Tag = $null }

    if ($SteamLaunch) {
        $r.Reason = "steam-launch-rejected: a Steam launch cannot be labelled or verified; only the approved installs may be launched"
        return [pscustomobject]$r
    }

    $raw = $null
    if (-not [string]::IsNullOrWhiteSpace($Target)) {
        if (-not [string]::IsNullOrWhiteSpace($GameDir) -and $GameDir -ine "C:\AlterumPinned") {
            $r.Reason = "conflicting-target: -Target and -GameDir both given"
            return [pscustomobject]$r
        }
        $raw = $Target
    } elseif (-not [string]::IsNullOrWhiteSpace($GameDir)) {
        if ([IO.Path]::GetExtension($GameDir) -ieq ".exe") { $raw = $GameDir }
        else { $raw = Join-Path $GameDir "Alterum.exe" }
    } else {
        $r.Reason = "no-target: pass -GameDir or -Target"
        return [pscustomobject]$r
    }

    $full = ConvertTo-InstanceLabelFullPath $raw
    if (-not $full) { $r.Reason = "unparsable-target: '$raw'"; return [pscustomobject]$r }

    $protected = Get-InstanceLabelProtectedPathReason $full
    if ($protected) { $r.Reason = "protected-target: $protected ('$full')"; return [pscustomobject]$r }

    $match = $null
    foreach ($t in $script:InstanceLabelApproved) {
        if ($full -ieq $t.Exe) { $match = $t; break }
    }
    if (-not $match) {
        $approved = ($script:InstanceLabelApproved | ForEach-Object { $_.Exe }) -join ", "
        $r.Reason = "target-not-approved: '$full' is not one of $approved"
        return [pscustomobject]$r
    }

    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        $r.Reason = "target-missing: no executable at '$full'"
        return [pscustomobject]$r
    }

    $r.Ok = $true
    $r.Code = 0
    $r.Reason = "approved"
    $r.ExePath = $match.Exe
    $r.Install = $match.Install
    $r.Label = $match.Label
    $r.Kind = $match.Kind
    $r.Tag = $match.Tag
    return [pscustomobject]$r
}

function Get-InstanceLabelArgTag {
    <#
      The instance tag as the caller typed it, from a raw argument string. Last occurrence wins -
      which is only ever seen for a string that Test-InstanceLabelArgString rejects as a duplicate.
    #>
    param([string]$Arguments)
    if ([string]::IsNullOrWhiteSpace($Arguments)) { return $null }
    $tag = $null
    foreach ($m in [regex]::Matches($Arguments, "-alterumInstance(?:=|\s+)(?<t>[A-Za-z0-9_.\-]+)", "IgnoreCase")) {
        $tag = $m.Groups["t"].Value
    }
    return $tag
}

function Get-InstanceLabelInstanceOccurrences {
    <# Every -alterumInstance in a raw argument string, in order: { Inline; Value }. #>
    param([string]$Arguments)
    $out = @()
    if ([string]::IsNullOrWhiteSpace($Arguments)) { return $out }
    foreach ($m in [regex]::Matches($Arguments, "(?<flag>-alterumInstance)(?<sep>=|\s+)(?<value>[^\s]*)", "IgnoreCase")) {
        $out += [pscustomobject]@{ Inline = ($m.Groups["sep"].Value -eq "="); Value = $m.Groups["value"].Value }
    }
    return $out
}

function Test-InstanceLabelInstanceTag {
    <#
      One instance tag, judged by the guard's own rule (InstanceLabelIdentity.IsValidTag):
      [A-Za-z0-9][A-Za-z0-9_-]{0,31}. Returns $null when acceptable, else a reason.
    #>
    param([string]$Tag)
    if ($null -eq $Tag) { return $null }
    if ($Tag.Length -eq 0) { return "arguments-rejected: empty -alterumInstance value (arg-empty)" }
    if ($Tag.Length -gt 32 -or $Tag -notmatch "^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$") {
        return ("arguments-rejected: -alterumInstance='" + $Tag + "' does not match [A-Za-z0-9][A-Za-z0-9_-]{0,31} (arg-malformed)")
    }
    return $null
}

function Test-InstanceLabelReservedTag {
    <#
      'main' is reserved to C:\AlterumPinned\Alterum.exe and 'b' to C:\AlterumB\Alterum.exe. The same
      rule and the same code the guard applies (reserved-tag), so a bad combination is a prelaunch 64
      instead of a rejection after the process is up.
    #>
    param([string]$Tag, [string]$InstallationTag)
    if ([string]::IsNullOrWhiteSpace($Tag) -or [string]::IsNullOrWhiteSpace($InstallationTag)) { return $null }
    $t = $Tag.ToLowerInvariant()
    if ($t -eq "main" -and $InstallationTag -ne "main") {
        return "arguments-rejected: tag 'main' is reserved to C:\AlterumPinned\Alterum.exe (reserved-tag)"
    }
    if ($t -eq "b" -and $InstallationTag -ne "b") {
        return "arguments-rejected: tag 'b' is reserved to C:\AlterumB\Alterum.exe (reserved-tag)"
    }
    return $null
}

function Test-InstanceLabelArgString {
    <#
      Argument hygiene for the string we pass through to the game and embed in a task action.
      Returns $null when acceptable, else a reason. Exit 64 territory.

      The -alterumInstance argument is judged with the guard's own rules (InstanceLabelIdentity):
      at most one occurrence, the value grammar above, and - when the target is known
      (InstallationTag) - the reserved-tag rule. Duplicates and 'bad!' therefore exit 64 BEFORE
      anything is launched, instead of failing later as a guard rejection.
    #>
    param([string]$Arguments, [string]$InstallationTag = "")
    if ($null -eq $Arguments) { return $null }
    if ($Arguments.IndexOf('"') -ge 0) { return "arguments-malformed: a double quote is not allowed in -ExtraArgs" }
    if ($Arguments -match "[\r\n\t]") { return "arguments-malformed: line breaks/tabs are not allowed in -ExtraArgs" }
    if ($Arguments -match "-alterumLabelLaunch") {
        return "arguments-rejected: -alterumLabelLaunch is generated by the launcher and must not be supplied by the caller"
    }
    if ($Arguments -match "-alterumInstance\s*$") {
        return "arguments-rejected: -alterumInstance has no value (arg-missing-value)"
    }
    $occurrences = @(Get-InstanceLabelInstanceOccurrences $Arguments)
    if ($occurrences.Count -eq 0) { return $null }
    if ($occurrences.Count -gt 1) {
        return ("arguments-rejected: -alterumInstance appears " + $occurrences.Count + " times; the guard refuses a duplicate (arg-duplicate)")
    }
    $value = $occurrences[0].Value
    if (-not $occurrences[0].Inline -and $value.StartsWith("-", [StringComparison]::Ordinal)) {
        return "arguments-rejected: -alterumInstance is followed by another flag, not a value (arg-missing-value)"
    }
    $grammar = Test-InstanceLabelInstanceTag $value
    if ($grammar) { return $grammar }
    return (Test-InstanceLabelReservedTag -Tag $value -InstallationTag $InstallationTag)
}

function Get-InstanceLabelToken {
    <# A new correlation token, or the caller's token if it is a GUID. #>
    param([string]$Token)
    if ([string]::IsNullOrWhiteSpace($Token)) { return [guid]::NewGuid().ToString("D") }
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($Token.Trim(), [ref]$parsed)) { return $null }
    return $parsed.ToString("D")
}

# ---------------------------------------------------------------- process identity

function Get-InstanceLabelProcessPath {
    param($Process)
    if (-not $Process) { return $null }
    $path = $null
    try { $path = $Process.Path } catch { }
    if (-not $path -and $Process.ProcessName -eq "Alterum") {
        # Only pay for the CIM fallback on the name we care about; every other process without a
        # readable Path is somebody else's business. The DECISION below is still the full path.
        try { $path = (Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)").ExecutablePath } catch { }
    }
    if ($path) { return (ConvertTo-InstanceLabelFullPath $path) }
    return $null
}

function Get-InstanceLabelProcessFact {
    <# Identity of one pid: { Found; Pid; Path; StartUtc; SessionId; HasExited } #>
    param([int]$TargetPid)
    $fact = [ordered]@{ Found = $false; Pid = $TargetPid; Path = $null; StartUtc = $null; SessionId = -1; HasExited = $true }
    if ($TargetPid -le 0) { return [pscustomobject]$fact }
    $p = $null
    try { $p = Get-Process -Id $TargetPid -ErrorAction Stop } catch { return [pscustomobject]$fact }
    if (-not $p) { return [pscustomobject]$fact }
    $fact.Found = $true
    $fact.HasExited = $false
    $fact.Path = Get-InstanceLabelProcessPath $p
    try { $fact.StartUtc = $p.StartTime.ToUniversalTime() } catch { }
    try { $fact.SessionId = [int]$p.SessionId } catch { }
    return [pscustomobject]$fact
}

function Get-InstanceLabelProcessesForPath {
    <# Every running process whose full executable path equals $ExePath. Never by name. #>
    param([string]$ExePath)
    $want = ConvertTo-InstanceLabelFullPath $ExePath
    $out = @()
    if (-not $want) { return $out }
    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue)) {
        $path = Get-InstanceLabelProcessPath $p
        if ($path -and $path -ieq $want) {
            $start = $null
            try { $start = $p.StartTime.ToUniversalTime() } catch { }
            $out += [pscustomobject]@{ Pid = $p.Id; Path = $path; StartUtc = $start; SessionId = $p.SessionId }
        }
    }
    return $out
}

function Get-InstanceLabelSessionId {
    <# Session of THIS process. 0 = SSH/service session, which cannot see the game's windows. #>
    try { return [int](Get-Process -Id $PID).SessionId } catch { return -1 }
}

# ---------------------------------------------------------------- windows (session-local)

if (-not ("AlterumLabel.Win32" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AlterumLabel {
    public class WindowInfo {
        public long Hwnd;
        public string Title;
        public string ClassName;
        public bool Visible;
        public bool TopLevel;
    }
    public static class Win32 {
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        // GetParent alone is not an ownership test: for an owned overlapped top-level
        // window it returns null as well. Ownership needs GetWindow(GW_OWNER), and
        // top-level-ness needs GetAncestor(GA_ROOT). Same predicate as InstanceLabelGuard.
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        private const uint GW_OWNER = 4, GA_ROOT = 2, GA_ROOTOWNER = 3;

        public static WindowInfo[] WindowsFor(uint targetPid) {
            List<WindowInfo> found = new List<WindowInfo>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != targetPid) { return true; }
                StringBuilder title = new StringBuilder(1024);
                GetWindowTextW(hWnd, title, title.Capacity);
                StringBuilder cls = new StringBuilder(256);
                GetClassNameW(hWnd, cls, cls.Capacity);
                WindowInfo w = new WindowInfo();
                w.Hwnd = hWnd.ToInt64();
                w.Title = title.ToString();
                w.ClassName = cls.ToString();
                w.Visible = IsWindowVisible(hWnd);
                w.TopLevel = GetWindow(hWnd, GW_OWNER) == IntPtr.Zero
                             && GetAncestor(hWnd, GA_ROOT) == hWnd
                             && GetAncestor(hWnd, GA_ROOTOWNER) == hWnd;
                found.Add(w);
                return true;
            }, IntPtr.Zero);
            return found.ToArray();
        }
    }
}
"@
}

function Get-InstanceLabelWindowReport {
    <#
      Enumerate the top-level windows of one pid and read their titles, from THIS session.
      { Ok; Reason; CurrentSession; TargetSession; Windows = @({ Hwnd; Title; ClassName; Visible; TopLevel }) }

      Ok = $false means "this check could not be performed" and is never a pass. In particular a
      session-0 caller gets Ok = $false: an empty enumeration there is blindness, not "no window".
    #>
    param([int]$TargetPid)
    $report = [ordered]@{ Ok = $false; Reason = ""; CurrentSession = (Get-InstanceLabelSessionId); TargetSession = -1; Windows = @() }

    if ($report.CurrentSession -le 0) {
        $report.Reason = "session-0-cannot-enumerate-windows: window titles of another session are not visible here"
        return [pscustomobject]$report
    }

    $fact = Get-InstanceLabelProcessFact -TargetPid $TargetPid
    if (-not $fact.Found) { $report.Reason = "process-not-found (pid $TargetPid)"; return [pscustomobject]$report }

    $report.TargetSession = $fact.SessionId
    if ($fact.SessionId -ne $report.CurrentSession) {
        $report.Reason = "cross-session: this process is in session $($report.CurrentSession), pid $TargetPid is in session $($fact.SessionId); its windows cannot be enumerated here"
        return [pscustomobject]$report
    }

    $windows = @()
    foreach ($w in [AlterumLabel.Win32]::WindowsFor([uint32]$TargetPid)) {
        if (-not $w.TopLevel) { continue }
        $windows += [pscustomobject]@{
            Hwnd      = ("0x{0:X}" -f $w.Hwnd)
            HwndValue = $w.Hwnd
            Title     = $w.Title
            ClassName = $w.ClassName
            Visible   = $w.Visible
        }
    }
    $report.Windows = $windows
    $report.Ok = $true
    return [pscustomobject]$report
}

# ---------------------------------------------------------------- guard evidence

function Add-InstanceLabelFieldBag {
    param($ObjectValue, [hashtable]$Bag, [int]$Depth)
    if ($null -eq $ObjectValue -or $Depth -gt 3) { return }
    if ($ObjectValue -is [System.Collections.IDictionary]) {
        foreach ($k in @($ObjectValue.Keys)) {
            $key = ConvertTo-InstanceLabelKeyName ([string]$k)
            $v = $ObjectValue[$k]
            if ($null -eq $v -or $key -eq "") { continue }
            if ($v -is [string] -or $v -is [ValueType]) {
                if (-not $Bag.ContainsKey($key)) { $Bag[$key] = $v }
            } else {
                Add-InstanceLabelFieldBag -ObjectValue $v -Bag $Bag -Depth ($Depth + 1)
            }
        }
        return
    }
    foreach ($pr in @($ObjectValue.PSObject.Properties)) {
        $key = ConvertTo-InstanceLabelKeyName $pr.Name
        $v = $pr.Value
        if ($null -eq $v -or $key -eq "") { continue }
        if ($v -is [string] -or $v -is [ValueType]) {
            if (-not $Bag.ContainsKey($key)) { $Bag[$key] = $v }
            continue
        }
        if ($v -is [System.Collections.IEnumerable] -and -not ($v -is [string])) {
            $scalars = @()
            foreach ($item in $v) {
                if ($item -is [string] -or $item -is [ValueType]) { $scalars += [string]$item }
            }
            if ($scalars.Count -gt 0 -and -not $Bag.ContainsKey($key)) { $Bag[$key] = ($scalars -join " ") }
            continue
        }
        Add-InstanceLabelFieldBag -ObjectValue $v -Bag $Bag -Depth ($Depth + 1)
    }
}

function Get-InstanceLabelBagValue {
    <# First non-empty value among the plausible spellings of a field. #>
    param([hashtable]$Bag, [string[]]$Names)
    foreach ($n in $Names) {
        $key = ConvertTo-InstanceLabelKeyName $n
        if ($key -ne "" -and $Bag.ContainsKey($key)) {
            $v = $Bag[$key]
            if ($null -ne $v -and -not [string]::IsNullOrWhiteSpace([string]$v)) { return $v }
        }
    }
    return $null
}

function ConvertTo-InstanceLabelEvidenceRecord {
    <#
      Canonical view of one guard record. Unknown key spellings are normalised away; missing
      fields stay $null so the verdict can name exactly what was absent.
    #>
    param($Bag, [string]$Path, [DateTime]$FileWriteUtc, [string]$Source = "status")
    $pidValue = Get-InstanceLabelBagValue $Bag @("pid", "processId", "process", "targetPid")
    $pidNumber = 0
    $pidOk = $false
    if ($null -ne $pidValue) { $pidOk = [int]::TryParse([string]$pidValue, [ref]$pidNumber) }
    $protocol = Get-InstanceLabelBagValue $Bag @("protocol", "protocolVersion", "formatVersion")
    $state = Get-InstanceLabelBagValue $Bag @("state", "status", "lifecycle")
    return [pscustomobject]@{
        Path                   = $Path
        Source                 = $Source
        FileWriteUtc           = $FileWriteUtc
        Protocol               = $protocol
        Pid                    = $(if ($pidOk) { $pidNumber } else { $null })
        ProcessStartUtc        = (ConvertTo-InstanceLabelUtc (Get-InstanceLabelBagValue $Bag @("processStartUtc", "processStartedUtc", "startUtc", "processStartTimeUtc", "startedUtc")))
        SessionId              = (Get-InstanceLabelBagValue $Bag @("sessionId", "session"))
        ExePath                = (Get-InstanceLabelBagValue $Bag @("exePath", "executablePath", "executable", "exe"))
        RawInstanceArg         = (Get-InstanceLabelBagValue $Bag @("rawInstanceArg", "instanceArg", "alterumInstance", "instance"))
        InstallationIdentity   = (Get-InstanceLabelBagValue $Bag @("installationIdentity", "installIdentity", "install", "installation"))
        EffectiveTag           = (Get-InstanceLabelBagValue $Bag @("effectiveTag", "tag", "instanceTag"))
        ExpectedTitle          = (Get-InstanceLabelBagValue $Bag @("expectedTitle", "expectedWindowTitle", "titleExpected"))
        ObservedTitle          = (Get-InstanceLabelBagValue $Bag @("observedTitle", "observedWindowTitle", "titleObserved", "windowTitle"))
        Hwnd                   = (ConvertTo-InstanceLabelHwnd (Get-InstanceLabelBagValue $Bag @("hwnd", "windowHandle", "handle")))
        WindowClass            = (Get-InstanceLabelBagValue $Bag @("windowClass", "className", "class"))
        GuardAssemblySha256    = (Get-InstanceLabelBagValue $Bag @("guardAssemblySha256", "guardSha256", "assemblySha256", "guardAssemblyHash"))
        HotDllSha256           = (Get-InstanceLabelBagValue $Bag @("loadedHotDllSha256", "hotDllSha256", "hotSha256", "loadedHotDllHash"))
        LaunchToken            = (Get-InstanceLabelBagValue $Bag @("launchToken", "alterumLabelLaunch", "token", "labelLaunchToken"))
        State                  = $(if ($state) { ([string]$state).ToLowerInvariant() } else { $null })
        LastVerifiedUtc        = (ConvertTo-InstanceLabelUtc (Get-InstanceLabelBagValue $Bag @("lastVerifiedUtc", "lastVerified", "verifiedUtc", "lastVerifiedAt")))
        Detail                 = (Get-InstanceLabelBagValue $Bag @("reason", "error", "detail", "message", "failureReason"))
    }
}

function Get-InstanceLabelEvidenceRecords {
    <#
      Fresh guard records for one install.
      { Ok; Dir; Exists; Records = @(record...); Notes = @(...) }
      "Fresh" = written no earlier than SinceUtc minus a small clock skew. Records are never
      deleted or modified here: another instance's evidence is its own.
    #>
    param(
        [string]$Install,
        [string]$EvidenceDir = "",
        [DateTime]$SinceUtc = ([DateTime]::MinValue),
        [int]$SkewSeconds = $script:InstanceLabelSkewSeconds
    )
    $out = [ordered]@{ Ok = $true; Dir = $null; Exists = $false; Records = @(); Notes = @() }
    $dir = $EvidenceDir
    if ([string]::IsNullOrWhiteSpace($dir)) { $dir = Join-Path $Install $script:InstanceLabelEvidenceSubdir }
    $out.Dir = $dir
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        $out.Notes += "evidence-dir-missing: $dir"
        return [pscustomobject]$out
    }
    $out.Exists = $true
    $threshold = $SinceUtc.AddSeconds(-1 * $SkewSeconds)
    $records = @()

    foreach ($f in @(Get-ChildItem -LiteralPath $dir -Filter "*.json" -File -ErrorAction SilentlyContinue)) {
        if ($f.Extension -ine ".json") { continue }
        if ($f.LastWriteTimeUtc -lt $threshold) { continue }
        $obj = $null
        try { $obj = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction Stop | ConvertFrom-Json } catch {
            $out.Notes += ("unparsable-record: " + $f.Name)
            continue
        }
        $bag = @{}
        Add-InstanceLabelFieldBag -ObjectValue $obj -Bag $bag -Depth 0
        $records += ConvertTo-InstanceLabelEvidenceRecord -Bag $bag -Path $f.FullName -FileWriteUtc $f.LastWriteTimeUtc -Source "status"
    }

    foreach ($f in @(Get-ChildItem -LiteralPath $dir -Filter "*.jsonl" -File -ErrorAction SilentlyContinue)) {
        if ($f.Extension -ine ".jsonl") { continue }
        if ($f.LastWriteTimeUtc -lt $threshold) { continue }
        $lines = @()
        try { $lines = Get-Content -LiteralPath $f.FullName -ErrorAction Stop } catch { continue }
        $i = 0
        foreach ($line in $lines) {
            $i++
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $obj = $null
            try { $obj = $line | ConvertFrom-Json } catch { continue }   # torn last line: skip
            $bag = @{}
            Add-InstanceLabelFieldBag -ObjectValue $obj -Bag $bag -Depth 0
            $rec = ConvertTo-InstanceLabelEvidenceRecord -Bag $bag -Path ("{0}#line{1}" -f $f.FullName, $i) -FileWriteUtc $f.LastWriteTimeUtc -Source "event"
            $records += $rec
        }
    }

    $out.Records = @($records | Sort-Object -Property FileWriteUtc -Descending)
    return [pscustomobject]$out
}

function Test-InstanceLabelStatusFields {
    <#
      Does this record carry what it takes to identify a launch at all? A status record does; an
      event line (the .jsonl sibling the guard appends to after every status write) does not: it has
      the pid, the state and the expected title, but no exe path, start time, protocol or token.
      A JSONL line that DOES carry all of them is a status record written as a line, and is eligible.
    #>
    param($Record)
    if ($null -eq $Record) { return $false }
    if ($null -eq $Record.Pid) { return $false }
    if ([string]::IsNullOrWhiteSpace([string]$Record.ExePath)) { return $false }
    if ($null -eq $Record.ProcessStartUtc) { return $false }
    if ($null -eq $Record.Protocol) { return $false }
    if ([string]::IsNullOrWhiteSpace([string]$Record.LaunchToken)) { return $false }
    return $true
}

function Select-InstanceLabelEvidenceRecord {
    <#
      The newest fresh record that claims this pid. A record for another pid is deliberately not
      returned: several instances write into the same directory, and their evidence must never be
      judged against this launch.

      Two rules keep an EVENT from displacing the status record it was appended after (the event
      file's write time is the newest thing in the directory, so sorting alone always picked it):
        * only a status record - or a record carrying every field a status record has - is
          eligible, and
        * a record must positively claim this pid; one without a pid cannot be tied to the launch.
    #>
    param($Records, [int]$TargetPid)
    $matching = @()
    foreach ($r in @($Records)) {
        if ($null -eq $r) { continue }
        if ([string]$r.Source -ne "status" -and -not (Test-InstanceLabelStatusFields $r)) { continue }
        if ($null -eq $r.Pid) { continue }
        if ([int]$r.Pid -ne $TargetPid) { continue }
        $matching += $r
    }
    if ($matching.Count -eq 0) { return $null }
    return ($matching | Sort-Object -Property FileWriteUtc -Descending | Select-Object -First 1)
}

# ---------------------------------------------------------------- the verification gate

function New-InstanceLabelVerdict {
    param(
        [bool]$Ok, [int]$Code, [string]$Reason, [bool]$Retryable,
        [string]$ExpectedTitle, [string[]]$ObservedTitles, $Record, $Checks
    )
    return [pscustomobject]@{
        Ok            = $Ok
        Code          = $Code
        Reason        = $Reason
        Retryable     = $Retryable
        ExpectedTitle = $ExpectedTitle
        ObservedTitles = @($ObservedTitles)
        Record        = $Record
        Checks        = @($Checks)
    }
}

function Resolve-InstanceLabelVerdict {
    <#
      The gate. Pure: identity facts in, verdict out, no I/O, no process or window access.
        Context = {
          Record; ProcessId; ExePath; ProcessStartUtc; Token; ExpectedLabel; InstanceTag;
          WindowReport; LaunchStartedUtc; MaxEvidenceAgeSeconds; HotDllSha256
        }
      Ok = $true (Code 0) only when every check agrees. Retryable failures can still resolve
      while a launch is starting; everything else fails at once.
    #>
    param([hashtable]$Context)

    $record     = $Context.Record
    $targetPid  = [int]$Context.ProcessId
    $exePath    = [string]$Context.ExePath
    $startUtc   = $Context.ProcessStartUtc
    $token      = [string]$Context.Token
    $label      = [string]$Context.ExpectedLabel
    $wantTag    = $Context.InstanceTag
    $windows    = $Context.WindowReport
    $launchUtc  = $Context.LaunchStartedUtc
    $maxAge     = 0
    if ($null -ne $Context.MaxEvidenceAgeSeconds) { $maxAge = [int]$Context.MaxEvidenceAgeSeconds }

    $checks = New-Object System.Collections.ArrayList
    $add = {
        param($name, $ok, $detail)
        [void]$checks.Add([pscustomobject]@{ check = $name; ok = [bool]$ok; detail = [string]$detail })
    }
    $nowUtc = (Get-Date).ToUniversalTime()
    $titlePattern = "^(?<label>.+?) \[instance=(?<tag>[^;\]]*); pid=(?<pid>\d+)\]$"

    # 1. evidence exists at all
    & $add "evidence-record" ($null -ne $record) $(if ($record) { $record.Path } else { "none" })
    if (-not $record) {
        return New-InstanceLabelVerdict $false 78 "no-evidence" $true $null @() $null $checks
    }

    $expectedTitle = [string]$record.ExpectedTitle
    $observedTitles = @()
    if ($windows -and $windows.Ok) { $observedTitles = @($windows.Windows | ForEach-Object { $_.Title }) }

    # 2. identity: pid, exe path, process start
    & $add "evidence-pid" ($null -ne $record.Pid -and [int]$record.Pid -eq $targetPid) ("record=" + $record.Pid + " expected=" + $targetPid)
    if ($null -eq $record.Pid) {
        return New-InstanceLabelVerdict $false 78 "evidence-pid-missing" $false $expectedTitle $observedTitles $record $checks
    }
    if ([int]$record.Pid -ne $targetPid) {
        return New-InstanceLabelVerdict $false 78 ("evidence-pid-mismatch: record says pid " + $record.Pid + ", launched pid " + $targetPid) $false $expectedTitle $observedTitles $record $checks
    }

    $recordExe = ConvertTo-InstanceLabelFullPath ([string]$record.ExePath)
    $exeOk = ($recordExe -and $recordExe -ieq (ConvertTo-InstanceLabelFullPath $exePath))
    & $add "evidence-exe-path" $exeOk ("record='" + $record.ExePath + "' expected='" + $exePath + "'")
    if (-not $exeOk) {
        return New-InstanceLabelVerdict $false 78 ("evidence-exe-mismatch: record='" + $record.ExePath + "' launched='" + $exePath + "'") $false $expectedTitle $observedTitles $record $checks
    }

    if ($null -eq $record.ProcessStartUtc -or $null -eq $startUtc) {
        & $add "evidence-process-start" $false "missing on one side"
        return New-InstanceLabelVerdict $false 78 "evidence-start-missing" $false $expectedTitle $observedTitles $record $checks
    }
    $drift = [Math]::Abs((([DateTime]$record.ProcessStartUtc) - ([DateTime]$startUtc)).TotalSeconds)
    $startOk = ($drift -le $script:InstanceLabelStartToleranceSeconds)
    & $add "evidence-process-start" $startOk ("record=" + $record.ProcessStartUtc.ToString("o") + " expected=" + ([DateTime]$startUtc).ToString("o") + " drift=" + [Math]::Round($drift, 3) + "s")
    if (-not $startOk) {
        return New-InstanceLabelVerdict $false 78 ("evidence-start-mismatch: drift " + [Math]::Round($drift, 3) + "s") $false $expectedTitle $observedTitles $record $checks
    }

    # 3. correlation token - the record must be from THIS launch, not an older one
    if ([string]::IsNullOrWhiteSpace([string]$record.LaunchToken)) {
        & $add "evidence-launch-token" $false "absent"
        return New-InstanceLabelVerdict $false 78 "evidence-token-absent: guard record carries no launch token, so it cannot be tied to this launch" $false $expectedTitle $observedTitles $record $checks
    }
    $tokenOk = ([string]$record.LaunchToken).Trim() -ieq $token
    & $add "evidence-launch-token" $tokenOk ("record='" + $record.LaunchToken + "' expected='" + $token + "'")
    if (-not $tokenOk) {
        return New-InstanceLabelVerdict $false 78 "evidence-token-mismatch" $false $expectedTitle $observedTitles $record $checks
    }

    # 4. protocol - a record without one is format drift, not something to guess at
    if ($null -eq $record.Protocol) {
        & $add "evidence-protocol" $false "absent"
        return New-InstanceLabelVerdict $false 78 "evidence-protocol-missing: the guard record does not state its protocol version" $false $expectedTitle $observedTitles $record $checks
    }
    $protoNum = 0
    $protoOk = [int]::TryParse([string]$record.Protocol, [ref]$protoNum) -and ($protoNum -eq $script:InstanceLabelProtocol)
    & $add "evidence-protocol" $protoOk ("record=" + $record.Protocol + " supported=" + $script:InstanceLabelProtocol)
    if (-not $protoOk) {
        return New-InstanceLabelVerdict $false 78 ("evidence-protocol-unsupported: " + $record.Protocol) $false $expectedTitle $observedTitles $record $checks
    }

    # 5. freshness - the record must postdate the launch and still be being verified
    $verifiedAt = $record.LastVerifiedUtc
    if ($null -eq $verifiedAt) { $verifiedAt = $record.FileWriteUtc }
    $recordAfterLaunch = ($null -eq $launchUtc) -or ([DateTime]$record.FileWriteUtc -ge ([DateTime]$launchUtc).AddSeconds(-1 * $script:InstanceLabelSkewSeconds))
    & $add "evidence-after-launch" $recordAfterLaunch ("written=" + $record.FileWriteUtc.ToString("o") + " launch=" + $(if ($launchUtc) { ([DateTime]$launchUtc).ToString("o") } else { "n/a" }))
    if (-not $recordAfterLaunch) {
        return New-InstanceLabelVerdict $false 78 "evidence-older-than-launch" $false $expectedTitle $observedTitles $record $checks
    }
    $ageSeconds = ($nowUtc - [DateTime]$verifiedAt).TotalSeconds
    $freshOk = ($maxAge -le 0) -or ($ageSeconds -le $maxAge)
    & $add "evidence-freshness" $freshOk ("verified " + [Math]::Round($ageSeconds, 1) + "s ago, limit " + $maxAge + "s")
    if (-not $freshOk) {
        return New-InstanceLabelVerdict $false 78 ("evidence-stale: last verified " + [Math]::Round($ageSeconds, 1) + "s ago") $false $expectedTitle $observedTitles $record $checks
    }

    # 6. guard state
    & $add "guard-state" ($record.State -eq "verified") ("state='" + $record.State + "'")
    if ($record.State -eq "fatal") {
        $detail = ""
        if ($record.Detail) { $detail = ": " + $record.Detail }
        return New-InstanceLabelVerdict $false 78 ("guard-fatal" + $detail) $false $expectedTitle $observedTitles $record $checks
    }
    if ($record.State -eq "quitting") {
        return New-InstanceLabelVerdict $false 78 "guard-state-quitting" $false $expectedTitle $observedTitles $record $checks
    }
    if ($record.State -ne "verified") {
        return New-InstanceLabelVerdict $false 78 ("guard-state-not-verified: '" + $record.State + "'") $true $expectedTitle $observedTitles $record $checks
    }

    # 7. the recorded expected title must be the exact contract: <label> [instance=<tag>; pid=<pid>]
    if ([string]::IsNullOrWhiteSpace($expectedTitle)) {
        & $add "expected-title" $false "absent"
        return New-InstanceLabelVerdict $false 78 "evidence-expected-title-missing" $false $expectedTitle $observedTitles $record $checks
    }
    if ($expectedTitle -notmatch $titlePattern) {
        & $add "expected-title-format" $false $expectedTitle
        return New-InstanceLabelVerdict $false 78 ("expected-title-format: '" + $expectedTitle + "'") $false $expectedTitle $observedTitles $record $checks
    }
    $titleLabel = $Matches["label"]
    $titleTag = $Matches["tag"]
    $titlePid = [int]$Matches["pid"]
    & $add "expected-title-format" $true $expectedTitle

    $labelOk = ($titleLabel -ceq $label)
    & $add "expected-title-label" $labelOk ("title='" + $titleLabel + "' expected='" + $label + "'")
    if (-not $labelOk) {
        return New-InstanceLabelVerdict $false 78 ("expected-title-label-mismatch: '" + $titleLabel + "' is not '" + $label + "'") $false $expectedTitle $observedTitles $record $checks
    }

    $titlePidOk = ($titlePid -eq $targetPid)
    & $add "expected-title-pid" $titlePidOk ("title=" + $titlePid + " expected=" + $targetPid)
    if (-not $titlePidOk) {
        return New-InstanceLabelVerdict $false 78 ("expected-title-pid-mismatch: title says " + $titlePid + ", launched pid " + $targetPid) $false $expectedTitle $observedTitles $record $checks
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$record.EffectiveTag)) {
        $tagInTitleOk = ($titleTag -ceq [string]$record.EffectiveTag)
        & $add "expected-title-tag-vs-record" $tagInTitleOk ("title='" + $titleTag + "' record='" + $record.EffectiveTag + "'")
        if (-not $tagInTitleOk) {
            return New-InstanceLabelVerdict $false 78 ("expected-title-tag-mismatch: title tag '" + $titleTag + "' vs record '" + $record.EffectiveTag + "'") $false $expectedTitle $observedTitles $record $checks
        }
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$wantTag)) {
        $tagOk = ($titleTag -ceq [string]$wantTag)
        & $add "expected-title-tag-vs-args" $tagOk ("title='" + $titleTag + "' requested='" + $wantTag + "'")
        if (-not $tagOk) {
            return New-InstanceLabelVerdict $false 78 ("expected-title-tag-mismatch: title tag '" + $titleTag + "' vs requested instance '" + $wantTag + "'") $false $expectedTitle $observedTitles $record $checks
        }
    }

    # 8. the guard's own readback must agree with what it said it would do
    $observedOk = ($record.ObservedTitle -ceq $expectedTitle)
    & $add "guard-observed-title" $observedOk ("observed='" + $record.ObservedTitle + "'")
    if (-not $observedOk) {
        return New-InstanceLabelVerdict $false 78 ("guard-observed-title-mismatch: guard read back '" + $record.ObservedTitle + "'") $true $expectedTitle $observedTitles $record $checks
    }

    # 9. the loaded hot DLL must be the deployment that was preflighted. A hash that cannot be
    #    compared on both sides is a FAILURE, never a pass: "no hash reported" would otherwise accept
    #    a process running an unknown hot module.
    if ([string]::IsNullOrWhiteSpace([string]$Context.HotDllSha256)) {
        & $add "hot-dll-hash" $false "preflight produced no hot DLL hash"
        return New-InstanceLabelVerdict $false 78 "hot-dll-preflight-hash-missing: this launch preflighted no hot DLL hash, so the loaded hot module cannot be identified" $false $expectedTitle $observedTitles $record $checks
    }
    if ([string]::IsNullOrWhiteSpace([string]$record.HotDllSha256)) {
        & $add "hot-dll-hash" $false "the guard reported no loaded hot DLL hash"
        return New-InstanceLabelVerdict $false 78 "hot-dll-record-hash-missing: the guard's record names no loaded hot DLL, so the deployment cannot be verified" $false $expectedTitle $observedTitles $record $checks
    }
    $hotOk = ([string]$record.HotDllSha256).Trim() -ieq ([string]$Context.HotDllSha256).Trim()
    & $add "hot-dll-hash" $hotOk ("record=" + $record.HotDllSha256 + " preflight=" + $Context.HotDllSha256)
    if (-not $hotOk) {
        return New-InstanceLabelVerdict $false 78 "hot-dll-mismatch: the instance loaded a different hot DLL than the preflighted one" $false $expectedTitle $observedTitles $record $checks
    }

    # 10. window titles, read here, in this session - never trusting the guard's own claim
    if (-not $windows) {
        & $add "window-report" $false "not performed"
        return New-InstanceLabelVerdict $false 78 "window-query-failed: no window report available" $false $expectedTitle $observedTitles $record $checks
    }
    & $add "window-report" $windows.Ok $windows.Reason
    if (-not $windows.Ok) {
        return New-InstanceLabelVerdict $false 78 ("window-query-failed: " + $windows.Reason) $false $expectedTitle $observedTitles $record $checks
    }
    & $add "window-count" (@($windows.Windows).Count -gt 0) (@($windows.Windows).Count.ToString() + " top-level window(s)")
    if (@($windows.Windows).Count -eq 0) {
        return New-InstanceLabelVerdict $false 78 "no-window: pid $targetPid has no top-level window in session $($windows.CurrentSession)" $true $expectedTitle $observedTitles $record $checks
    }

    if ($null -ne $record.Hwnd) {
        $hwndFound = $false
        foreach ($w in @($windows.Windows)) { if ([long]$w.HwndValue -eq [long]$record.Hwnd) { $hwndFound = $true; break } }
        & $add "window-hwnd" $hwndFound ("record=0x{0:X}" -f [long]$record.Hwnd)
        if (-not $hwndFound) {
            return New-InstanceLabelVerdict $false 78 ("window-hwnd-mismatch: guard named hwnd 0x{0:X}, not a window of pid {1}" -f [long]$record.Hwnd, $targetPid) $false $expectedTitle $observedTitles $record $checks
        }
    }

    $titleOnWindow = $false
    foreach ($w in @($windows.Windows)) {
        if ([string]$w.Title -ceq $expectedTitle) { $titleOnWindow = $true; break }
    }
    & $add "window-title-exact" $titleOnWindow ($expectedTitle)
    if (-not $titleOnWindow) {
        return New-InstanceLabelVerdict $false 78 ("window-title-mismatch: expected '" + $expectedTitle + "', observed [" + (($observedTitles | ForEach-Object { "'" + $_ + "'" }) -join ", ") + "]") $true $expectedTitle $observedTitles $record $checks
    }

    return New-InstanceLabelVerdict $true 0 ("verified: window title is exactly '" + $expectedTitle + "' (pid $targetPid, token $token)") $false $expectedTitle $observedTitles $record $checks
}

# ---------------------------------------------------------------- deployment preflight

function Invoke-InstanceLabelPreflight {
    <#
      Verify that the deployment is the one this launch believes in.
      { Ok; Code (0|70); Reason; ManifestPath; ManifestPresent; Degraded; Files; HotDllSha256; Notes; ExeSha256 }

      A manifest at <install>\BepInEx\deployment-manifest.json pins the hashes of the permanent
      host DLL, the Contracts DLL and the selected hot DLL:
        { "protocol": 1, "install": "C:\\AlterumPinned",
          "files": [ { "role": "permanent|contracts|hot", "path": "<relative>", "sha256": "<64 hex>",
                       "instance": "<tag, optional>" } ] }
      No manifest is a FAILURE (exit 70) unless -AllowManifestlessPreflight is given; in that mode
      the three required files are checked for presence and their hashes are reported, so the
      caller sees exactly what was not pinned instead of a silent pass.
    #>
    param(
        [string]$Install,
        [string]$ManifestPath = "",
        [string]$InstanceTag = "",
        [switch]$AllowManifestlessPreflight
    )
    $r = [ordered]@{
        Ok = $false; Code = 70; Reason = ""; ManifestPath = $null; ManifestPresent = $false
        Degraded = $false; Files = @(); HotDllSha256 = $null; Notes = @(); ExeSha256 = $null
    }
    $r.ManifestPath = $ManifestPath
    if ([string]::IsNullOrWhiteSpace($r.ManifestPath)) { $r.ManifestPath = Join-Path $Install "BepInEx\deployment-manifest.json" }

    $exe = Join-Path $Install "Alterum.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        $r.Reason = "install-exe-missing: $exe"
        return [pscustomobject]$r
    }
    try { $r.ExeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash } catch { }

    # Selected hot DLL: exactly what HotLoader.cs resolves at runtime - `BepInEx\hot\<instance>\` when
    # an instance name is set, else the shared `BepInEx\hot\`. There is NO fallback from the
    # per-instance directory: HotLoader never falls back either, so a launch that selects an instance
    # without its own hot copy has a missing deployment (reported as such below), not a shared one.
    $hotRelative = $script:InstanceLabelDefaultRoles["hot"]
    if (-not [string]::IsNullOrWhiteSpace($InstanceTag)) {
        $hotRelative = "BepInEx\hot\$InstanceTag\AlterumModelSwap.Hot.dll"
        $r.Notes += "hot-selected: per-instance $hotRelative (the loader has no shared fallback)"
    }

    $required = [ordered]@{
        permanent = $script:InstanceLabelDefaultRoles["permanent"]
        contracts = $script:InstanceLabelDefaultRoles["contracts"]
        hot       = $hotRelative
    }

    # Presence first, always, so a missing file is reported whatever the manifest says.
    $present = @{}
    foreach ($role in $required.Keys) {
        $full = Join-Path $Install $required[$role]
        $present[$role] = Test-Path -LiteralPath $full -PathType Leaf
        if (-not $present[$role]) {
            $r.Reason = "missing-deployment: role '$role' expected at '$full'"
            return [pscustomobject]$r
        }
    }

    $manifest = $null
    if (Test-Path -LiteralPath $r.ManifestPath -PathType Leaf) {
        try { $manifest = Get-Content -LiteralPath $r.ManifestPath -Raw -ErrorAction Stop | ConvertFrom-Json } catch {
            $r.Reason = "manifest-unparsable: $($r.ManifestPath): $($_.Exception.Message)"
            return [pscustomobject]$r
        }
        $r.ManifestPresent = $true
    }

    $files = @()
    if (-not $r.ManifestPresent) {
        if (-not $AllowManifestlessPreflight) {
            $r.Reason = "manifest-missing: no deployment manifest at '$($r.ManifestPath)'; pass -AllowManifestlessPreflight to accept a presence+hash report"
            return [pscustomobject]$r
        }
        $r.Degraded = $true
        $r.Notes += "manifest-absent: presence and hashes reported, hashes NOT verified"
        foreach ($role in $required.Keys) {
            $full = Join-Path $Install $required[$role]
            $hash = $null
            try { $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash } catch { }
            $files += [pscustomobject]@{ Role = $role; Path = $full; Present = $true; Sha256 = $hash; ExpectedSha256 = $null; Ok = $true }
            if ($role -eq "hot") { $r.HotDllSha256 = $hash }
        }
        $r.Files = $files
        $r.Ok = $true
        $r.Code = 0
        $r.Reason = "preflight-presence-only: deployment manifest absent, 3 file(s) present, hashes reported"
        return [pscustomobject]$r
    }

    # Manifest present: protocol, install identity, required roles, exact hashes.
    if ($null -ne $manifest.protocol) {
        $proto = 0
        if (-not ([int]::TryParse([string]$manifest.protocol, [ref]$proto) -and $proto -eq $script:InstanceLabelProtocol)) {
            $r.Reason = "manifest-protocol-unsupported: $($manifest.protocol)"
            return [pscustomobject]$r
        }
    }
    if ($manifest.install) {
        $manifestInstall = ConvertTo-InstanceLabelFullPath ([string]$manifest.install)
        if (-not $manifestInstall -or $manifestInstall -ine (ConvertTo-InstanceLabelFullPath $Install)) {
            $r.Reason = "manifest-install-mismatch: manifest says '$($manifest.install)', target is '$Install'"
            return [pscustomobject]$r
        }
    }
    $entries = @($manifest.files)
    if ($entries.Count -eq 0) {
        $r.Reason = "manifest-empty: '$($r.ManifestPath)' lists no files"
        return [pscustomobject]$r
    }

    foreach ($role in $required.Keys) {
        $candidates = @()
        foreach ($e in $entries) {
            if (-not $e.role) { continue }
            if ([string]$e.role -ine $role) { continue }
            if ($role -eq "hot" -and $e.instance -and ([string]$e.instance -ine $InstanceTag)) { continue }
            $candidates += $e
        }
        if ($candidates.Count -eq 0) {
            $r.Reason = "manifest-role-missing: no '$role' entry in '$($r.ManifestPath)'"
            return [pscustomobject]$r
        }
        $chosen = $candidates[0]
        if ($role -eq "hot") {
            $preferred = @($candidates | Where-Object { $_.instance })
            if ($preferred.Count -gt 0) { $chosen = $preferred[0] }
        }

        $relPath = [string]$chosen.path
        if ([string]::IsNullOrWhiteSpace($relPath)) { $relPath = $required[$role] }
        $full = Join-Path $Install $relPath
        $expected = ([string]$chosen.sha256).Trim().ToUpperInvariant()
        if ($expected -notmatch "^[0-9A-F]{64}$") {
            $r.Reason = "manifest-hash-malformed: role '$role' sha256='$($chosen.sha256)' (need 64 hex chars)"
            return [pscustomobject]$r
        }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $r.Reason = "manifest-file-missing: role '$role' at '$full'"
            return [pscustomobject]$r
        }
        $actual = $null
        try { $actual = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash } catch {
            $r.Reason = "hash-failed: role '$role' at '$full': $($_.Exception.Message)"
            return [pscustomobject]$r
        }
        if ($actual -ine $expected) {
            $r.Files += [pscustomobject]@{ Role = $role; Path = $full; Present = $true; Sha256 = $actual; ExpectedSha256 = $expected; Ok = $false }
            $r.Reason = "manifest-hash-mismatch: role '$role' at '$full': actual $actual, manifest $expected"
            return [pscustomobject]$r
        }
        $files += [pscustomobject]@{ Role = $role; Path = $full; Present = $true; Sha256 = $actual; ExpectedSha256 = $expected; Ok = $true }
        if ($role -eq "hot") {
            $r.HotDllSha256 = $actual
            $selectedFull = ConvertTo-InstanceLabelFullPath (Join-Path $Install $required[$role])
            if ($selectedFull -and $selectedFull -ine (ConvertTo-InstanceLabelFullPath $full)) {
                $r.Reason = "manifest-hot-selection-mismatch: manifest pins '$full' but this launch selects '$selectedFull'"
                return [pscustomobject]$r
            }
        }
    }

    # Optional: verify a guard assembly if the manifest pins one (it is not required to be there).
    foreach ($e in $entries) {
        if (-not $e.role -or ([string]$e.role -ine "guard")) { continue }
        $full = Join-Path $Install ([string]$e.path)
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $r.Reason = "guard-missing: manifest pins '$full' but it is not deployed"
            return [pscustomobject]$r
        }
        $actual = $null
        try { $actual = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash } catch { }
        $expected = ([string]$e.sha256).Trim().ToUpperInvariant()
        if ($expected -notmatch "^[0-9A-F]{64}$" -or $actual -ine $expected) {
            $r.Reason = "guard-hash-mismatch: '$full': actual $actual, manifest $($e.sha256)"
            return [pscustomobject]$r
        }
        $r.Notes += "guard-verified: $full"
    }

    $r.Files = $files
    $r.Ok = $true
    $r.Code = 0
    $r.Reason = ("preflight-ok: {0} file(s) match the manifest" -f @($files).Count)
    return [pscustomobject]$r
}

# ---------------------------------------------------------------- exact-path termination

function Test-InstanceLabelKillIdentity {
    <#
      Is this process handle still the one the caller meant? Exact identity: pid, exact exe path and
      exact process start time (ticks, no drift window). { Ok; Reason; Pid; Path; StartUtc }
      Reads only the handle it is given, so a pid reused between two checks cannot answer for the
      process this handle refers to.
    #>
    param($Process, [string]$ExpectedPath, [DateTime]$ExpectedStartUtc)
    $r = [ordered]@{ Ok = $false; Reason = ""; Pid = 0; Path = $null; StartUtc = $null }
    if (-not $Process) { $r.Reason = "process-not-running"; return [pscustomobject]$r }
    try { $r.Pid = [int]$Process.Id } catch { $r.Reason = "refused-pid-unknown"; return [pscustomobject]$r }

    $path = Get-InstanceLabelProcessPath $Process
    $r.Path = $path
    if (-not $path) { $r.Reason = "refused-path-unknown: cannot read the executable path of pid $($r.Pid)"; return [pscustomobject]$r }
    if ($path -ine $ExpectedPath) {
        $r.Reason = "refused-path-mismatch: pid $($r.Pid) runs '$path', expected '$ExpectedPath'"
        return [pscustomobject]$r
    }
    $start = $null
    try { $start = $Process.StartTime.ToUniversalTime() } catch { $start = $null }
    $r.StartUtc = $start
    if ($null -eq $start) { $r.Reason = "refused-start-unknown: cannot read the start time of pid $($r.Pid)"; return [pscustomobject]$r }
    if ($start.Ticks -ne $ExpectedStartUtc.Ticks) {
        $r.Reason = ("refused-start-mismatch: pid {0} started {1:o}, expected {2:o}" -f $r.Pid, $start, $ExpectedStartUtc)
        return [pscustomobject]$r
    }
    $r.Ok = $true
    $r.Reason = "identity-exact"
    return [pscustomobject]$r
}

function Stop-InstanceLabelProcess {
    <#
      Terminate ONE process, and only if pid + exact exe path + exact start time all still agree.
      { Ok; Killed; Reason; Pid; Path }

      Ok = $false means the kill was refused or not completed; the caller must say so and must not
      claim a clean cleanup. Never matches by name, never enumerates the installation: one pid in,
      that pid or nothing out.

      There is no bypass switch: the target must be an Alterum.exe outside the protected paths, the
      identity check has no drift window, the process HANDLE that was verified is the one terminated
      (never a pid that could have been reused), and the identity is re-confirmed on that same handle
      immediately before the kill.
    #>
    param(
        [int]$TargetPid,
        [string]$ExpectedExePath,
        [DateTime]$ExpectedStartUtc,
        [int]$TimeoutSeconds = 10
    )
    $r = [ordered]@{ Ok = $false; Killed = $false; Reason = ""; Pid = $TargetPid; Path = $null }

    if ($TargetPid -le 0) { $r.Reason = "refused-invalid-pid"; return [pscustomobject]$r }

    $expected = ConvertTo-InstanceLabelFullPath $ExpectedExePath
    if (-not $expected) { $r.Reason = "refused-invalid-expected-path"; return [pscustomobject]$r }
    if ([IO.Path]::GetFileName($expected) -ine "Alterum.exe") {
        $r.Reason = "refused-not-an-alterum-executable: '$expected'"
        return [pscustomobject]$r
    }
    $protected = Get-InstanceLabelProtectedPathReason $expected
    if ($protected) { $r.Reason = "refused-protected-path: $protected ('$expected')"; return [pscustomobject]$r }
    if ($ExpectedStartUtc.Ticks -le 0) { $r.Reason = "refused-start-missing: no expected start time was given"; return [pscustomobject]$r }

    # One handle, verified twice: the process object that answered the identity check is the one that
    # is terminated, so a pid reused after the check cannot be killed in its place.
    $proc = $null
    try { $proc = Get-Process -Id $TargetPid -ErrorAction Stop } catch { $proc = $null }
    if (-not $proc) { $r.Ok = $true; $r.Reason = "process-not-running"; return [pscustomobject]$r }

    $verified = Test-InstanceLabelKillIdentity -Process $proc -ExpectedPath $expected -ExpectedStartUtc $ExpectedStartUtc
    $r.Path = $verified.Path
    if (-not $verified.Ok) { $r.Reason = $verified.Reason; return [pscustomobject]$r }

    # Re-confirm immediately before the kill, on the same handle.
    $reconfirm = Test-InstanceLabelKillIdentity -Process $proc -ExpectedPath $expected -ExpectedStartUtc $ExpectedStartUtc
    if (-not $reconfirm.Ok) {
        $r.Reason = "refused-identity-changed-before-kill: $($reconfirm.Reason)"
        return [pscustomobject]$r
    }

    try { $proc.Kill() } catch {
        $r.Reason = "kill-failed: $($_.Exception.Message)"
        return [pscustomobject]$r
    }
    $gone = $false
    try { $gone = $proc.WaitForExit([Math]::Max(1, $TimeoutSeconds) * 1000) } catch { $gone = $false }
    if ($gone -or (-not (Get-InstanceLabelProcessFact -TargetPid $TargetPid).Found)) {
        $r.Ok = $true
        $r.Killed = $true
        $r.Reason = "killed: pid $TargetPid ($expected)"
        return [pscustomobject]$r
    }
    $r.Reason = "kill-not-confirmed: pid $TargetPid still present after $TimeoutSeconds s"
    return [pscustomobject]$r
}

# ---------------------------------------------------------------- result files

function Write-InstanceLabelJson {
    <# Atomic JSON write: a polling reader never sees half a file. #>
    param([string]$Path, $Value, [int]$Depth = 8)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $dir -Force)
    }
    $tmp = $Path + ".tmp"
    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    [IO.File]::Move($tmp, $Path)
}

function Get-InstanceLabelResultFileName {
    <# Token -> a filesystem-safe name. The token is already a GUID; sanitise defensively. #>
    param([string]$Token)
    $safe = ($Token -replace "[^0-9A-Za-z\-]", "_")
    if ($safe.Length -gt 72) { $safe = $safe.Substring(0, 72) }
    return $safe
}
