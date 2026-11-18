using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AlterumModelSwap;

// Permanent identity guard. It lives in the host assembly on purpose: the hot module is optional
// (HotLoader keeps the game running when it is absent or fails to load), so a guard there could be
// skipped or half-initialised. Nothing in this file may depend on the hot assembly.
//
// The identity rule is a pure function so it can be tested without a window or a game process.
public static class InstanceLabelIdentity
{
    // The wire format of the evidence records. Unchanged by the third installation identity:
    // `installation` was always an opaque string, so adding "portable" changes no field's type or
    // meaning and existing readers keep working.
    public const int ProtocolVersion = 1;

    // Three identities, decided only by the executable path this process was started from:
    //
    //   main      C:\AlterumPinned\Alterum.exe   this machine's pinned install
    //   b         C:\AlterumB\Alterum.exe        this machine's B (SAHUR test) install
    //   portable  any other fully qualified path whose file name is Alterum.exe
    //
    // The two local installs stay exact-path identities. No other path may claim main or b, so a
    // shipped copy can never put the MAIN [pinned] or B label on its window, and its evidence can
    // never be read as one of this machine's installs. "portable" is the honest identity for an
    // ordinary copy of the game (another PC, a Steam library, a track install): it is named
    // distinctly from both local labels and claims nothing about provenance - it is not a build,
    // signature or allowlist check, only a statement that this is an Alterum executable at a path
    // that is not one of the two pinned ones.
    //
    // Any other executable is still fatal (unknown-exe): a relative path, or a file that is not
    // named Alterum.exe, makes the record this guard writes untrustworthy, so it refuses to write
    // one. Silently labelling such a process would defeat the guard.
    public const string MainExePath = @"C:\AlterumPinned\Alterum.exe";
    public const string BExePath = @"C:\AlterumB\Alterum.exe";
    public const string MainTag = "main";
    public const string BTag = "b";
    public const string PortableTag = "portable";
    public const string MainLabel = "Alterum - MAIN (all features) [pinned]";
    public const string BLabel = "Alterum B - SAHUR TEST";
    public const string PortableLabel = "Alterum (portable)";
    public const string ExeFileName = "Alterum.exe";

    public const string InstanceFlag = "-alterumInstance";
    public const string LaunchFlag = "-alterumLabelLaunch=";

    public enum Fault
    {
        None = 0,
        ArgDuplicate,
        ArgMissingValue,
        ArgEmpty,
        ArgMalformed,
        UnknownExe,
        ReservedTag,
    }

    // Stable reason codes: they are written to the log and to the evidence files, so scripts can
    // rely on them. Keep them kebab-case and never renumber an existing one.
    public static string FaultCode(Fault fault)
    {
        switch (fault)
        {
            case Fault.ArgDuplicate: return "arg-duplicate";
            case Fault.ArgMissingValue: return "arg-missing-value";
            case Fault.ArgEmpty: return "arg-empty";
            case Fault.ArgMalformed: return "arg-malformed";
            case Fault.UnknownExe: return "unknown-exe";
            case Fault.ReservedTag: return "reserved-tag";
            default: return "none";
        }
    }

    public sealed class Result
    {
        public Fault Fault;
        public string FaultDetail;

        public string ExeRaw;
        public string Exe;                 // normalized, drive-uppercase, backslash form
        public string InstallationTag;     // "main" | "b" | "portable", null when the exe is unsupported
        public string InstallationLabel;
        public string RawInstanceArg;      // null when the argument was not supplied
        public bool HasInstanceArg;
        public string EffectiveTag;
        public string LaunchToken;         // null when absent (absence is never a failure)
        public int Pid;
        public string ExpectedTitle;

        public bool Ok => Fault == Fault.None;
        public string Code => FaultCode(Fault);
    }

    /// <summary>
    /// Resolve the identity of this process from its executable path and command line.
    /// <paramref name="argv"/> is the array returned by Environment.GetCommandLineArgs(): index 0 is
    /// the executable path and is not parsed for flags.
    /// </summary>
    public static Result Resolve(string exePath, string[] argv, int pid)
    {
        var r = new Result { ExeRaw = exePath ?? "", Pid = pid };
        var args = argv ?? Array.Empty<string>();
        r.LaunchToken = FindLaunchToken(args);

        bool seen = false;
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i] ?? "";
            if (!TryMatchInstanceFlag(token, out bool inline, out string inlineValue)) continue;

            if (seen) return Fail(r, Fault.ArgDuplicate, "second -alterumInstance at argv[" + i + "]");
            seen = true;

            string value;
            if (inline)
            {
                value = inlineValue ?? "";
            }
            else
            {
                if (i + 1 >= args.Length)
                    return Fail(r, Fault.ArgMissingValue, "no value after argv[" + i + "]");
                string next = args[i + 1] ?? "";
                if (next.StartsWith("-", StringComparison.Ordinal))
                    return Fail(r, Fault.ArgMissingValue, "argv[" + (i + 1) + "] is another flag");
                i++;
                value = next;
            }

