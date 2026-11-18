using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using AlterumModelSwap.Contracts;

namespace AlterumModelSwap.Hot;

// Lobby-music transport over the existing bridge (the host's named-message transport, see Net.cs).
//
// ONE authoritative session state, owned by the host, followed by everyone else:
//
//   host  -> peers      state        nonce + revision + sequence + control generation + canonical
//                                    url/title/duration + explicit intent (buffering|playing|paused|
//                                    stopped|failed) + position anchor + the HOST-MONOTONIC time of
//                                    that anchor
//   peers -> host       ask          "make this url the session track" (request id + url)
//   peers -> host       control      pause|resume|seek, bound to host nonce + track revision +
//                                    request id + the CONTROL generation it was based on
//   host  -> peer       control-ack  the verdict for one control request id (+ current revision,
//                                    current control generation, applied position)
//   peers -> host       probe        clock probe (id + the client's own monotonic send time)
//   host  -> peer       reply        the same id, the echoed send time, the host's receive and
//                                    reply times
//   host  -> peer       ack          the verdict for one request id (accepted + revision / rejected)
//
// Transport is host-authoritative on BOTH sides: a follower's pause/resume/seek click sends a
// control request and commits NOTHING locally; the host validates it (transport-provided sender,
// active membership, host nonce, track revision, current control generation), commits once through
// its own FMOD path and publishes the new state. Every peer, the requester included, then applies
// that state. A request bound to an older control generation is stale and is answered with the
// current snapshot - the periodic 500 ms state sequence is deliberately NOT the CAS token, or every
// cadence frame would invalidate a click that is still in flight.
//
// The old frame carried a sender-local stopwatch and the follower compared it with its own: two
// independent process clocks, so the value was unusable and the "anchor" was recorded and never
// applied. The anchor here is only meaningful together with an offset between the two monotonic
// clocks, and that offset is measured - probe/reply round trips, NTP style, best (smallest round
// trip) sample wins. No process clock is ever compared with another directly.
//
// A client never dictates a position and never publishes state. The follower derives one target per
// tick, `anchorPos + estimatedHostNow - anchorHostTime` while the host plays and `anchorPos` while
// it is paused or buffering, then reconciles its own channel towards that target: a 250 ms no-op
// band, a correction above 500 ms, coverage first, readback verified, 1/2/4/8 s bounded backoff.
//
// Vanilla peers are never addressed: sends go through RemoteModels.SendToModPeers, which only
// reaches peers that answered the mod handshake. A vanilla peer hears nothing, which is the
// accepted behaviour ("forget about the users with no mod").
//
// Message ids: 1-3 belong to RemoteModels, 4-9 to the reel, so music starts at 20. RemoteModels
// dispatches every type >= MsgState here.
internal static class LobbyMusicNet
{
    internal const string Tag = "[music:net]";
    internal const string TagSync = "[music:sync]";
    internal const string TagReq = "[music:request]";

    internal const byte MsgState = 20;
    internal const byte MsgAsk = 21;
    internal const byte MsgProbe = 22;
    internal const byte MsgProbeReply = 23;
    internal const byte MsgAck = 24;
    internal const byte MsgControl = 25;
    internal const byte MsgControlAck = 26;

    // Protocol 3 = the control-request generation. A peer on 2 is rejected loudly on every message
    // type: the state frame grew a control generation, so a mixed pair cannot be interpreted.
    internal const byte Version = 3;

    // Intent: the whole of what a follower acts on. `Buffering` is the host having a request but no
    // sound yet - it is NOT `Stopped` (that was the old encoding of preflight, which made a peer that
    // joined during preflight sit on a track that had already begun).
    internal const byte IntentBuffering = 0;
    internal const byte IntentPlaying = 1;
    internal const byte IntentPaused = 2;
    internal const byte IntentStopped = 3;
    internal const byte IntentFailed = 4;

    // Control actions. The value on the wire; the desired pause state is explicit (pause/resume),
    // never a toggle.
    internal const byte ControlPause = 0;
    internal const byte ControlResume = 1;
    internal const byte ControlSeek = 2;

    private const byte FlagRestart = 1;        // this revision restarts the URL of the previous one
    private const byte FlagHasTitle = 2;
    private const byte FlagHasDuration = 4;

    // Wire budget (bridge ceiling: NetProtocol.MaxPayloadBytes = 1024).
    //   state       = 48 header + url (<= 400) + title (<= 96) = 544 bytes worst case
    //   ask         =  8 header + url (<= 400)                 = 408
    //   control     = 28
    //   control-ack = 26 + reason (<= 96)                      = 122
    //   probe       = 14, reply = 30, ack = 13 + reason (<= 96) = 109
    internal const int MaxUrlBytes = 400;
    private const int MaxTitleBytes = 96;
    private const int MaxReasonBytes = 96;
    private const int StateBytes = 48;
    private const int AskBytes = 8;
    private const int ControlBytes = 28;
    private const int ControlAckBytes = 26;
    private const int ProbeBytes = 14;
    private const int ReplyBytes = 30;
    private const int AckBytes = 13;
    private const int AckCacheSize = 64;
    private const int ControlCacheSize = 64;

    // Corrections. A band below 250 ms is two playheads that are the same; above 500 ms a seek is
    // worth its audible cost; in between the drift is logged and left alone.
    private const long NoOpBandMs = 250;
    private const long CorrectAboveMs = 500;
    private const long SwitchMarginMs = MusicStreamHost.SwitchMarginMs;   // a prefix must reach past the target

    private const float CadenceSeconds = 0.5f;      // while the session owns playback
    private const float HeartbeatSeconds = 5f;      // while it only owns the panel (failed/cancelled)
    private const float ProbeFastSeconds = 1f;     // until a few samples are in
    private const float ProbeSteadySeconds = 5f;
    private const int ProbeWarmSamples = 3;
    private const float RequestTimeoutSeconds = 5f;
    private const float ControlTimeoutSeconds = 2.5f;   // bounded pending for pause/resume/seek
    private const float ReconcileLogSeconds = 1f;

    private static IHostContext _log;
    private static INetBridge _net;
    // Monotonic milliseconds, per process. Never compared with another process' value without the
    // probe-measured offset below.
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static bool _live, _isServer;
    private static ulong _serverId;
    // Whether the host id is KNOWN - never whether that id is non-zero. Netcode's
    // `NetworkManager.ServerClientId` is the constant 0 and the bridge reports exactly that, so the
    // host is client 0 and `_serverId == 0` is the normal case. Known-ness comes from a real bridge
    // status read (the bridge is registered and this machine is a client in a live session), so
    // "we do not know the host yet" and "the host is client 0" are two different states.
    private static bool _haveServer;
    private static bool _bound, _boundServer;
    private static ulong _boundServerId, _boundLocalId;
    private static uint _boundEpoch;        // bridge SessionEpoch this binding is for

    // ---- host authorship
    // Depth of the takeover ritual. A start stops the previous request and that stop is an
    // intermediate step, not a session event: publishing it would make every peer drop its stream
    // and re-download the very track the host is restarting.
    private static int _ritual;
    private static uint _nonce;             // host-session nonce (0 = not authoring)
    private static uint _revision;          // track revision: bumped on EVERY accepted start
    private static uint _seq;               // state sequence
    // Control generation: bumped ONLY by a committed pause/resume/seek (local or remote). It is the
    // CAS token for control requests; the state sequence above is a publication counter and must
    // never stale a click (500 ms cadence).
    private static uint _controlGen;
    private static string _acceptedUrl = "";   // the URL of the current revision (restart detection)
    private static bool _restartFlag;
    private static float _nextCadenceAt, _nextHeartbeatAt;

    // ---- follower
    private static uint _fNonce, _fRevision, _fSeq, _fControlGen;
    private static string _fUrl = "", _fTitle = "";
    private static long _fDurationMs, _fAnchorPos, _fAnchorHostMs;
    private static byte _fIntent;
    private static bool _following, _fDead;
    // True once this revision's channel has actually run unpaused. It is what separates "the first
    // start has not happened yet" (hold the pause until a prefix covers the target) from "a mid-track
    // pause" (resume immediately, the audio is already here).
    private static bool _fPlayed;
    private static long _targetMs, _driftMs, _coverageMs;
    private static string _lastReconcileResult = "";
    private static float _nextReconcileLogAt;
    // When this revision's target first became reachable (coverage). Convergence is measured from
    // here - "within 2 s once both have coverage" - and it is measured on our own monotonic clock, so
    // the evidence does not depend on log timestamp precision.
    private static long _fCoveredAtMs = -1;
    private static int _correctionFails;
    private static float _nextCorrectionAt;

    // ---- clock offset (hostMs - localMs)
    private static long _offsetMs;
    private static bool _haveOffset;
    private static long _bestRttMs = long.MaxValue;
    private static int _probeSamples;
    private static uint _probeId, _probeOutId;
    private static long _probeOutT0;
    private static float _nextProbeAt;

    // ---- pending request (client)
    private static uint _reqId;
    private static string _pendingUrl = "";
    private static float _pendingAt = -1f;
    private static bool _reqPending;
    // Why the last `SubmitRequest` did not reach a host. Read by the caller so the panel and the log
    // name the real reason instead of the old catch-all "not a yt-dlp link".
    private static string _submitRefusal = "";

