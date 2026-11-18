using System;

// The two engine surfaces YtDlpPipeline reads while resolving the game root. The resolver under test
// (CacheRoot) is the shipping code; everything else in this project is real.
namespace BepInEx
{
    internal static class Paths
    {
        public static string GameRootPath => AppContext.BaseDirectory.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }
}

namespace UnityEngine
{
    internal static class Application
    {
        public static string dataPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Alterum_Data");
    }
}
