using System;

namespace AlterumModelSwap;

// The single parse of the diagnostics-only switch, shared by the two assemblies that need it: the
// permanent host (AlterumModelSwap.dll, where it gates NetBridge) and the reloadable hot module
// (AlterumModelSwap.Hot.dll, where it gates every gameplay path). The file lives at the mod root
// and each project links it in (`<Compile Include="../DiagnosticsMode.cs" Link="DiagnosticsMode.cs" />`),
// so the two sides read the same flag the same way and cannot drift. Plain managed code: no
// Unity/IL2CPP type is touched here, and nothing in Contracts changes.
//
// Launch:  Alterum.exe -alterumDiagnosticsOnly
//
// Diagnostics-only means the mod may observe (census, traces, screenshots, QcGate) but must not
// touch shared game state: no gameplay patches, no custom roster registered, and the host's
// named-message bridge never registers a handler, never sends, and never reads `net.request`.
public static class DiagnosticsMode
{
    /// <summary>
    /// The launch argument, spelled exactly like this. There is deliberately no `=value` form:
    /// presence of the argument is the switch.
    /// </summary>
    public const string Flag = "-alterumDiagnosticsOnly";

    /// <summary>
    /// True when the process was started with <see cref="Flag"/> in any casing. Parsed once, when
    /// this type is first read: the command line cannot change afterwards, so no caller re-parses.
    /// </summary>
    public static readonly bool Enabled = Parse(Environment.GetCommandLineArgs());

    private static bool Parse(string[] args)
    {
        if (args == null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