            r.HasInstanceArg = true;
            r.RawInstanceArg = value;
            if (value.Length == 0)
                return Fail(r, Fault.ArgEmpty, "empty -alterumInstance value");
            if (!IsValidTag(value))
                return Fail(r, Fault.ArgMalformed, "-alterumInstance value does not match [A-Za-z0-9][A-Za-z0-9_-]{0,31}");

            // Lowercase for display and comparison. The raw value still drives config/log/hot
            // directory selection (HostPlugin.ParseInstance), which this function must not change.
            r.EffectiveTag = value.ToLowerInvariant();
        }

        r.Exe = NormalizeExePath(r.ExeRaw);
        if (string.Equals(r.Exe, MainExePath, StringComparison.OrdinalIgnoreCase))
        {
            r.InstallationTag = MainTag;
            r.InstallationLabel = MainLabel;
        }
        else if (string.Equals(r.Exe, BExePath, StringComparison.OrdinalIgnoreCase))
        {
            r.InstallationTag = BTag;
            r.InstallationLabel = BLabel;
        }
        else if (IsOrdinaryInstallPath(r.Exe))
        {
            r.InstallationTag = PortableTag;
            r.InstallationLabel = PortableLabel;
        }
        else
        {
            return Fail(r, Fault.UnknownExe, UnknownExeDetail(r.Exe));
        }

        if (!r.HasInstanceArg)
        {
            r.EffectiveTag = r.InstallationTag;
        }
        else
        {
            // main/b are reserved to their own install, on every installation: an ordinary copy
            // cannot borrow a local label, and the local installs cannot borrow each other's.
            // Every other valid tag is allowed everywhere.
            if (r.EffectiveTag == MainTag && r.InstallationTag != MainTag)
                return Fail(r, Fault.ReservedTag, "tag 'main' is reserved to " + MainExePath);
            if (r.EffectiveTag == BTag && r.InstallationTag != BTag)
                return Fail(r, Fault.ReservedTag, "tag 'b' is reserved to " + BExePath);
        }

        r.ExpectedTitle = BuildTitle(r.InstallationLabel, r.EffectiveTag, pid);
        return r;
    }

    /// <summary>Exactly `&lt;label&gt; [instance=&lt;tag&gt;; pid=&lt;pid&gt;]` - the pid is always present.</summary>
    public static string BuildTitle(string label, string tag, int pid)
    {
        return label + " [instance=" + tag + "; pid=" + pid.ToString(CultureInfo.InvariantCulture) + "]";
    }

    /// <summary>
    /// An ordinary (shipped) copy of the game: a fully qualified path, not one of the two pinned
    /// paths, whose file name is Alterum.exe. Callers pass the already-normalized path.
    /// </summary>
    public static bool IsOrdinaryInstallPath(string exe)
    {
        if (!IsAbsoluteExePath(exe)) return false;
        return string.Equals(FileNameOf(exe), ExeFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A fully qualified Windows path: drive-letter rooted or UNC. The identity and its evidence
    /// name the executable, so a relative path - which only a broken process environment could
    /// produce - is never accepted as an install. Host OS independent, like NormalizeExePath.
    /// </summary>
    public static bool IsAbsoluteExePath(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        if (exe.Length >= 3 && char.IsLetter(exe[0]) && exe[1] == ':' && exe[2] == '\\') return true;
        return exe.StartsWith("\\\\", StringComparison.Ordinal);
    }

    /// <summary>1..32 chars: ASCII alphanumeric first, then alphanumeric, '_' or '-'.</summary>
    public static bool IsValidTag(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 32) return false;
        if (!IsAsciiAlnum(value[0])) return false;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!IsAsciiAlnum(c) && c != '_' && c != '-') return false;
        }
        return true;
    }

    /// <summary>
    /// Windows-style absolute path normalization that does not depend on the host OS, so the rule
    /// is testable off-Windows: '/' becomes '\', a long-path prefix is dropped, the drive letter is
    /// uppercased, empty and '.' segments are removed and '..' pops. Comparison at the call site is
    /// OrdinalIgnoreCase, matching NTFS.
    /// </summary>
    public static string NormalizeExePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path.Trim().Replace('/', '\\');
        if (p.StartsWith("\\\\?\\", StringComparison.Ordinal) || p.StartsWith("\\\\.\\", StringComparison.Ordinal))
            p = p.Substring(4);
        if (p.Length < 3 || !char.IsLetter(p[0]) || p[1] != ':' || p[2] != '\\') return p;

        char drive = char.ToUpperInvariant(p[0]);
        var segments = p.Substring(3).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);
        foreach (string segment in segments)
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(segment);
        }
        return drive + ":\\" + string.Join("\\", stack);
    }

    // Deliberately bounded wording: the detail is logged and stored, so it never echoes the
    // executable path (that path is already in the same log line and evidence record).
    private static string UnknownExeDetail(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return "executable path is empty";
        if (!IsAbsoluteExePath(exe)) return "executable path is not absolute";
        return "executable file name is not " + ExeFileName;
    }

    // Normalized paths use '\\' only, so the leaf is everything after the last separator.
    private static string FileNameOf(string exe)
    {
        int slash = exe.LastIndexOf('\\');
        return slash < 0 ? exe : exe.Substring(slash + 1);
    }

    private static Result Fail(Result r, Fault fault, string detail)
    {
        r.Fault = fault;
        r.FaultDetail = detail;
        r.ExpectedTitle = null;
        return r;
    }

    private static bool TryMatchInstanceFlag(string token, out bool inline, out string value)
    {
        inline = false;
        value = null;
        if (string.IsNullOrEmpty(token)) return false;
        if (token.StartsWith(InstanceFlag + "=", StringComparison.OrdinalIgnoreCase))
        {
            inline = true;
            value = token.Substring(InstanceFlag.Length + 1);
            return true;
        }
        return token.Equals(InstanceFlag, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindLaunchToken(string[] args)
    {
        // Optional launcher correlation id. It is recorded verbatim (escaped in the evidence file)
        // and its absence or malformed shape is never fatal.
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i] ?? "";
            if (!token.StartsWith(LaunchFlag, StringComparison.OrdinalIgnoreCase)) continue;
            string value = token.Substring(LaunchFlag.Length).Trim();
            if (value.Length == 0) continue;
            return value.Length > 256 ? value.Substring(0, 256) : value;
        }
        return null;
    }

    private static bool IsAsciiAlnum(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    }
}

