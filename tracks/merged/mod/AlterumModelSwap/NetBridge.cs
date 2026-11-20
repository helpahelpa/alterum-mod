using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AlterumModelSwap.Contracts;
using BepInEx.Logging;
using Unity.Collections;
using LogLevel = BepInEx.Logging.LogLevel;
using Unity.Netcode;

namespace AlterumModelSwap;

// Stage-1 mod networking: the permanent named-message bridge.
//
// Why this lives in the host assembly and not the hot module: a new NetworkBehaviour cannot be
// attached to an already-spawned object and a new prefab cannot be registered, so anything that
// must exist on the il2cpp side has to be created by the assembly that never unloads. This class
// therefore owns the single `CustomMessagingManager.RegisterNamedMessageHandler` registration and
// the delegate it hands to IL2CPP; hot generations only ever see AlterumModelSwap.Contracts.INetBridge.
//
// Lifecycle (all on the main thread, polled at 4 Hz from PumpBehaviour):
//   WaitingForNetwork -> a NetworkManager exists but its CustomMessagingManager is not ready yet
//   Registered        -> handler installed on a specific (NetworkManager, CustomMessagingManager) pair
//   WaitingForNetwork -> the session ended (manager gone / listening==false); an unregister is
//                        attempted, then the next session's manager is picked up automatically
//   Unsupported       -> the Unity.Netcode API is absent on this build: logged once, never retried
//
// The receive callback does the absolute minimum and never touches anything stateful that could
// throw: it copies the bytes out of the FastBufferReader immediately (the reader is only valid for
// the duration of the callback), validates the envelope, and appends to a bounded ring. Nothing is
// sent from inside the callback - echo replies are flushed on the next tick, so no il2cpp call is
// ever re-entered from within IL2CPP's own message dispatch.
internal sealed unsafe class NetBridge : INetBridge
{
    internal static readonly NetBridge Instance = new NetBridge();

    private const double PollSeconds = 0.25;

    // ---------------------------------------------------------------- state
    // Diagnostics-only starts parked and stays there: Disabled is the state the hot module also
    // reports, and no generation may revive it (see Tick and NewGeneration).
    private NetState _state = DiagnosticsMode.Enabled ? NetState.Disabled : NetState.WaitingForNetwork;
    private NetCaps _caps = NetCaps.None;
    private string _unsupportedReason;

    private NetworkManager _nm;
    private CustomMessagingManager _cmm;
    private IntPtr _nmPtr;
    private IntPtr _cmmPtr;
    private string _registeredName;

    // The IL2CPP delegate and its managed source must both stay rooted forever: IL2CPP holds only
    // a pointer to the thunk, the thunk resolves the managed delegate from this Action.
    private Action<ulong, FastBufferReader> _handlerAction;
    private CustomMessagingManager.HandleNamedMessageDelegate _handler;

    private uint _generation;
    private uint _sessionEpoch;
    private uint _sendSeq;
    private NetBridgeConfig _cfg = NetBridgeConfig.Default;
    private int _consumerActive;

    private readonly object _ringLock = new object();
    private NetInboundFrame[] _ring = new NetInboundFrame[NetBridgeConfig.Default.InboundCapacity];
    private int _ringHead;
    private int _ringCount;

    private readonly List<(ulong target, byte[] payload)> _pendingReplies = new List<(ulong, byte[])>();
    private readonly Dictionary<ulong, (uint epoch, uint seq)> _senderSeq = new Dictionary<ulong, (uint, uint)>();

    private readonly List<ulong> _peers = new List<ulong>();
    private string _peerSignature = "";

    private long _sent, _received, _dropped, _oversized, _malformed, _versionRejected, _seqRejected;
    private int _faultStreak;
    private string _lastError;

    private double _pollAccum;

    // echo test path
    private uint _echoSeq;
    private readonly List<(ulong target, byte[] payload, DateTime at)> _echoOutstanding = new List<(ulong, byte[], DateTime)>();

    private readonly Dictionary<string, DateTime> _limited = new Dictionary<string, DateTime>();
    private readonly object _limitedLock = new object();

    // ---------------------------------------------------------------- logging

