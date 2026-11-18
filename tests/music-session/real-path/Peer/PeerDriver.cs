using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using AlterumModelSwap.Contracts;
using AudioPlayer.UI;
using Il2CppInterop.Runtime;

namespace AlterumModelSwap.Hot
{
    // The harness-side public surface of one peer assembly. The runner loads two copies of this
    // assembly (host/follower) so every static in LobbyMusicNet is independent, and talks to each
    // copy only through these primitive methods.
    public static class PeerDriver
    {
        private sealed class LogSink : IHostContext
        {
            public readonly List<string> Lines = new List<string>();
            public string ScreensDir => "/tmp/music-harness";
            public void LogInfo(string message) => Lines.Add("INFO " + message);
            public void LogWarning(string message) => Lines.Add("WARN " + message);
            public void LogError(string message) => Lines.Add("ERROR " + message);
        }

        private sealed class Bridge : INetBridge
        {
            public readonly List<ulong> Connected = new List<ulong>();
            public readonly List<ulong> Targets = new List<ulong>();
            public readonly List<byte[]> Payloads = new List<byte[]>();

            public bool Send(NetKind kind, ulong targetClientId, ReadOnlySpan<byte> payload)
            {
                if (kind != NetKind.App) return false;
                if (!Connected.Contains(targetClientId)) return false;
                Targets.Add(targetClientId);
                Payloads.Add(payload.ToArray());
                return true;
            }

            public int Peers(List<ulong> into)
            {
                if (into == null) return Connected.Count;
                into.Clear();
                into.AddRange(Connected);
                return Connected.Count;
            }
        }

        private static readonly LogSink Log = new LogSink();
        private static readonly Bridge Net = new Bridge();
        private static readonly AudioPlayerUI Ui = new AudioPlayerUI();
        private static MusicStreamHost _host;
        private static bool _isServer;
        private static ulong _localId, _serverId;

        public static void Initialize(bool isServer, ulong localId, ulong serverId, ulong[] peers)
        {
            Log.Lines.Clear();
            Net.Targets.Clear();
            Net.Payloads.Clear();
            Net.Connected.Clear();
            Net.Connected.AddRange(peers ?? Array.Empty<ulong>());
            RemoteModels.SetPeers(peers ?? Array.Empty<ulong>());
            SceneCache.Ui = new object[] { Ui };
            Ui._playButton.IsInteractable = true;
            Ui._pauseButton.IsInteractable = true;
            Ui._progressSlider.interactable = true;
            Ui._progressSlider.minValue = 0f;
            Ui._progressSlider.maxValue = 1000f;
            Ui._progressSlider.value = 0f;
            Ui._statusText.text = "";
            Ui._errorText.text = "";
            Ui._titleText.text = "";
            Ui._timeText.text = "";
            Ui._state = 0;
            Ui._isDraggingProgressSlider = false;
            UnityEngine.Time.SetNow(0);

            _isServer = isServer;
            _localId = localId;
            _serverId = serverId;
            LobbyMusicNet.Reset();
            LobbyMusicHooks.Install(Log);
            LobbyMusicNet.Bind(Net, true, isServer, localId, serverId, 1u);
            _host = LobbyMusicHooks.Host;
        }

        // Shipping Bind with a new bridge epoch: same transport object, same role, new session.
        public static void Rebind(uint epoch) => LobbyMusicNet.Bind(Net, true, _isServer, _localId, _serverId, epoch);
        // Role transitions for the session-lifecycle scenario. Rehost keeps the same bridge object.
        public static void Rehost(uint epoch) => LobbyMusicNet.Bind(Net, true, true, _localId, _serverId, epoch);
        public static void Leave() => LobbyMusicNet.Bind(null, false, false, 0, 0, 0);

        // The process-lifetime id counter lives in AppDomain data. The harness can move it to the
        // bound to prove the explicit refusal path without a 2-billion-request loop.
        public static long IdSlotValue()
        {
            var slot = AppDomain.CurrentDomain.GetData(LobbyMusicNet.IdSlotName) as long[];
            return slot == null ? 0 : slot[0];
        }

        public static void SetIdSlot(long next)
        {
            var slot = AppDomain.CurrentDomain.GetData(LobbyMusicNet.IdSlotName) as long[];
            if (slot == null)
            {
                slot = new long[1];
                AppDomain.CurrentDomain.SetData(LobbyMusicNet.IdSlotName, slot);
            }
            slot[0] = next;
        }

        public static void SetScene(string scene) => UnityEngine.SceneManagement.SceneManager.Active = scene;

        public static void Tick(double now)
        {
            UnityEngine.Time.SetNow(now);
            LobbyMusicHooks.Tick(Log);
        }

        // A frame the transport delivered to this peer. `sender` is the transport's sender id, never
        // a value from the payload.
        public static void Deliver(ulong sender, byte[] payload)
        {
            if (payload == null) return;
            var frame = new NetInboundFrame
            {
                SenderClientId = sender,
                Kind = NetKind.App,
                Payload = payload,
            };
            LobbyMusicNet.HandleFrame(Log, Net, frame, UnityEngine.Time.unscaledTime);
        }

        public static object[] Drain()
        {
            var targets = Net.Targets.ToArray();
            var payloads = Net.Payloads.ToArray();
            Net.Targets.Clear();
            Net.Payloads.Clear();
            return new object[] { targets, payloads };
        }

