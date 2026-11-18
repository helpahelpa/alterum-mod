using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AlterumModelSwap.Contracts;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// The yt-dlp half: everything that turns a page URL into a growing `dl.mp3`.
//
//   yt-dlp -f bestaudio[ext=m4a]/bestaudio -o - <url>   (stdout = the audio container bytes)
//        |  ffmpeg -i pipe:0 -vn -c:a libmp3lame -b:a 128k -f mp3 pipe:1   (stdout = mp3 frames)
//        ->  dl.mp3
//
// Both processes are ours (no cmd.exe), the two pipes are pumped on background threads, and both
// processes are assigned to the same Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so
// cancelling (or the module dying) takes the tree down. Killing only yt-dlp is not enough: ffmpeg
// then holds the pipes and the wait hangs (measured).
//
// Progress is measured, not guessed: the timestamps in `Report()` come from a Stopwatch started at
// the request, with the first-byte and ready-prefix marks taken on the pump threads, so the
// granularity is not the 15 fps main thread.
internal sealed class YtDlpPipeline : IDisposable
{
    internal const string Tag = "[music:yt]";

    // How much audio must exist before playback may start. 8 s of 128 kbps audio = 128 KB; the
    // pipeline produces 25-50x realtime, so this is reached ~0.2 s after the first byte.
    internal const long ReadyBytes = 128 * 1024;

    // 128 kbps CBR = 16 000 B/s: the conversion the host uses to reason about "how much audio does
    // this prefix hold" without asking FMOD.
    internal const long BytesPerMs = 16;

    internal enum Stage { Idle, Preflight, Spawning, Downloading, Complete, Failed, Cancelled }

    internal enum UrlKind { Empty, NotHttp, Media, Page }

    // The only failures that are ours: nothing is installed, we cannot write the disk, or our own
    // cache ceiling was hit. Everything the extractor decides - age, privacy, liveness, length,
    // whether the site is supported at all - is not judged here: yt-dlp's own sentence is surfaced
    // unchanged (see `Reason`).
    internal enum Refusal
    {
        None = 0,
        ToolsMissing = 1,
        BadUrl = 2,
        Extraction = 3,          // yt-dlp refused; its own sentence is the message
        CrashMidDownload = 4,
        DiskError = 5,
        Budget = 6,              // our cache ceiling, not a judgement about the link
        Cancelled = 7,
    }

    internal sealed class Info
    {
        public string Url = "";
        public string Extractor = "";
        public string Id = "";
        public string LiveStatus = "";
        public string Availability = "";
        public string Title = "";
        public string PlaylistTitle = "";
        public int PlaylistCount;
        public double DurationSeconds;
        public int AgeLimit;

        // `live_status` is reported, never judged: a live stream is simply unbounded.
        public bool IsLive
        {
            get
            {
                var s = (LiveStatus ?? "").Trim().ToLowerInvariant();
                return s.Length > 0 && s != "not_live" && s != "was_live" && s != "na" && s != "none";
            }
        }
    }

    // `--playlist-items 1`: a bare playlist/channel URL is attempted like any other link, and the
    // only surprise we remove is "the whole playlist concatenated into one stream". The first entry
    // plays; the panel's status says `1/N` so it is not a silent substitution.
    private const string PrintFields =
        "%(extractor_key)s|%(id)s|%(live_status)s|%(availability)s|%(age_limit)s|%(duration)s|%(playlist_title)s|%(n_entries)s|%(title)s";

    private IHostContext _log;
    private readonly object _gate = new object();

    // state (written by workers, read by the main thread under _gate where it matters)
    private volatile Stage _stage = Stage.Idle;
    private volatile Refusal _refusal = Refusal.None;
    private volatile string _message = "";

    private Info _info = new Info();
    private string _url = "";
    private string _trackDir, _dlPath, _cachedPath;
    // The URL hash alone: stable across processes, so it is what a track without an extractor/id
    // falls back to as its cache key (`LockInCachePath`). The working directory is *not* stable -
    // it carries the process and the request (see `LockInStreamDir`).
    private string _urlKey;

    private Process _ytdlp, _ffmpeg, _preflight;
    private IntPtr _job;
    private Thread _preflightThread, _pumpYt, _pumpFf, _waitThread;
    private volatile bool _cancel;
    private FileStream _out;
    // Bytes ffmpeg produced before the pre-flight verdict. Nothing reaches dl.mp3 (and therefore
    // nothing can be played or snapshotted) until the verdict is OK, so a refused link never writes
    // or plays a byte - but the network work overlaps the pre-flight instead of queueing behind it,
    // which is what keeps time-to-first-sound flat instead of doubling the extraction latency.
    private readonly List<byte[]> _preRoll = new List<byte[]>();
    private long _preRollBytes;
    private volatile bool _verdictOk;
    // Set when the pre-flight is finished (OK or refused). The exit path waits on it so a refusal
    // sentence always wins over "the download stopped".
    private readonly ManualResetEventSlim _verdictDone = new ManualResetEventSlim(false);
    private readonly object _outGate = new object();
    private const long PreRollCapBytes = 4 * 1024 * 1024;
    private readonly StringBuilder _ytErr = new StringBuilder(), _ffErr = new StringBuilder();
    private int _ytExit = int.MinValue, _ffExit = int.MinValue;
    // Signalled when the pre-roll stops being the only sink (the verdict opened `_out`) or when the
    // request ended. The pump waits on it while the pre-roll is full - outside `_outGate`.
    private readonly ManualResetEventSlim _sinkEvent = new ManualResetEventSlim(false);

    // measurements (ms since the request)
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _tRequestMs, _tPreflightMs, _tVerdictMs, _tSpawnMs, _tFirstByteMs = -1, _tReadyMs = -1, _tCompleteMs = -1;
    private long _bytesBest;

    internal Stage Current => _stage;
    internal Refusal RefusalKind => _refusal;
    internal string Message => _message;
    internal Info TrackInfo => _info;
    internal string TrackDir => _trackDir;
    internal string DlPath => _dlPath;
    internal string CachedPath => _cachedPath;
    internal long FirstByteMs => _tFirstByteMs;
    internal long ReadyMs => _tReadyMs;
    internal long SpawnMs => _tSpawnMs;
    internal long PreflightMs => _tPreflightMs;
    internal long VerdictMs => _tVerdictMs;
    internal long NowMs => _clock.ElapsedMilliseconds;
    internal long CompleteMs => _tCompleteMs;
    internal bool Failed => _stage == Stage.Failed;
    internal bool Done => _stage == Stage.Complete;

    // ================================================================ tools

    // Where this installation's game files are. BepInEx resolves its own game root from the
    // executable it was loaded next to, so it names the install the process is actually running from
    // - never another instance (C:\AlterumA vs C:\AlterumB), never a copy of the game elsewhere on
    // the machine, and never the directory the hot assembly happened to be loaded out of. The
    // Unity data path is the fallback because it is the same per-install answer from the engine side.
    private static string _gameRoot;
    private static string _gameRootSource;

    internal static string GameRoot => ResolveGameRoot();

    internal static string GameRootSource => _gameRootSource ?? "unresolved";

    private static string ResolveGameRoot()
    {
        var cached = _gameRoot;
        if (!string.IsNullOrEmpty(cached)) return cached;

        string root = null, source = null;
        try
        {
            // Stable first choice: BepInEx's own view of the game root (the exe's directory).
            var bep = BepInEx.Paths.GameRootPath;
            if (!string.IsNullOrEmpty(bep) && Directory.Exists(bep))
            {
                root = bep; source = "BepInEx.Paths.GameRootPath";
            }
        }
        catch (Exception e)
        {
            _gameRootSource = $"BepInEx.Paths threw {e.GetType().Name}";
        }
        if (root == null)
        {
            try
            {
                var data = Application.dataPath;              // <game>/Alterum_Data
                if (!string.IsNullOrEmpty(data))
                {
                    var parent = Path.GetDirectoryName(data);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    {
                        root = parent; source = "Application.dataPath parent";
                    }
                }
            }
            catch { }
        }
        if (root == null)
        {
            // Never the working directory: it can belong to another install (a launcher, a shortcut,
            // a different game copy), and resolving the tools there would spawn that install's
            // yt-dlp against this process. No root means no tools, and the actionable missing-tools
            // sentence below is the answer.
            source = "unresolved (no BepInEx root, no Application.dataPath; the working directory is never used)";
            _gameRootSource = source;
            return null;
        }
        _gameRoot = root;
        _gameRootSource = source;
        return root;
    }

