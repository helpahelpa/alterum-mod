using System;
using System.Collections.Generic;
using AlterumModelSwap.Contracts;

namespace AlterumModelSwap.Hot
{
    // A transport shim at the MusicStreamHost boundary. It commits Pause/Resume/Seek and reports a
    // paused readback, and it notifies LobbyMusicNet exactly the way the shipping host does. The
    // harness therefore tests the SHIPPING control protocol and commit decision; the real FMOD
    // readback is the live controller's evidence, not this shim's.
    internal sealed class MusicStream
    {
        private readonly MusicStreamHost _host;
        internal MusicStream(MusicStreamHost host) { _host = host; }
        internal bool IsPaused => _host.EnginePaused;
        internal bool HasChannel => _host.HasPlayableChannel;
        internal bool Playable => _host.HasPlayableChannel;
        internal long LengthMs() => _host.TotalMs;
        internal long PositionMs => _host.PositionMs;
        internal bool Playing => _host.HasPlayableChannel && !_host.EnginePaused;
        internal string BusState => "harness";
        internal long OpenMs => 0;
        internal string FilePath => null;
        internal long SnapshotBytes => _host.CoverageMs * 32;
        internal long SnapshotLengthMs => _host.CoverageMs;
    }

    internal sealed class MusicStreamHost
    {
        internal enum Phase { Idle, Preflight, Buffering, Playing, Paused, Failed, Cancelled }

        internal const long SwitchMarginMs = 1500;
        internal const long ReadbackToleranceMs = 300;

        private readonly IHostContext _log;
        private readonly MusicStream _engine;
        private Phase _phase;
        private string _url = "";
        private string _source = "test";
        private string _title = "";
        private bool _hasChannel, _paused, _growing;
        private long _positionMs, _coverageMs = 222000, _totalMs = 222000;
        private long _startAtMs;
        private bool _startPaused;
        private bool _producerDone = true;
        private string _error = "";
        private bool _errorVisible;

        internal MusicStreamHost(IHostContext log)
        {
            _log = log;
            _engine = new MusicStream(this);
        }

        internal Phase Current => _phase;
        internal MusicStream Engine => _engine;
        internal string Url => _url;
        internal string DisplayTitle => _title;
        internal bool OwnsPanel =>
            _phase == Phase.Preflight || _phase == Phase.Buffering || _phase == Phase.Playing ||
            _phase == Phase.Paused || _phase == Phase.Failed || _phase == Phase.Cancelled;
        internal bool OwnsPlayback =>
            _phase == Phase.Preflight || _phase == Phase.Buffering || _phase == Phase.Playing || _phase == Phase.Paused;
        internal bool HasPlayableChannel => _hasChannel;
        internal bool IsGrowing => _growing;
        internal long PositionMs => _positionMs;
        internal long CoverageMs => _coverageMs;
        internal long TotalMs => _totalMs;
        internal long LengthMs => _totalMs;
        internal double BufferFraction => _totalMs <= 0 ? 0 : Math.Min(1.0, (double)_coverageMs / _totalMs);
        internal bool CanPlay => _phase == Phase.Paused && _hasChannel;
        internal bool CanPause => _phase == Phase.Playing && _hasChannel;
        internal bool CanSeek => !_growing && _hasChannel && _totalMs > 0;
        internal bool ProducerDone => _producerDone;
        internal string LastError => _error;
        internal string DeferredHandoff => null;
        internal string SnapshotDir => null;
        internal string CachedPath => null;
        internal YtDlpPipeline Pipeline => null;
        internal string CacheRoot => "/tmp/music-harness/cache";
        internal long SnapshotSwitches => 0;
        internal long MaxSwitchGapMs => 0;
        internal int SnapOpen => 0;
        internal int SnapNewest => 0;
        internal bool EnginePaused => _paused;

        internal int PauseCalls, ResumeCalls, SeekCalls, PlayCalls, PlayAtCalls, StopCalls, CancelCalls, TickChannelCreates;
        internal long LastSeekTarget = -1;

        internal void SetCoverage(long ms) => _coverageMs = Math.Max(0, ms);
        internal void SetTotal(long ms) => _totalMs = Math.Max(0, ms);
        internal void SetProgressive(bool growing) => _growing = growing;

        internal bool Play(string url, string source)
        {
            if (YtDlpPipeline.Classify(url) != YtDlpPipeline.UrlKind.Page) return false;
            Stop("new request");
            _url = url;
            _source = source ?? "test";
            _title = "";
            _phase = Phase.Buffering;
            _hasChannel = false;
            _paused = false;
            _positionMs = 0;
            _startAtMs = 0;
            _startPaused = false;
            PlayCalls++;
            _log?.LogInfo($"[test:host] play url='{_url}' source={_source}");
            return true;
        }