// Window-recreation policy, also pure: Unity replaces its UnityWndClass window during display-mode
// changes, so a missing handle is expected for a moment. A single match is authoritative; absence
// or ambiguity is only fatal once the grace period expires, which is what lets a resolution change
// complete without killing the game.
public static class WindowReacquirePolicy
{
    public enum Decision
    {
        Wait,
        Reacquire,
        Fatal,
    }

    public static Decision Decide(int matchCount, TimeSpan sinceLost, TimeSpan grace)
    {
        if (matchCount == 1) return Decision.Reacquire;
        return sinceLost >= grace ? Decision.Fatal : Decision.Wait;
    }
}

// The permanent in-process guard: validates the identity, owns the title, and re-acquires the
// window after Unity recreates it. It never restores Unity's original title, not on shutdown and
// not when the hot module is disposed. Logging is injected so this file stays free of BepInEx and
// UnityEngine and can be linked into the identity test project.
//
// Fatal reason codes (one Fatal() function, exit code 78):
//   arg-duplicate, arg-missing-value, arg-empty, arg-malformed, unknown-exe, reserved-tag,
//   window-not-found, window-ambiguous, guard-install-failed, title-readback-mismatch,
//   reacquire-failed, internal-error.
// Evidence: <install>\BepInEx\instance-labels\<pid>-<startUtcTicks>.json (atomic status, ~1/s)
// plus the append-only .jsonl next to it (initializing, verified, scene, attempted-change,
// corrected, correction-failed, window-lost, reacquire, reacquire-failed, quitting, fatal).
public static class InstanceLabelGuard
{
    public static Action<string> Info;
    public static Action<string> Warn;
    public static Action<string> Error;
    // Returns the hot dll currently loaded by HotLoader, or null when no hot module is active.
    public static Func<string> HotDllPathProvider;