    // ---- PROCESS-LIFETIME request/control id allocator.
    //
    // A hot reload loads a fresh assembly into the SAME process, so every static here starts at zero
    // again while the host process keeps its verdict caches. Restarting the ids at 1 made the new
    // generation's first request indistinguishable from the previous generation's first request when
    // it named the same url, and the same collision silently swallowed the first pause after a reload
    // (ControlKey is sender + id + host nonce, all three of which a reload reproduces).
    //
    // A per-generation clock seed was NOT a fix: generation 1 could issue t+1..t+n and the next
    // generation, reloaded at t+d with d<n, reissued the same ids. The counter below lives in
    // AppDomain data - process-lifetime storage that survives collectible hot-module unload - and is
    // shared by BOTH id spaces, so every id in this process is handed out once. It stores only a
    // long[1] (System.Int64[], a corelib type): no hot type, delegate or lease is retained by the
    // loader, so the assembly can still unload.
    //
    // 32-bit wire bound: ids are uint32 on the wire and stay in 1..0x7FFFFFFF. When that band is
    // exhausted the allocator refuses (SubmitRequest/SubmitControl surface an explicit refusal that
    // names a process restart); it never wraps into an old id. A PROCESS RESTART is the explicit
    // incarnation boundary: it starts a fresh counter at 1, so uniqueness is guaranteed inside one
    // process incarnation, not across two processes with the same sender id. The host also clears
    // its verdict caches on every new host session (see Bind), so a restarted host cannot answer
    // from a previous incarnation's cache.
    internal const string IdSlotName = "AlterumModelSwap.Music.NextId";
    private const long MaxId = 0x7FFFFFFFL;   // top bit free; ids stay positive on the wire

    private static long[] IdSlot()
    {
        var slot = AppDomain.CurrentDomain.GetData(IdSlotName) as long[];
        if (slot != null) return slot;
        // AppDomain.CurrentDomain is a corelib instance shared by every generation in this process.
        lock (AppDomain.CurrentDomain)
        {
            slot = AppDomain.CurrentDomain.GetData(IdSlotName) as long[];
            if (slot == null)
            {
                slot = new long[1];           // [0] = last handed-out id; 0 means none yet
                AppDomain.CurrentDomain.SetData(IdSlotName, slot);
            }
            return slot;
        }
    }

    // Null when an id was handed out. Linearizable across generations because every generation uses
    // the same array element; no clock and no RNG take part in the uniqueness argument.
    internal static bool TryNextMusicId(out uint id, out string refusal)
    {
        id = 0; refusal = "";
        long next = Interlocked.Increment(ref IdSlot()[0]);
        if (next > MaxId)
        {
            refusal = "the process-wide music request/control id space is exhausted " +
                      $"(limit {MaxId}, ids cannot wrap without reusing an old request); restart the game process";
            return false;
        }
        id = (uint)next;
        return true;
    }

    // ---- pending control (follower). One request at a time, bounded by ControlTimeoutSeconds.
    private static bool _ctrlPending;
    private static uint _ctrlId;
    private static byte _ctrlAction;
    private static long _ctrlTargetMs;
    private static uint _ctrlBaseGen;
    private static uint _ctrlRev;
    private static float _ctrlAt = -1f;

