using System;
using System.IO;
using AlterumModelSwap.Hot;

// One process invocation = one cache-root resolution. `run-cases.sh` invokes this binary with the
// actual launch argument(s) and this program asserts the SHIPPING YtDlpPipeline.CacheRoot result.
internal static class Program
{
    private static int Main(string[] argv)
    {
        if (argv.Length == 1 && argv[0] == "--windows-forms") return WindowsForms();
        string expect = null;
        foreach (var a in argv)
            if (a.StartsWith("--expect=", StringComparison.Ordinal)) expect = a.Substring("--expect=".Length);
        if (expect == null)
        {
            Console.WriteLine("usage: MusicCacheRoot.RealPath --expect=default|<absolute path> [real launch args]");
            return 2;
        }
        if (expect == "default") expect = DefaultCacheRoot();

        string got = YtDlpPipeline.CacheRoot;
        string want = Path.GetFullPath(expect).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string actual = Path.GetFullPath(got).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool ok = string.Equals(want, actual, PathComparison);
        Console.WriteLine($"expect={want}");
        Console.WriteLine($"cacheRoot={actual}");
        Console.WriteLine(ok ? "PASS cache root" : "FAIL cache root");
        return ok ? 0 : 1;
    }

    // The Windows-only parser cannot be executed on a Linux test host, so the shipping lexical
    // form rule is asserted directly. The platform call (Path.IsPathFullyQualified) is the gate;
    // this predicate is its Windows-specific cross-check and is also used on Windows builds.
    private static int WindowsForms()
    {
        var cases = new (string Value, bool Want, string Why)[]
        {
            (@"C:\cache", true, "drive-absolute"),
            (@"C:/cache", true, "drive-absolute forward slash"),
            (@"\\server\share", true, "UNC"),
            ("C:cache", false, "drive-relative"),
            (@"\cache", false, "current-drive root-relative"),
            (@"cache\x", false, "relative"),
            ("/tmp/cache", false, "POSIX absolute is not a Windows fully qualified form"),
        };
        int bad = 0;
        foreach (var c in cases)
        {
            bool got = YtDlpPipeline.IsWindowsFullyQualifiedForm(c.Value);
            bool ok = got == c.Want;
            Console.WriteLine("{0} windows_form {1,-34} got={2} want={3} ({4})",
                ok ? "PASS" : "FAIL", c.Value, got, c.Want, c.Why);
            if (!ok) bad++;
        }
        Console.WriteLine(bad == 0 ? "PASS windows path forms" : $"FAIL {bad} windows path form(s)");
        return bad == 0 ? 0 : 1;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string DefaultCacheRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
        return Path.Combine(local, "AlterumModSwap", "music-cache");
    }
}