    internal static string ToolsDir
    {
        get
        {
            var root = GameRoot;
            return root == null ? "" : Path.Combine(root, "BepInEx", "plugins", "alterum-music");
        }
    }

    // A directory written into a sentence keeps its trailing separator: the message has to say
    // "Expected in <dir>\", which is also what makes the path look like a folder in the log.
    internal static string ToolsDirText
    {
        get
        {
            var d = ToolsDir;
            // No game root: the sentence says what could not be established instead of naming a
            // path this process never resolved.
            if (string.IsNullOrEmpty(d)) return "<unresolved: the game root could not be established>";
            return d.EndsWith("\\", StringComparison.Ordinal) || d.EndsWith("/", StringComparison.Ordinal)
                ? d : d + "\\";
        }
    }

    // The shipped layout is three executables. All three are validated, so a half-copied folder is
    // reported by name instead of failing later as "the download stopped".
    private static readonly string[] RequiredTools = { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe" };

    internal static List<string> MissingTools()
    {
        var missing = new List<string>();
        foreach (var name in RequiredTools)
        {
            var path = Path.Combine(ToolsDir, name);
            bool present;
            try { present = File.Exists(path); } catch { present = false; }
            if (!present) missing.Add(name);
        }
        return missing;
    }

    // The exact sentence the player sees when the bundled folder is incomplete, e.g.
    //   Music tools missing: ffmpeg.exe. Expected in C:\AlterumB\BepInEx\plugins\alterum-music\.
    //   Repair/reinstall the mod's bundled Music Tools folder. No separate installation is required.
    internal static string MissingToolsMessage(IReadOnlyList<string> missing)
        => $"Music tools missing: {string.Join(", ", missing)}. Expected in {ToolsDirText}. " +
           "Repair/reinstall the mod's bundled Music Tools folder. No separate installation is required.";

    internal static bool ToolsReady(out string error)
    {
        error = null;
        var missing = MissingTools();
        if (missing.Count == 0) return true;
        error = MissingToolsMessage(missing);
        return false;
    }

    // One permanent line per request: the resolved root, where it came from, the assembly the code
    // was loaded from, and per-file presence. This is what tells a log reader which install the
    // tools were looked for in.
    internal static string DescribeTools()
    {
        var sb = new StringBuilder();
        sb.Append($"tools='{ToolsDir}' root='{GameRoot}' from={GameRootSource} ");
        try { sb.Append($"assembly='{typeof(YtDlpPipeline).Assembly.Location}' "); } catch { }
        foreach (var name in RequiredTools)
        {
            bool present;
            try { present = File.Exists(Path.Combine(ToolsDir, name)); } catch { present = false; }
            sb.Append($"{name}={(present ? "ok" : "MISSING")} ");
        }
        return sb.ToString().TrimEnd();
    }

    // Test-only knob (`music.request rate=<yt-dlp --limit-rate value>`): throttles the producer so the
    // snapshot chain has to switch many times instead of once. Empty = full speed.
    internal static string LimitRate = "";
    internal static string YtDlpExe => Path.Combine(ToolsDir, "yt-dlp.exe");
    internal static string FfmpegExe => Path.Combine(ToolsDir, "ffmpeg.exe");
    internal static string FfprobeExe => Path.Combine(ToolsDir, "ffprobe.exe");

    // ---- effective cache root -------------------------------------------------
    //
    // A cloned install must never share the per-Windows-user default with the originals (or with
    // another cloned pair): `done/track.mp3` would be published into one directory by two processes
    // and an eviction in one root would delete the other's finished track. The override is therefore
    // *run-only and root-bound*: it is honoured only when it is an absolute path strictly inside
    // THIS process' game root, which is exactly the owned `...\BepInEx\music-cache` directory the
    // launcher supplies. Anything else - relative, duplicated, traversing out, another install - is
    // ignored with a warning and the normal per-user default is used unchanged.
    //
    // Resolution order: -alterumMusicCache=<abs path>  >  ALTERUM_MUSIC_CACHE=<abs path>  >  default.
    private const string CacheArg = "-alterumMusicCache";
    private const string CacheEnv = "ALTERUM_MUSIC_CACHE";

    private static string _cacheRoot, _cacheRootSource = "default", _cacheRootWarning;
    private static bool _cacheRootLogged;
    private static readonly object _cacheRootGate = new object();

    internal static string DefaultCacheRoot
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
            return Path.Combine(local, "AlterumModSwap", "music-cache");
        }
    }

    internal static string CacheRoot
    {
        get { EnsureCacheRoot(); return _cacheRoot; }
    }

    // "arg" | "env" | "default" - the live log line uses this, and the harness asserts the value.
    internal static string CacheRootSource
    {
        get { EnsureCacheRoot(); return _cacheRootSource; }
    }

    internal static string CacheRootWarning
    {
        get { EnsureCacheRoot(); return _cacheRootWarning; }
    }

    private static void EnsureCacheRoot()
    {
        if (_cacheRoot != null) return;
        lock (_cacheRootGate)
        {
            if (_cacheRoot != null) return;
            string reason;
            _cacheRoot = ResolveCacheRoot(Environment.GetCommandLineArgs(), GameRoot,
                                          Environment.GetEnvironmentVariable(CacheEnv),
                                          out _cacheRootSource, out reason);
            _cacheRootWarning = reason;
        }
    }

    // The one line the handoff asks a live process to prove: which directory, and why.
    internal static void LogCacheRoot(IHostContext log)
    {
        if (_cacheRootLogged) return;
        _cacheRootLogged = true;
        log?.LogInfo($"[music] cache root={CacheRoot} (source={CacheRootSource})");
        if (!string.IsNullOrEmpty(CacheRootWarning))
            log?.LogWarning($"[music] cache root override ignored: {CacheRootWarning}");
    }

    // Pure resolver, exercised by tests/music-session/cache-root against the real property above.
    internal static string ResolveCacheRoot(string[] argv, string gameRoot, string envValue,
                                            out string source, out string reason)
    {
        source = "default"; reason = null;
        string argValue = null;
        int argCount = 0;
        bool argMalformed = false;
        if (argv != null)
        {
            for (int i = 1; i < argv.Length; i++)
            {
                var a = argv[i] ?? "";
                if (a.Equals(CacheArg, StringComparison.OrdinalIgnoreCase))
                {
                    argCount++;
                    if (i + 1 < argv.Length && !string.IsNullOrWhiteSpace(argv[i + 1]) &&
                        !argv[i + 1].StartsWith("-", StringComparison.Ordinal))
                        argValue = argv[++i];
                    else
                        argMalformed = true;
                }
                else if (a.StartsWith(CacheArg + "=", StringComparison.OrdinalIgnoreCase))
                {
                    argCount++;
                    argValue = a.Substring(CacheArg.Length + 1);
                }
            }
        }

        string candidate = null, candidateSource = null;
        if (argCount > 1 || argMalformed)
        {
            reason = "duplicate or empty " + CacheArg;
            return DefaultCacheRoot;
        }
        if (argCount == 1)
        {
            candidate = argValue;
            candidateSource = "arg";
        }
        else if (!string.IsNullOrWhiteSpace(envValue))
        {
            candidate = envValue;
            candidateSource = "env";
        }
        if (candidate == null) return DefaultCacheRoot;

        if (!TryCanonicalDir(candidate, out var full, out var why))
        {
            reason = $"{candidateSource} value '{candidate}' is not a usable absolute path ({why})";
            return DefaultCacheRoot;
        }
        var def = DefaultCacheRoot;
        if (TryCanonicalDir(def, out var defFull, out _) && PathEquals(defFull, full))
        {
            reason = $"{candidateSource} value is the shared default cache; use a run-owned directory";
            return DefaultCacheRoot;
        }
        if (string.IsNullOrEmpty(gameRoot) || !TryCanonicalDir(gameRoot, out var root, out _))
        {
            reason = $"{candidateSource} value cannot be bound: the game root is unresolved";
            return DefaultCacheRoot;
        }
        if (!IsInside(root, full))
        {
            reason = $"{candidateSource} value '{full}' is outside this process' game root '{root}'";
            return DefaultCacheRoot;
        }
        source = candidateSource;
        return full;
    }

    // The Windows form rule, as a pure predicate so the Windows-only cases can be asserted on a
    // Linux test host instead of being skipped (the platform parser itself cannot be emulated).
    //   C:\cache, C:/cache  -> fully qualified drive path      -> true
    //   \\server\share     -> fully qualified UNC path          -> true
    //   C:cache             -> drive-relative                  -> false
    //   \cache              -> current-drive root-relative     -> false
    //   cache\x             -> relative                         -> false
    internal static bool IsWindowsFullyQualifiedForm(string value)
    {
        var v = (value ?? "").Trim();
        if (v.Length < 3) return false;                       // shortest valid form is "C:\"
        if (char.IsLetter(v[0]) && v[1] == ':')
            return v[2] == '\\' || v[2] == '/';              // C:cache is drive-relative
        if (v[0] == '\\' || v[0] == '/')
            return (v[1] == '\\' || v[1] == '/') && v.Length > 2;   // \\server\share, not \\ alone
        return false;
    }

    private static bool TryCanonicalDir(string value, out string full, out string why)
    {
        full = null; why = null;
        try
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) { why = "empty"; return false; }
            // IsPathRooted() is NOT enough: on Windows "C:cache" is rooted (drive-relative) and
            // "\cache" is rooted (current-drive root-relative), yet neither is a fully qualified
            // path. Path.GetFullPath then silently resolves them against the process' current
            // directory, which can place an override inside GameRoot that was never absolute.
            if (!Path.IsPathFullyQualified(v))
            {
                why = "not fully qualified/absolute (drive-relative and root-relative paths are not accepted)";
                return false;
            }
            if (Path.DirectorySeparatorChar == '\\' && !IsWindowsFullyQualifiedForm(v))
            {
                why = "not a fully qualified Windows path (drive-relative and root-relative paths are not accepted)";
                return false;
            }
            full = Path.GetFullPath(v);
            var pathRoot = Path.GetPathRoot(full);
            bool isFsRoot = !string.IsNullOrEmpty(pathRoot) &&
                            (full.Length == pathRoot.Length ||
                             (full.Length == pathRoot.Length + 1 && IsSep(full[full.Length - 1])));
            if (!isFsRoot)
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length == 0) { why = "empty after normalization"; return false; }
            return true;
        }
        catch (Exception e)
        {
            why = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    private static bool IsInside(string root, string candidate)
    {
        if (!candidate.StartsWith(root, PathComparison)) return false;
        if (candidate.Length <= root.Length) return false;                   // must be strictly inside
        return IsSep(candidate[root.Length]);
    }

    private static bool IsSep(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool PathEquals(string a, string b) => string.Equals(a, b, PathComparison);

    internal static string StreamRoot => Path.Combine(CacheRoot, "stream");
    internal static string DoneRoot => Path.Combine(CacheRoot, "done");

    // CacheRoot belongs to the Windows *user*, so two instances of the game under one account share
    // it. The finished cache is meant to be shared; a working file is not. This tag is what keeps two
    // instances apart: the pid plus a random per-process token (a bare pid can be reused by an older
    // run, and the token makes that ambiguity harmless).
    private static string _processTag;
    private static int _reqSeq;
    private readonly int _reqNo = Interlocked.Increment(ref _reqSeq);

    internal static string ProcessTag
    {
        get
        {
            var have = _processTag;
            if (have != null) return have;
            var made = Environment.ProcessId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            // Losing this race just means both threads compute the same shape; the winner is kept.
            return Interlocked.CompareExchange(ref _processTag, made, null) ?? made;
        }
    }

    // ================================================================ disk ceiling

    // There is no duration limit any more, so the disk is what has to be bounded. One ceiling for
    // the whole cache (finished tracks + in-flight stream dirs); above it the oldest finished track
    // is evicted. A single track that would exceed the ceiling on its own stops with a sentence that
    // says so - that is the only "too long" left, and it is about our disk, not about the link.
    internal static long CacheBudgetBytes = 3L * 1024 * 1024 * 1024;

    // The working directories get their own ceiling, because a request that is stopped or abandoned
    // leaves a partial `dl.mp3` behind and nothing else would ever remove it. A directory whose owner
    // process is gone is always junk; one whose owner is alive is never touched (that is the
    // isolation the per-process name buys - another instance may be using it right now).
    internal static long StreamBudgetBytes = 512L * 1024 * 1024;
    // Our own leftover directory (a retired request whose delete was refused by a live handle) is
    // only swept once it has been idle this long.
    private const int StreamDirIdleMinutes = 10;

    // How much audio is written between two ceiling checks on the producer's own thread, so the
    // budget is enforced while a long track is still growing instead of only when it ends.
    private const long BudgetCheckBytes = 64L * 1024 * 1024;
    private long _nextBudgetCheck;

    // A delete another handle refuses (a finished file a second instance is playing, a directory whose
    // file is still open) is retried at most once per window per path, so a refusal cannot become a
    // retry loop. The cache then stays over its ceiling - which is harmless, because admission (see
    // EnforceCacheCeiling) refuses anything that would grow it and every live request is capped.
    private const long RefusedDeleteCooldownMs = 60_000;
    private static readonly Dictionary<string, long> _refusedDeletes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _refusedGate = new object();
    private static bool _streamOverLogged;

    private static bool InRefusedCooldown(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        long now = Environment.TickCount64;
        lock (_refusedGate)
        {
            return _refusedDeletes.TryGetValue(path, out long at) && now - at < RefusedDeleteCooldownMs;
        }
    }

    private static void NoteRefusedDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (_refusedGate)
        {
            // Bounded: this is a cooldown list, not a ledger. Dropping it early only means a retry
            // sooner than the window, never a leak.
            if (_refusedDeletes.Count > 256) _refusedDeletes.Clear();
            _refusedDeletes[path] = Environment.TickCount64;
        }
    }

    internal static long DirBytes(string root)
    {
        long total = 0;
        try
        {
            if (Directory.Exists(root))
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
    }

    // Delete the least recently written finished tracks until the whole cache fits. This has three
    // callers on paths that actually run: the producer's own thread while a track is still growing
    // (`_nextBudgetCheck`, every `BudgetCheckBytes` written), the host at finalisation, right after a
    // track is published into the shared cache, and admission (every request start, cold or cached).
    // The open snapshot and the current track are never candidates (`keepPath`). Returns the number
    // evicted; `freed` the bytes.
    internal static int EvictOldest(string keepPath, IHostContext log, out long freed)
    {
        freed = 0;
        int evicted = 0;
        int refused = 0;
        try
        {
            // One measurement, then decrement per deletion: the old shape walked the whole tree for
            // every candidate, which is why it was too expensive to call on the hot path.
            long total = DirBytes(CacheRoot);
            if (total <= CacheBudgetBytes) return 0;
            if (!Directory.Exists(DoneRoot)) return 0;
            var files = new List<FileInfo>();
            foreach (var f in Directory.GetFiles(DoneRoot, "track.mp3", SearchOption.AllDirectories))
            {
                if (!string.IsNullOrEmpty(keepPath) &&
                    string.Equals(f, keepPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { files.Add(new FileInfo(f)); } catch { }
            }
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var fi in files)
            {
                if (total <= CacheBudgetBytes) break;
                long len = 0;
                try { len = fi.Length; } catch { }
                // A file another instance is playing right now cannot be deleted on Windows; that is
                // a lost race, not a failure - move on to the next candidate, and do not come back to
                // this one on the next check before the cooldown window has passed.
                if (InRefusedCooldown(fi.FullName)) { refused++; continue; }
                if (!TryDelete(fi.FullName, log)) { NoteRefusedDelete(fi.FullName); refused++; continue; }
                evicted++; freed += len; total -= len;
                // The entry is the directory (track.mp3 + meta.txt): dropping the metadata keeps the
                // cache from filling with entries that can never be found again.
                var dir = fi.Directory;
                if (dir != null)
                {
                    TryDelete(Path.Combine(dir.FullName, "meta.txt"), log);
                    try { dir.Delete(false); } catch { }
                }
                log?.LogInfo($"[music:cache] evicted '{dir?.Name}' ({len / 1024}KB, " +
                             $"written {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm}Z) to stay under the " +
                             $"{CacheBudgetBytes / (1024 * 1024)}MB ceiling");
            }
            if (total > CacheBudgetBytes)
                log?.LogWarning($"[music:cache] {total / (1024 * 1024)}MB is still over the " +
                                $"{CacheBudgetBytes / (1024 * 1024)}MB ceiling after eviction: " +
                                $"{refused} finished track(s) are held by another handle. Nothing new is " +
                                "admitted until the cache is under, and no live request may exceed its " +
                                $"{StreamBudgetBytes / (1024 * 1024)}MB stream budget");
        }
        catch (Exception e) { log?.LogWarning($"[music:cache] eviction failed: {e.GetType().Name}: {e.Message}"); }
        return evicted;
    }

    // The rule the ceilings now enforce, in one place:
    //
    //   cache   : bytes under CacheRoot <= CacheBudgetBytes whenever work is admitted.
    //   stream  : one request's own dl.mp3 <= StreamBudgetBytes; past it the request FAILS with
    //             Refusal.Budget and its process tree is killed (a defined stop, not unbounded growth).
    //   deletes : a path a handle refuses is retried at most once per RefusedDeleteCooldownMs.
    //
    // Admission runs this and then decides: a COLD start is refused when the answer is still over the
    // ceiling (the bytes that are left cannot be evicted: they are in use by this or another
    // instance), while a cached hit plays because it writes nothing. `keepPath` (the file the caller
    // is about to publish or play) is never a candidate. Returns the bytes still over the ceiling
    // after one eviction + sweep pass (0 = met); `total` is what the pass measured.
    internal static long EnforceCacheCeiling(string keepPath, string keepDir, IHostContext log, out long total)
    {
        EvictOldest(keepPath, log, out _);
        SweepStreamRoot(keepDir, log);
        total = DirBytes(CacheRoot);
        return total > CacheBudgetBytes ? total - CacheBudgetBytes : 0;
    }

    private static string OverBudgetSentence(long total, long over)
        => $"The music cache is full ({total / (1024 * 1024)}MB of {CacheBudgetBytes / (1024 * 1024)}MB, " +
           $"over by {over / (1024 * 1024)}MB) and its finished tracks cannot be removed right now " +
           "(another player has them open). No new download is started until the cache is under the ceiling.";

    // Bound the working root. The name of a stream directory carries the pid that owns it, which is
    // what makes this decision possible at all: a directory whose owner is gone is deleted, one whose
    // owner is alive belongs to another instance and is left alone. `keepDir` (this request's own
    // directory) is never a candidate. Returns the number of directories removed.
    internal static int SweepStreamRoot(string keepDir, IHostContext log)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(StreamRoot)) return 0;
            var dirs = new List<DirectoryInfo>();
            foreach (var path in Directory.GetDirectories(StreamRoot))
            {
                try { dirs.Add(new DirectoryInfo(path)); } catch { }
            }
            if (dirs.Count == 0) return 0;
            dirs.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            long total = 0;
            foreach (var d in dirs) total += DirBytes(d.FullName);
            int live = 0;
            foreach (var d in dirs)
            {
                long bytes = DirBytes(d.FullName);
                int owner = OwnerPid(d.Name);
                bool eligible;
                if (owner == Environment.ProcessId)
                    eligible = !SamePath(d.FullName, keepDir) && IdleMinutes(d) >= StreamDirIdleMinutes;
                else if (owner > 0)
                    eligible = !ProcessAlive(owner);           // its owner is gone: nobody can be writing there
                else
                    // A name with no owner at all is a leftover of an older build; the age rule keeps a
                    // still-running older instance's directory safe during a rolling update.
                    eligible = IdleMinutes(d) >= StreamDirIdleMinutes;
                if (!eligible) { live++; continue; }
                // A refused delete is retried once per cooldown window, never on every pass.
                if (InRefusedCooldown(d.FullName)) { live++; continue; }
                if (!TryRemoveTree(d.FullName, log)) { NoteRefusedDelete(d.FullName); live++; continue; }
                removed++; total -= bytes;
                log?.LogInfo($"{Tag} removed abandoned stream dir '{d.Name}' ({bytes / 1024}KB, " +
                             $"owner pid={owner}) to stay under the {StreamBudgetBytes / (1024 * 1024)}MB cap");
            }
            // The live owners cannot be deleted from under them - that isolation is the point of the
            // per-process name - but each live request is capped at StreamBudgetBytes by the producer
            // (PumpFfmpegToFile), so this remainder is bounded, not an open-ended leak.
            if (total > StreamBudgetBytes && !_streamOverLogged)
            {
                _streamOverLogged = true;
                log?.LogWarning($"{Tag} stream root holds {total / (1024 * 1024)}MB in {live} dir(s) whose " +
                                 "owner is still running; nothing is deleted out from under a live request " +
                                 $"and every live request is capped at {StreamBudgetBytes / (1024 * 1024)}MB");
            }
        }
        catch (Exception e) { log?.LogWarning($"{Tag} stream root sweep failed: {e.GetType().Name}: {e.Message}"); }
        return removed;
    }

    private static double IdleMinutes(DirectoryInfo d)
    {
        try { return (DateTime.UtcNow - d.LastWriteTimeUtc).TotalMinutes; } catch { return 0; }
    }

    private static bool TryRemoveTree(string dir, IHostContext log)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir)) TryDelete(f, log);
            if (Directory.GetFiles(dir).Length > 0) return false;    // a live handle refused: keep the dir
            Directory.Delete(dir, false);
            return true;
        }
        catch (Exception e)
        {
            log?.LogInfo($"{Tag} stream dir '{Path.GetFileName(dir)}' not removed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    // "u<hash>-p<pid>-<proc>-r<n>": the owner is the pid segment. 0 = an older layout with no owner.
    private static int OwnerPid(string dirName)
    {
        var name = dirName ?? "";
        int i = name.IndexOf("-p", StringComparison.Ordinal);
        if (i < 0 || i + 2 >= name.Length) return 0;
        int j = i + 2;
        while (j < name.Length && char.IsDigit(name[j])) j++;
        if (j == i + 2) return 0;
        return int.TryParse(name.Substring(i + 2, j - i - 2), out int pid) ? pid : 0;
    }

    private static bool ProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        if (pid == Environment.ProcessId) return true;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch { return true; }        // cannot tell: assume alive, never delete another instance's work
    }

    private static bool SamePath(string a, string b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
           string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                         Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // Publish a finished track into the shared cache. The cache is shared on purpose (that is the
    // whole point of it), so the publish has to be atomic: the bytes first appear under a name of
    // this process/request's own (`.part-...`), and only then does one rename put the complete file
    // under `track.mp3`. A second instance scanning the cache therefore sees either nothing or a
    // whole track, never a half-written one. Two instances finishing the same link both "win":
    // whoever loses the rename finds a usable file already in place and keeps it.
    internal static bool PublishFinished(string source, string cachedPath, IHostContext log, out string error)
    {
        error = null;
        string tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(cachedPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(source)) { error = "no path"; return false; }
            Directory.CreateDirectory(dir);
            tmp = Path.Combine(dir, $"track.part-{ProcessTag}-{Interlocked.Increment(ref _publishSeq)}");
            File.Move(source, tmp);          // same volume (both under CacheRoot); a copy lands here, not on track.mp3
            try
            {
                File.Move(tmp, cachedPath, true);   // atomic replace: the whole file at once
                tmp = null;
                return true;
            }
            catch (Exception e)
            {
                if (UsableTrack(cachedPath))
                {
                    log?.LogInfo($"{Tag} publish race: '{Path.GetFileName(cachedPath)}' already complete; " +
                                 $"keeping it ({e.GetType().Name})");
                    TryDelete(tmp, log);
                    tmp = null;
                    return true;
                }
                // Nothing usable is in the cache: put the finished bytes back where they came from, so
                // the next attempt has them (a refused rename must not cost the download).
                try
                {
                    File.Move(tmp, source);
                    tmp = null;
                }
                catch { }
                error = $"rename into the cache refused ({e.GetType().Name}: {e.Message})";
                return false;
            }
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
        finally
        {
            // Only on failure: the finished bytes stay under the `.part` name, which no reader treats
            // as a track, and the next attempt overwrites it.
            if (tmp != null) log?.LogWarning($"{Tag} publish left '{Path.GetFileName(tmp)}' in place ({error})");
        }
    }

    private static int _publishSeq;

    // A cache entry is usable only when the whole file is there. Every finished track clears
    // `ReadyBytes` by construction (the waiter refuses to call a shorter download complete), so a
    // truncated or `.part` file can never pass this test.
    internal static bool UsableTrack(string path)
    {
        try { return !string.IsNullOrEmpty(path) && File.Exists(path) && new FileInfo(path).Length > ReadyBytes; }
        catch { return false; }
    }

    internal static string KeyOf(Info info)
    {
        var ex = Safe(info.Extractor);
        var id = Safe(info.Id);
        if (ex.Length == 0 || id.Length == 0) return null;
        var sb = new StringBuilder(ex.Length + id.Length + 1);
        foreach (var c in ex.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
        sb.Append('-');
        foreach (var c in id) if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
        return sb.Length > 2 ? sb.ToString() : null;
    }

    // ================================================================ URL classification

    // Direct audio/video links stay with the game's player. Anything else that is an http(s) URL is
    // a yt-dlp candidate; the pre-flight decides whether it is supported.
    internal static UrlKind Classify(string url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return UrlKind.Empty;
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return UrlKind.NotHttp;
        try
        {
            var uri = new Uri(u);
            var path = uri.AbsolutePath.ToLowerInvariant();
            foreach (var ext in new[] { ".mp3", ".ogg", ".oga", ".opus", ".wav", ".flac", ".m4a", ".aac", ".wma", ".mp4", ".webm" })
                if (path.EndsWith(ext, StringComparison.Ordinal)) return UrlKind.Media;
            return UrlKind.Page;
        }
        catch { return UrlKind.NotHttp; }
    }


    // ================================================================ start / pre-flight

    internal void Begin(IHostContext log, string url)
    {
        _log = log;
        _url = (url ?? "").Trim();
        _tRequestMs = _clock.ElapsedMilliseconds;
        _stage = Stage.Preflight;

        if (_url.Length == 0 || Classify(_url) != UrlKind.Page)
        {
            Fail(Refusal.BadUrl, $"'{Short(_url, 80)}' is not an http(s) page link");
            return;
        }
        _log?.LogInfo($"{Tag} tools: {DescribeTools()}");
        if (!ToolsReady(out string toolsError))
        {
            Fail(Refusal.ToolsMissing, toolsError);
            return;
        }
        // The stream directory is keyed by the URL (the extractor/id are only known after the
        // pre-flight), so the download can start immediately.
        //
        // ADMISSION under the cache ceiling, before anything is spawned: evict what may go, then
        // refuse to start when the cache is still over it. Growing from an already-over cache is the
        // unbounded case; refusing to start is the defined outcome (the sentence names the ceiling).
        long over = EnforceCacheCeiling(null, null, _log, out long cacheBytes);
        if (over > 0)
        {
            Fail(Refusal.Budget, OverBudgetSentence(cacheBytes, over));
            return;
        }
        LockInStreamDir();
        if (_dlPath == null) return;
        Spawn();
        if (_stage == Stage.Failed) return;

        _preflightThread = new Thread(PreflightWorker) { IsBackground = true, Name = "alterum-music-preflight" };
        _preflightThread.Start();
    }

    private void PreflightWorker()
    {
        try
        {
            long t0 = _clock.ElapsedMilliseconds;
            var psi = new ProcessStartInfo(YtDlpExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[]
                     {
                         "--no-playlist", "--playlist-items", "1", "--no-warnings", "--no-progress",
                         "--skip-download", "--socket-timeout", "15", "--retries", "2",
                         "--print", PrintFields, "--", _url
                     }) psi.ArgumentList.Add(a);

            using (var p = new Process { StartInfo = psi })
            {
                _preflight = p;
                var outText = new StringBuilder();
                var errText = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) outText.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) errText.AppendLine(e.Data); };
                p.Start();
                if (_job != IntPtr.Zero) AssignToJob(_job, p, "yt-dlp pre-flight");
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(45000))
                {
                    try { p.Kill(true); } catch { }
                    Fail(Refusal.Extraction, "yt-dlp pre-flight timed out after 45 s");
                    return;
                }
                p.WaitForExit();
                _preflight = null;
                _tPreflightMs = _clock.ElapsedMilliseconds - t0;
                var stdout = outText.ToString().Trim();
                var stderr = errText.ToString().Trim();
                if (p.ExitCode != 0 || stdout.Length == 0)
                {
                    // The extractor's own words, not ours.
                    Fail(Refusal.Extraction, Reason(stderr, stdout));
                    _log?.LogWarning($"{Tag} pre-flight exit={p.ExitCode} ms={_tPreflightMs} " +
                                     $"reason='{Message}' raw='{Short(stderr, 300)}'");
                    return;
                }
                var info = ParsePrint(stdout);
                if (info == null)
                {
                    Fail(Refusal.Extraction, "yt-dlp returned no track metadata: " + Short(stdout, 160));
                    return;
                }
                _info = info;
                _tVerdictMs = _clock.ElapsedMilliseconds;
                _log?.LogInfo($"{Tag} pre-flight OK in {_tPreflightMs}ms ({_tVerdictMs}ms into the request) " +
                              $"extractor={info.Extractor} id={info.Id} live={info.LiveStatus} " +
                              $"availability={info.Availability} age={info.AgeLimit} " +
                              $"duration={info.DurationSeconds:0.#}s{(info.IsLive ? " (unbounded)" : "")}" +
                              $" playlist='{Short(info.PlaylistTitle, 40)}' n={info.PlaylistCount} " +
                              $"title='{Short(info.Title, 80)}'; prerecorded audio already staged: {_preRollBytes} bytes");
                LockInCachePath();
                ReleasePreRoll();
                // After playback may start, still off the main thread: trim the shared cache to its
                // ceiling and drop the working directories of processes that are gone (this is also
                // what keeps the root bounded when an earlier request was killed before it could
                // clean up after itself). Never on the pre-flight latency path.
                CheckCeiling();
            }
        }
        catch (Exception e)
        {
            Fail(Refusal.Extraction, $"pre-flight threw {e.GetType().Name}: {e.Message}");
        }
        finally { _verdictDone.Set(); }
    }

    private static Info ParsePrint(string stdout)
    {
        var lines = stdout.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var line = lines.Length > 0 ? lines[0].TrimEnd('\r') : null;
        if (string.IsNullOrEmpty(line)) return null;
        var parts = line.Split('|');
        if (parts.Length < 6) return null;
        var info = new Info
        {
            Extractor = parts[0].Trim(),
            Id = parts[1].Trim(),
            LiveStatus = parts[2].Trim(),
            Availability = parts[3].Trim(),
            PlaylistTitle = parts[6].Trim() == "NA" ? "" : parts[6].Trim(),
        };
        double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double age);
        double.TryParse(parts[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out info.DurationSeconds);
        if (int.TryParse(parts[7].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) info.PlaylistCount = n;
        info.Title = parts.Length > 8 ? string.Join("|", parts, 8, parts.Length - 8).Trim() : "";
        info.AgeLimit = (int)age;
        if (info.AgeLimit > 0 && info.Title.Length == 0) info.Title = "";
        return info;
    }

    // The working directory can be named before anything is known about the track: a stable hash of
    // the URL, qualified by the process and the request. It is what the snapshot chain points at.
    //
    // NOT the hash alone. `CacheRoot` is per Windows user, so two instances of the game under one
    // account would otherwise share `stream/u<hash>/dl.mp3` and the snapshot names in it: one writer
    // fails its open, and snapshot cutting or cleanup in one instance damages the other's files.
    // Two instances following the same link are the normal setup here, so the name carries
    // `p<pid>-<process token>` (which process) and `r<n>` (which request in that process):
    //   stream/u71c0e0d4c7f9a1b3-p48212-9f3ac1d0-r1/
    //   stream/u71c0e0d4c7f9a1b3-p51008-77b0e2aa-r1/     <- same URL, other instance
    private void LockInStreamDir()
    {
        try
        {
            ulong h = 1469598103934665603UL;
            foreach (var c in _url) { h ^= (byte)c; h *= 1099511628211UL; }
            _urlKey = "u" + h.ToString("x16");
            _trackDir = Path.Combine(StreamRoot, $"{_urlKey}-p{ProcessTag}-r{_reqNo}");
            _dlPath = Path.Combine(_trackDir, "dl.mp3");
            Directory.CreateDirectory(_trackDir);
        }
        catch (Exception e)
        {
            Fail(Refusal.DiskError, $"cannot create the stream directory: {e.Message}");
            _trackDir = null; _dlPath = null;
            return;
        }
        _cachedPath = null;                     // only known once the pre-flight names the track
    }

    private void LockInCachePath()
    {
        var key = KeyOf(_info);
        // A track with neither extractor nor id still needs a *stable* cache key: the working
        // directory's name is not one (it is per request), the URL hash is.
        if (key == null) key = _urlKey;
        if (key == null) key = "unknown";
        _cachedPath = Path.Combine(DoneRoot, key, "track.mp3");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachedPath));
            File.WriteAllText(Path.Combine(_trackDir, "meta.txt"),
                $"url={_url}\nextractor={_info.Extractor}\nid={_info.Id}\ntitle={_info.Title}\n" +
                $"duration={_info.DurationSeconds}\n");
        }
        catch (Exception e)
        {
            Fail(Refusal.DiskError, $"cannot create the cache directory: {e.Message}");
        }
    }

    // Move everything the pump staged in memory into dl.mp3 and switch the pump to file writes.
    private void ReleasePreRoll()
    {
        lock (_outGate)
        {
            if (_out == null)
            {
                try
                {
                    _out = new FileStream(_dlPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                                          64 * 1024, FileOptions.SequentialScan);
                }
                catch (Exception e)
                {
                    Fail(Refusal.DiskError, $"cannot open '{_dlPath}': {e.Message}");
                    return;
                }
            }
            foreach (var block in _preRoll) _out.Write(block, 0, block.Length);
            _out.Flush();
            _preRoll.Clear();
            long flushed = _preRollBytes;
            _preRollBytes = 0;
            _tReadyMs = _tReadyMs < 0 && flushed >= ReadyBytes ? _clock.ElapsedMilliseconds : _tReadyMs;
            _verdictOk = true;
            // The releasing signal comes *inside* the gate: the pump re-checks `_out != null` under
            // this same gate, so it can never observe the signal without seeing the open sink.
            try { _sinkEvent.Set(); } catch (ObjectDisposedException) { }
            _log?.LogInfo($"{Tag} pre-roll flushed: {flushed} bytes -> '{Path.GetFileName(_dlPath)}' at " +
                          $"{_clock.ElapsedMilliseconds}ms (playback may start now)");
        }
    }

    // ================================================================ spawn

    private void Spawn()
    {
        try
        {
            _stage = Stage.Spawning;
            TryDelete(_dlPath, _log);
            _job = CreateKillOnCloseJob();
            long t0 = _clock.ElapsedMilliseconds;

            var ytArgs = new List<string>
            {
                "--no-playlist", "--playlist-items", "1", "--no-warnings", "--no-progress", "--no-mtime",
                "--socket-timeout", "15", "--retries", "2", "--http-chunk-size", "1M",
            };
            if (!string.IsNullOrEmpty(LimitRate)) { ytArgs.Add("--limit-rate"); ytArgs.Add(LimitRate); }
            ytArgs.Add("-f"); ytArgs.Add("bestaudio[ext=m4a]/bestaudio");
            ytArgs.Add("-o"); ytArgs.Add("-"); ytArgs.Add("--"); ytArgs.Add(_url);
            _ytdlp = StartOne("yt-dlp", YtDlpExe, ytArgs.ToArray(), _ytErr);
            _ffmpeg = StartOne("ffmpeg", FfmpegExe, new[]
            {
                "-hide_banner", "-nostats", "-nostdin", "-i", "pipe:0",
                "-vn", "-c:a", "libmp3lame", "-b:a", "128k", "-f", "mp3", "pipe:1"
            }, _ffErr);

            _stage = Stage.Downloading;
            _tSpawnMs = _clock.ElapsedMilliseconds - t0;

            _pumpYt = new Thread(PumpYtToFfmpeg) { IsBackground = true, Name = "alterum-music-pipe1" };
            _pumpFf = new Thread(PumpFfmpegToFile) { IsBackground = true, Name = "alterum-music-pipe2" };
            _waitThread = new Thread(WaitForExit) { IsBackground = true, Name = "alterum-music-wait" };
            _pumpYt.Start();
            _pumpFf.Start();
            _waitThread.Start();

            _log?.LogInfo($"{Tag} spawned in {_tSpawnMs}ms yt-dlp pid={Pid(_ytdlp)} ffmpeg pid={Pid(_ffmpeg)} " +
                          $"job=0x{_job.ToInt64():X} out='{_dlPath}'");
        }
        catch (Exception e)
        {
            Fail(Refusal.CrashMidDownload, $"could not start the pipeline: {e.GetType().Name}: {e.Message}");
            KillTree();
        }
    }

    private Process StartOne(string what, string exe, string[] args, StringBuilder err)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            lock (_gate) { err.AppendLine(e.Data); if (err.Length > 8192) err.Remove(0, err.Length - 8192); }
        };
        p.Start();
        p.BeginErrorReadLine();
        if (_job != IntPtr.Zero) AssignToJob(_job, p, what);
        return p;
    }

    private void PumpYtToFfmpeg()
    {
        try
        {
            var src = _ytdlp.StandardOutput.BaseStream;
            var dst = _ffmpeg.StandardInput.BaseStream;
            var buf = new byte[64 * 1024];
            while (!_cancel)
            {
                int n = src.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                dst.Write(buf, 0, n);
                dst.Flush();
            }
            try { dst.Flush(); } catch { }
            try { _ffmpeg.StandardInput.Close(); } catch { }
        }
        catch (Exception e)
        {
            if (!_cancel) _log?.LogWarning($"{Tag} pipe yt-dlp->ffmpeg ended: {e.GetType().Name}: {e.Message}");
        }
    }

    private void PumpFfmpegToFile()
    {
        try
        {
            var src = _ffmpeg.StandardOutput.BaseStream;
            var buf = new byte[64 * 1024];
            while (!_cancel)
            {
                // Ask for room *before* reading. While the pre-flight has not answered the sink is
                // the in-memory pre-roll, and when it is full the pump simply stops reading: ffmpeg
                // blocks on its own stdout, yt-dlp blocks on the pipe, and the whole chain slows to
                // the rate the pre-roll can absorb. Nothing is ever read and then dropped.
                if (!WaitForRoom()) break;
                int n = src.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                if (_tFirstByteMs < 0) _tFirstByteMs = _clock.ElapsedMilliseconds;
                bool ceilingDue = false;
                bool overStream = false;
                lock (_outGate)
                {
                    if (_out == null)
                    {
                        // Staged: the pre-flight has not cleared this track yet, so nothing is
                        // written to dl.mp3 (a refused link never produces a playable file). The
                        // block itself is kept - it is flushed by ReleasePreRoll in order.
                        var block = new byte[n];
                        Array.Copy(buf, block, n);
                        _preRoll.Add(block);
                        _preRollBytes += n;
                    }
                    else
                    {
                        _out.Write(buf, 0, n);
                        _out.Flush();                           // visible to the snapshotter immediately
                        long len = _out.Length;
                        if (len > _bytesBest) _bytesBest = len;
                        // The bound on ONE request's growing file. Past it the request stops with a
                        // sentence instead of growing without limit: a track longer than the stream
                        // budget is the only "too long" left, and refusing it is the defined answer.
                        if (len > StreamBudgetBytes) overStream = true;
                        else
                        {
                            if (_tReadyMs < 0 && len >= ReadyBytes) _tReadyMs = _clock.ElapsedMilliseconds;
                            if (len >= _nextBudgetCheck)
                            {
                                ceilingDue = true;
                                _nextBudgetCheck = len + BudgetCheckBytes;
                            }
                        }
                    }
                }
                if (overStream)
                {
                    // Never under `_outGate` (Fail takes it): the lock above is already released.
                    Fail(Refusal.Budget, $"This track alone is longer than the " +
                                         $"{StreamBudgetBytes / (1024 * 1024)}MB stream budget; the download " +
                                         "is stopped here (the cache ceiling is not a judgement about the link).");
                    break;
                }
                // The ceiling check runs on this thread but never under `_outGate`: a directory walk
                // there would stall the pre-roll flush (and with it the first sound).
                if (ceilingDue) CheckCeiling();
            }
        }
        catch (Exception e)
        {
            if (!_cancel) _log?.LogWarning($"{Tag} pipe ffmpeg->file ended: {e.GetType().Name}: {e.Message}");
        }
    }

    // Is the block that is about to be read allowed to proceed? Called with no lock held, and it
    // never takes one while waiting: a full pre-roll parks this thread on `_sinkEvent` until the
    // verdict opens the sink, or until the request ends (refusal, cancel, dispose). The wait is
    // bounded and the state is re-checked from scratch, so a torn-down generation can never leave a
    // thread parked here.
    //
    // The wake itself is NOT a verdict. Three different things signal `_sinkEvent` - the SUCCESSFUL
    // release of the pre-roll into dl.mp3, a refusal, and a cancel - and only the last two end the
    // request. Treating any signalled wait as "this request is over" made the successful release
    // return false here, so `PumpFfmpegToFile` exited with the remaining output undrained (ffmpeg
    // blocked on its stdout and the file truncated). So the signal is consumed and the room is
    // re-evaluated: false is returned only for genuine cancellation or a refusal.
    private bool WaitForRoom()
    {
        while (!_cancel)
        {
            // A refusal clears the pre-roll and closes the sink: after it no byte may be staged or
            // written (the file must not exist for a link we refused).
            if (_stage == Stage.Failed) return false;
            bool room;
            lock (_outGate) { room = _out != null || _preRollBytes < PreRollCapBytes; }
            if (room) return true;
            try
            {
                // Any state change that matters (`_out` opened, refusal, cancel) is published before
                // the signal, and is re-read at the top of this loop after the reset - so resetting
                // here cannot swallow a wakeup that ended the request.
                if (_sinkEvent.Wait(250))
                {
                    try { _sinkEvent.Reset(); } catch (ObjectDisposedException) { return false; }
                }
            }
            catch (ObjectDisposedException) { return false; }
        }
        return false;
    }

    private void WaitForExit()
    {
        try
        {
            // A pipeline can die before the pre-flight has an answer (a 404 page, a live stream's
            // manifest). Waiting for the verdict first means the user sees the specific sentence
            // ("live streams are not supported") instead of "the download stopped".
            try { _verdictDone.Wait(45000); } catch { }
            if (_pumpYt != null) _pumpYt.Join(120000);
            _ffmpeg.WaitForExit(120000);
            _ffExit = _ffmpeg.HasExited ? _ffmpeg.ExitCode : int.MinValue;
            _ytdlp.WaitForExit(120000);
            _ytExit = _ytdlp.HasExited ? _ytdlp.ExitCode : int.MinValue;

            // A exited process is not a drained file. `WaitForExit` only says the child closed its end
            // of the pipe; the tail of the mp3 can still be sitting in the pipe buffer, and `_pumpFf`
            // is the only thing that can move it into dl.mp3 (it is the sole writer, under `_outGate`).
            // Sizing and closing here used to throw that tail away - and a pump read made after
            // CloseOutput staged its bytes into `_preRoll`, which nothing flushes once the verdict is
            // in - so Complete was published for a shorter file than the download produced.
            // The order below is the whole fix: every pump finished -> no writer left -> flush/close ->
            // size -> and only then may a stage be published.
            const int DrainMs = 120000;
            bool drained;
            if (_pumpFf == null) drained = true;                 // never started: nothing can write
            else
            {
                try { drained = _pumpFf.Join(DrainMs); }
                catch (ThreadStateException) { drained = true; }  // never started: nothing can write
                if (!drained)
                {
                    // Stuck on a pipe a child never closed. Nothing may be published, and the file
                    // cannot be closed under a live writer either: take the tree down first, then
                    // give the pump the moment it needs to observe the EOF.
                    _log?.LogError($"{Tag} output pump did not reach EOF within {DrainMs}ms; killing the tree");
                    KillTree();
                    try { drained = _pumpFf.Join(15000); } catch (ThreadStateException) { drained = true; }
                }
            }
            long finalBytes = 0;
            if (drained)
            {
                // Last writer joined: the bytes on disk are final, so flush, close and size them.
                CloseOutput();
                finalBytes = _dlPath != null && File.Exists(_dlPath) ? new FileInfo(_dlPath).Length : 0;
                if (_preRollBytes != 0 && !_cancel && _stage != Stage.Failed)
                {
                    // Bytes that were staged instead of written are bytes dl.mp3 does not have.
                    // (A refusal or a cancel drops the pre-roll on purpose, which is not a drain
                    // failure - those are reported as themselves further down.)
                    _log?.LogError($"{Tag} {_preRollBytes} staged bytes never reached dl.mp3; " +
                                   $"the file is not the whole download ({finalBytes} bytes written)");
                    drained = false;
                }
            }
            if (_cancel) { _stage = Stage.Cancelled; _log?.LogInfo($"{Tag} pipeline cancelled; {finalBytes} bytes on disk"); return; }
            if (_stage == Stage.Failed)
            {
                // Refused while we were downloading: the refusal is the answer, keep its sentence.
                _log?.LogInfo($"{Tag} pipeline stopped after the refusal (yt-dlp={_ytExit} ffmpeg={_ffExit}, " +
                              $"{finalBytes} bytes); message stays '{_message}'");
                return;
            }
            if (!drained)
            {
                // No Complete without a drained file: the pumps own the bytes, the publish owns the
                // claim, and the claim is only allowed after the writers are gone.
                Fail(Refusal.CrashMidDownload,
                     $"the output pump did not finish writing dl.mp3 ({finalBytes} bytes written)");
                return;
            }
            if (_ytExit == 0 && _ffExit == 0 && finalBytes > ReadyBytes)
            {
                _tCompleteMs = _clock.ElapsedMilliseconds;
                _bytesBest = finalBytes;
                _stage = Stage.Complete;
                _log?.LogInfo($"{Tag} complete in {_tCompleteMs}ms bytes={finalBytes} yt-dlp={_ytExit} ffmpeg={_ffExit}");
                return;
            }
            string err, ffErr;
            lock (_gate) { err = _ytErr.ToString().Trim(); ffErr = _ffErr.ToString().Trim(); }
            // The extractor's own line wins over any rewording of ours.
            var reason = Reason(err, "");
            if (reason != null) Fail(Refusal.Extraction, reason);
            else if (ffErr.Length > 0) Fail(Refusal.CrashMidDownload,
                $"ffmpeg stopped (exit={_ffExit}, {finalBytes} bytes written): {FirstLine(ffErr, "")}");
            else Fail(Refusal.CrashMidDownload,
                $"the download stopped (yt-dlp={_ytExit} ffmpeg={_ffExit}, {finalBytes} bytes written)");
            _log?.LogWarning($"{Tag} yt-dlp stderr: {Short(err, 400)}");
            _log?.LogWarning($"{Tag} ffmpeg stderr: {Short(ffErr, 400)}");
        }
        catch (Exception e)
        {
            if (!_cancel) Fail(Refusal.CrashMidDownload, $"wait thread threw {e.GetType().Name}: {e.Message}");
        }
        finally { KillTree(); }
    }

    private void CloseOutput()
    {
        lock (_outGate)
        {
            var o = _out;
            _out = null;
            if (o == null) return;
            try { o.Flush(); o.Dispose(); } catch { }
        }
    }

    // ================================================================ cancel / fail / dispose

    // Enforce the disk ceiling from the producer's own thread. Two things are bounded: the finished
    // cache (oldest completed tracks go first, this request's own track is never a candidate) and the
    // working root (directories whose owner process is gone). Both are cheap to skip and expensive to
    // run, so this is driven by `_nextBudgetCheck` (every `BudgetCheckBytes` of audio written).
    private void CheckCeiling()
    {
        int evicted = EvictOldest(_cachedPath, _log, out long freed);
        int swept = SweepStreamRoot(_trackDir, _log);
        if (evicted > 0 || swept > 0)
            _log?.LogInfo($"{Tag} ceiling: {CacheBudgetBytes / (1024 * 1024)}MB cache / " +
                          $"{StreamBudgetBytes / (1024 * 1024)}MB stream -> evicted {evicted} finished track(s) " +
                          $"({freed / 1024}KB), swept {swept} abandoned stream dir(s)");
    }

    // True once no worker of this request can still write into its stream directory - the guard the
    // host's cleanup uses. `&` and not `&&`: every thread is joined, in order, before anyone decides.
    internal bool WorkersFinished(int waitMs)
    {
        bool a = JoinQuiet(_preflightThread, waitMs);
        bool b = JoinQuiet(_pumpYt, waitMs);
        bool c = JoinQuiet(_pumpFf, waitMs);
        bool d = JoinQuiet(_waitThread, waitMs);
        return a & b & c & d;
    }

    private static bool JoinQuiet(Thread t, int waitMs)
    {
        if (t == null) return true;
        try { return t.Join(waitMs); }
        catch (ThreadStateException) { return true; }     // never started: it can write nothing
        catch (ThreadInterruptedException) { return false; }
    }
    internal void Cancel(string why)
    {
        _cancel = true;
        try { _sinkEvent.Set(); } catch (ObjectDisposedException) { }
        KillTree();
        CloseOutput();
        bool wasLive = _stage == Stage.Downloading || _stage == Stage.Spawning || _stage == Stage.Preflight;
        if (wasLive)
        {
            _stage = Stage.Cancelled;
            _log?.LogInfo($"{Tag} cancel ('{why}'): job terminated; yt-dlp exit={_ytExit} ffmpeg exit={_ffExit}");
        }
        else _log?.LogInfo($"{Tag} cancel ('{why}'): nothing was running (stage={_stage})");
    }

    private void Fail(Refusal kind, string detail)
    {
        // First verdict wins: a refusal that kills the tree also makes both processes exit non-zero,
        // and that must not overwrite the sentence the user is shown.
        if (_stage == Stage.Cancelled || _stage == Stage.Failed) return;
        _stage = Stage.Failed;
        _refusal = kind;
        _message = SentenceFor(kind, detail);
        // A refusal must not leave the download running: the whole tree goes, and so does anything
        // that was staged for it (nothing has been written to dl.mp3, so nothing can play).
        lock (_outGate) { _preRoll.Clear(); _preRollBytes = 0; }
        CloseOutput();
        try { _sinkEvent.Set(); } catch (ObjectDisposedException) { }   // release a parked pump
        KillTree();
        _log?.LogWarning($"{Tag} FAILED[{kind}] {_message}");
    }

    internal static string SentenceFor(Refusal kind, string detail) => kind switch
    {
        // The detail is already the full, exact sentence (see MissingToolsMessage).
        Refusal.ToolsMissing => detail ?? MissingToolsMessage(RequiredTools),
        Refusal.BadUrl => $"Not a link we can play ({detail})",
        // Verbatim: this is the extractor talking, not us.
        Refusal.Extraction => detail ?? "yt-dlp failed without a message",
        Refusal.CrashMidDownload => detail ?? "The download stopped",
        Refusal.DiskError => $"Cannot write the music cache ({detail})",
        Refusal.Budget => detail ?? "The music cache limit was reached",
        Refusal.Cancelled => "Cancelled",
        _ => detail ?? "Unknown failure",
    };

    // yt-dlp's own sentence: its `ERROR:` line, with the `ERROR: ` tag and the
    // `[extractor] id: ` locator removed so what is left is the reason it actually printed
    // ("Sign in to confirm your age", "Unsupported URL: …", "This video is unavailable").
    // `null` when yt-dlp printed no error line at all.
    internal static string Reason(string stderr, string stdout)
    {
        string best = null;
        foreach (var src in new[] { stderr, stdout })
        {
            if (string.IsNullOrEmpty(src)) continue;
            foreach (var line in src.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) best = t.Substring(6).Trim();
                else if (t.StartsWith("error:", StringComparison.OrdinalIgnoreCase)) best = t.Substring(6).Trim();
            }
        }
        if (string.IsNullOrEmpty(best)) return null;
        return Short(StripLocator(best), 300);
    }

    // "[youtube] dQw4w9WgXcQ: Sign in to confirm your age" -> "Sign in to confirm your age".
    // A message that does not start with a bracketed extractor is left exactly as it is
    // ("Unsupported URL: https://…" must keep its own label).
    private static string StripLocator(string s)
    {
        if (s.Length == 0 || s[0] != '[') return s;
        int close = s.IndexOf(']');
        if (close <= 0 || close + 1 >= s.Length) return s;
        var rest = s.Substring(close + 1).TrimStart();
        int colon = rest.IndexOf(':');
        if (colon > 0 && colon <= 48) rest = rest.Substring(colon + 1).TrimStart();
        return rest.Length > 0 ? rest : s;
    }

    public void Dispose()
    {
        _cancel = true;
        try { _sinkEvent.Set(); } catch (ObjectDisposedException) { }
        KillTree();
        CloseOutput();
        _stage = Stage.Cancelled;
    }

    // ================================================================ job object

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            _log?.LogWarning($"{Tag} CreateJobObject failed ({Marshal.GetLastWin32Error()}); " +
                             "falling back to Kill(entireProcessTree) only");
            return IntPtr.Zero;
        }
        var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        ext.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        var mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(ext, mem, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, mem, (uint)size))
                _log?.LogWarning($"{Tag} SetInformationJobObject(KILL_ON_JOB_CLOSE) failed ({Marshal.GetLastWin32Error()})");
        }
        finally { Marshal.FreeHGlobal(mem); }
        return job;
    }

    private void AssignToJob(IntPtr job, Process p, string what)
    {
        try
        {
            if (!AssignProcessToJobObject(job, p.Handle))
                _log?.LogWarning($"{Tag} AssignProcessToJobObject({what} pid={p.Id}) failed ({Marshal.GetLastWin32Error()})");
        }
        catch (Exception e) { _log?.LogWarning($"{Tag} job assign ({what}) threw {e.GetType().Name}: {e.Message}"); }
    }

    // Terminate the whole tree, then close the job so KILL_ON_JOB_CLOSE mops up anything the
    // termination raced with. Both children were assigned before they produced anything.
    private void KillTree()
    {
        var job = _job;
        if (job != IntPtr.Zero)
        {
            try { TerminateJobObject(job, 1); } catch { }
            _job = IntPtr.Zero;
            try { CloseHandle(job); } catch { }
        }
        KillOne(_ytdlp);
        KillOne(_ffmpeg);
        KillOne(_preflight);
    }

    private void KillOne(Process p)
    {
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(true); _log?.LogInfo($"{Tag} killed pid={p.Id} ({p.ProcessName})"); } }
        catch (Exception e) { _log?.LogWarning($"{Tag} kill pid={Pid(p)} threw {e.GetType().Name}: {e.Message}"); }
    }

    // ================================================================ reporting

    internal long BytesOnDisk
    {
        get
        {
            try { return _dlPath != null && File.Exists(_dlPath) ? new FileInfo(_dlPath).Length : 0; }
            catch { return 0; }
        }
    }

    internal long BytesStaged { get { lock (_outGate) return _preRollBytes; } }

    internal string Report() =>
        $"stage={_stage} verdictOk={_verdictOk} bytes={BytesOnDisk} staged={BytesStaged} " +
        $"t:request={_tRequestMs}ms preflight={_tPreflightMs}ms spawn={_tSpawnMs}ms " +
        $"firstByte={_tFirstByteMs}ms verdict={_tVerdictMs}ms ready({ReadyBytes / 1024}KB)={_tReadyMs}ms complete={_tCompleteMs}ms " +
        $"exit(yt={_ytExit} ff={_ffExit}) key='{KeyOf(_info) ?? "?"}' track='{Short(_info.Title, 60)}'";

    internal static bool TryDelete(string path, IHostContext log = null)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return true;
        try { File.Delete(path); return true; }
        catch (Exception e)
        {
            log?.LogInfo($"{Tag} delete '{path}' failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private static int Pid(Process p) { try { return p == null ? -1 : p.Id; } catch { return -1; } }

    private static string Safe(string s) => s == null ? "" : s.Trim();

    private static string Short(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    // The line worth showing is yt-dlp's ERROR line, not the first progress line. Preference:
    // the last line containing "ERROR", else the last non-empty line.
    private static string FirstLine(string a, string b)
    {
        string best = null, last = null;
        foreach (var src in new[] { a, b })
        {
            if (string.IsNullOrEmpty(src)) continue;
            foreach (var line in src.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                last = t;
                if (t.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0) best = t;
            }
        }
        return Short(best ?? last ?? "(no output)", 220);
    }
}
