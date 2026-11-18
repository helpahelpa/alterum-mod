using System;
using System.Collections.Generic;
using AlterumModelSwap.Contracts;

// The game world around the shipping music code. Every type here exists only to satisfy the
// compiler and to let the harness observe/commit a transport; none of it implements music policy.
// The policy under test (protocol, authority, dedupe, UI eligibility, publication) stays in the
// linked LobbyMusicNet.cs / LobbyMusicHooks.cs.

namespace UnityEngine
{
    internal static class Time
    {
        internal static float unscaledTime;
        internal static void SetNow(double now) => unscaledTime = (float)now;
    }
}

namespace UnityEngine.SceneManagement
{
    internal static class SceneManager
    {
        internal static string Active = "Lobby";
        internal static Scene GetActiveScene() => new Scene { name = Active };
    }
    internal struct Scene { public string name; }
}

namespace UnityEngine.UI
{
    public sealed class Slider
    {
        public float minValue = 0f;
        public float maxValue = 1f;
        public float value;
        public bool interactable = true;
    }

    public sealed class Button
    {
        public bool IsInteractable = true;
    }
}

namespace TMPro
{
    public sealed class TMP_Text
    {
        public string text = "";
        public bool enableWordWrapping = true;
    }

    public sealed class TMP_InputField
    {
        public string text = "";
    }
}

namespace Il2CppInterop.Runtime
{
    public sealed class Il2CppType
    {
        public static Il2CppType Of<T>() => new Il2CppType();
    }
}

namespace AudioPlayer.Network
{
    // The game's networked controller. Only the identity/TryCast path is used by the console request
    // seam; the shipping request code never calls into it here.
    public sealed class NetworkedAudioController : AudioPlayer.Core.IAudioPlayerController
    {
        public string CurrentUrl => "";
        public AudioPlayer.Core.PlayerState CurrentPlayerState => AudioPlayer.Core.PlayerState.Idle;
        public object ActiveHandle => null;
        public void Stop() { }
    }
}

namespace AudioPlayer.Core
{
    // The scene's Audio3DSystem, which is the controller carrier in the native panel.
    public sealed class Audio3DSystem : IAudioPlayerController
    {
        public string CurrentUrl => "";
        public PlayerState CurrentPlayerState => PlayerState.Idle;
        public object ActiveHandle => null;
        public void Stop() { }
    }

    public enum PlayerState
    {
        Idle = 0,
        Loading = 1,
        Playing = 2,
        Paused = 3,
    }

    public interface IAudioPlayerController
    {
        string CurrentUrl { get; }
        PlayerState CurrentPlayerState { get; }
        object ActiveHandle { get; }
        void Stop();
    }
}

namespace AudioPlayer.UI
{
    using System;
    using AudioPlayer.Core;
    using TMPro;
    using UnityEngine.UI;

    public sealed class View
    {
        public bool activeSelf = true;
        public void SetActive(bool on) => activeSelf = on;
    }

    public sealed class AudioPlayerUI
    {
        public TMP_InputField _urlInputField = new TMP_InputField();
        public TMP_Text _statusText = new TMP_Text();
        public TMP_Text _errorText = new TMP_Text();
        public TMP_Text _titleText = new TMP_Text();
        public TMP_Text _timeText = new TMP_Text();
        public View _view = new View();
        public Slider _progressSlider = new Slider { maxValue = 1000f };
        public Button _playButton = new Button();
        public Button _pauseButton = new Button();
        public bool _isDraggingProgressSlider;
        public bool _isShown;
        public int _state;
        public IAudioPlayerController _controller;
        public Action<PlayerState> StateChanged;