        internal bool PlayAt(string url, long startAtMs, string source, bool startPaused = false)
        {
            if (!Play(url, source)) return false;
            _startAtMs = Math.Max(0, startAtMs);
            _startPaused = startPaused;
            PlayAtCalls++;
            _log?.LogInfo($"[test:host] playAt url='{_url}' anchor={startAtMs} paused={startPaused}");
            return true;
        }

        internal void SetStartTarget(long startAtMs, bool paused, float now)
        {
            if (_hasChannel) return;
            if (_phase != Phase.Preflight && _phase != Phase.Buffering && _phase != Phase.Paused) return;
            _startAtMs = Math.Max(0, startAtMs);
            _startPaused = paused;
        }

        internal void Tick(float now)
        {
            if (_hasChannel) return;
            if (_phase != Phase.Preflight && _phase != Phase.Buffering) return;
            // The channel appears here, from the start target in force at this moment - exactly the
            // shipping rule (the pending start is what the first channel is created from).
            _hasChannel = true;
            _positionMs = Math.Max(0, _startAtMs);
            _paused = _startPaused;
            _phase = _startPaused ? Phase.Paused : Phase.Playing;
            TickChannelCreates++;
            _log?.LogInfo($"[test:host] channel created at {_positionMs}ms paused={_startPaused}");
            LobbyMusicNet.NoteTransport(_startPaused ? "start-paused" : "start-playing");
        }

        internal bool Pause()
        {
            if (!_hasChannel) return false;
            PauseCalls++;
            _paused = true;
            _phase = Phase.Paused;
            _log?.LogInfo($"[test:host] paused at {_positionMs}ms");
            LobbyMusicNet.NoteTransport("pause");
            return true;
        }

        internal bool Resume()
        {
            if (!_hasChannel) return false;
            ResumeCalls++;
            _paused = false;
            _phase = Phase.Playing;
            _log?.LogInfo($"[test:host] resumed at {_positionMs}ms");
            LobbyMusicNet.NoteTransport("resume");
            return true;
        }

        internal bool SetVolume(float v) => true;

        internal bool Seek(long absoluteMs)
        {
            if (!_hasChannel) return false;
            if (absoluteMs < 0) return false;
            if (absoluteMs > CoverageMs - SwitchMarginMs)
            {
                _log?.LogWarning($"[test:host] seek({absoluteMs}ms) refused: coverage={CoverageMs}ms");
                return false;
            }
            SeekCalls++;
            LastSeekTarget = absoluteMs;
            _positionMs = absoluteMs;
            _log?.LogInfo($"[test:host] seek({absoluteMs}ms) ok");
            LobbyMusicNet.NoteTransport("seek");
            return true;
        }

        internal bool SeekVerified(long absoluteMs, out long actualMs)
        {
            bool ok = Seek(absoluteMs);
            actualMs = _positionMs;
            if (!ok) return false;
            long delta = actualMs - absoluteMs;
            if (Math.Abs(delta) <= ReadbackToleranceMs) return true;
            _log?.LogWarning($"[test:host] seek readback mismatch target={absoluteMs} actual={actualMs}");
            return false;
        }

        internal void Stop(string why)
        {
            bool owned = _phase != Phase.Idle;
            _phase = Phase.Idle;
            _hasChannel = false;
            _paused = false;
            _growing = false;
            StopCalls++;
            if (owned) LobbyMusicNet.NoteTransport("stop");
        }

        internal void Cancel(string why)
        {
            bool had = _phase != Phase.Idle || _hasChannel;
            if (!had) return;
            CancelCalls++;
            _phase = Phase.Cancelled;
            _hasChannel = false;
            _paused = false;
            LobbyMusicNet.NoteTransport("cancel");
        }

        internal bool TakeAnnounce() => false;
        internal void Dispose() { }

        internal string StatusText()
        {
            switch (_phase)
            {
                case Phase.Preflight: return "READ";
                case Phase.Buffering: return "BUF";
                case Phase.Paused: return "PAUSED";
                case Phase.Playing: return _producerDone ? "CACHE" : "STREAM";
                case Phase.Failed: return "FAIL";
                case Phase.Cancelled: return "STOP";
                default: return "";
            }
        }

        internal string ErrorText() => _errorVisible ? _error : "";

        internal int UiState()
        {
            switch (_phase)
            {
                case Phase.Preflight:
                case Phase.Buffering: return 1;
                case Phase.Playing: return 2;
                case Phase.Paused: return 3;
                default: return 0;
            }
        }

        internal static string Fmt(long ms)
        {
            if (ms < 0) ms = 0;
            long s = ms / 1000;
            return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }

        internal string TimingReport() => "[test:host] timing n/a";
    }
}