    private static void Info(string message) => HostPlugin.L?.LogInfo(message);
    private static void Warn(string message) => HostPlugin.L?.LogWarning(message);

    private void LogLimited(LogLevel level, string key, string message, double seconds = 5)
    {
        lock (_limitedLock)
        {
            if (_limited.TryGetValue(key, out var at) && (DateTime.UtcNow - at).TotalSeconds < seconds) return;
            _limited[key] = DateTime.UtcNow;
        }
        if (level == LogLevel.Warning) Warn(message);
        else HostPlugin.L?.LogInfo(message);
    }

    // ---------------------------------------------------------------- polling

    internal void Tick(float unscaledDeltaTime)
    {
        // Diagnostics-only owns nothing on the wire. Returning before the first poll is what keeps
        // RegisterNamedMessageHandler from ever being called, so there is no handler to echo, no
        // stored peer list to send to, and RequestFile (the `net.request` channel) is never read.
        if (DiagnosticsMode.Enabled) return;
        if (_state == NetState.Unsupported) return;
        _pollAccum += unscaledDeltaTime;
        if (_pollAccum < PollSeconds) return;
        _pollAccum = 0;

        try { Step(); }
        catch (Exception e) { Fault("poll", e); }

        try { FlushReplies(); }
        catch (Exception e) { Fault("reply-flush", e); }

        try { RequestFile(); }
        catch (Exception e) { LogLimited(LogLevel.Warning, "request-fault", $"[net:request-fault] {e.GetType().Name}: {e.Message}"); }
    }

    private void Step()
    {
        if (_state == NetState.Disabled) return;

        NetworkManager nm;
        try { nm = NetworkManager.Singleton; }
        catch (Exception e) when (IsMissingApi(e))
        {
            Unsupported(e);
            return;
        }

        if (nm == null)
        {
            if (_state == NetState.Registered) LeaveSession("no NetworkManager");
            _state = NetState.WaitingForNetwork;
            return;
        }

        var nmPtr = nm.Pointer;
        if (nmPtr != _nmPtr && _state == NetState.Registered) LeaveSession("NetworkManager replaced");

        var cmm = nm.CustomMessagingManager;
        if (cmm == null)
        {
            if (_state == NetState.Registered) LeaveSession("CustomMessagingManager gone");
            _state = NetState.WaitingForNetwork;
            _nm = nm;
            _nmPtr = nmPtr;
            return;
        }

        var cmmPtr = cmm.Pointer;
        if (_state == NetState.Registered && cmmPtr == _cmmPtr && nmPtr == _nmPtr)
        {
            RefreshSession(nm);
            return;
        }

        if (_state == NetState.Registered) LeaveSession("CustomMessagingManager replaced");
        Register(nm, cmm);
    }

    private void Register(NetworkManager nm, CustomMessagingManager cmm)
    {
        _nm = nm;
        _nmPtr = nm.Pointer;
        _cmm = cmm;
        _cmmPtr = cmm.Pointer;

        _handlerAction ??= OnNamedMessage;
        _handler ??= _handlerAction; // implicit managed -> il2cpp delegate conversion

        try
        {
            cmm.RegisterNamedMessageHandler(NetProtocol.MessageName, _handler);
        }
        catch (Exception e) when (IsMissingApi(e))
        {
            Unsupported(e);
            return;
        }
        catch (Exception e)
        {
            Fault("register", e);
            return;
        }

        _registeredName = NetProtocol.MessageName;
        _state = NetState.Registered;
        _caps = NetCaps.Send | NetCaps.Receive | NetCaps.Broadcast;
        _sessionEpoch = NewEpoch();
        _sendSeq = 0;
        lock (_ringLock)
        {
            _senderSeq.Clear();
            _pendingReplies.Clear();
        }

        ulong local = 0, server = 0;
        bool isServer = false, isClient = false, listening = false;
        try
        {
            local = nm.LocalClientId;
            server = NetworkManager.ServerClientId;
            isServer = nm.IsServer;
            isClient = nm.IsClient;
            listening = nm.IsListening;
        }
        catch (Exception e) { LogLimited(LogLevel.Warning, "session-info", $"[net:session-info] {e.GetType().Name}: {e.Message}"); }

        Info($"[net] handler registered name={NetProtocol.MessageName} nm=0x{_nmPtr.ToInt64():x} cmm=0x{_cmmPtr.ToInt64():x}");
        Info($"[net] session epoch={_sessionEpoch} local={local} server={server} isServer={isServer} isClient={isClient} listening={listening}");
        RefreshSession(nm);
    }