        public void ApplyState(PlayerState state) { _state = (int)state; }
        public void SetControlsInteractable(bool play, bool pause, bool seek)
        {
            _playButton.IsInteractable = play;
            _pauseButton.IsInteractable = pause;
            _progressSlider.interactable = seek;
        }
        public void SetTitle(string title) => _titleText.text = title ?? "";
        public void SetTimeText(int elapsed, int total) => _timeText.text = elapsed + ":" + total;
        public void ResetProgress() => _progressSlider.value = _progressSlider.minValue;
        public void ShowError(string text) => _errorText.text = text ?? "";
        public void HideError() => _errorText.text = "";
        public void RefreshFromController() { }
        public int GetInstanceID() => 7;
        public void OnLoadClicked() { }
        public void OnPlayClicked() { }
        public void OnPauseClicked() { }
        public void OnCloseClicked() { }
        public void Show(IAudioPlayerController controller, object data) { _controller = controller; _isShown = true; }
    }
}

namespace HarmonyLib
{
    using System;
    using System.Reflection;

    public sealed class HarmonyMethod
    {
        public HarmonyMethod(Type type, string method) { }
    }

    public sealed class Harmony
    {
        public Harmony(string id) { }
        public void Patch(MethodInfo target, HarmonyMethod prefix = null, HarmonyMethod postfix = null) { }
        public void UnpatchSelf() { }
    }

    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name, Type[] parameters = null) => null;
    }
}

namespace AlterumModelSwap.Hot
{
    using System;
    using Il2CppInterop.Runtime;

    internal static class StubCast
    {
        internal static T TryCast<T>(this object value) where T : class => value as T;
    }

    internal static class MenuProof
    {
        internal static readonly List<string> Queue = new List<string>();
        internal static void Enqueue(string request) => Queue.Add(request);
    }

    internal static class SceneCache
    {
        internal static object[] Ui = Array.Empty<object>();
        internal static object[] ScanPolled(Il2CppType type, double timeoutSeconds) => Ui;
    }

    internal sealed class YtDlpPipeline
    {
        internal enum UrlKind { Empty, NotHttp, Media, Page }

        internal const long BytesPerMs = 24;             // ~192 kbps
        internal const long CacheBudgetBytes = 8L * 1024 * 1024 * 1024;
        internal static string LimitRate = "";
        internal static string DoneRoot => "/tmp/music-harness/done";
        internal static string StreamRoot => "/tmp/music-harness/stream";
        internal static string CacheRoot => "/tmp/music-harness";
        internal static string CacheRootSource => "harness";
        internal static string CacheRootWarning => "";
        internal static void LogCacheRoot(AlterumModelSwap.Contracts.IHostContext log) { }
        internal static string ToolsDir => "/tmp/music-harness/tools";
        internal static string GameRootSource => "harness";
        internal static string DescribeTools() => "tools: harness";
        internal static List<string> MissingTools() => new List<string>();
        internal static string MissingToolsMessage(List<string> missing) => "missing: " + string.Join(",", missing);
        internal string Report() => "pipeline: harness";

        // Same rule as the shipping Classify (a direct audio extension is the game's, an http page
        // is ours, anything else is neither).
        internal static UrlKind Classify(string url)
        {
            var u = (url ?? "").Trim();
            if (u.Length == 0) return UrlKind.Empty;
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return UrlKind.NotHttp;
            try
            {
                var path = new Uri(u).AbsolutePath.ToLowerInvariant();
                foreach (var ext in new[] { ".mp3", ".ogg", ".oga", ".opus", ".wav", ".flac", ".m4a", ".aac", ".wma", ".mp4", ".webm" })
                    if (path.EndsWith(ext, StringComparison.Ordinal)) return UrlKind.Media;
                return UrlKind.Page;
            }
            catch { return UrlKind.NotHttp; }
        }
    }

    internal static class RemoteModels
    {
        internal static readonly List<ulong> ModPeers = new List<ulong>();

        internal static void SetPeers(ulong[] peers)
        {
            ModPeers.Clear();
            if (peers != null) ModPeers.AddRange(peers);
        }

        internal static int SendToModPeers(IHostContext log, INetBridge net, byte[] payload, string what, ulong except)
        {
            if (net == null || payload == null) return 0;
            int sent = 0;
            foreach (var id in ModPeers)
            {
                if (except != 0 && id == except) continue;
                bool ok = false;
                try { ok = net.Send(NetKind.App, id, payload); } catch { }
                if (ok) sent++;
            }
            return sent;
        }
    }
}