        // Same shape as Drain, but leaves the queue in place: a test can assert what a click put on
        // the wire before routing it.
        public static object[] PeekOut() => new object[] { Net.Targets.ToArray(), Net.Payloads.ToArray() };

        public static bool ClickPause() => InvokePrefix("PauseClickedPrefix");
        public static bool ClickPlay() => InvokePrefix("PlayClickedPrefix");
        public static void OpenPlayer() => Ui.Show(Ui._controller, null);
        public static void ClosePlayer() => Ui.OnCloseClicked();
        public static bool PlayerIsShown() => Ui._isShown;

        public static bool ClickSeek(double fraction)
        {
            double f = fraction < 0 ? 0 : (fraction > 1 ? 1 : fraction);
            Ui._progressSlider.value = (float)(Ui._progressSlider.minValue + f * (Ui._progressSlider.maxValue - Ui._progressSlider.minValue));
            return InvokePrefix("ProgressReleasedPrefix");
        }

        private static bool InvokePrefix(string name)
        {
            var m = typeof(LobbyMusicHooks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            return (bool)m.Invoke(null, new object[] { Ui });
        }

        public static bool Start(string url) => LobbyMusicHooks.AuthoritativeStart(url, "harness");
        public static bool Submit(string url) => LobbyMusicHooks.SubmitIntent(url, "harness") == LobbyMusicHooks.IntentOutcome.Sent;

        public static bool ConsolePause() => LobbyMusicHooks.Pause();
        public static bool ConsoleResume() => LobbyMusicHooks.Resume();
        public static bool ConsoleSeek(long ms) => LobbyMusicHooks.Seek(ms);

        public static void LateJoin(ulong peerId) => LobbyMusicNet.OnPeerMod(Log, peerId);

        public static void SetCoverage(long ms) => _host.SetCoverage(ms);
        public static void SetTotal(long ms) => _host.SetTotal(ms);
        public static void SetPeers(ulong[] peers)
        {
            Net.Connected.Clear();
            Net.Connected.AddRange(peers ?? Array.Empty<ulong>());
            RemoteModels.SetPeers(peers ?? Array.Empty<ulong>());
        }

        public static string Snapshot()
        {
            var sb = new StringBuilder();
            Line(sb, "role", _isServer ? "host" : "follower");
            Line(sb, "localId", _localId.ToString(CultureInfo.InvariantCulture));
            Line(sb, "serverId", _serverId.ToString(CultureInfo.InvariantCulture));
            Line(sb, "url", _host.Url);
            Line(sb, "phase", _host.Current.ToString());
            Line(sb, "channel", _host.HasPlayableChannel ? "1" : "0");
            Line(sb, "enginePaused", _host.EnginePaused ? "1" : "0");
            Line(sb, "positionMs", _host.PositionMs.ToString(CultureInfo.InvariantCulture));
            Line(sb, "coverageMs", _host.CoverageMs.ToString(CultureInfo.InvariantCulture));
            Line(sb, "totalMs", _host.TotalMs.ToString(CultureInfo.InvariantCulture));
            Line(sb, "canPlay", _host.CanPlay ? "1" : "0");
            Line(sb, "canPause", _host.CanPause ? "1" : "0");
            Line(sb, "canSeek", _host.CanSeek ? "1" : "0");
            Line(sb, "uiPlay", Ui._playButton.IsInteractable ? "1" : "0");
            Line(sb, "uiPause", Ui._pauseButton.IsInteractable ? "1" : "0");
            Line(sb, "uiSeek", Ui._progressSlider.interactable ? "1" : "0");
            Line(sb, "uiStatus", Ui._statusText.text);
            Line(sb, "uiError", Ui._errorText.text);
            Line(sb, "uiTitle", Ui._titleText.text);
            Line(sb, "uiTime", Ui._timeText.text);
            Line(sb, "uiState", Ui._state.ToString(CultureInfo.InvariantCulture));
            Line(sb, "pauseCalls", _host.PauseCalls.ToString(CultureInfo.InvariantCulture));
            Line(sb, "resumeCalls", _host.ResumeCalls.ToString(CultureInfo.InvariantCulture));
            Line(sb, "seekCalls", _host.SeekCalls.ToString(CultureInfo.InvariantCulture));
            Line(sb, "seekTarget", _host.LastSeekTarget.ToString(CultureInfo.InvariantCulture));
            Line(sb, "controlPending", ReflectBool("ControlPending"));
            Line(sb, "controlGen", ReflectUInt("FollowerControlGeneration"));
            Line(sb, "hostControlGen", ReflectUInt("HostControlGeneration"));
            Line(sb, "counters", LobbyMusicNet.Counters);
            return sb.ToString();
        }

        public static string[] Logs() => Log.Lines.ToArray();
        public static void ClearLogs() => Log.Lines.Clear();

        private static string ReflectBool(string property)
        {
            var p = typeof(LobbyMusicNet).GetProperty(property, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (p == null) return "na";
            try { return (bool)p.GetValue(null) ? "1" : "0"; } catch { return "na"; }
        }

        private static string ReflectUInt(string property)
        {
            var p = typeof(LobbyMusicNet).GetProperty(property, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (p == null) return "na";
            try { return ((uint)p.GetValue(null)).ToString(CultureInfo.InvariantCulture); } catch { return "na"; }
        }

        private static void Line(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append('=').Append((value ?? "").Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
        }
    }
}