    // ---- host: the verdicts already sent, so a retransmitted request cannot start twice.
    //
    // Keyed by the request itself - sender + id + URL - not by (sender, id) alone. A client hot
    // reload is a fresh assembly whose request counter starts at zero again; keying on the id alone
    // would make its first new URL look like a retransmission of the previous generation's first
    // request and the host would re-send that old verdict instead of adopting the track (observed:
    // `dedupe id=1 ... not starting again` while the host stayed on the old URL).
    //
    // The URL alone is not sufficient either: a reloaded generation that names the SAME url it named
    // first in the previous generation produces the identical (sender, id, url) triple, which is a
    // new play intention and must start. The id side is handled by TryNextMusicId()'s process-wide
    // counter, so this key only ever collapses true retransmissions.
    private struct RequestKey : IEquatable<RequestKey>
    {
        internal ulong Sender;
        internal uint Id;
        internal string Url;
        public bool Equals(RequestKey other) =>
            Sender == other.Sender && Id == other.Id && string.Equals(Url, other.Url, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is RequestKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)(Sender * 397 ^ Id);
                return (h * 31) ^ (Url == null ? 0 : StringComparer.Ordinal.GetHashCode(Url));
            }
        }
    }
    private static readonly Dictionary<RequestKey, byte> _verdicts = new Dictionary<RequestKey, byte>();
    private static readonly Queue<RequestKey> _verdictOrder = new Queue<RequestKey>();

    // Control verdicts are keyed by (sender, request id, host nonce): the nonce is what makes a
    // retransmission from an older host session a different request, not a duplicate.
    private struct ControlKey : IEquatable<ControlKey>
    {
        internal ulong Sender;
        internal uint Id;
        internal uint Nonce;
        public bool Equals(ControlKey other) => Sender == other.Sender && Id == other.Id && Nonce == other.Nonce;
        public override bool Equals(object obj) => obj is ControlKey other && Equals(other);
        public override int GetHashCode() => (int)(Sender * 397 ^ Id * 31 ^ Nonce);
    }
    private static readonly Dictionary<ControlKey, byte> _controlVerdicts = new Dictionary<ControlKey, byte>();
    private static readonly Queue<ControlKey> _controlOrder = new Queue<ControlKey>();

    private static int _sent, _recv, _applied, _probes, _replies, _asks, _acks, _lateJoins,
                   _corrections, _stale, _stops, _txControl, _rxControl, _controlCommits,
                   _controlRejects, _controlTimeouts;

    internal static void SetLog(IHostContext log) { _log = log; }

    // `sessionEpoch` is the bridge's SessionEpoch for this poll, not a local counter: a new session
    // on the SAME bridge with the SAME role is otherwise invisible, and the old nonce/revision/
    // control generation would keep validating requests from the previous host session.
    internal static void Bind(INetBridge net, bool live, bool isServer, ulong localId, ulong serverId, uint sessionEpoch)
    {
        bool epochChanged = _bound && sessionEpoch != _boundEpoch;
        bool sessionChanged = !_bound || epochChanged || !ReferenceEquals(net, _net) || isServer != _boundServer ||
                              serverId != _boundServerId || localId != _boundLocalId;
        bool becameServer = isServer && !_isServer;
        bool lostSession = net == null;

        _net = net; _live = live;
        _bound = true; _boundServer = isServer;
        _boundServerId = serverId; _boundLocalId = localId; _boundEpoch = sessionEpoch;
        _isServer = isServer; _serverId = serverId;
        // The only "we know who to address" signal this side has: the bridge reported a registered
        // session for this generation and we are a client in it. The id itself proves nothing.
        _haveServer = live && !isServer && net != null;

        if (isServer)
        {
            // The nonce identifies THIS host session. A peer that reconnects to a different host, to
            // the same host after a restart, or into a new bridge epoch must not apply the old
            // session's sequence numbers - and the old session's verdict caches must not answer a
            // new session's request/control as a duplicate.
            if (sessionChanged || becameServer || lostSession || _nonce == 0)
            {
                _nonce = NewNonce();
                ClearFollow("new host session");
                ClearPending("new host session");
                ClearPendingControl("new host session");
                _revision = 0; _seq = 0; _controlGen = 0; _acceptedUrl = ""; _restartFlag = false;
                _nextCadenceAt = 0f; _nextHeartbeatAt = 0f;
                _verdicts.Clear(); _verdictOrder.Clear();
                _controlVerdicts.Clear(); _controlOrder.Clear();
                _log?.LogInfo($"{TagSync} tx host-session nonce={_nonce} (new session epoch={sessionEpoch} " +
                              $"server={serverId}); verdict caches and follower state dropped");
            }
            ClearPending("we are the host");
            ClearPendingControl("we are the host");
            return;
        }

        if (lostSession)
        {
            // EndSession passes net=null: the host session is over, so its nonce and its verdict
            // caches must not survive into the next hosting attempt on the same process.
            _nonce = 0; _revision = 0; _seq = 0; _controlGen = 0; _acceptedUrl = ""; _restartFlag = false;
            _verdicts.Clear(); _verdictOrder.Clear();
            _controlVerdicts.Clear(); _controlOrder.Clear();
            ClearPending("session ended");
            ClearPendingControl("session ended");
            ClearFollow("session ended");
            _fNonce = 0; _fRevision = 0; _fSeq = 0; _fControlGen = 0;
            _haveOffset = false; _probeSamples = 0; _bestRttMs = long.MaxValue; _probeOutId = 0;
            _nextProbeAt = 0f;
            _log?.LogInfo($"{TagSync} rx host-session ended (net=null epoch={sessionEpoch}); " +
                          "nonce and verdict caches cleared");
            return;
        }

        if (sessionChanged || becameServer)
        {
            ClearPending("session changed");
            ClearPendingControl("session changed");
            ClearFollow("session changed");
            _fNonce = 0; _fRevision = 0; _fSeq = 0; _fControlGen = 0;
            _haveOffset = false; _probeSamples = 0; _bestRttMs = long.MaxValue; _probeOutId = 0;
            _nextProbeAt = 0f;
            _log?.LogInfo($"{TagSync} rx host-session changed (epoch={sessionEpoch} server={serverId} live={live}); " +
                          "the watermark and the clock offset are dropped");
        }
    }

    internal static void Reset()
    {
        _net = null; _live = false; _isServer = false; _haveServer = false;
        _bound = false; _boundServer = false;
        _boundServerId = 0; _boundLocalId = 0; _boundEpoch = 0;
        _nonce = 0; _revision = 0; _seq = 0; _controlGen = 0; _acceptedUrl = ""; _restartFlag = false;
        _fNonce = 0; _fRevision = 0; _fSeq = 0; _fControlGen = 0; _fUrl = ""; _fTitle = ""; _fIntent = 0;
        _fDurationMs = 0; _fAnchorPos = 0; _fAnchorHostMs = 0;
        ClearFollow("reset");
        ClearPending("reset");
        ClearPendingControl("reset");
        _haveOffset = false; _offsetMs = 0; _bestRttMs = long.MaxValue; _probeSamples = 0;
        _sent = _recv = _applied = _probes = _replies = _asks = _acks = _lateJoins = 0;
        _corrections = _stale = _stops = 0;
        _txControl = _rxControl = _controlCommits = _controlRejects = _controlTimeouts = 0;
        _verdicts.Clear(); _verdictOrder.Clear();
        _controlVerdicts.Clear(); _controlOrder.Clear();
        _ritual = 0;                          // the process-lifetime id counter is NOT reset here
    }

    // A track we were following was stopped or replaced on this machine: forget the episode. The
    // watermark (nonce/sequence) is kept, so the same revision cannot come back to life.
    internal static void ResetFollow(string why) => ClearFollow(why);

    internal static void EndLobbyPlayback()
    {
        if (_following)
        {
            ClearFollow("left Lobby scene");
            // Keep the revision watermark; a delayed Playing frame cannot reopen this track.
            _fIntent = IntentStopped;
        }
        ClearPending("left Lobby scene");
        ClearPendingControl("left Lobby scene");
    }

    internal static void Debug(string line) => _log?.LogInfo(line);

    internal static bool IsAuthority => _isServer;

    // A follower never dictates a position or a pause state locally: the click sends a control
    // request and the host commits. This property is kept as "is there a control request waiting"
    // for the panel, not as a lock - controls remain interactable while it is true.
    internal static bool ControlPending => _ctrlPending;
    internal static uint FollowerControlGeneration => _fControlGen;
    internal static uint HostControlGeneration => _controlGen;

    internal static string Counters =>
        $"sent={_sent} recv={_recv} applied={_applied} probes={_probes} replies={_replies} asks={_asks} " +
        $"acks={_acks} corrections={_corrections} stale={_stale} stops={_stops} lateJoins={_lateJoins} " +
        $"txControl={_txControl} rxControl={_rxControl} controlCommits={_controlCommits} " +
        $"controlRejects={_controlRejects} controlTimeouts={_controlTimeouts} " +
        $"role={(_isServer ? "host" : "follower")} nonce={_nonce} rev={_revision} seq={_seq} " +
        $"ctrlGen={_controlGen} following={_following} fRev={_fRevision} fSeq={_fSeq} fCtrlGen={_fControlGen} " +
        $"intent={Word(_fIntent)} offsetMs={_offsetMs} haveOffset={_haveOffset} bestRttMs={_bestRttMs} " +
        $"samples={_probeSamples} request={(_reqPending ? _pendingUrl : "-")} " +
        $"control={(_ctrlPending ? ActionWord(_ctrlAction) : "-")} targetMs={_targetMs} driftMs={_driftMs} " +
        $"coverageMs={_coverageMs}";

    private static long NowMs() => Clock.ElapsedMilliseconds;
    private static float Time0() => UnityEngine.Time.unscaledTime;

    private static uint NewNonce()
    {
        uint n = (uint)(NowMs() ^ (Environment.TickCount * 2654435761u) ^ (uint)Guid.NewGuid().GetHashCode());
        return n == 0 ? 1u : n;
    }

    // ================================================================ publish (host)

    // `NoteTransport` is the transport-commit signal. It is deliberately NOT MusicStreamHost's
    // `TakeAnnounce`: that flag belongs to the panel mirror (it is consumed per commit for the UI),
    // and using it here would steal commits from the panel.
    internal static void NoteTransport(string what)
    {
        if (!_isServer || _net == null) return;
        // One bump per committed transport change, and only for the control actions. Start/stop are
        // ordered by the track revision, not by this token.
        if (IsControlAction(what)) _controlGen++;
        _nextCadenceAt = Time0() + CadenceSeconds;
        Publish("transport:" + what);
    }

    private static bool IsControlAction(string what) =>
        what == "pause" || what == "resume" || what == "seek";

    // The takeover ritual brackets a start. Whatever the transport does inside it (the stop of the
    // request being replaced, the phase changes) is intermediate: the session event is the accepted
    // revision published after it.
    internal static void BeginRitual() { _ritual++; }
    internal static void EndRitual() { if (_ritual > 0) _ritual--; }

    // An accepted start: the revision is what makes "a client's track becomes everyone's track"
    // orderable, so it is bumped here and only here (plus the restart case, which is a new revision
    // of the same URL).
    internal static void NoteAcceptedStart(string url, string why)
    {
        if (!_isServer) return;
        _revision++;
        if (_revision == 0) _revision = 1;                  // 0 is "no revision yet"
        _restartFlag = string.Equals(url, _acceptedUrl, StringComparison.Ordinal);
        _acceptedUrl = url ?? "";
        _log?.LogInfo($"{TagSync} tx accepted-start rev={_revision} url='{Short(url, 90)}' why={why} " +
                      $"restart={_restartFlag}");
        Publish("accepted-start");
    }

    // A start the host REFUSED before it became a revision (the URL is not a streamable page link, or
    // the panel could not be claimed). The session is untouched - the current track keeps playing,
    // and only the requester is told (MsgAck). A failure AFTER adoption is a different thing: the
    // host's phase goes Failed and that is published like any other commit, so every peer learns it
    // and nobody quietly starts a private replacement.
    internal static void NoteStartRefused(string url, string reason)
    {
        if (!_isServer) return;
        _log?.LogWarning($"{TagSync} start refused url='{Short(url, 90)}' rev={_revision} " +
                         $"reason='{Short(reason, 80)}'; the session track is unchanged");
    }

    internal static void Publish(string why)
    {
        var host = LobbyMusicHooks.Host;
        if (host == null || _net == null || !_isServer || _nonce == 0) return;
        if (_ritual > 0) return;                       // see BeginRitual
        // A stopped track still has to reach the peers (that is how B stops), so the URL - not the
        // phase - is what makes a frame worth sending.
        if (!host.OwnsPanel && string.IsNullOrEmpty(host.Url)) return;

        var payload = Encode(host, out byte intent, out long anchorPos, out long hostMs, out long duration);
        if (payload == null) return;
        int peers = RemoteModels.SendToModPeers(_log, _net, payload, $"music:{why}", 0);
        _sent++;
        _nextCadenceAt = Time0() + CadenceSeconds;
        _nextHeartbeatAt = Time0() + HeartbeatSeconds;
        _log?.LogInfo($"{TagSync} tx state why={why} nonce={_nonce} rev={_revision} seq={_seq} " +
                      $"ctrlGen={_controlGen} intent={Word(intent)} url='{Short(host.Url, 90)}' anchorMs={anchorPos} " +
                      $"hostMs={hostMs} durMs={duration} bytes={payload.Length} peers={peers}");
    }

    private static byte HostIntent(MusicStreamHost host)
    {
        switch (host.Current)
        {
            case MusicStreamHost.Phase.Playing: return IntentPlaying;
            case MusicStreamHost.Phase.Paused: return IntentPaused;
            case MusicStreamHost.Phase.Failed: return IntentFailed;
            case MusicStreamHost.Phase.Preflight:
            case MusicStreamHost.Phase.Buffering: return IntentBuffering;
            default: return IntentStopped;
        }
    }

    // Wire layout (little-endian), version 3:
    //
    //   0     1   type (20)
    //   1     1   version (3)
    //   2     1   intent (buffering|playing|paused|stopped|failed)
    //   3     1   flags (restart|hasTitle|hasDuration)
    //   4..7   4   host-session nonce
    //   8..11  4   track revision      (bumped on every accepted start)
    //  12..15  4   state sequence      (bumped on every state frame)
    //  16..23  8   anchor position ms  (int64: a 4-byte int was 4 GB of track)
    //  24..31  8   anchor host time ms (HOST-monotonic; meaningful only with the probe offset)
    //  32..39  8   canonical duration ms (0 = unknown)
    //  40..41  2   url length bytes    (uint16: the 1-byte length could not hold the 400-byte ceiling)
    //  42..43  2   title length bytes
    //  44..47  4   control generation  (the CAS token for control requests, NOT the state sequence)
    //  48..       url (UTF-8) + title (UTF-8)
    private static byte[] Encode(MusicStreamHost host, out byte intent, out long anchorPos,
                                out long hostMs, out long duration)
    {
        intent = HostIntent(host);
        anchorPos = Math.Max(0, host.PositionMs);
        hostMs = 0; duration = 0;
        var url = host.Url ?? "";
        var title = host.DisplayTitle ?? "";
        var urlBytes = Encoding.UTF8.GetBytes(url);
        var titleBytes = Encoding.UTF8.GetBytes(title);
        if (urlBytes.Length > MaxUrlBytes)
        {
            _log?.LogWarning($"{TagSync} url is {urlBytes.Length} bytes, over the {MaxUrlBytes} ceiling; not syncing it");
            return null;
        }
        if (titleBytes.Length > MaxTitleBytes) Array.Resize(ref titleBytes, MaxTitleBytes);
        duration = Math.Max(0, host.TotalMs);
        hostMs = NowMs();

        byte flags = 0;
        if (titleBytes.Length > 0) flags |= FlagHasTitle;
        if (duration > 0) flags |= FlagHasDuration;
        if (_restartFlag) flags |= FlagRestart;

        _seq++;
        if (_seq == 0) _seq = 1;
        var buf = new byte[StateBytes + urlBytes.Length + titleBytes.Length];
        buf[0] = MsgState;
        buf[1] = Version;
        buf[2] = intent;
        buf[3] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), _nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), _revision);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), _seq);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), anchorPos);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), hostMs);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32, 8), duration);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(40, 2), (ushort)urlBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(42, 2), (ushort)titleBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(44, 4), _controlGen);
        urlBytes.CopyTo(buf, StateBytes);
        titleBytes.CopyTo(buf, StateBytes + urlBytes.Length);
        return buf;
    }

    // ================================================================ tick

    internal static void Tick(IHostContext log, MusicStreamHost host)
    {
        _log = log;
        var net = _net;
        if (net == null || host == null) return;
        if (!_live && !_isServer) return;
        float now = Time0();
        if (_isServer) { HostTick(host, now); return; }
        FollowerTick(host, now);
    }

    private static void HostTick(MusicStreamHost host, float now)
    {
        if (_nonce == 0) return;
        if (host.OwnsPlayback)
        {
            // 500 ms while the session owns playback: this is the cadence a follower needs to keep a
            // target advancing and to notice a pause promptly.
            if (now < _nextCadenceAt) return;
            _nextCadenceAt = now + CadenceSeconds;
            Publish("cadence");
            return;
        }
        if (!host.OwnsPanel) return;
        if (now < _nextHeartbeatAt) return;
        _nextHeartbeatAt = now + HeartbeatSeconds;
        Publish("heartbeat");
    }

    private static void FollowerTick(MusicStreamHost host, float now)
    {
        PendingRequestTick(now);
        PendingControlTick(now);
        if (!_following) return;
        ProbeTick(now);
        ReconcileTick(host, now);
    }

    private static void PendingRequestTick(float now)
    {
        if (!_reqPending) return;
        if (now - _pendingAt < RequestTimeoutSeconds) return;
        _reqPending = false;
        _log?.LogWarning($"{TagReq} reject id={_reqId} url='{Short(_pendingUrl, 90)}' " +
                         $"reason='no verdict from the host within {RequestTimeoutSeconds:F0}s'; " +
                         "keeping the current track");
        LobbyMusicHooks.NoteRequestRejected("the host did not answer the request");
    }

    // ================================================================ clock (probe / reply)

    private static void ProbeTick(float now)
    {
        if (!_following) return;
        // Known host, not a non-zero host: the probe is addressed to host 0 in the normal case.
        if (_net == null || !_haveServer) return;
        bool warm = _probeSamples >= ProbeWarmSamples;
        if (now < _nextProbeAt) return;
        _probeId++;
        if (_probeId == 0) _probeId = 1;
        long t0 = NowMs();
        var buf = new byte[ProbeBytes];
        buf[0] = MsgProbe; buf[1] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(2, 4), _probeId);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(6, 8), t0);
        bool ok = false;
        try { ok = _net.Send(NetKind.App, _serverId, buf); } catch { }
        _probeOutId = _probeId; _probeOutT0 = t0;
        _probes++;
        _nextProbeAt = now + (warm ? ProbeSteadySeconds : ProbeFastSeconds);
        _log?.LogInfo($"{TagSync} tx probe id={_probeId} sendMs={t0} host={_serverId} ok={ok} bytes={buf.Length}");
    }

    private static void HandleProbe(IHostContext log, INetBridge net, in NetInboundFrame frame)
    {
        var p = frame.Payload;
        if (p.Length < ProbeBytes) return;
        if (p[1] != Version)
        {
            log?.LogWarning($"{Tag} probe version={p[1]} (ours {Version}); ignored - mixed music protocol " +
                            "(both peers must run the same music protocol)");
            return;
        }
        if (!_isServer) return;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(2, 4));
        long t0 = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(6, 8));
        long t1 = NowMs();
        var buf = new byte[ReplyBytes];
        buf[0] = MsgProbeReply; buf[1] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(2, 4), id);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(6, 8), t0);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(14, 8), t1);
        long t2 = NowMs();
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(22, 8), t2);
        bool ok = false;
        try { ok = net.Send(NetKind.App, frame.SenderClientId, buf); } catch { }
        _replies++;
        log?.LogInfo($"{TagSync} rx probe id={id} from={frame.SenderClientId} clientSendMs={t0} " +
                     $"recvMs={t1} replyMs={t2} ok={ok}");
    }

    // NTP-style, without touching the network clock: the host's receive/reply pair brackets the same
    // interval as our send/receive pair, so
    //     delay = (t3 - t0) - (t2 - t1)
    //     offset = ((t1 - t0) + (t2 - t3)) / 2      (hostMs - localMs)
    // and the sample with the smallest delay is the one that was least disturbed by the main threads.
    private static void HandleProbeReply(IHostContext log, in NetInboundFrame frame)
    {
        var p = frame.Payload;
        if (p.Length < ReplyBytes) return;
        if (p[1] != Version)
        {
            log?.LogWarning($"{Tag} probe-reply version={p[1]} (ours {Version}); ignored - mixed music protocol");
            return;
        }
        if (frame.SenderClientId != _serverId) return;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(2, 4));
        long t0 = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(6, 8));
        long t1 = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(14, 8));
        long t2 = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(22, 8));
        long t3 = NowMs();
        if (id != _probeOutId || t0 != _probeOutT0)
        {
            _stale++;
            log?.LogInfo($"{TagSync} rx probe-reply id={id} is not the probe in flight (id={_probeOutId}); ignored");
            return;
        }
        long rtt = (t3 - t0) - (t2 - t1);
        if (rtt < 0) rtt = 0;
        long offset = ((t1 - t0) + (t2 - t3)) / 2;
        _probeSamples++;
        bool better = !_haveOffset || rtt <= _bestRttMs;
        if (better) { _bestRttMs = rtt; _offsetMs = offset; _haveOffset = true; }
        log?.LogInfo($"{TagSync} rx probe-reply id={id} rttMs={rtt} offsetMs={offset} bestRttMs={_bestRttMs} " +
                     $"usingOffsetMs={_offsetMs} samples={_probeSamples} hostRecvMs={t1} hostReplyMs={t2}");
    }

    // ================================================================ inbound

    internal static void HandleFrame(IHostContext log, INetBridge net, in NetInboundFrame frame, float now)
    {
        if (!LobbyMusicHooks.InLobby) return;
        _log = log;
        var p = frame.Payload;
        if (p == null || p.Length < 2) return;
        byte type = p[0];
        switch (type)
        {
            case MsgState: HandleState(log, frame); return;
            case MsgAsk: HandleAsk(log, net, frame); return;
            case MsgProbe: HandleProbe(log, net, frame); return;
            case MsgProbeReply: HandleProbeReply(log, frame); return;
            case MsgAck: HandleAck(log, frame); return;
            case MsgControl: HandleControl(log, net, frame); return;
            case MsgControlAck: HandleControlAck(log, frame); return;
            default:
                log?.LogInfo($"{Tag} unknown music message type={type} from={frame.SenderClientId} bytes={p.Length}");
                return;
        }
    }

    // ---- the authoritative frame

    private static void HandleState(IHostContext log, in NetInboundFrame frame)
    {
        var p = frame.Payload;
        _recv++;
        if (p.Length < StateBytes) { log?.LogWarning($"{Tag} state short bytes={p.Length}"); return; }
        if (p[1] != Version)
        {
            log?.LogWarning($"{Tag} state version={p[1]} (ours {Version}); ignored - mixed music protocol " +
                            "(the peer must be updated to the same music protocol; a version-2 state has " +
                            "no control generation)");
            return;
        }
        if (_isServer)
        {
            log?.LogWarning($"{Tag} state from={frame.SenderClientId} ignored: we are the authority");
            return;
        }
        if (frame.SenderClientId != _serverId)
        {
            log?.LogWarning($"{Tag} state from={frame.SenderClientId} ignored (authority is {_serverId})");
            return;
        }

        byte intent = p[2];
        byte flags = p[3];
        uint nonce = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4, 4));
        uint rev = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8, 4));
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(12, 4));
        long anchorPos = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(16, 8));
        long anchorHost = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(24, 8));
        long duration = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(32, 8));
        int urlLen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(40, 2));
        int titleLen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(42, 2));
        uint controlGen = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(44, 4));
        if (urlLen == 0 || urlLen > MaxUrlBytes || titleLen > MaxTitleBytes ||
            StateBytes + urlLen + titleLen > p.Length)
        {
            log?.LogWarning($"{Tag} state lengths url={urlLen} title={titleLen} bytes={p.Length}; ignored");
            return;
        }
        var url = Encoding.UTF8.GetString(p, StateBytes, urlLen);
        var title = titleLen > 0 ? Encoding.UTF8.GetString(p, StateBytes + urlLen, titleLen) : "";

        if (!AcceptState(nonce, rev, seq, out string why))
        {
            _stale++;
            log?.LogInfo($"{TagSync} rx state nonce={nonce} rev={rev} seq={seq} ignored: {why}");
            return;
        }

        if (intent == IntentStopped) { RxStopped(log, frame, url, rev, seq, anchorPos); return; }
        if (intent == IntentFailed) { RxFailed(log, frame, url, rev, seq); return; }
        if (rev == _fRevision && _fIntent == IntentStopped) return;
        RxFollow(log, frame, url, title, duration, intent, rev, seq, anchorPos, anchorHost, flags, controlGen);
    }

    // A frame is applied only if it is newer than the watermark: a different host session always is,
    // the same session only with a strictly newer sequence, and never with an older revision.
    private static bool AcceptState(uint nonce, uint rev, uint seq, out string why)
    {
        why = null;
        if (_fNonce != nonce)
        {
            _log?.LogInfo($"{TagSync} rx host-session nonce={nonce} (was {_fNonce}): watermark and clock " +
                          $"offset reset (offsetMs={_offsetMs} samples={_probeSamples})");
            _fNonce = nonce; _fSeq = 0; _fRevision = 0; _fControlGen = 0;
            _haveOffset = false; _probeSamples = 0; _bestRttMs = long.MaxValue; _probeOutId = 0;
            _nextProbeAt = 0f;
            ClearPendingControl("host session changed");
        }
        if (_fSeq != 0 && (int)(seq - _fSeq) <= 0) { why = $"sequence {seq} is not newer than {_fSeq}"; return false; }
        if (_fRevision != 0 && (int)(rev - _fRevision) < 0) { why = $"revision {rev} is older than {_fRevision}"; return false; }
        _fSeq = seq;
        return true;
    }

    private static void RxStopped(IHostContext log, in NetInboundFrame frame, string url, uint rev, uint seq, long anchorPos)
    {
        var h = LobbyMusicHooks.Host;
        log?.LogInfo($"{TagSync} rx state rev={rev} seq={seq} intent=stopped url='{Short(url, 90)}' " +
                     $"from={frame.SenderClientId}");
        if (h != null && h.OwnsPlayback && string.Equals(h.Url, url, StringComparison.Ordinal))
        {
            _stops++;
            log?.LogInfo($"{TagSync} the host stopped/replaced '{Short(url, 60)}' -> stopping locally");
            h.Stop("remote-stopped");
        }
        ClearFollow("host stopped");
        _fRevision = rev; _fUrl = url; _fAnchorPos = anchorPos; _fIntent = IntentStopped;
    }

    private static void RxFailed(IHostContext log, in NetInboundFrame frame, string url, uint rev, uint seq)
    {
        var h = LobbyMusicHooks.Host;
        log?.LogWarning($"{TagSync} rx state rev={rev} seq={seq} intent=failed url='{Short(url, 90)}' " +
                        $"from={frame.SenderClientId}: the host could not play this track");
        if (h != null && h.OwnsPlayback && string.Equals(h.Url, url, StringComparison.Ordinal))
        {
            log?.LogWarning($"{TagSync} the host failed this track; stopping our copy - a private " +
                            "replacement is not what the session asked for");
            h.Stop("remote-failed");
            _stops++;
        }
        ClearFollow("host failed the track");
        _fRevision = rev; _fUrl = url;
    }

    private static void RxFollow(IHostContext log, in NetInboundFrame frame, string url, string title,
                                 long duration, byte intent, uint rev, uint seq,
                                 long anchorPos, long anchorHost, byte flags, uint controlGen)
    {
        bool newEpisode = !_following || rev != _fRevision || !string.Equals(url, _fUrl, StringComparison.Ordinal);
        _fUrl = url; _fTitle = title; _fDurationMs = duration;
        _fIntent = intent; _fAnchorPos = anchorPos; _fAnchorHostMs = anchorHost;
        // The generation only advances inside one host session; never go backwards on a reordered frame.
        if (rev >= _fRevision && controlGen > _fControlGen) _fControlGen = controlGen;
        _following = true;
        ResolveControlFromState(intent, rev, controlGen);

        // THE PENDING FIRST START FOLLOWS EVERY ACCEPTED STATE, not only the frame that opened the
        // episode: both the position AND the pause state. A buffering follower can receive
        // "playing" and then "paused" before its first snapshot is playable; without this the
        // channel would be created later from the previous `_startPaused = false` and the
        // reconciliation would pause it right after the first emitted frame. The channel is only
        // ever created from what `SetStartTarget` holds at that moment (MusicStreamHost.PlayAt /
        // the start in MusicStreamHost.Tick), and this line runs when the frame is applied -
        // inbound frames are drained by RemoteModels.Tick, which HotModule runs BEFORE
        // LobbyMusicHooks.Tick, so the transport is always serviced with the state in force.
        // `SetStartTarget` is a no-op once a channel exists (position/pause are then corrected
        // through the transport, never by pretending the start is still pending).
        var h0 = LobbyMusicHooks.Host;
        if (h0 != null) h0.SetStartTarget(ComputeTarget(), intent != IntentPlaying, Time0());

        if (newEpisode)
        {
            _applied++;
            _fRevision = rev;
            _fDead = false;
            _fPlayed = false;
            _fCoveredAtMs = -1;
            _correctionFails = 0;
            _nextCorrectionAt = 0f;
            long target = ComputeTarget();
            var h = LobbyMusicHooks.Host;
            bool keepLocal = h != null && h.OwnsPlayback && string.Equals(h.Url, url, StringComparison.Ordinal);
            log?.LogInfo($"{TagSync} rx FOLLOW rev={rev} seq={seq} intent={Word(intent)} url='{Short(url, 90)}' " +
                         $"title='{Short(title, 60)}' anchorMs={anchorPos} hostMs={anchorHost} rev-flag={(flags & FlagRestart) != 0} " +
                         $"ctrlGen={controlGen} -> targetMs={target}");
            if (keepLocal)
            {
                log?.LogInfo($"{TagSync} rev={rev} is the track we already stream: re-anchored, no restart " +
                             "(the producer and the open snapshot keep running)");
            }
            else if (!LobbyMusicHooks.StartRequest(url, "remote-follow", target, null, intent != IntentPlaying))
            {
                _fDead = true;
                log?.LogWarning($"{TagSync} cannot stream '{Short(url, 90)}' here (not a streamable page link); " +
                                "following the session state without audio");
            }
            if (_reqPending && string.Equals(url, _pendingUrl, StringComparison.Ordinal))
            {
                _reqPending = false;
                log?.LogInfo($"{TagReq} accept url='{Short(url, 90)}' rev={rev}: the host adopted our request");
            }
        }
        else
        {
            log?.LogInfo($"{TagSync} rx state rev={rev} seq={seq} intent={Word(intent)} anchorMs={anchorPos} " +
                         $"hostMs={anchorHost} durMs={duration} ctrlGen={controlGen}");
        }
    }

    // ---- requests

    private static void HandleAsk(IHostContext log, INetBridge net, in NetInboundFrame frame)
    {
        var p = frame.Payload;
        if (p.Length < AskBytes) { log?.LogWarning($"{Tag} ask short bytes={p.Length}"); return; }
        if (p[1] != Version)
        {
            log?.LogWarning($"{Tag} ask version={p[1]} (ours {Version}); ignored - mixed music protocol");
            return;
        }
        if (!_isServer) { log?.LogWarning($"{Tag} ask from={frame.SenderClientId} arrived at a follower; ignored"); return; }
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(2, 4));
        int len = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6, 2));
        if (len == 0 || len > MaxUrlBytes || AskBytes + len > p.Length)
        {
            log?.LogWarning($"{Tag} ask from={frame.SenderClientId} malformed len={len} bytes={p.Length}");
            return;
        }
        var url = Encoding.UTF8.GetString(p, AskBytes, len);
        _asks++;

        var key = new RequestKey { Sender = frame.SenderClientId, Id = id, Url = url };
        if (_verdicts.TryGetValue(key, out byte prior))
        {
            // Retransmission of one request (same sender, id and URL): the verdict is re-sent, the
            // start is not repeated. A reused id for a different URL is a new key and does start.
            log?.LogInfo($"{TagReq} dedupe id={id} from={frame.SenderClientId} url='{Short(url, 90)}' " +
                         $"verdict={(prior == 0 ? "accept" : "reject")} (already handled; not starting again)");
            SendAck(net, frame.SenderClientId, id, prior, _revision,
                    prior == 0 ? "" : "duplicate of a rejected request", quiet: true);
            return;
        }

        log?.LogInfo($"{TagReq} submit id={id} from={frame.SenderClientId} url='{Short(url, 90)}'");
        if (YtDlpPipeline.Classify(url) != YtDlpPipeline.UrlKind.Page)
        {
            RecordVerdict(key, 1);
            SendAck(net, frame.SenderClientId, id, 1, _revision, "not a streamable page link (direct audio stays with the game)");
            return;
        }
        if (!LobbyMusicHooks.AuthoritativeStart(url, "request:" + frame.SenderClientId))
        {
            RecordVerdict(key, 1);
            SendAck(net, frame.SenderClientId, id, 1, _revision, "the host refused to start it (panel unavailable)");
            return;
        }
        RecordVerdict(key, 0);
        SendAck(net, frame.SenderClientId, id, 0, _revision, "");
    }

    private static void SendAck(INetBridge net, ulong to, uint id, byte verdict, uint rev, string reason, bool quiet = false)
    {
        var rb = Encoding.UTF8.GetBytes(reason ?? "");
        if (rb.Length > MaxReasonBytes) Array.Resize(ref rb, MaxReasonBytes);
        var buf = new byte[AckBytes + rb.Length];
        buf[0] = MsgAck; buf[1] = Version; buf[2] = verdict;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(3, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(7, 4), rev);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(11, 2), (ushort)rb.Length);
        rb.CopyTo(buf, AckBytes);
        bool ok = false;
        try { ok = net.Send(NetKind.App, to, buf); } catch { }
        if (!quiet)
            _log?.LogInfo($"{TagReq} {(verdict == 0 ? "accept" : "reject")} id={id} -> client={to} rev={rev} " +
                          $"ok={ok} bytes={buf.Length} reason='{Short(reason, 70)}'");
        else
            _log?.LogInfo($"{TagReq} ack re-sent id={id} -> client={to} ok={ok}");
    }

    private static void RecordVerdict(RequestKey key, byte verdict)
    {
        _verdicts[key] = verdict;
        _verdictOrder.Enqueue(key);
        while (_verdictOrder.Count > AckCacheSize)
        {
            var old = _verdictOrder.Dequeue();
            _verdicts.Remove(old);
        }
    }

    private static void HandleAck(IHostContext log, in NetInboundFrame frame)
    {
        var p = frame.Payload;
        if (p.Length < AckBytes) { log?.LogWarning($"{Tag} ack short bytes={p.Length}"); return; }
        if (p[1] != Version)
        {
            log?.LogWarning($"{Tag} ack version={p[1]} (ours {Version}); ignored - mixed music protocol");
            return;
        }
        if (_isServer) return;
        if (frame.SenderClientId != _serverId) return;
        byte verdict = p[2];
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(3, 4));
        uint rev = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(7, 4));
        int rlen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(11, 2));
        if (AckBytes + rlen > p.Length) rlen = 0;
        var reason = rlen > 0 ? Encoding.UTF8.GetString(p, AckBytes, rlen) : "";
        _acks++;
        if (!_reqPending || id != _reqId)
        {
            _stale++;
            log?.LogInfo($"{TagReq} stale ack id={id} (pending={_reqId} {_reqPending}); ignored");
            return;
        }
        _reqPending = false;
        if (verdict == 0)
            log?.LogInfo($"{TagReq} accept id={id} rev={rev} url='{Short(_pendingUrl, 90)}': the host will " +
                         "publish the session state; we start from that frame, not from here");
        else
        {
            log?.LogWarning($"{TagReq} reject id={id} url='{Short(_pendingUrl, 90)}' reason='{Short(reason, 90)}'; " +
                            "keeping the current track");
            LobbyMusicHooks.NoteRequestRejected(string.IsNullOrEmpty(reason) ? "the host rejected the request" : reason);
        }
    }

    // ================================================================ transport control

    // A follower's transport intent. The local FMOD channel is NOT touched here: the request reaches
    // the host, the host commits, and the publication is what moves this machine (the requester
    // included). One request is in flight at a time and it is bounded by ControlTimeoutSeconds.
    internal static bool SubmitControl(byte action, long targetMs, string source, out string refusal)
    {
        refusal = "";
        if (_isServer) { refusal = "this machine is the host; its transport commits locally"; return false; }
        if (_net == null || !_haveServer)
        {
            refusal = _net == null
                ? "the mod's network bridge is not up on this machine"
                : "this machine is not in a live session yet, so there is no host to control";
            return false;
        }
        if (!_following || _fNonce == 0 || _fRevision == 0)
        {
            refusal = "there is no followed session track to control yet";
            return false;
        }
        if (action != ControlPause && action != ControlResume && action != ControlSeek)
        {
            refusal = "unknown transport action";
            return false;
        }
        if (_ctrlPending)
        {
            refusal = "a control request is already waiting for the host";
            return false;
        }
        targetMs = Math.Max(0, targetMs);
        if (!TryNextMusicId(out _ctrlId, out refusal))
        {
            _log?.LogWarning($"{TagReq} control refused: {refusal}");
            return false;
        }
        var buf = new byte[ControlBytes];
        buf[0] = MsgControl; buf[1] = Version; buf[2] = action; buf[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), _fNonce);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), _fRevision);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), _ctrlId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), _fControlGen);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(20, 8), targetMs);
        bool ok = false;
        try { ok = _net.Send(NetKind.App, _serverId, buf); } catch { }
        if (!ok)
        {
            refusal = "the bridge refused to send the control request";
            _log?.LogWarning($"{TagReq} tx control id={_ctrlId} action={ActionWord(action)} failed: {refusal}");
            return false;
        }
        _ctrlPending = true;
        _ctrlAction = action;
        _ctrlTargetMs = targetMs;
        _ctrlBaseGen = _fControlGen;
        _ctrlRev = _fRevision;
        _ctrlAt = Time0();
        _txControl++;
        _log?.LogInfo($"{TagReq} tx control id={_ctrlId} action={ActionWord(action)} targetMs={targetMs} " +
                      $"rev={_fRevision} gen={_fControlGen} nonce={_fNonce} bytes={buf.Length} source={source}; " +
                      "the local transport is unchanged until the host publishes");
        return true;
    }

    private static void PendingControlTick(float now)
    {
        if (!_ctrlPending) return;
        if (now - _ctrlAt < ControlTimeoutSeconds) return;
        _ctrlPending = false;
        _controlTimeouts++;
        _log?.LogWarning($"{TagReq} control timeout id={_ctrlId} action={ActionWord(_ctrlAction)} after " +
                         $"{ControlTimeoutSeconds:F1}s: no verdict from the host; the controls stay usable, " +
                         "the transport is still the host's");
        LobbyMusicHooks.NoteRequestRejected("the host did not answer the control request");
    }

    private static void ResolveControlFromState(byte intent, uint rev, uint controlGen)
    {
        if (!_ctrlPending) return;
        if (rev != _ctrlRev) return;                 // a control for another track is not resolved here
        if (controlGen < _ctrlBaseGen) return;
        if (_ctrlAction == ControlSeek) return;      // a seek verdict is per request: the ack carries it
        bool wantsPaused = _ctrlAction == ControlPause;
        if ((wantsPaused && intent == IntentPaused) || (!wantsPaused && intent == IntentPlaying))
        {
            _ctrlPending = false;
            _log?.LogInfo($"{TagReq} control id={_ctrlId} applied from the publication " +
                          $"(action={ActionWord(_ctrlAction)} rev={_fRevision} ctrlGen={controlGen})");
            return;
        }
        if (controlGen > _ctrlBaseGen)
        {
            _ctrlPending = false;
            _controlRejects++;
            _log?.LogWarning($"{TagReq} control id={_ctrlId} superseded: another transport change landed " +
                             $"first (action={ActionWord(_ctrlAction)} intent={Word(intent)} " +
                             $"gen={_ctrlBaseGen}->{controlGen})");
            LobbyMusicHooks.NoteRequestRejected("another transport change landed first");
        }
    }

    private static void ClearPendingControl(string why)
    {
        if (_ctrlPending)
            _log?.LogInfo($"{TagReq} pending control dropped ({why}): id={_ctrlId} action={ActionWord(_ctrlAction)}");
        _ctrlPending = false;
        _ctrlAt = -1f;
        _ctrlRev = 0;
    }

    // Host side: validate against the transport-provided sender and the LIVE session watermark, then
    // commit through the host's own transport exactly once. The payload never carries a trusted
    // sender id, and the periodic state sequence is not consulted: only the control generation can
    // supersede a click.
    private static void HandleControl(IHostContext log, INetBridge net, in NetInboundFrame frame)
    {
        var p = frame.Payload;
        if (p.Length < ControlBytes) { log?.LogWarning($"{Tag} control short bytes={p.Length}"); return; }
        if (p[1] != Version)
        {
            log?.LogWarning($"{Tag} control version={p[1]} (ours {Version}) from={frame.SenderClientId}; " +
                            "ignored - mixed music protocol (both peers must run the same music protocol)");
            return;
        }
        if (!_isServer) { log?.LogWarning($"{Tag} control from={frame.SenderClientId} arrived at a follower; ignored"); return; }
        byte action = p[2];
        uint nonce = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4, 4));
        uint rev = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8, 4));
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(12, 4));
        uint baseGen = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(16, 4));
        long target = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(20, 8));
        ulong sender = frame.SenderClientId;

        if (action != ControlPause && action != ControlResume && action != ControlSeek)
        {
            log?.LogWarning($"{TagReq} control id={id} from={sender}: unknown action={action}; ignored");
            return;
        }
        if (!IsActiveMember(net, sender))
        {
            // No ack and no state: an unproven sender gets nothing to work with.
            log?.LogWarning($"{TagReq} control id={id} from={sender} rejected: not an active member of this " +
                            "session (the transport does not list the sender)");
            return;
        }
        if (nonce != _nonce)
        {
            RejectControl(log, net, sender, id, action, rev, baseGen,
                          "stale host session (the request belongs to another host session)", sendState: true);
            return;
        }
        _rxControl++;
        var key = new ControlKey { Sender = sender, Id = id, Nonce = nonce };
        if (_controlVerdicts.TryGetValue(key, out byte prior))
        {
            log?.LogInfo($"{TagReq} control dedupe id={id} from={sender} verdict={(prior == 0 ? "accept" : "reject")} " +
                         "(already handled; not committing again)");
            SendControlAck(net, sender, id, prior, action, _revision, _controlGen, -1,
                           prior == 0 ? "" : "duplicate of a rejected request", quiet: true);
            return;
        }

        var host = LobbyMusicHooks.Host;
        if (host == null)
        {
            RejectControl(log, net, sender, id, action, rev, baseGen, "the host has no music transport", sendState: false);
            return;
        }
        if (_revision == 0 || !host.OwnsPlayback)
        {
            RejectControl(log, net, sender, id, action, rev, baseGen,
                          "no session track is playing on the host", sendState: true);
            return;
        }
        if (rev != _revision)
        {
            RejectControl(log, net, sender, id, action, rev, baseGen,
                          $"the session track changed (request rev={rev}, current rev={_revision}); " +
                          "a control for another track is not applied here", sendState: true);
            return;
        }
        if (baseGen != _controlGen)
        {
            RejectControl(log, net, sender, id, action, rev, baseGen,
                          $"superseded by a newer transport commit (request gen={baseGen}, current gen={_controlGen})",
                          sendState: true, verdict: 2);
            return;
        }
        if (!host.HasPlayableChannel)
        {
            RejectControl(log, net, sender, id, action, rev, baseGen,
                          "no playable channel yet (the track is still buffering)", sendState: true);
            return;
        }

        bool committed;
        long appliedMs = host.PositionMs;
        string reason = "";
        switch (action)
        {
            case ControlPause:
                committed = host.Pause();
                if (!committed) reason = "the transport refused the pause";
                break;
            case ControlResume:
                committed = host.Resume();
                if (!committed) reason = "the transport refused the resume";
                break;
            default:
                if (target < 0)
                {
                    committed = false;
                    reason = "the target is before the start of the track";
                }
                else if (host.TotalMs > 0 && target > host.TotalMs)
                {
                    committed = false;
                    reason = $"the target {target}ms is past the end of the track ({host.TotalMs}ms)";
                }
                else if (target > host.CoverageMs - SwitchMarginMs)
                {
                    committed = false;
                    reason = $"the target {target}ms is beyond the available range " +
                             $"(coverage {host.CoverageMs}ms, margin {SwitchMarginMs}ms)";
                }
                else
                {
                    committed = host.SeekVerified(target, out appliedMs);
                    if (!committed) reason = $"the transport could not hold {target}ms";
                }
                break;
        }

        if (!committed)
        {
            RecordControlVerdict(key, 1);
            RejectControl(log, net, sender, id, action, rev, baseGen, reason, sendState: true, alreadyRecorded: true);
            return;
        }
        RecordControlVerdict(key, 0);
        _controlCommits++;
        _log?.LogInfo($"{TagReq} control id={id} from={sender} action={ActionWord(action)} rev={rev} gen={baseGen} " +
                      $"accepted commitGen={_controlGen} appliedMs={appliedMs} bytes={p.Length}");
        SendControlAck(net, sender, id, 0, action, _revision, _controlGen, appliedMs, "");
    }

    private static bool IsActiveMember(INetBridge net, ulong sender)
    {
        if (net == null || sender == 0) return false;
        var ids = new List<ulong>(8);
        try { net.Peers(ids); } catch { return false; }
        for (int i = 0; i < ids.Count; i++) if (ids[i] == sender) return true;
        return false;
    }

    private static void RejectControl(IHostContext log, INetBridge net, ulong to, uint id, byte action,
                                      uint rev, uint baseGen, string reason, bool sendState,
                                      byte verdict = 1, bool alreadyRecorded = false)
    {
        if (!alreadyRecorded)
            RecordControlVerdict(new ControlKey { Sender = to, Id = id, Nonce = _nonce }, verdict);
        _controlRejects++;
        log?.LogWarning($"{TagReq} control id={id} from={to} action={ActionWord(action)} rev={rev} gen={baseGen} " +
                        $"REJECTED: {reason}");
        SendControlAck(net, to, id, verdict, action, _revision, _controlGen, -1, reason);
        if (sendState) SendStateTo(log, net, to, "control-rejected");
    }

    private static void RecordControlVerdict(ControlKey key, byte verdict)
    {
        _controlVerdicts[key] = verdict;
        _controlOrder.Enqueue(key);
        while (_controlOrder.Count > ControlCacheSize)
        {
            var old = _controlOrder.Dequeue();
            _controlVerdicts.Remove(old);
        }
    }

    // Control ack (little-endian, version 3):
    //   0 type 26 | 1 version | 2 verdict (0 accept, 1 reject, 2 stale) | 3 action
    //   4..7 request id | 8..11 current revision | 12..15 current control generation
    //   16..23 applied position ms (-1 when not a commit) | 24..25 reason length | 26.. reason
    private static void SendControlAck(INetBridge net, ulong to, uint id, byte verdict, byte action,
                                       uint rev, uint controlGen, long appliedMs, string reason, bool quiet = false)
    {
        var rb = Encoding.UTF8.GetBytes(reason ?? "");
        if (rb.Length > MaxReasonBytes) Array.Resize(ref rb, MaxReasonBytes);
        var buf = new byte[ControlAckBytes + rb.Length];
        buf[0] = MsgControlAck; buf[1] = Version; buf[2] = verdict; buf[3] = action;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), rev);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), controlGen);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), appliedMs);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24, 2), (ushort)rb.Length);
        rb.CopyTo(buf, ControlAckBytes);
        bool ok = false;
        try { ok = net.Send(NetKind.App, to, buf); } catch { }
        if (!quiet)
            _log?.LogInfo($"{TagReq} control {(verdict == 0 ? "accept" : verdict == 2 ? "stale" : "reject")} id={id} " +
                          $"-> client={to} rev={rev} gen={controlGen} appliedMs={appliedMs} ok={ok} " +
                          $"bytes={buf.Length} reason='{Short(reason, 70)}'");
    }

    private static void HandleControlAck(IHostContext log, in NetInboundFrame frame)
    {
        var p = frame.Payload;
        if (p.Length < ControlAckBytes) { log?.LogWarning($"{Tag} control-ack short bytes={p.Length}"); return; }
        if (p[1] != Version)
        {
            log?.LogWarning($"{Tag} control-ack version={p[1]} (ours {Version}); ignored - mixed music protocol");
            return;
        }
        if (_isServer) return;
        if (frame.SenderClientId != _serverId) return;
        byte verdict = p[2];
        byte action = p[3];
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4, 4));
        uint rev = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8, 4));
        uint controlGen = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(12, 4));
        long appliedMs = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(16, 8));
        int rlen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(24, 2));
        if (ControlAckBytes + rlen > p.Length) rlen = 0;
        var reason = rlen > 0 ? Encoding.UTF8.GetString(p, ControlAckBytes, rlen) : "";
        if (controlGen > _fControlGen) _fControlGen = controlGen;
        if (!_ctrlPending || id != _ctrlId)
        {
            _stale++;
            log?.LogInfo($"{TagReq} stale control-ack id={id} (pending={_ctrlId} {_ctrlPending}); ignored");
            return;
        }
        _ctrlPending = false;
        if (verdict == 0)
        {
            log?.LogInfo($"{TagReq} control id={id} accepted action={ActionWord(action)} rev={rev} gen={controlGen} " +
                         $"appliedMs={appliedMs}: the host committed the transport; we move on its publication");
        }
        else
        {
            _controlRejects++;
            var why = string.IsNullOrEmpty(reason)
                ? (verdict == 2 ? "another transport change landed first" : "the host rejected the control request")
                : reason;
            log?.LogWarning($"{TagReq} control id={id} rejected action={ActionWord(action)} rev={rev} gen={controlGen} " +
                            $"verdict={(verdict == 2 ? "stale" : "reject")} reason='{Short(why, 90)}'; " +
                            "the transport is still the host's");
            LobbyMusicHooks.NoteRequestRejected(why);
        }
    }

    // A targeted snapshot for a requester whose view is stale (rejected control). `Encode` advances
    // the publication sequence, exactly like the late-join snapshot: the next cadence frame is still
    // newer for every other peer.
    private static void SendStateTo(IHostContext log, INetBridge net, ulong to, string why)
    {
        var host = LobbyMusicHooks.Host;
        if (host == null || net == null) return;
        var payload = Encode(host, out byte intent, out long anchorPos, out long hostMs, out long duration);
        if (payload == null) return;
        bool ok = false;
        try { ok = net.Send(NetKind.App, to, payload); } catch { }
        log?.LogInfo($"{TagSync} tx state why={why} client={to} ok={ok} nonce={_nonce} rev={_revision} " +
                     $"seq={_seq} ctrlGen={_controlGen} intent={Word(intent)} anchorMs={anchorPos} " +
                     $"hostMs={hostMs} durMs={duration} bytes={payload.Length}");
    }

    internal static string ActionWord(byte action)
    {
        switch (action)
        {
            case ControlPause: return "pause";
            case ControlResume: return "resume";
            case ControlSeek: return "seek";
            default: return "none";
        }
    }

    // Client intent. The local stream is not touched: no optimistic start, no handoff, no audio
    // transfer - the host decides, and everyone downloads the accepted URL independently.
    internal static bool SubmitRequest(string url, string source)
    {
        if (_isServer) return false;
        if (_net == null || !_haveServer)
        {
            _submitRefusal = _net == null
                ? "the mod's network bridge is not up on this machine"
                : "this machine is not in a live session yet, so there is no host to ask";
            _log?.LogWarning($"{TagReq} submit ignored: no host to ask (net={(_net == null ? "none" : "up")} " +
                             $"live={_live} haveServer={_haveServer} server={_serverId}): {_submitRefusal}");
            return false;
        }
        var bytes = Encoding.UTF8.GetBytes(url ?? "");
        if (bytes.Length == 0 || bytes.Length > MaxUrlBytes)
        {
            _submitRefusal = $"the url is {bytes.Length} bytes (limit {MaxUrlBytes})";
            _log?.LogWarning($"{TagReq} submit ignored: {_submitRefusal}");
            return false;
        }
        if (!TryNextMusicId(out _reqId, out _submitRefusal))
        {
            _log?.LogWarning($"{TagReq} submit ignored: {_submitRefusal}");
            return false;
        }
        var buf = new byte[AskBytes + bytes.Length];
        buf[0] = MsgAsk; buf[1] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(2, 4), _reqId);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), (ushort)bytes.Length);
        bytes.CopyTo(buf, AskBytes);
        bool ok = false;
        try { ok = _net.Send(NetKind.App, _serverId, buf); } catch { }
        _submitRefusal = ok ? "" : "the bridge refused to send the request to the host";
        _reqPending = ok; _pendingUrl = url ?? ""; _pendingAt = Time0();
        _log?.LogInfo($"{TagReq} submit id={_reqId} url='{Short(url, 90)}' -> host={_serverId} ok={ok} " +
                      $"bytes={buf.Length} source={source}; the current track keeps playing until an " +
                      "accepted revision arrives");
        return ok;
    }

    // The reason the last submit did not reach the host ("" when it did). Display only.
    internal static string LastSubmitRefusal => _submitRefusal ?? "";

    // ================================================================ reconcile (follower)

    // target = anchorPos + (estimatedHostNow - anchorHostTime) while the host plays; = anchorPos
    // while it is paused or buffering. `estimatedHostNow = localNow + offset`, and `offset` comes
    // from the probe round trips - never from comparing the two process clocks directly.
    private static long ComputeTarget()
    {
        long target = _fAnchorPos;
        if (_fIntent == IntentPlaying && _haveOffset)
        {
            long elapsed = NowMs() + _offsetMs - _fAnchorHostMs;
            if (elapsed < 0) elapsed = 0;                    // a frame from the future: hold at the anchor
            target += elapsed;
        }
        if (_fDurationMs > 0 && target > _fDurationMs) target = _fDurationMs;
        return target < 0 ? 0 : target;
    }

    private static void ReconcileTick(MusicStreamHost host, float now)
    {
        if (!_following || _fDead) return;
        if (_fIntent == IntentStopped || _fIntent == IntentFailed) return;
        // Our local stream is not the session track yet (the takeover is refused, or still in flight):
        // nothing to reconcile. The desired state below is independent of local FMOD readiness, so
        // this is a "not yet", never a "never".
        if (!string.Equals(host.Url, _fUrl, StringComparison.Ordinal)) return;
        if (!host.OwnsPlayback) return;

        bool desiredPaused = _fIntent != IntentPlaying;
        long target = ComputeTarget();
        long coverage = host.CoverageMs;
        long actual = host.PositionMs;
        bool actualPaused = host.Engine.IsPaused;
        long drift = actual - target;
        if (!actualPaused) _fPlayed = true;
        bool covered = coverage > 0 && target <= coverage - SwitchMarginMs;
        if (covered && _fCoveredAtMs < 0) _fCoveredAtMs = NowMs();
        _targetMs = target; _driftMs = drift; _coverageMs = coverage;

        if (!host.Engine.HasChannel)
        {
            // Before the first sound (a channel, not a phase: an exhausted prefix still has one and is
            // handled below). The target keeps moving with the anchors, and the start uses whatever it
            // is at that moment - never the position a frame happened to carry.
            host.SetStartTarget(target, desiredPaused, now);
            LogReconcile(now, target, actual, drift, desiredPaused, actualPaused, coverage,
                         covered ? "await-start" : "await-coverage", covered);
            return;
        }

        if (desiredPaused != actualPaused)
        {
            // A pause/resume the transport refused is retried on the SAME 1/2/4/8 s backoff as a
            // refused position correction. Without this gate the mismatch branch (which runs
            // before the position backoff below) re-attempted the transport on every tick.
            if (now < _nextCorrectionAt)
            {
                LogReconcile(now, target, actual, drift, desiredPaused, actualPaused, coverage, "backoff", false);
                return;
            }
            // The pause state is corrected first: it decides whether a position correction is audible.
            if (desiredPaused)
            {
                if (!host.Pause()) { Fail(now, "fail-pause"); LogReconcile(now, target, actual, drift, desiredPaused, actualPaused, coverage, "fail-pause", true); return; }
                _correctionFails = 0;
                long pos = host.PositionMs;
                string result = "paused";
                if (Math.Abs(pos - target) > CorrectAboveMs && target <= coverage - SwitchMarginMs &&
                    host.SeekVerified(target, out long settled))
                {
                    _corrections++;
                    pos = settled;
                    result = "paused-seek";
                }
                LogApplied(true);
                LogReconcile(now, target, pos, pos - target, true, host.Engine.IsPaused, coverage, result, true);
                return;
            }

            // The first start of this revision has not happened yet: hold the pause until a prefix
            // covers the target. Resuming here would emit audio from a position the host is not at,
            // which is the whole defect this loop exists to remove.
            if (!_fPlayed && target > coverage - SwitchMarginMs)
            {
                LogReconcile(now, target, actual, drift, false, actualPaused, coverage, "await-coverage", false);
                return;
            }

            // Unpausing: resume at the CURRENT target, not at the position the channel was frozen at
            // (that was the defect: the host resumed and the follower carried on from its old
            // playhead). A paused seek is silent, so it happens before the first audible frame - but
            // it is not a precondition: a channel that cannot hold the target yet (only a prefix
            // snapshot is open while the finished file is deferred to the resume) still resumes, and
            // the next ticks correct the drift while playing.
            if (Math.Abs(drift) > NoOpBandMs)
            {
                if (target <= coverage - SwitchMarginMs && host.SeekVerified(target, out long before))
                {
                    _corrections++;
                    actual = before; drift = before - target;
                }
                else
                {
                    _log?.LogInfo($"{TagSync} resume at targetMs={target}: the paused channel cannot hold " +
                                  $"it yet (driftMs={drift} coverageMs={coverage} limitMs={coverage - SwitchMarginMs}); " +
                                  "resuming first and correcting afterwards");
                }
            }
            if (!host.Resume()) { Fail(now, "fail-resume"); LogReconcile(now, target, actual, drift, false, actualPaused, coverage, "fail-resume", true); return; }
            _correctionFails = 0;
            LogApplied(false);
            LogReconcile(now, target, host.PositionMs, host.PositionMs - target, false, host.Engine.IsPaused, coverage, "resumed", true);
            return;
        }

        long abs = Math.Abs(drift);
        string res;
        if (abs <= NoOpBandMs) { res = "ok"; _correctionFails = 0; }
        else if (abs <= CorrectAboveMs) res = "band";
        else if (target > coverage - SwitchMarginMs) res = "await-coverage";
        else if (now < _nextCorrectionAt) res = "backoff";
        else
        {
            long before = actual;
            if (!host.SeekVerified(target, out long after))
            {
                Fail(now, "fail-seek");
                LogReconcile(now, target, actual, drift, desiredPaused, actualPaused, coverage, "fail-seek", true);
                return;
            }
            _corrections++;
            _correctionFails = 0;
            drift = after - target;
            res = $"seek({before}->{after})";
        }
        LogReconcile(now, target, actual, drift, desiredPaused, actualPaused, coverage, res, false);
    }

    // 1 s, 2 s, 4 s, 8 s, then a steady 8 s: a correction that the transport refuses is retried, not
    // hammered and not given up on. The pending target itself is never stale - it is recomputed from
    // the newest anchors on every tick.
    private static void Fail(float now, string what)
    {
        if (_correctionFails < 4) _correctionFails++;
        float delay = 1f;
        for (int i = 1; i < _correctionFails; i++) delay *= 2f;
        if (delay > 8f) delay = 8f;
        _nextCorrectionAt = now + delay;
        _log?.LogWarning($"{TagSync} {what} at targetMs={_targetMs} actualMs={_targetMs + _driftMs} " +
                         $"driftMs={_driftMs} coverageMs={_coverageMs}: retrying in {delay:F0}s " +
                         $"(attempt {_correctionFails}); the stream keeps running");
    }

    // The transport commit itself, with the propagation it actually took: the age of the anchor it was
    // applied from, on the offset-corrected clock. This is the pause/resume latency the acceptance
    // claim is about (500 ms), measured without depending on either side's log timestamps.
    private static void LogApplied(bool paused)
    {
        long age = _haveOffset ? NowMs() + _offsetMs - _fAnchorHostMs : -1;
        if (age < 0 && _haveOffset) age = 0;
        var h = LobbyMusicHooks.Host;
        bool actual = h != null && h.Engine.IsPaused;
        _log?.LogInfo($"{TagSync} applied desiredPaused={paused} afterMs={age} rev={_fRevision} " +
                      $"anchorMs={_fAnchorPos} offsetMs={_offsetMs} bestRttMs={_bestRttMs} actualPaused={actual}");
    }

    private static void LogReconcile(float now, long target, long actual, long drift, bool desiredPaused,
                                     bool actualPaused, long coverage, string result, bool force)
    {
        bool changed = !string.Equals(result, _lastReconcileResult, StringComparison.Ordinal);
        if (!force && !changed && now < _nextReconcileLogAt) return;
        _nextReconcileLogAt = now + ReconcileLogSeconds;
        _lastReconcileResult = result;
        long covered = _fCoveredAtMs < 0 ? -1 : NowMs() - _fCoveredAtMs;
        _log?.LogInfo($"{TagSync} reconcile targetMs={target} actualMs={actual} driftMs={drift} " +
                      $"desiredPaused={desiredPaused} actualPaused={actualPaused} coverageMs={coverage} " +
                      $"result={result} rev={_fRevision} intent={Word(_fIntent)} offsetMs={_offsetMs} " +
                      $"bestRttMs={_bestRttMs} coveredMs={covered}");
    }

    private static void ClearFollow(string why)
    {
        if (_following || _fUrl.Length > 0)
            _log?.LogInfo($"{TagSync} follow cleared ({why}): was='{Short(_fUrl, 60)}' rev={_fRevision} intent={Word(_fIntent)}");
        _following = false;
        _fDead = false;
        _fPlayed = false;
        _fCoveredAtMs = -1;
        _fUrl = ""; _fTitle = ""; _fDurationMs = 0;
        _fIntent = 255;
        _fAnchorPos = 0; _fAnchorHostMs = 0;
        _correctionFails = 0; _nextCorrectionAt = 0f;
        _lastReconcileResult = "";
        ClearPendingControl("follow cleared");
    }

    private static void ClearPending(string why)
    {
        if (_reqPending)
            _log?.LogInfo($"{TagReq} pending request dropped ({why}): id={_reqId} url='{Short(_pendingUrl, 80)}'");
        _reqPending = false;
        _pendingUrl = "";
        _pendingAt = -1f;
    }

    // A peer that just finished the handshake gets the current state at once: the late joiner
    // behaviour is "join mid-track and you hear it where the host is", not "from the beginning".
    internal static void OnPeerMod(IHostContext log, ulong peerId)
    {
        _log = log;
        _lateJoins++;
        var host = LobbyMusicHooks.Host;
        if (host == null || _net == null || !LobbyMusicHooks.InLobby) return;
        if (!_isServer || !host.OwnsPlayback || peerId == 0)
        {
            log?.LogInfo($"{Tag} late-join client={peerId}: nothing playing at the host (phase={host.Current})");
            return;
        }
        var payload = Encode(host, out byte intent, out long anchorPos, out long hostMs, out long duration);
        if (payload == null) return;
        bool ok = false;
        try { ok = _net.Send(NetKind.App, peerId, payload); } catch { }
        log?.LogInfo($"{TagSync} tx state why=late-join client={peerId} ok={ok} nonce={_nonce} rev={_revision} " +
                     $"seq={_seq} ctrlGen={_controlGen} intent={Word(intent)} url='{Short(host.Url, 90)}' " +
                     $"anchorMs={anchorPos} hostMs={hostMs} durMs={duration} bytes={payload.Length}");
    }

    // ================================================================ helpers

    internal static string Word(byte intent)
    {
        switch (intent)
        {
            case IntentBuffering: return "buffering";
            case IntentPlaying: return "playing";
            case IntentPaused: return "paused";
            case IntentStopped: return "stopped";
            case IntentFailed: return "failed";
            default: return "none";
        }
    }

    private static string Short(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
