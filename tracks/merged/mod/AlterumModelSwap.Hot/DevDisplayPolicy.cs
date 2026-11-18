using System;

namespace AlterumModelSwap.Hot;

internal static class DevDisplayPolicy
{
    internal static bool Enabled(string[] args) => Array.Exists(args,
        arg => string.Equals(arg, "-alterumDevMode", StringComparison.OrdinalIgnoreCase));
}