    // ConnectedClientsIds is the il2cpp IReadOnlyList<ulong> interface, whose generated type only
    // exposes the indexer (the Count it inherits is not projected). The live object is a List<ulong>,
    // so read it through that; the index loop is only a fallback.
    private static void ReadPeers(NetworkManager nm, List<ulong> into)
    {
        into.Clear();
        try
        {
            var ids = nm.ConnectedClientsIds;
            if (ids == null) return;
            var local = nm.LocalClientId;

            var list = ids.TryCast<Il2CppSystem.Collections.Generic.List<ulong>>();
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var id = list[i];
                    if (id != local && !into.Contains(id)) into.Add(id);
                }
                return;
            }

            for (int i = 0; i < 32; i++)
            {
                ulong id;
                try { id = ids[i]; } catch { return; }
                if (id != local && !into.Contains(id)) into.Add(id);
            }
        }
        catch { }
    }

    private void RefreshSession(NetworkManager nm)
    {
        var peers = new List<ulong>(4);
        ReadPeers(nm, peers);

        lock (_ringLock)
        {
            _peers.Clear();
            _peers.AddRange(peers);
        }
        var signature = string.Join(",", peers);
        if (signature != _peerSignature)
        {
            _peerSignature = signature;
            if (_state == NetState.Registered) Info($"[net] peers [{signature}] local={SafeLocal(nm)}");
        }
    }

    private static ulong SafeLocal(NetworkManager nm)
    {
        try { return nm.LocalClientId; } catch { return 0; }
    }

    private void LeaveSession(string why)
    {
        var name = _registeredName;
        var cmm = _cmm;
        if (cmm != null && name != null)
        {
            try
            {
                cmm.UnregisterNamedMessageHandler(name);
                Info($"[net] handler unregistered name={name} reason={why}");
            }
            catch (Exception e)
            {
                // The manager is usually already torn down here; that is not an error.
                Info($"[net] handler unregister skipped reason={why} ({e.GetType().Name})");
            }
        }
        _registeredName = null;
        _cmm = null;
        _cmmPtr = IntPtr.Zero;
        _state = NetState.WaitingForNetwork;
        _caps = NetCaps.None;
        _sessionEpoch = 0;
        _peerSignature = "";
        _peers.Clear();
        lock (_ringLock)
        {
            _senderSeq.Clear();
            _pendingReplies.Clear();
        }
    }

    private static uint NewEpoch()
    {
        var v = (uint)((DateTime.UtcNow.Ticks >> 8) ^ (Environment.ProcessId * 2654435761u));
        return v == 0 ? 1u : v;
    }

    // ---------------------------------------------------------------- inbound

    private void OnNamedMessage(ulong senderClientId, FastBufferReader reader)
    {
        try { HandleFrame(senderClientId, reader); }
        catch (Exception e)
        {
            Interlocked.Increment(ref _malformed);
            _lastError = $"{e.GetType().Name}: {e.Message}";
            LogLimited(LogLevel.Warning, "callback-fault", $"[net:callback-fault] from={senderClientId} {e.GetType().Name}: {e.Message}");
        }
    }

    private void HandleFrame(ulong senderClientId, FastBufferReader reader)
    {
        Interlocked.Increment(ref _received);

        int length;
        int position;
        try
        {
            length = reader.Length;
            position = reader.Position;
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref _malformed);
            LogLimited(LogLevel.Warning, "reader-fault", $"[net:malformed] from={senderClientId} reader-read-failed {e.GetType().Name}: {e.Message}");
            return;
        }

        var available = length - position;
        if (available <= 0)
        {
            Interlocked.Increment(ref _malformed);
            LogLimited(LogLevel.Warning, "empty", $"[net:malformed] from={senderClientId} empty-frame len={length} pos={position}");
            return;
        }
        if (available > NetProtocol.MaxFrameBytes)
        {
            Interlocked.Increment(ref _oversized);
            LogLimited(LogLevel.Warning, "oversize", $"[net:drop-oversized] from={senderClientId} bytes={available} ceiling={NetProtocol.MaxFrameBytes}");
            return;
        }

        var buffer = new byte[available];
        fixed (byte* p = buffer) reader.ReadBytesSafe((byte*)p, available, 0);

        if (!NetEnvelope.TryDecode(buffer, out var header, out var payload, out var error))
        {
            if (error == "oversized") Interlocked.Increment(ref _oversized);
            else if (error == "version-major") Interlocked.Increment(ref _versionRejected);
            else Interlocked.Increment(ref _malformed);
            _lastError = "decode:" + error;
            LogLimited(LogLevel.Warning, "decode-" + error,
                $"[net:malformed] from={senderClientId} reason={error} bytes={available} hex={NetEnvelope.Hex(buffer)}");
            return;
        }

        if (!AcceptSequence(senderClientId, header))
        {
            Interlocked.Increment(ref _seqRejected);
            LogLimited(LogLevel.Warning, $"seq-{senderClientId}",
                $"[net:seq-rejected] from={senderClientId} epoch={header.SessionEpoch} seq={header.Sequence} last={LastSeq(senderClientId)}");
            return;
        }

        var body = payload.ToArray();

        if (header.Kind == NetKind.Echo)
        {
            Info($"[net:recv] kind=Echo from={senderClientId} epoch={header.SessionEpoch} seq={header.Sequence} bytes={body.Length} hex={NetEnvelope.Hex(body)}");
            if (_cfg.AutoEcho && _cfg.Enabled && _state == NetState.Registered)
            {
                lock (_ringLock)
                {
                    if (_pendingReplies.Count < 8) _pendingReplies.Add((senderClientId, body));
                    else Interlocked.Increment(ref _dropped);
                }
            }
            return;
        }

        if (header.Kind == NetKind.EchoReply)
        {
            var matched = false;
            lock (_ringLock)
            {
                for (int i = 0; i < _echoOutstanding.Count; i++)
                {
                    if (!PayloadEquals(_echoOutstanding[i].payload, body)) continue;
                    var at = _echoOutstanding[i].at;
                    _echoOutstanding.RemoveAt(i);
                    matched = true;
                    Info($"[net:echo:ok] from={senderClientId} matched=1 bytes={body.Length} hex={NetEnvelope.Hex(body)} rtt_ms={(int)(DateTime.UtcNow - at).TotalMilliseconds}");
                    break;
                }
            }
            if (!matched)
                Info($"[net:echo:mismatch] from={senderClientId} bytes={body.Length} hex={NetEnvelope.Hex(body)}");
            return;
        }

        Info($"[net:recv] kind=App from={senderClientId} epoch={header.SessionEpoch} seq={header.Sequence} bytes={body.Length} hex={NetEnvelope.Hex(body)}");
        Enqueue(new NetInboundFrame
        {
            SenderClientId = senderClientId,
            Kind = header.Kind,
            Flags = header.Flags,
            SessionEpoch = header.SessionEpoch,
            Sequence = header.Sequence,
            Payload = body,
        });
    }

    private bool AcceptSequence(ulong sender, in NetEnvelopeHeader header)
    {
        lock (_ringLock)
        {
            if (_senderSeq.TryGetValue(sender, out var seen))
            {
                if (seen.epoch == header.SessionEpoch && header.Sequence <= seen.seq) return false;
                _senderSeq[sender] = (header.SessionEpoch, header.Sequence);
                return true;
            }
            _senderSeq[sender] = (header.SessionEpoch, header.Sequence);
            return true;
        }
    }

    private uint LastSeq(ulong sender)
    {
        lock (_ringLock) return _senderSeq.TryGetValue(sender, out var seen) ? seen.seq : 0;
    }

    private static bool PayloadEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private void Enqueue(NetInboundFrame frame)
    {
        lock (_ringLock)
        {
            if (_ring == null || _ring.Length == 0) { Interlocked.Increment(ref _dropped); return; }
            if (_ringCount >= _ring.Length)
            {
                Interlocked.Increment(ref _dropped);
                _lastError = "inbound-ring-full";
                return; // drop newest: keeps ordering of what is already queued
            }
            _ring[(_ringHead + _ringCount) % _ring.Length] = frame;
            _ringCount++;
        }
    }

    private void FlushReplies()
    {
        (ulong target, byte[] payload)[] replies;
        lock (_ringLock)
        {
            if (_pendingReplies.Count == 0) return;
            replies = _pendingReplies.ToArray();
            _pendingReplies.Clear();
        }
        foreach (var reply in replies)
            Send(NetKind.EchoReply, reply.target, reply.payload);
    }

    // ---------------------------------------------------------------- outbound

    public bool Send(NetKind kind, ulong targetClientId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > NetProtocol.MaxPayloadBytes)
        {
            Interlocked.Increment(ref _dropped);
            LogLimited(LogLevel.Warning, "send-oversize", $"[net:send-refused] bytes={payload.Length} reason=over-ceiling ceiling={NetProtocol.MaxPayloadBytes}");
            return false;
        }
        if (_state != NetState.Registered || !_cfg.Enabled) return false;

        try
        {
            var nm = _nm;
            var cmm = _cmm;
            if (nm == null || cmm == null) return false;

            if (!nm.IsServer && targetClientId != NetworkManager.ServerClientId)
            {
                LogLimited(LogLevel.Warning, "send-route",
                    $"[net:send-refused] target={targetClientId} reason=not-server (clients may only address the server id {NetworkManager.ServerClientId})");
                return false;
            }

            Span<byte> frame = stackalloc byte[NetProtocol.MaxFrameBytes];
            var sequence = unchecked(++_sendSeq);
            var written = NetEnvelope.Encode(kind, _sessionEpoch, sequence, payload, frame);
            if (written <= 0)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            var writer = new FastBufferWriter(written, Allocator.Temp, written);
            try
            {
                fixed (byte* p = frame) writer.WriteBytesSafe((byte*)p, written, 0);
                cmm.SendNamedMessage(NetProtocol.MessageName, targetClientId, writer, NetworkDelivery.ReliableSequenced);
            }
            finally
            {
                try { writer.Dispose(); } catch { }
            }

            Interlocked.Increment(ref _sent);
            if (_cfg.Verbose || kind != NetKind.App)
                Info($"[net:sent] kind={kind} target={targetClientId} epoch={_sessionEpoch} seq={sequence} bytes={written} payload_hex={NetEnvelope.Hex(payload)}");
            return true;
        }
        catch (Exception e) when (IsMissingApi(e))
        {
            Unsupported(e);
            return false;
        }
        catch (Exception e)
        {
            Fault("send", e);
            return false;
        }
    }

    public bool Broadcast(NetKind kind, ReadOnlySpan<byte> payload)
    {
        if (_state != NetState.Registered) return false;
        bool any = false;
        // Copy first: Send cannot fail the peer list, but a session change mid-loop must not
        // let us index a cleared list.
        ulong[] peers;
        lock (_ringLock) peers = _peers.ToArray();
        foreach (var peer in peers) any |= Send(kind, peer, payload);
        if (!any) LogLimited(LogLevel.Warning, "broadcast-empty", "[net:send-refused] broadcast with no peers");
        return any;
    }

    public bool Echo(ulong targetClientId)
    {
        var payload = Encoding.UTF8.GetBytes($"echo {HostPlugin.InstanceName ?? "?"}#{++_echoSeq} {DateTime.UtcNow.Ticks}");
        if (!Send(NetKind.Echo, targetClientId, payload)) return false;
        lock (_ringLock)
        {
            if (_echoOutstanding.Count >= 8) _echoOutstanding.RemoveAt(0);
            _echoOutstanding.Add((targetClientId, payload, DateTime.UtcNow));
        }
        Info($"[net:echo] sent target={targetClientId} bytes={payload.Length} hex={NetEnvelope.Hex(payload)}");
        return true;
    }

    public int Peers(List<ulong> into)
    {
        if (into == null) return 0;
        into.Clear();
        lock (_ringLock) into.AddRange(_peers);
        return into.Count;
    }

    // ---------------------------------------------------------------- consumer ring

    public bool TryTake(out NetInboundFrame frame)
    {
        lock (_ringLock)
        {
            if (_ringCount == 0) { frame = default; return false; }
            frame = _ring[_ringHead];
            _ring[_ringHead] = default;
            _ringHead = (_ringHead + 1) % _ring.Length;
            _ringCount--;
            return true;
        }
    }

    public int Drain(List<NetInboundFrame> into, int max)
    {
        if (into == null || max <= 0) return 0;
        var taken = 0;
        while (taken < max && TryTake(out var frame)) { into.Add(frame); taken++; }
        return taken;
    }

    public void ClearInbound()
    {
        lock (_ringLock)
        {
            _ringHead = 0;
            _ringCount = 0;
        }
    }

    // ---------------------------------------------------------------- generation leases

    /// <summary>
    /// Create the consumer view for a new hot generation. Bumping the generation here (rather than
    /// at the old module's dispose) is what makes the swap ordering safe: HotLoader initialises the
    /// replacement *before* disposing the old module, and once the generation has moved on every
    /// call through the old view - including its Dispose - is a no-op.
    /// </summary>
    internal INetBridgeLease NewGeneration(out INetBridge view)
    {
        _generation++;
        // A fault-parked bridge is revived by the next module; a diagnostics-only one is not.
        if (_state == NetState.Disabled && !DiagnosticsMode.Enabled)
        {
            _state = NetState.WaitingForNetwork;
            _faultStreak = 0;
            Info("[net] re-enabled after module load");
        }
        var gen = new NetBridgeGeneration(this, _generation);
        _cfg = NetBridgeConfig.Default;
        ResizeRing(_cfg.InboundCapacity);
        _consumerActive = 1;
        view = gen;
        Info($"[net] generation {_generation} activated (handler registration untouched)");
        return gen;
    }

    public INetBridgeLease Activate(in NetBridgeConfig config)
    {
        var lease = NewGeneration(out var view);
        if (lease is NetBridgeGeneration generation) generation.Reconfigure(config);
        else _cfg = Normalize(config);
        return lease;
    }

    internal void Release(uint generation)
    {
        if (generation != _generation)
        {
            Info($"[net:lease] stale release ignored gen={generation} active={_generation} (successor kept)");
            return;
        }
        _consumerActive = 0;
        _cfg.Enabled = false;
        Info($"[net:lease] generation {generation} released (registration stays installed)");
    }

    internal void SetConfig(uint generation, in NetBridgeConfig config)
    {
        if (generation != _generation)
        {
            LogLimited(LogLevel.Info, $"reconfig-{generation}",
                $"[net:lease] stale reconfigure ignored gen={generation} active={_generation}");
            return;
        }
        _cfg = Normalize(config);
        ResizeRing(_cfg.InboundCapacity);
    }

    internal bool IsCurrent(uint generation) => generation == _generation;

    internal uint CurrentGeneration => _generation;

    private static NetBridgeConfig Normalize(in NetBridgeConfig config)
    {
        var cap = config.InboundCapacity;
        if (cap <= 0) cap = NetBridgeConfig.Default.InboundCapacity;
        if (cap > 256) cap = 256;
        return new NetBridgeConfig
        {
            Enabled = config.Enabled,
            AutoEcho = config.AutoEcho,
            Verbose = config.Verbose,
            InboundCapacity = cap,
        };
    }

    private void ResizeRing(int capacity)
    {
        lock (_ringLock)
        {
            if (_ring != null && _ring.Length == capacity) return;
            _ring = new NetInboundFrame[capacity];
            _ringHead = 0;
            _ringCount = 0;
        }
    }

    // ---------------------------------------------------------------- status

    public NetBridgeStatus Status
    {
        get
        {
            var status = new NetBridgeStatus
            {
                State = _state,
                Caps = _caps,
                ApiVersionMajor = NetProtocol.Major,
                ApiVersionMinor = NetProtocol.Minor,
                SessionEpoch = _sessionEpoch,
                Generation = _generation,
                FramesSent = Interlocked.Read(ref _sent),
                FramesReceived = Interlocked.Read(ref _received),
                FramesDropped = Interlocked.Read(ref _dropped),
                FramesOversized = Interlocked.Read(ref _oversized),
                FramesMalformed = Interlocked.Read(ref _malformed),
                FramesVersionRejected = Interlocked.Read(ref _versionRejected),
                SequencesRejected = Interlocked.Read(ref _seqRejected),
                LastError = _lastError ?? _unsupportedReason,
            };
            lock (_ringLock)
            {
                status.InboundQueued = _ringCount;
                status.InboundCapacity = _ring?.Length ?? 0;
                status.PeerCount = _peers.Count;
            }
            var nm = _nm;
            if (nm != null)
            {
                try
                {
                    status.LocalClientId = nm.LocalClientId;
                    status.ServerClientId = NetworkManager.ServerClientId;
                    status.IsServer = nm.IsServer;
                    status.IsClient = nm.IsClient;
                    status.IsListening = nm.IsListening;
                }
                catch { }
            }
            return status;
        }
    }

    internal string Describe()
    {
        var s = Status;
        var ids = new StringBuilder();
        Peers(_scratchPeers);
        for (int i = 0; i < _scratchPeers.Count; i++)
        {
            if (i > 0) ids.Append(',');
            ids.Append(_scratchPeers[i]);
        }
        return $"state={s.State} caps={s.Caps} api={s.ApiVersionMajor}.{s.ApiVersionMinor} local={s.LocalClientId} " +
               $"server={s.ServerClientId} isServer={s.IsServer} isClient={s.IsClient} listening={s.IsListening} " +
               $"epoch={s.SessionEpoch} gen={s.Generation} peers=[{ids}] queued={s.InboundQueued}/{s.InboundCapacity} " +
               $"sent={s.FramesSent} recv={s.FramesReceived} drop={s.FramesDropped} oversized={s.FramesOversized} " +
               $"malformed={s.FramesMalformed} version={s.FramesVersionRejected} seq={s.SequencesRejected} " +
               $"lastError={s.LastError ?? "-"}";
    }

    private static readonly List<ulong> _scratchPeers = new List<ulong>();

    // ---------------------------------------------------------------- request file (host-only test path)

    private const string RequestName = "net.request";

    private void RequestFile()
    {
        var dir = HostPlugin.ScreensDir;
        if (string.IsNullOrEmpty(dir)) return;
        var path = Path.Combine(dir, RequestName);
        if (!File.Exists(path)) return;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return; }
        try { File.Delete(path); } catch { }

        foreach (var raw in lines)
        {
            var line = raw?.Trim() ?? "";
            if (line.Length == 0 || line[0] == '#') continue;
            try { Execute(line); }
            catch (Exception e) { Warn($"[net:request] '{line}' threw {e.GetType().Name}: {e.Message}"); }
        }
    }

    private static readonly List<ulong> _peerScratch = new List<ulong>();

    private void Execute(string line)
    {
        var parts = line.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        switch (cmd)
        {
            case "status":
                Info($"[net:status] {Describe()}");
                return;

            case "on":
                _cfg.Enabled = true;
                Info("[net:request] enabled");
                return;

            case "off":
                _cfg.Enabled = false;
                Info("[net:request] disabled");
                return;

            case "echo":
            {
                if (parts.Length < 2) { Warn("[net:request] echo needs 'all' or a client id"); return; }
                if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    var n = Peers(_peerScratch);
                    if (n == 0) { Warn("[net:request] echo all: no peers"); return; }
                    foreach (var peer in _peerScratch) Echo(peer);
                    return;
                }
                if (!ulong.TryParse(parts[1], out var target)) { Warn($"[net:request] bad client id '{parts[1]}'"); return; }
                if (!Echo(target)) Warn($"[net:request] echo to {target} refused (state={_state})");
                return;
            }

            case "app":
            {
                if (parts.Length < 3) { Warn("[net:request] app needs <clientId|all> <text>"); return; }
                var bytes = Encoding.UTF8.GetBytes(parts[2]);
                if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase)) Broadcast(NetKind.App, bytes);
                else if (ulong.TryParse(parts[1], out var target)) Send(NetKind.App, target, bytes);
                else Warn($"[net:request] bad client id '{parts[1]}'");
                return;
            }

            case "drain":
            {
                var frames = new List<NetInboundFrame>();
                var n = Drain(frames, 32);
                Info($"[net:request] drain n={n}");
                foreach (var f in frames)
                    Info($"[net:request] frame from={f.SenderClientId} kind={f.Kind} epoch={f.SessionEpoch} seq={f.Sequence} bytes={f.Payload?.Length ?? 0} hex={NetEnvelope.Hex(f.Payload ?? Array.Empty<byte>())}");
                return;
            }

            case "join":
                // Test scaffolding only (see LobbyJoinProbe): the shipped @JoinLobby automation
                // fills the Settings framerate field in this menu layout.
                if (parts.Length < 2) { Warn("[net:request] join needs a lobby code"); return; }
                Info($"[net:request] join {parts[1]} ok={LobbyJoinProbe.Join(parts[1])}");
                return;

            default:
                Warn($"[net:request] unknown command '{cmd}'");
                return;
        }
    }

    // ---------------------------------------------------------------- faults

    private static bool IsMissingApi(Exception e) =>
        e is TypeLoadException || e is DllNotFoundException || e is EntryPointNotFoundException ||
        e is MissingMethodException || e is MissingFieldException || e is FileNotFoundException ||
        e is BadImageFormatException || e is TypeInitializationException;

    private void Unsupported(Exception e)
    {
        if (_state == NetState.Unsupported) return;
        _state = NetState.Unsupported;
        _caps = NetCaps.None;
        _unsupportedReason = $"{e.GetType().Name}: {e.Message}";
        Warn($"[net:unsupported] Unity Netcode API unavailable ({_unsupportedReason}) - bridge idle for this session");
    }

    private void Fault(string phase, Exception e)
    {
        _lastError = $"{phase}: {e.GetType().Name}: {e.Message}";
        _faultStreak++;
        Warn($"[net:fault] phase={phase} n={_faultStreak} {e.GetType().Name}: {e.Message}");
        if (_faultStreak < 3) return;
        _state = NetState.Disabled;
        _caps = NetCaps.None;
        Warn($"[net:disabled] {_faultStreak} consecutive faults, bridge parked until the next module load");
    }

    // ---------------------------------------------------------------- per-generation view

    /// <summary>
    /// What a hot module sees. Every call is checked against the current generation, so a module
    /// that was swapped out can neither send, drain nor mute its replacement.
    /// </summary>
    private sealed class NetBridgeGeneration : INetBridge, INetBridgeLease
    {
        private readonly NetBridge _bridge;
        private readonly uint _generation;
        private int _disposed;

        internal NetBridgeGeneration(NetBridge bridge, uint generation)
        {
            _bridge = bridge;
            _generation = generation;
        }

        public uint Generation => _generation;
        public bool IsActive => _bridge.IsCurrent(_generation) && _disposed == 0;

        public INetBridgeLease Activate(in NetBridgeConfig config) => _bridge.Activate(config);

        public NetBridgeStatus Status => _bridge.Status;

        public bool TryTake(out NetInboundFrame frame)
        {
            if (!IsActive) { frame = default; return false; }
            return _bridge.TryTake(out frame);
        }

        public int Drain(List<NetInboundFrame> into, int max) => IsActive ? _bridge.Drain(into, max) : 0;

        public bool Send(NetKind kind, ulong targetClientId, ReadOnlySpan<byte> payload)
            => IsActive && _bridge.Send(kind, targetClientId, payload);

        public bool Broadcast(NetKind kind, ReadOnlySpan<byte> payload) => IsActive && _bridge.Broadcast(kind, payload);

        public bool Echo(ulong targetClientId) => IsActive && _bridge.Echo(targetClientId);

        public int Peers(List<ulong> into) => IsActive ? _bridge.Peers(into) : 0;

        public void ClearInbound()
        {
            if (IsActive) _bridge.ClearInbound();
        }

        public void Reconfigure(in NetBridgeConfig config) => _bridge.SetConfig(_generation, config);

        public void Dispose()
        {
            if (_disposed != 0) return;
            _disposed = 1;
            _bridge.Release(_generation);
        }
    }
}