    private const uint WM_SETTEXT = 0x000C;
    private const uint WM_NCDESTROY = 0x0082;
    private const string UnityWindowClass = "UnityWndClass";
    private static readonly TimeSpan ReacquireGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);
    private static readonly UIntPtr SubclassId = new UIntPtr(0xA11E0001u);
    private static readonly IntPtr SubclassMarker = new IntPtr(0x414C424C); // "ALBL"
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly object Gate = new object();

    private static InstanceLabelIdentity.Result _id;
    private static uint _pid;
    private static DateTime _processStartUtc;
    private static string _sessionId;
    private static string _guardSha;
    private static string _evidenceDir;
    private static string _statusPath;
    private static string _eventsPath;

    private static IntPtr _hwnd;
    private static string _windowClass = "";
    private static IntPtr _subclassHwnd;
    private static bool? _subclassProbeAvailable;
    private static SubclassProc _subclassProc;

    private static string _state = "initializing";
    private static bool _fatal;
    private static bool _quitting;
    private static DateTime _lastVerifiedUtc = DateTime.MinValue;
    private static DateTime _lastStatusUtc = DateTime.MinValue;
    private static bool _lost;
    private static DateTime _lostSince = DateTime.MinValue;
    private static int _corrections;
    private static int _reacquires;
    private static int _attempts;
    private static bool _suppressAttemptLog;
    private static bool _evidenceWarned;
    private static DateTime _lastReacquireFailureUtc = DateTime.MinValue;

    private static string _hotPath;
    private static string _hotSha;
    private static long _hotLength = -1;
    private static DateTime _hotMtime = DateTime.MinValue;

    public static string SessionId => _sessionId;
    public static string ExpectedTitle => _id?.ExpectedTitle;

    /// <summary>
    /// Startup gate. Must be called from HostPlugin.Load() after the logger exists and before any
    /// per-instance config/pump/hot work: every failure below terminates this process with code 78.
    /// </summary>
    public static void Initialize(string installRoot)
    {
        try
        {
            _sessionId = Guid.NewGuid().ToString("D");
            _pid = (uint)Environment.ProcessId;
            _processStartUtc = SafeProcessStartUtc();
            _guardSha = HashFile(SafeAssemblyLocation());
            InitEvidencePaths(installRoot);

            _id = InstanceLabelIdentity.Resolve(SafeProcessPath(), Environment.GetCommandLineArgs(), Environment.ProcessId);
            if (!_id.Ok)
            {
                Fatal(_id.Code, _id.FaultDetail);
                return;
            }

            _state = "initializing";
            WriteStatus();
            AppendEvent("initializing", "exe=" + _id.Exe);

            var matches = FindUnityWindows(_pid);
            if (matches.Count == 0)
            {
                Fatal("window-not-found", "no top-level unowned UnityWndClass window for pid " + _pid);
                return;
            }
            if (matches.Count > 1)
            {
                Fatal("window-ambiguous", matches.Count + " UnityWndClass windows for pid " + _pid);
                return;
            }

            _hwnd = matches[0];
            _windowClass = ReadClassName(_hwnd);
            if (!InstallSubclass(_hwnd))
            {
                Fatal("guard-install-failed", "SetWindowSubclass failed hwnd=" + Hex(_hwnd) + " win32=" + Marshal.GetLastWin32Error());
                return;
            }
            if (!TryApplyCanonical(out string observed, out string failure))
            {
                Fatal("title-readback-mismatch", failure);
                return;
            }

            _state = "verified";
            _lastVerifiedUtc = DateTime.UtcNow;
            WriteStatus();
            AppendEvent("verified", "hwnd=" + Hex(_hwnd) + " class=" + _windowClass);
            SafeInfo("[instance-label] exe=" + _id.Exe + " installation=" + _id.InstallationTag +
                     " tag=" + _id.EffectiveTag + " pid=" + _pid + " hwnd=" + Hex(_hwnd) +
                     " title='" + _id.ExpectedTitle + "' session=" + _sessionId);
        }
        catch (Exception e)
        {
            // The gate has no catch-and-continue path: an unexpected shape of the world is fatal.
            Fatal("internal-error", e.GetType().Name + ": " + OneLine(e.Message));
        }
    }

    /// <summary>
    /// The one fatal path. Logs the stable FATAL line, flushes evidence as far as it can, then
    /// terminates this process with exit code 78 using its own process handle. Never returns.
    /// </summary>
    public static void Fatal(string code, string detail)
    {
        try
        {
            if (_fatal)
            {
                TerminateNow();
            }
            _fatal = true;
            _state = "fatal";

            string expected = _id?.ExpectedTitle ?? "<unavailable>";
            string actual = "<unavailable>";
            try
            {
                if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
                {
                    string title = ReadTitle(_hwnd);
                    actual = title == null ? "<unavailable>" : title.Length == 0 ? "<empty>" : title;
                }
            }
            catch { }

            string line = "FATAL [instance-label] code=" + code +
                          " pid=" + _pid +
                          " exe=" + (_id?.Exe ?? SafeProcessPath()) +
                          " expected=" + OneLine(expected) +
                          " actual=" + OneLine(actual);
            try
            {
                if (Error != null) Error(line);
                else Console.Error.WriteLine(line);
            }
            catch { }
            if (!string.IsNullOrEmpty(detail)) SafeInfo("[instance-label] fatal-detail: " + OneLine(detail));

            try { WriteStatus(); } catch { }
            try { AppendEvent("fatal", code + (string.IsNullOrEmpty(detail) ? "" : ": " + OneLine(detail))); } catch { }
            TerminateNow();
        }
        catch
        {
            // Fall through to the hard exit below.
        }
        TerminateNow();
    }

    /// <summary>Re-assert the title after a Unity scene event and log it as evidence.</summary>
    public static void Reassert(string reason)
    {
        try
        {
            if (!Ready) return;
            Enforce(DateTime.UtcNow, reason ?? "scene", sceneEvent: true);
        }
        catch (Exception e)
        {
            SafeWarn("[instance-label] reassert failed: " + e.GetType().Name + ": " + OneLine(e.Message));
        }
    }

    /// <summary>Called from PumpBehaviour.Update before anything else.</summary>
    public static void TickEarly() => Tick("frame-early");

    /// <summary>Called from PumpBehaviour.LateUpdate; catches recreations that happen mid-frame.</summary>
    public static void TickLate() => Tick("frame-late");

    /// <summary>Records the quitting state; the canonical title is deliberately left in place.</summary>
    public static void MarkQuitting()
    {
        try
        {
            if (_id == null || _fatal) return;
            _quitting = true;
            _state = "quitting";
            WriteStatus();
            AppendEvent("quitting", "application quitting; title left at canonical");
        }
        catch { }
    }

    private static bool Ready => _id != null && _id.Ok && !_fatal && !_quitting;

    private static void Tick(string where)
    {
        try
        {
            if (!Ready) return;
            DateTime now = DateTime.UtcNow;
            Enforce(now, where, sceneEvent: false);
            WriteStatusIfDue(now);
        }
        catch (Exception e)
        {
            SafeWarn("[instance-label] tick failed: " + e.GetType().Name + ": " + OneLine(e.Message));
        }
    }

    private static void Enforce(DateTime now, string reason, bool sceneEvent)
    {
        if (sceneEvent) AppendEvent("scene", reason);

        if (!IsOurWindow(_hwnd))
        {
            MarkLost(now, reason);
            TryReacquire(now, reason);
            return;
        }

        if (!HasSubclass(_hwnd))
        {
            if (!InstallSubclass(_hwnd))
            {
                MarkLost(now, reason + ": subclass reattach failed");
                ReacquireFailure("guard-install-failed",
                    "subclass reattach failed hwnd=" + Hex(_hwnd) + " during " + reason, now, reason);
                TryReacquire(now, reason);
                return;
            }
            _reacquires++;
            AppendEvent("reacquire", "subclass reattached hwnd=" + Hex(_hwnd) + " during " + reason);
        }

        string observed = ReadTitle(_hwnd);
        if (string.Equals(observed, _id.ExpectedTitle, StringComparison.Ordinal))
        {
            _lastVerifiedUtc = now;
        }
        else if (TryApplyCanonical(out _, out string failure))
        {
            _corrections++;
            AppendEvent("corrected", reason + ": '" + Clip(observed) + "' -> expected");
        }
        else
        {
            AppendEvent("correction-failed", reason + ": " + failure);
            MarkLost(now, reason + ": correction failed");
            ReacquireFailure("reacquire-failed", failure, now, reason);
        }

        if (_lost) TryReacquire(now, reason);
    }

    private static void MarkLost(DateTime now, string reason)
    {
        if (_lost) return;
        _lost = true;
        _lostSince = now;
        AppendEvent("window-lost", reason + " hwnd=" + Hex(_hwnd));
        SafeWarn("[instance-label] guarded window lost (" + reason + "); re-acquiring");
    }

    private static void TryReacquire(DateTime now, string reason)
    {
        var matches = FindUnityWindows(_pid);
        TimeSpan elapsed = _lostSince == DateTime.MinValue ? TimeSpan.Zero : now - _lostSince;
        switch (WindowReacquirePolicy.Decide(matches.Count, elapsed, ReacquireGrace))
        {
            case WindowReacquirePolicy.Decision.Reacquire:
                IntPtr hwnd = matches[0];
                string cls = ReadClassName(hwnd);
                if (!InstallSubclass(hwnd))
                {
                    // The candidate can die between enumeration and installation while Unity is
                    // recreating the window; retry inside the grace period instead of killing it.
                    ReacquireFailure("guard-install-failed",
                        "SetWindowSubclass failed hwnd=" + Hex(hwnd) + " win32=" + Marshal.GetLastWin32Error(), now, reason);
                    return;
                }
                _hwnd = hwnd;
                _windowClass = cls;
                if (!TryApplyCanonical(out _, out string failure))
                {
                    ReacquireFailure("reacquire-failed", failure + " hwnd=" + Hex(hwnd), now, reason);
                    return;
                }
                _reacquires++;
                _lost = false;
                _lostSince = DateTime.MinValue;
                _state = "verified";
                _lastVerifiedUtc = now;
                AppendEvent("reacquire", "new hwnd=" + Hex(hwnd) + " class=" + cls + " reason=" + reason);
                SafeInfo("[instance-label] window re-acquired hwnd=" + Hex(hwnd) + " title verified");
                break;

            case WindowReacquirePolicy.Decision.Fatal:
                Fatal(matches.Count == 0 ? "reacquire-failed" : "window-ambiguous",
                      "matches=" + matches.Count + " after " + elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) +
                      "s (" + reason + ")");
                break;
        }
    }

    // A failed attempt during a recreation is not itself fatal: retry until the grace expires, then
    // surface the failure through the one fatal path with the original reason code.
    private static void ReacquireFailure(string code, string detail, DateTime now, string reason)
    {
        if (_lostSince == DateTime.MinValue) _lostSince = now;
        TimeSpan elapsed = now - _lostSince;
        if (now - _lastReacquireFailureUtc >= StatusInterval)
        {
            _lastReacquireFailureUtc = now;
            AppendEvent("reacquire-failed", code + ": " + detail);
            SafeWarn("[instance-label] re-acquire attempt failed (" + code + "): " + detail);
        }
        if (elapsed >= ReacquireGrace)
            Fatal(code, detail + " after " + elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s (" + reason + ")");
    }

    private static bool TryApplyCanonical(out string observed, out string failure)
    {
        observed = null;
        failure = null;
        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            failure = "window unavailable";
            return false;
        }

        bool set;
        int win32;
        _suppressAttemptLog = true;
        try
        {
            set = SetWindowTextW(hwnd, _id.ExpectedTitle);
            win32 = Marshal.GetLastWin32Error();
        }
        finally
        {
            _suppressAttemptLog = false;
        }

        observed = ReadTitle(hwnd);
        if (string.Equals(observed, _id.ExpectedTitle, StringComparison.Ordinal))
        {
            _lastVerifiedUtc = DateTime.UtcNow;
            return true;
        }

        failure = "readback='" + Clip(observed) + "' set-result=" + set + " win32=" + win32;
        return false;
    }

    private static bool InstallSubclass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            if (_subclassProc == null) _subclassProc = new SubclassProc(SubclassProcImpl);

            // Reinstalling over a live entry would duplicate the subclass chain (and double-forward
            // messages), so always remove first when this handle was previously ours.
            if (_subclassHwnd == hwnd)
            {
                try { RemoveWindowSubclass(hwnd, _subclassProc, SubclassId); } catch { }
            }

            if (!SetWindowSubclass(hwnd, _subclassProc, SubclassId, SubclassMarker))
            {
                SafeWarn("[instance-label] SetWindowSubclass failed hwnd=" + Hex(hwnd) + " win32=" + Marshal.GetLastWin32Error());
                return false;
            }
            _subclassHwnd = hwnd;
            return true;
        }
        catch (Exception e)
        {
            SafeWarn("[instance-label] subclass install failed: " + e.GetType().Name + ": " + OneLine(e.Message));
            return false;
        }
    }

    private static bool HasSubclass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _subclassProc == null) return false;
        if (_subclassProbeAvailable == false) return _subclassHwnd == hwnd;
        try
        {
            // GetWindowSubclass is comctl32 v6; it also detects handle reuse after recreation.
            bool present = GetWindowSubclass(hwnd, _subclassProc, SubclassId, out IntPtr refData);
            _subclassProbeAvailable = true;
            return present && refData == SubclassMarker;
        }
        catch (EntryPointNotFoundException)
        {
            _subclassProbeAvailable = false;
            return _subclassHwnd == hwnd;
        }
        catch
        {
            return _subclassHwnd == hwnd;
        }
    }

    private static IntPtr SubclassProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        if (msg == WM_SETTEXT)
        {
            try
            {
                return OnSetText(hwnd, msg, wParam, lParam);
            }
            catch (Exception e)
            {
                // Never let an exception cross the native seam and never forward the same message
                // twice: the frame checks will re-assert the canonical title instead.
                SafeWarn("[instance-label] WM_SETTEXT guard failed: " + e.GetType().Name + ": " + OneLine(e.Message));
                return IntPtr.Zero;
            }
        }

        if (msg == WM_NCDESTROY)
        {
            try { if (_subclassProc != null) RemoveWindowSubclass(hwnd, _subclassProc, SubclassId); } catch { }
            if (hwnd == _hwnd)
            {
                _hwnd = IntPtr.Zero;
                _subclassHwnd = IntPtr.Zero;
            }
            return DefSubclassProc(hwnd, msg, wParam, lParam);
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static IntPtr OnSetText(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        string requested = null;
        try { requested = lParam == IntPtr.Zero ? "" : Marshal.PtrToStringUni(lParam) ?? ""; } catch { }

        string canonical = _id?.ExpectedTitle;
        bool substitute = canonical != null && !string.Equals(requested, canonical, StringComparison.Ordinal);

        IntPtr result;
        if (!substitute)
        {
            LogAttempt(requested);
            result = DefSubclassProc(hwnd, msg, wParam, lParam);
        }
        else
        {
            IntPtr memory = IntPtr.Zero;
            try
            {
                try { memory = Marshal.StringToHGlobalUni(canonical); } catch { memory = IntPtr.Zero; }
                // Forward exactly once, with our text in place of the caller's: no recursive
                // SetWindowTextW call from inside the callback.
                result = memory != IntPtr.Zero
                    ? DefSubclassProc(hwnd, msg, wParam, memory)
                    : DefSubclassProc(hwnd, msg, wParam, lParam);
            }
            finally
            {
                if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
            }
            LogAttempt(requested);
        }

        VerifyAfterSet(hwnd, requested);
        return result;
    }

    private static void LogAttempt(string requested)
    {
        if (_suppressAttemptLog) return;
        _attempts++;
        AppendEvent("attempted-change", "requested='" + Clip(requested) + "'");
    }

    private static void VerifyAfterSet(IntPtr hwnd, string requested)
    {
        try
        {
            string expected = _id?.ExpectedTitle;
            if (expected == null) return;
            string observed = ReadTitle(hwnd);
            if (string.Equals(observed, expected, StringComparison.Ordinal))
            {
                if (!string.Equals(requested, expected, StringComparison.Ordinal))
                {
                    _corrections++;
                    AppendEvent("corrected", "WM_SETTEXT '" + Clip(requested) + "' -> canonical");
                }
                _lastVerifiedUtc = DateTime.UtcNow;
            }
            else
            {
                AppendEvent("correction-failed", "WM_SETTEXT readback='" + Clip(observed) + "'");
            }
        }
        catch (Exception e)
        {
            SafeWarn("[instance-label] readback after WM_SETTEXT failed: " + e.GetType().Name + ": " + OneLine(e.Message));
        }
    }

    private static bool IsOurWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        try
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid != _pid) return false;
            if (!IsTopLevelUnowned(hwnd)) return false;
            return string.Equals(ReadClassName(hwnd), UnityWindowClass, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // Top-level and unowned, by explicit owner inspection. GetParent is not that test: it answers the
    // child/parent relation, and only for a top-level WS_POPUP window does it report the owner, so an
    // owned overlapped top-level window could pass it as unowned. GW_OWNER answers the owner itself;
    // GA_ROOT/GA_ROOTOWNER say the window is its own root and its own root owner (no owner chain to
    // another window).
    private static bool IsTopLevelUnowned(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return false;
        if (GetAncestor(hwnd, GaRoot) != hwnd) return false;
        return GetAncestor(hwnd, GaRootOwner) == hwnd;
    }

    private static List<IntPtr> FindUnityWindows(uint pid)
    {
        var found = new List<IntPtr>();
        try
        {
            // Exact owning PID, top level, unowned (GW_OWNER/GA_ROOTOWNER, not GetParent) and class
            // UnityWndClass. MainWindowHandle is deliberately not used: it can be zero or stale
            // during startup and recreation.
            var callback = new EnumWindowsProc((hwnd, lParam) =>
            {
                try
                {
                    uint owner;
                    GetWindowThreadProcessId(hwnd, out owner);
                    if (owner != pid) return true;
                    if (!IsTopLevelUnowned(hwnd)) return true;
                    if (!string.Equals(ReadClassName(hwnd), UnityWindowClass, StringComparison.Ordinal)) return true;
                    found.Add(hwnd);
                }
                catch { }
                return true;
            });
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch (Exception e)
        {
            SafeWarn("[instance-label] window enumeration failed: " + e.GetType().Name + ": " + OneLine(e.Message));
        }
        return found;
    }

    private static string ReadTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int copied = GetWindowTextW(hwnd, buffer, buffer.Capacity);
            return copied <= 0 ? "" : buffer.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        try
        {
            var buffer = new StringBuilder(256);
            int copied = GetClassNameW(hwnd, buffer, buffer.Capacity);
            return copied <= 0 ? "" : buffer.ToString();
        }
        catch
        {
            return "";
        }
    }

    // ---------------------------------------------------------------- evidence

    private static void InitEvidencePaths(string installRoot)
    {
        // <install>\BepInEx\instance-labels\<pid>-<startUtcTicks>.json + .jsonl
        _evidenceDir = Path.Combine(installRoot ?? "", "BepInEx", "instance-labels");
        string baseName = _pid.ToString(CultureInfo.InvariantCulture) + "-" +
                          _processStartUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        _statusPath = Path.Combine(_evidenceDir, baseName + ".json");
        _eventsPath = Path.Combine(_evidenceDir, baseName + ".jsonl");
        try { Directory.CreateDirectory(_evidenceDir); }
        catch (Exception e) { WarnEvidence(e); }
    }

    private static void WriteStatusIfDue(DateTime now)
    {
        if (now - _lastStatusUtc < StatusInterval) return;
        _lastStatusUtc = now;
        WriteStatus();
    }

    private static void WriteStatus()
    {
        if (string.IsNullOrEmpty(_statusPath)) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_evidenceDir);
                RefreshHotHash();
                string json = BuildStatusJson();
                string temp = _statusPath + ".tmp";
                File.WriteAllText(temp, json, Utf8NoBom);
                // Replace-in-place: a reader never sees a partially written status file.
                File.Move(temp, _statusPath, true);
            }
        }
        catch (Exception e)
        {
            WarnEvidence(e);
        }
    }

    private static string BuildStatusJson()
    {
        string observed = null;
        try { if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) observed = ReadTitle(_hwnd); } catch { }
        if (observed != null && string.Equals(observed, _id?.ExpectedTitle, StringComparison.Ordinal))
            _lastVerifiedUtc = DateTime.UtcNow;

        var fields = new List<string>
        {
            "\"protocolVersion\":" + InstanceLabelIdentity.ProtocolVersion,
            "\"pid\":" + _pid.ToString(CultureInfo.InvariantCulture),
            "\"processStartUtc\":" + Quote(_processStartUtc.ToString("o", CultureInfo.InvariantCulture)),
            "\"sessionId\":" + Quote(_sessionId),
            "\"exe\":" + Quote(_id?.Exe ?? SafeProcessPath()),
            "\"exeRaw\":" + Quote(_id?.ExeRaw),
            "\"rawInstanceArg\":" + Quote(_id?.RawInstanceArg),
            "\"instanceArgPresent\":" + Bool(_id != null && _id.HasInstanceArg),
            "\"installation\":" + Quote(_id?.InstallationTag),
            "\"installationLabel\":" + Quote(_id?.InstallationLabel),
            "\"effectiveTag\":" + Quote(_id?.EffectiveTag),
            "\"expectedTitle\":" + Quote(_id?.ExpectedTitle),
            "\"observedTitle\":" + Quote(observed),
            "\"hwnd\":" + (_hwnd == IntPtr.Zero ? "null" : _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture)),
            "\"hwndHex\":" + Quote(Hex(_hwnd)),
            "\"windowClass\":" + Quote(_windowClass),
            "\"guardAssemblySha256\":" + Quote(_guardSha),
            "\"hotDllPath\":" + Quote(_hotPath),
            "\"hotDllSha256\":" + Quote(_hotSha),
            "\"launchToken\":" + Quote(_id?.LaunchToken),
            "\"state\":" + Quote(_state),
            "\"lastVerifiedUtc\":" + Quote(_lastVerifiedUtc == DateTime.MinValue ? null : _lastVerifiedUtc.ToString("o", CultureInfo.InvariantCulture)),
            "\"faultCode\":" + Quote(_id != null && !_id.Ok ? _id.Code : null),
            "\"faultDetail\":" + Quote(_id != null && !_id.Ok ? _id.FaultDetail : null),
            "\"corrections\":" + _corrections.ToString(CultureInfo.InvariantCulture),
            "\"reacquires\":" + _reacquires.ToString(CultureInfo.InvariantCulture),
            "\"titleAttempts\":" + _attempts.ToString(CultureInfo.InvariantCulture),
        };
        return "{\n  " + string.Join(",\n  ", fields) + "\n}\n";
    }

    private static void AppendEvent(string name, string detail)
    {
        if (string.IsNullOrEmpty(_eventsPath)) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_evidenceDir);
                var fields = new List<string>
                {
                    "\"utc\":" + Quote(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
                    "\"sessionId\":" + Quote(_sessionId),
                    "\"pid\":" + _pid.ToString(CultureInfo.InvariantCulture),
                    "\"event\":" + Quote(name),
                    "\"detail\":" + Quote(detail),
                    "\"state\":" + Quote(_state),
                    "\"hwnd\":" + Quote(Hex(_hwnd)),
                    "\"expectedTitle\":" + Quote(_id?.ExpectedTitle),
                };
                File.AppendAllText(_eventsPath, "{" + string.Join(",", fields) + "}\n", Utf8NoBom);
            }
        }
        catch (Exception e)
        {
            WarnEvidence(e);
        }
    }

    private static void RefreshHotHash()
    {
        string path = null;
        try { path = HotDllPathProvider?.Invoke(); } catch { }
        if (string.IsNullOrEmpty(path))
        {
            _hotPath = null;
            _hotSha = null;
            _hotLength = -1;
            return;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                _hotPath = path;
                _hotSha = null;
                _hotLength = -1;
                return;
            }
            if (path == _hotPath && file.LastWriteTimeUtc == _hotMtime && file.Length == _hotLength) return;
            string sha = HashFile(path);
            if (sha == null) return;
            _hotPath = path;
            _hotSha = sha;
            _hotMtime = file.LastWriteTimeUtc;
            _hotLength = file.Length;
        }
        catch (Exception e)
        {
            WarnEvidence(e);
        }
    }

    private static string HashFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
        catch
        {
            return null;
        }
    }

    private static DateTime SafeProcessStartUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    private static string SafeProcessPath()
    {
        try { return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
        catch { return ""; }
    }

    private static string SafeAssemblyLocation()
    {
        try { return typeof(InstanceLabelGuard).Assembly.Location ?? ""; }
        catch { return ""; }
    }

    private static void TerminateNow()
    {
        // TerminateProcess with this process's own handle sets exit code 78; Environment.Exit is
        // only a fallback. TerminateProcess on self does not return.
        try
        {
            using (var process = Process.GetCurrentProcess())
            {
                if (TerminateProcess(process.Handle, 78)) { }
            }
        }
        catch { }
        try
        {
            if (TerminateProcess(GetCurrentProcess(), 78)) { }
        }
        catch { }
        try { Environment.Exit(78); } catch { }
        try { Process.GetCurrentProcess().Kill(); } catch { }
    }

    private static void WarnEvidence(Exception e)
    {
        if (_evidenceWarned) return;
        _evidenceWarned = true;
        SafeWarn("[instance-label] evidence write failed: " + e.GetType().Name + ": " + OneLine(e.Message));
    }

    private static void SafeInfo(string message)
    {
        try
        {
            if (Info != null) Info(message);
        }
        catch { }
    }

    private static void SafeWarn(string message)
    {
        try
        {
            if (Warn != null) Warn(message);
            else Console.Error.WriteLine(message);
        }
        catch { }
    }

    private static string Hex(IntPtr value)
    {
        return "0x" + value.ToInt64().ToString("x", CultureInfo.InvariantCulture);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Clip(string value)
    {
        if (value == null) return "<unavailable>";
        string one = OneLine(value);
        return one.Length <= 256 ? one : one.Substring(0, 256) + "...";
    }

    private static string OneLine(string value)
    {
        if (value == null) return "";
        return value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    }

    private static string Quote(string value)
    {
        if (value == null) return "null";
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7f) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Win32

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private const uint GwOwner = 4;       // GetWindow(.., GW_OWNER): the window's owner, or NULL
    private const uint GaRoot = 2;        // GetAncestor(.., GA_ROOT): the top-level ancestor
    private const uint GaRootOwner = 3;   // GetAncestor(.., GA_ROOTOWNER): the root of the owner chain

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc subclassProc, UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll", EntryPoint = "GetWindowSubclass", SetLastError = true)]
    private static extern bool GetWindowSubclass(IntPtr hWnd, SubclassProc subclassProc, UIntPtr id, out IntPtr refData);

    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc subclassProc, UIntPtr id);

    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
