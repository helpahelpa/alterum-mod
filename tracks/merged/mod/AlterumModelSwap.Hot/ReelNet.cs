using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using HealthSystem;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

internal enum ReelAuthority
{
    /// <summary>No live session: the machine layout is this process's own file (solo play).</summary>
    Solo = 0,
    /// <summary>Netcode server: owns the machine layout and every settlement.</summary>
    Host = 1,
    /// <summary>Netcode client: renders the host's machines and only ever asks.</summary>
    Client = 2,
}

// Stage-3B: the Reel of Fortune is host-authoritative.
//
// Two things cross the wire and nothing else does:
//
//   placements  host -> clients   the machine anchors for the current map entry
//   request     client -> host    "I pulled machine M, I expect to be holding item H"
//   spin        host -> clients   an animation to play (deck seed + landing cell + timing)
//   settle      host -> clients   the result line, for the actor's own client to show
//   verdict     host -> client    accepted / settled / rejected(reason) for one requestId
//
// A client never sends a result, a grant, a damage, an effect or a placement. It sends exactly one
// InteractRequest and then plays what it is told. The host validates the request, runs the SAME
// spin/settle code the local path runs (ReelLogic.TryBeginPull -> ReelDeck -> ReelLogic.Resolve)
// against the requesting player, and broadcasts what happened.
//
// Identities: `sceneEpoch` is OUR counter for "this map entry" (bumped once per map generation on
// the host), never a Unity scene handle - handles are process-local and differ per client. The map
// key is the game's own map identity (`GameMapData|scene`) that ReelPlacements already stores
// placements under. A machine is named by the placement's own name ("lair-1"), which the host
// broadcasts, so both sides agree without either side inventing an id.
//
// Ledger: one entry per (client, requestId) on the host and per requestId on the client, with
// accepted -> stake consumed -> settled. A duplicate requestId returns the existing status instead
// of charging a second stake; a stale epoch, a wrong held item, a busy machine, an out-of-range or
// non-facing actor and the quest-item exclusions all reject with a reason and change nothing.
internal static class ReelNet
{
    internal const string Tag = "[reel:net]";

    // Message types inside a NetKind.App payload. 1-3 are RemoteModels' (hello/state/emote).
    internal const byte MsgSpin = 4;
    internal const byte MsgSettle = 5;
    internal const byte MsgInteract = 6;
    internal const byte MsgVerdict = 7;
    internal const byte MsgPlace = 8;
    internal const byte MsgPlaceReq = 9;

    internal const byte StatusPending = 0;
    internal const byte StatusAccepted = 1;
    internal const byte StatusSettled = 2;
    internal const byte StatusRejected = 3;

    internal const byte ReasonNone = 0;
    internal const byte ReasonWrongItem = 1;
    internal const byte ReasonOutOfRange = 2;
    internal const byte ReasonBusy = 3;
    internal const byte ReasonStaleEpoch = 4;
    internal const byte ReasonForbiddenItem = 5;
    internal const byte ReasonNoItem = 6;
    internal const byte ReasonIneligible = 7;
    internal const byte ReasonUnknownMachine = 8;
    internal const byte ReasonDuplicate = 9;
    internal const byte ReasonMalformed = 10;
    internal const byte ReasonInternal = 11;
    internal const byte ReasonNotHost = 12;

    private const int MaxMachineIdBytes = 32;
    private const int MaxHeldIdBytes = 48;
    private const int MaxTextBytes = 96;

    // Request validation. The distance matches the local path exactly; the facing test is the
    // body-forward dot against the direction to the sign face, looser than the 0.90 the camera
    // looker needs for the prompt (a player at the machine is not always staring at its centre).
    private const float RequestRange = 2.80f;
    private const float RequestFacingDot = 0.35f;
    // After a settle the machine is busy for a moment, so a queued request cannot ride the same
    // frame as the previous payout.
    private const float SettleCooldown = 0.75f;
    private const float LedgerSeconds = 60f;

    internal sealed class Machine
    {
        public string Id = "";
        public Vector3 Position;
        public float Yaw;
    }

    private sealed class Req
    {
        public uint RequestId;
        public ulong ClientId;
        public string MachineId = "";
        public byte Status;
        public byte Reason;
        public uint SpinId;
        public string Text = "";
        public float At;
        public bool Shown;         // the client already showed this result / refusal
    }

    private static IHostContext _log;
    private static INetBridge _net;
    private static bool _live, _isServer;
    private static ulong _localId, _serverId;
    private static string _role = "off";

    // ---- host: the layout it owns and publishes
    private static string _pubMap = "";
    private static string _pubGeneration = "";
    private static uint _pubEpoch;
    private static uint _pubRev;
    private static readonly List<Machine> _pubMachines = new List<Machine>();
    private static readonly HashSet<uint> _settleSent = new HashSet<uint>();
    // spinId -> machine id, remembered when a spin frame arrives. The booth forgets its spin id the
    // moment its own strip stops (LobbyProp.StepSpin resets it), and a settle frame normally lands
    // just after that, so resolving the outcome cue by spin id alone would be silent exactly when
    // the result is on screen.
    private static readonly Dictionary<uint, string> _spinMachine = new Dictionary<uint, string>();
    private static uint _nextSpinId;
    private static string _forceNextOutcome = "";

    // ---- client: the layout it was given
    private static string _hostMap = "";
    private static uint _hostEpoch, _hostRev;
    private static readonly List<Machine> _hostMachines = new List<Machine>();
    private static bool _hostReady;
    private static string _localMap = "";
    private static uint _nextRequestId;

    private static readonly Dictionary<string, Req> _ledger = new Dictionary<string, Req>();
    private static float _nextPlaceReq;
    private static int _placeReqs;
    private static float _nextPrune;
    private static int _rejects, _dups, _forged, _spinsStarted, _settles;

    internal static ReelAuthority Authority
    {
        get
        {
            if (!_live) return ReelAuthority.Solo;
            return _isServer ? ReelAuthority.Host : ReelAuthority.Client;
        }
    }

    internal static bool IsClient => Authority == ReelAuthority.Client;
    internal static ulong LocalId => _localId;
    // Client 0 IS the host in this game's Netcode session, so "no server" cannot be tested with
    // `_serverId == 0`; a client always has the server id Netcode reports.
    private static bool ServerKnown => _live && !_isServer;
    internal static uint PublishedEpoch => _pubEpoch;
    internal static uint HostEpoch => _hostEpoch;

    internal static string StatusText() =>
        $"role={_role} live={_live} server={_isServer} local={_localId} hostSnap={( _hostReady ? _hostMap + "#" + _hostRev : "-")} " +
        $"pub='{_pubMap}' epoch={_pubEpoch} rev={_pubRev} machines={_pubMachines.Count} " +
        $"ledger={_ledger.Count} spins={_spinsStarted} settles={_settles} rejects={_rejects} dups={_dups} forged={_forged}";

    // ================================================================== session

    // Called once per RemoteModels poll. The role and the ids are not final when the handler
    // registers, so this is a cheap re-read rather than a one-time init.
    internal static void Bind(IHostContext log, INetBridge net, bool live, bool isServer, ulong localId, ulong serverId)
    {
        if (log != null) _log = log;
        if (net != null) _net = net;
        string role = !live ? "off" : (isServer ? "host" : "client");
        bool roleChanged = role != _role || localId != _localId;
        bool cameAlive = live && !_live;
        _live = live;
        _isServer = isServer;
        _localId = localId;
        _serverId = serverId;
        _role = role;
        if (!roleChanged && !cameAlive) return;
        try
        {
            log?.LogInfo($"{Tag} authority={role} local={localId} server={serverId} epoch={_pubEpoch} " +
                         $"machines={(isServer ? _pubMachines.Count : _hostMachines.Count)} " +
                         $"{(isServer ? "host owns placements and settlement" : live ? "host placements and settlement are authoritative" : "solo: local placements file")}");
        }
        catch { }
        if (!live)
        {
            _hostMachines.Clear();
            _hostReady = false;
            _hostMap = "";
        }
    }

    internal static void Tick(IHostContext log)
    {
        if (log != null) _log = log;
        float now = Now();
        if (now < _nextPrune) return;
        _nextPrune = now + 10f;
        if (_ledger.Count == 0) return;
        var drop = new List<string>();
        foreach (var kv in _ledger) if (now - kv.Value.At > LedgerSeconds) drop.Add(kv.Key);
        for (int i = 0; i < drop.Count; i++) _ledger.Remove(drop[i]);
    }

    private static float Now()
    {
        try { return Time.unscaledTime; } catch { return 0f; }
    }

    // ================================================================== placements (host)

    // Called by ReelPlacements once it has settled the booths for a map generation. The first call
    // for a generation bumps the wire epoch (a new map entry) and publishes; a later call on the
    // same generation is a revision (a hot reload re-spawn), which bumps the revision only.
    internal static void PublishPlacements(IHostContext log, string mapKey, string generation, List<LobbyPropRuntime> booths)
    {
        if (log != null) _log = log;
        if (!_isServer) return;
        var machines = new List<Machine>(booths == null ? 0 : booths.Count);
        if (booths != null)
        {
            for (int i = 0; i < booths.Count; i++)
            {
                var b = booths[i];
                if (b == null || b.Root == null) continue;
                machines.Add(new Machine { Id = string.IsNullOrEmpty(b.MachineId) ? ("machine-" + (i + 1)) : b.MachineId,
                                           Position = b.Anchor, Yaw = b.Yaw });
            }
        }
        bool newGeneration = generation != _pubGeneration;
        string changed = MachineSignature(machines);
        if (!newGeneration && changed == MachineSignature(_pubMachines) && _pubEpoch != 0) return;

        if (newGeneration) { _pubGeneration = generation ?? ""; _pubEpoch = NextEpoch(_pubEpoch); _pubRev = 1; }
        else _pubRev++;
        _pubMap = mapKey ?? "";
        _pubMachines.Clear();
        _pubMachines.AddRange(machines);

        var sb = new StringBuilder();
        for (int i = 0; i < _pubMachines.Count; i++)
            sb.Append(_pubMachines[i].Id).Append('@').Append(V(_pubMachines[i].Position)).Append(' ');
        log?.LogInfo($"{Tag} publish map='{_pubMap}' epoch={_pubEpoch} rev={_pubRev} machines={_pubMachines.Count} " +
                     $"generation='{_pubGeneration}' [{sb}] -> {(newGeneration ? "new map entry" : "revision")}");
        BroadcastSnapshot(log, "publish");
    }

    private static uint NextEpoch(uint previous) => previous == 0 ? 1 : previous + 1;

    private static string MachineSignature(List<Machine> list)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
            sb.Append(list[i].Id).Append('|').Append(list[i].Position.x.ToString("F3")).Append(',')
              .Append(list[i].Position.y.ToString("F3")).Append(',').Append(list[i].Position.z.ToString("F3"))
              .Append('|').Append(list[i].Yaw.ToString("F2")).Append(';');
        return sb.ToString();
    }

    // Late join: RemoteModels calls this the moment a peer answers the handshake, so a client that
    // enters a map mid-run has the current anchors (and current epoch/revision) at once.
    internal static void OnPeerMod(IHostContext log, ulong peerId)
    {
        if (log != null) _log = log;
        if (!_isServer) return;
        if (_pubEpoch == 0 || _pubMachines.Count == 0)
        {
            log?.LogInfo($"{Tag} late-join client={peerId}: no placements published yet (epoch={_pubEpoch}); " +
                         "the next map entry will send them");
            return;
        }
        SendSnapshot(log, peerId, "late-join");
    }

    private static void BroadcastSnapshot(IHostContext log, string why)
    {
        if (_net == null) return;
        int peers = RemoteModels.SendToModPeers(log, _net, BuildPlace(_pubEpoch, _pubRev, _pubMap, _pubMachines), $"place{_pubRev}", 0);
        if (peers > 0) log?.LogInfo($"{Tag} placements -> {peers} mod peer(s) ({why}) epochs={_pubEpoch} rev={_pubRev}");
    }

    private static void SendSnapshot(IHostContext log, ulong peerId, string why)
    {
        if (_net == null || peerId == 0) return;
        var payload = BuildPlace(_pubEpoch, _pubRev, _pubMap, _pubMachines);
        bool ok = false;
        try { ok = _net.Send(NetKind.App, peerId, payload); } catch { }
        log?.LogInfo($"{Tag} placements -> client={peerId} ({why}) ok={ok} epoch={_pubEpoch} rev={_pubRev} " +
                     $"machines={_pubMachines.Count} bytes={payload.Length}");
    }

    // ================================================================== placements (client)

    internal static bool TryHostPlacements(string mapKey, out uint epoch, out uint rev, out List<Machine> machines)
    {
        epoch = _hostEpoch;
        rev = _hostRev;
        machines = _hostMachines;
        if (!_hostReady) return false;
        if (!string.IsNullOrEmpty(mapKey) && !string.IsNullOrEmpty(_hostMap) && mapKey != _hostMap) return false;
        return _hostMachines.Count > 0;
    }

    internal static void NoteLocalMap(string mapKey) => _localMap = mapKey ?? "";

    // A client that reloaded (or joined) mid-map and has no layout yet asks the host for one. Bounded:
    // ten asks, then it waits for the host's next map entry and says so once.
    internal static void RequestSnapshot(IHostContext log)
    {
        if (!IsClient || _net == null || !ServerKnown) return;
        float now = Now();
        if (now < _nextPlaceReq) return;
        _nextPlaceReq = now + 2f;
        if (_placeReqs >= 10)
        {
            if (_placeReqs == 10) { _placeReqs++; log?.LogInfo($"{Tag} no placements snapshot after 10 asks; waiting for the host's next map entry"); }
            return;
        }
        _placeReqs++;
        var p = new byte[1];
        p[0] = MsgPlaceReq;
        bool ok = false;
        try { ok = _net.Send(NetKind.App, _serverId, p); } catch { }
        log?.LogInfo($"{Tag} asked the host for the placements snapshot ok={ok} ask={_placeReqs} -> server={_serverId}");
    }

    // A new map entry on the client resets the ask budget, so the next map is asked for again.
    internal static void ResetSnapshotBudget() => _placeReqs = 0;

    // ================================================================== inbound

    // Returns true when the frame was a reel-authority message (RemoteModels then stops dispatching
    // it). Reel frames are never relayed: the host sends spin/settle/place to everyone itself, and a
    // client can only address the server, which is where a forged frame is dropped.
    internal static bool HandleFrame(IHostContext log, in NetInboundFrame frame, float now)
    {
        if (log != null) _log = log;
        var payload = frame.Payload;
        if (payload == null || payload.Length < 1) return false;
        byte type = payload[0];
        if (type < MsgSpin || type > MsgPlaceReq) return false;

        if (type == MsgPlaceReq)
        {
            if (_isServer) SendSnapshot(log, frame.SenderClientId, "requested");
            return true;
        }

        if (type == MsgInteract)
        {
            if (!_isServer)
            {
                log?.LogWarning($"{Tag} interact from={frame.SenderClientId} arrived at a CLIENT; ignored " +
                                "(requests are only valid at the host)");
                return true;
            }
            HandleInteract(log, frame.SenderClientId, payload, now);
            return true;
        }

        if (type == MsgVerdict)
        {
            if (_isServer)
            {
                _forged++;
                log?.LogWarning($"{Tag} IGNORED verdict from client={frame.SenderClientId}: the host sends verdicts");
            }
            else if (frame.SenderClientId != _serverId)
            {
                _forged++;
                log?.LogWarning($"{Tag} IGNORED verdict from client={frame.SenderClientId} (not the server)");
            }
            else HandleVerdict(log, payload);
            return true;
        }

        // Spin / settle / place are the HOST's to send. A client that sends one is forging a result;
        // a frame that is not from the host at a client is not the authority either.
        string what = type == MsgSpin ? "spin" : type == MsgSettle ? "settle" : "placements";
        if (_isServer)
        {
            _forged++;
            log?.LogWarning($"{Tag} IGNORED forged {what} from client={frame.SenderClientId} bytes={payload.Length}: " +
                            "only the host settles (nothing applied)");
            return true;
        }
        if (frame.SenderClientId != _serverId)
        {
            _forged++;
            log?.LogWarning($"{Tag} IGNORED {what} from client={frame.SenderClientId} (not the server {_serverId}): " +
                            "only the host is the authority");
            return true;
        }
        try
        {
            if (type == MsgSpin) HandleSpin(log, payload);
            else if (type == MsgSettle) HandleSettle(log, payload);
            else HandlePlace(log, payload);
        }
        catch (Exception e) { log.LogWarning($"{Tag} {what} frame threw: {e.Message}"); }
        return true;
    }

    private static void HandlePlace(IHostContext log, byte[] payload)
    {
        int at = 1;
        if (!U32(payload, ref at, out uint epoch) || !U32(payload, ref at, out uint rev) || !Str(payload, ref at, out string map))
        {
            log.LogWarning($"{Tag} placements short/unreadable bytes={payload.Length}");
            return;
        }
        if (!U8(payload, ref at, out byte count)) { log.LogWarning($"{Tag} placements truncated count"); return; }
        var list = new List<Machine>(count);
        for (int i = 0; i < count; i++)
        {
            if (!Str(payload, ref at, out string id) || !F32(payload, ref at, out float x) ||
                !F32(payload, ref at, out float y) || !F32(payload, ref at, out float z) ||
                !F32(payload, ref at, out float yaw))
            {
                log.LogWarning($"{Tag} placements truncated at machine {i}/{count}");
                return;
            }
            list.Add(new Machine { Id = id, Position = new Vector3(x, y, z), Yaw = yaw });
        }
        bool mapChanged = map != _hostMap;
        bool revised = epoch != _hostEpoch || rev != _hostRev;
        _hostMap = map;
        _hostEpoch = epoch;
        _hostRev = rev;
        _hostMachines.Clear();
        _hostMachines.AddRange(list);
        _hostReady = true;
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++) sb.Append(list[i].Id).Append('@').Append(V(list[i].Position)).Append(' ');
        log.LogInfo($"{Tag} placements from host map='{map}' epoch={epoch} rev={rev} machines={list.Count} [{sb}] " +
                    $"source=host ({(mapChanged ? "map entry" : revised ? "revision" : "repeat")})");
    }

    private static void HandleSpin(IHostContext log, byte[] payload)
    {
        int at = 1;
        if (!U32(payload, ref at, out uint spinId) || !U64(payload, ref at, out ulong actor) ||
            !F64(payload, ref at, out double startAt) || !F32(payload, ref at, out float duration) ||
            !U16(payload, ref at, out int fromCell) || !U16(payload, ref at, out int targetCell) ||
            !U32(payload, ref at, out uint composeSeed) || !U32(payload, ref at, out uint shuffleSeed) ||
            !U8(payload, ref at, out byte mask) ||
            !U8(payload, ref at, out byte c0) || !U8(payload, ref at, out byte c1) || !U8(payload, ref at, out byte c2) ||
            !U8(payload, ref at, out byte c3) || !U8(payload, ref at, out byte c4) ||
            !Str(payload, ref at, out string machine))
        {
            log.LogWarning($"{Tag} spin short/unreadable bytes={payload.Length}");
            return;
        }
        // The ordered deck rides after the machine id (the tail of the frame), so a peer that predates
        // it still parses every field it knows and simply does not find one.
        ReelCard[] deck = null;
        if (at < payload.Length)
        {
            if (!ReelCanonical.TryRead(payload, at, ReelDeck.Cells, out deck))
            {
                log.LogWarning($"{Tag} spin {spinId} deck frame unreadable bytes={payload.Length} at={at}; " +
                               $"falling back to a local rebuild from seeds={composeSeed}/{shuffleSeed}");
                deck = null;
            }
        }
        var booth = ReelPlacements.FindBooth(machine);
        if (booth == null)
        {
            log.LogWarning($"{Tag} spin {spinId} machine='{machine}': no booth with that id on this client; ignored");
            return;
        }
        double age = RemoteModels.Clock() - startAt;
        if (!RemoteModels.ClockIsServer && (age < 0 || age > 6)) age = 0;
        _spinsStarted++;
        log.LogInfo($"{Tag} spin {spinId} actor={actor} machine='{machine}' age={age:F3}s dur={duration:F2}s " +
                    $"cells={fromCell}->{targetCell} seeds={composeSeed}/{shuffleSeed} mask=0x{mask:x2} " +
                    $"hostCounts=[blank={c4} item={c0} damage={c1} debuff={c2} double={c3}] " +
                    $"hostDeck={(deck == null ? "not sent (old host)" : $"{deck.Length} cards sig=0x{ReelCanonical.Signature(deck):x8}")}");
        booth.RemoteSpin(log, spinId, actor, (float)Math.Max(0.0, age), duration, targetCell,
                         (int)composeSeed, (int)shuffleSeed, mask,
                         new[] { (int)c0, (int)c1, (int)c2, (int)c3, (int)c4 }, deck);
        // Kept for the settle frame's cue (see the field): the booth will have dropped the id by then.
        if (_spinMachine.Count > 256) _spinMachine.Clear();
        _spinMachine[spinId] = machine;
    }

    private static void HandleSettle(IHostContext log, byte[] payload)
    {
        int at = 1;
        if (!U32(payload, ref at, out uint spinId) || !U64(payload, ref at, out ulong actor) ||
            !U8(payload, ref at, out byte kind) || !Str(payload, ref at, out string item) ||
            !U8(payload, ref at, out byte count) || !I16(payload, ref at, out int hp) ||
            !Str(payload, ref at, out string text))
        {
            log.LogWarning($"{Tag} settle short/unreadable bytes={payload.Length}");
            return;
        }
        _settles++;
        bool mine = actor == _localId;
        string word = kind == (byte)ReelCardType.Item ? "item" : kind == (byte)ReelCardType.Double ? "double"
                    : kind == (byte)ReelCardType.Damage ? "damage" : kind == (byte)ReelCardType.Debuff ? "debuff" : "blank";
        log.LogInfo($"{Tag} settle spin={spinId} actor={actor}{(mine ? " (you)" : "")} type={word} " +
                    $"item='{item}' n={count} hpLost={hp} text='{text}' " +
                    $"{(mine ? "" : "-> shown only to the actor")}");
        var booth = ReelPlacements.BoothForSpin(spinId);
        // The outcome cue, at the machine that turned: the host played (or is playing) the same cue
        // from ReelLogic.Resolve, from the one card-type mapping, so both peers hear the card they
        // can see come to rest. Played on receipt rather than at this peer's own strip stop: the
        // settle frame IS the host's stop moment, so the two peers stay together. Positioned by
        // MACHINE (with the spin id as the first choice), because this peer's booth may already
        // have released the id.
        if (ReelAudio.TryOutcomeCue(kind, out var outcomeCue))
        {
            var cueBooth = booth;
            string cueMachine;
            if (cueBooth == null && _spinMachine.TryGetValue(spinId, out cueMachine))
                cueBooth = ReelPlacements.FindBooth(cueMachine);
            if (cueBooth != null) ReelAudio.Cue(log, outcomeCue, cueBooth.LookCentre);
        }
        if (booth != null) booth.RemoteSettle(log, spinId, mine, text);
        else if (mine) ReelHud.Result(log, text);
    }

    // ================================================================== request (client)

    // The single frame a client may send about the machine. `why` is only for the log ("E",
    // "request", "dup", "stale", "wrongitem").
    internal static bool Request(IHostContext log, string machineId, string why)
    {
        if (log != null) _log = log;
        if (Authority != ReelAuthority.Client)
        {
            log?.LogInfo($"{Tag} request ignored: this instance is the {_role} (authority={Authority})");
            return false;
        }
        if (_net == null || !ServerKnown) { log?.LogWarning($"{Tag} request: no server to address"); return false; }
        if (_hostEpoch == 0) { log?.LogWarning($"{Tag} request: no host placements yet (epoch=0); not sending"); return false; }

        var player = ReelLogic.FindLocalPlayerEntity();
        string held = ReelLogic.HeldItemId(player);
        uint requestId = ++_nextRequestId;
        uint epoch = _hostEpoch;
        string machine = machineId ?? "";
        byte[] payload = BuildInteract(requestId, epoch, machine, held);
        bool ok = false;
        try { ok = _net.Send(NetKind.App, _serverId, payload); } catch (Exception e) { log?.LogWarning($"{Tag} request send failed: {e.Message}"); }
        _ledger[Key(_localId, requestId)] = new Req
        {
            RequestId = requestId, ClientId = _localId, MachineId = machine, Status = StatusPending, At = Now(),
            Text = "",
        };
        if (ok)
            log?.LogInfo($"{Tag} request id={requestId} machine='{machine}' held='{held}' epoch={epoch} " +
                         $"actor={_localId} why={why} bytes={payload.Length} -> server={_serverId}");
        else
            log?.LogWarning($"{Tag} request id={requestId} machine='{machine}' NOT sent (bridge refused)");
        return ok;
    }

    // Test hooks that need to forge or duplicate a request on purpose. Same encoder as Request, so a
    // "duplicate" is byte-identical to what a double press would send.
    internal static void RequestVariant(IHostContext log, string machineId, string variant)
    {
        if (log != null) _log = log;
        if (Authority != ReelAuthority.Client) { Request(log, machineId, variant); return; }
        if (_net == null || !ServerKnown) return;
        var player = ReelLogic.FindLocalPlayerEntity();
        string held = ReelLogic.HeldItemId(player);
        uint requestId = ++_nextRequestId;
        uint epoch = _hostEpoch;
        switch (variant)
        {
            case "dup":
                {
                    var payload = BuildInteract(requestId, epoch, machineId, held);
                    bool a = false, b = false;
                    try { a = _net.Send(NetKind.App, _serverId, payload); b = _net.Send(NetKind.App, _serverId, payload); } catch { }
                    _ledger[Key(_localId, requestId)] = new Req { RequestId = requestId, ClientId = _localId, MachineId = machineId, At = Now() };
                    log?.LogInfo($"{Tag} request id={requestId} machine='{machineId}' sent TWICE " +
                                 $"(same bytes, same id) ok={a}/{b} -> server={_serverId}");
                    return;
                }
            case "stale":
                {
                    uint staleEpoch = epoch > 1 ? epoch - 1 : 0;
                    var payload = BuildInteract(requestId, staleEpoch, machineId, held);
                    bool ok = false;
                    try { ok = _net.Send(NetKind.App, _serverId, payload); } catch { }
                    log?.LogInfo($"{Tag} request id={requestId} machine='{machineId}' with STALE epoch={staleEpoch} " +
                                 $"(current {epoch}) ok={ok}");
                    return;
                }
            case "wrongitem":
                {
                    string wrong = string.Equals(held, "torch", StringComparison.OrdinalIgnoreCase) ? "healthflask" : "torch";
                    var payload = BuildInteract(requestId, epoch, machineId, wrong);
                    bool ok = false;
                    try { ok = _net.Send(NetKind.App, _serverId, payload); } catch { }
                    log?.LogInfo($"{Tag} request id={requestId} machine='{machineId}' with WRONG held='{wrong}' " +
                                 $"(really holding '{held}') ok={ok}");
                    return;
                }
            case "forge":
                {
                    // A forged settlement: a client claiming an outcome. The host must ignore it.
                    var fake = BuildSettle(9001 + requestId, _localId, (byte)ReelCardType.Item, "healthflask", 99, 0,
                                           "FORGED by a client");
                    bool ok = false;
                    try { ok = _net.Send(NetKind.App, _serverId, fake); } catch { }
                    log?.LogInfo($"{Tag} FORGED settle spin={9001 + requestId} from client={_localId} sent ok={ok} " +
                                 "(the host must refuse it and apply nothing)");
                    return;
                }
            default:
                log?.LogWarning($"{Tag} request variant '{variant}' is not dup/stale/wrongitem/forge");
                Request(log, machineId, variant);
                return;
        }
    }

    private static string Key(ulong clientId, uint requestId) => clientId.ToString() + ":" + requestId;

    // The audio origin for anything about `machineId`: that machine's sign face on THIS peer. False
    // when this peer has no such booth (a map transition, an id that predates this generation), so
    // the caller stays silent instead of playing a cue from nowhere.
    private static bool BoothOrigin(string machineId, out Vector3 at)
    {
        at = default(Vector3);
        if (string.IsNullOrEmpty(machineId)) return false;
        try
        {
            var booth = ReelPlacements.FindBooth(machineId);
            if (booth == null) return false;
            at = booth.LookCentre;
            return ReelAudio.Positional(at);
        }
        catch { return false; }
    }

    // ================================================================== host validation + settle

    private static void HandleInteract(IHostContext log, ulong sender, byte[] payload, float now)
    {
        int at = 1;
        if (!U32(payload, ref at, out uint requestId) || !U32(payload, ref at, out uint epoch) ||
            !Str(payload, ref at, out string machine) || !Str(payload, ref at, out string expectedHeld))
        {
            log.LogWarning($"{Tag} interact from={sender} malformed bytes={payload.Length}");
            SendVerdict(log, sender, 0, StatusRejected, ReasonMalformed, 0, "Malformed request");
            _rejects++;
            return;
        }

        string key = Key(sender, requestId);
        if (_ledger.TryGetValue(key, out var existing) && existing.Status != StatusPending)
        {
            _dups++;
            log.LogInfo($"{Tag} duplicate request id={requestId} from client={sender} machine='{machine}' -> " +
                        $"existing status={Word(existing.Status)} reason={ReasonWord(existing.Reason)} spin={existing.SpinId} " +
                        "(no second stake)");
            SendVerdict(log, sender, requestId, existing.Status, ReasonDuplicate, existing.SpinId, existing.Text);
            return;
        }

        string reject = "";
        byte reason = ReasonNone;
        ReelLogic.Pull pull = null;
        LobbyPropRuntime booth = null;
        Player actor = null;
        float dist = -1f, facing = -2f;

        if (epoch != _pubEpoch || _pubEpoch == 0)
        {
            reject = "Stale map"; reason = ReasonStaleEpoch;
            log.LogInfo($"{Tag} reject id={requestId} client={sender} reason=stale-epoch request={epoch} current={_pubEpoch}");
        }
        else
        {
            booth = ReelPlacements.FindBooth(machine);
            if (booth == null)
            {
                reject = "No such machine"; reason = ReasonUnknownMachine;
                log.LogInfo($"{Tag} reject id={requestId} client={sender} reason=unknown-machine machine='{machine}' " +
                            $"epoch={epoch} known=[{ReelPlacements.BoothIds()}]");
            }
        }

        if (reason == ReasonNone)
        {
            actor = FindActor(sender);
            if (actor == null)
            {
                reject = "No player"; reason = ReasonIneligible;
                log.LogInfo($"{Tag} reject id={requestId} client={sender} reason=no-actor (no Player with " +
                            $"OwnerClientId={sender} in this scene)");
            }
        }

        if (reason == ReasonNone)
        {
            var health = ReelLogic.FindHealth(actor);
            if (health != null && health.IsDead)
            {
                reject = "You are down"; reason = ReasonIneligible;
                log.LogInfo($"{Tag} reject id={requestId} client={sender} reason=dead hp={health.Value:F1}");
            }
        }

        if (reason == ReasonNone)
        {
            if (!ReelLogic.TryGetPose(actor, out Vector3 pos, out Vector3 fwd, out string poseSource))
            {
                reject = "No position"; reason = ReasonIneligible;
            }
            else
            {
                Vector3 to = booth.LookCentre - pos;
                dist = to.magnitude;
                facing = dist > 0.001f ? Vector3.Dot(fwd, to / dist) : 1f;
                if (dist > RequestRange)
                {
                    reject = "Too far"; reason = ReasonOutOfRange;
                    log.LogInfo($"{Tag} reject id={requestId} client={sender} reason=out-of-range dist={dist:F2} " +
                                $"limit={RequestRange:F2} machine='{machine}' pose={poseSource} at={V(pos)} " +
                                $"look={V(booth.LookCentre)}");
                }
                else if (facing < RequestFacingDot)
                {
                    reject = "Not facing it"; reason = ReasonOutOfRange;
                    log.LogInfo($"{Tag} reject id={requestId} client={sender} reason=not-facing dot={facing:F3} " +
                                $"limit={RequestFacingDot:F2} dist={dist:F2} machine='{machine}' pose={poseSource} " +
                                $"at={V(pos)} look={V(booth.LookCentre)}");
                }
                else
                {
                    log.LogInfo($"{Tag} range ok id={requestId} client={sender} machine='{machine}' dist={dist:F2} " +
                                $"facing={facing:F3} pose={poseSource} at={V(pos)} look={V(booth.LookCentre)}");
                }
            }
        }

        if (reason == ReasonNone && booth != null && booth.SpinBusy)
        {
            reject = "Busy"; reason = ReasonBusy;
            log.LogInfo($"{Tag} reject id={requestId} client={sender} reason=busy machine='{machine}' " +
                        $"(cooldown/another spin)");
        }

        if (reason == ReasonNone)
        {
            string held = ReelLogic.HeldItemId(actor);
            if (held.Length == 0)
            {
                reject = "You need an item to bet"; reason = ReasonNoItem;
                log.LogInfo($"{Tag} reject id={requestId} client={sender} reason=no-held-item");
            }
            else if (!string.Equals(held, expectedHeld, StringComparison.OrdinalIgnoreCase))
            {
                reject = "Item changed"; reason = ReasonWrongItem;
                log.LogInfo($"{Tag} reject id={requestId} client={sender} reason=wrong-item expected='{expectedHeld}' " +
                            $"actual='{held}'");
            }
            else
            {
                string refusal = ReelExclusions.ReasonFor(held, ReelLogic.DisplayNameOf(held));
                if (refusal.Length > 0)
                {
                    reject = $"Not the {ReelLogic.DisplayNameOf(held)}: quest item";
                    reason = ReasonForbiddenItem;
                    log.LogInfo($"{Tag} reject id={requestId} client={sender} reason={refusal} held='{held}' " +
                                "consumed=0");
                }
            }
        }

        if (reason == ReasonNone)
        {
            if (!ReelLogic.TryBeginPull(log, actor, booth.LookCentre, out pull))
            {
                reject = "Could not take the stake"; reason = ReasonInternal;
                log.LogWarning($"{Tag} reject id={requestId} client={sender} reason=consume-failed machine='{machine}'");
            }
            else
            {
                log.LogInfo($"{Tag} ACCEPT id={requestId} client={sender} machine='{machine}' held='{pull.HeldId}' " +
                            $"consumed=1 dist={dist:F2} facing={facing:F3} " +
                            "(validated: epoch, machine, actor, distance, facing, busy, held item, exclusions)");
            }
        }

        if (reason != ReasonNone)
        {
            _rejects++;
            // A refusal is a cue from the machine that refused, on the host, exactly like the
            // requesting client's own refusal cue from the verdict below. The origin is resolved
            // from the machine id, not from the (possibly never found) validated booth, so a stale
            // epoch or an unknown id still points at the right sign face when this peer has it.
            Vector3 rejectAt;
            if (BoothOrigin(machine, out rejectAt)) ReelAudio.Cue(log, ReelCue.Error, rejectAt);
            var rej = new Req { RequestId = requestId, ClientId = sender, MachineId = machine,
                                Status = StatusRejected, Reason = reason, At = now, Text = reject };
            _ledger[key] = rej;
            SendVerdict(log, sender, requestId, StatusRejected, reason, 0, reject);
            return;
        }

        uint spinId = ++_nextSpinId;
        var rec = new Req { RequestId = requestId, ClientId = sender, MachineId = machine,
                            Status = StatusAccepted, Reason = ReasonNone, SpinId = spinId, At = now };
        _ledger[key] = rec;
        SendVerdict(log, sender, requestId, StatusAccepted, ReasonNone, spinId, "");
        log.LogInfo($"{Tag} spin {spinId} accepted for client={sender} request={requestId} machine='{machine}' " +
                    "-> running the host's own spin/settle against that player");

        string forced = _forceNextOutcome;
        _forceNextOutcome = "";
        if (forced.Length > 0) log.LogInfo($"{Tag} forced outcome='{forced}' consumed by request {requestId}");
        booth.StartRequestSpin(log, pull, requestId, spinId, sender, forced);
    }

    private static Player FindActor(ulong clientId)
    {
        try
        {
            if (clientId == _localId) return ReelLogic.FindLocalPlayerEntity();
            var players = SceneCache.Scan(Il2CppType.Of<Player>());
            for (int i = 0; players != null && i < players.Length; i++)
            {
                var p = players[i].TryCast<Player>();
                if (p == null || p.transform == null) continue;
                var go = p.gameObject;
                if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
                if (p.OwnerClientId != clientId) continue;
                return p;
            }
        }
        catch { }
        return null;
    }

    // Called by the booth when its spin settles, exactly once per spin id.
    internal static void OnHostSettled(IHostContext log, uint spinId, uint requestId, ulong actor, ReelLogic.Outcome o)
    {
        if (log != null) _log = log;
        if (!_isServer || spinId == 0 || _settleSent.Contains(spinId)) return;
        _settleSent.Add(spinId);
        if (_settleSent.Count > 256) _settleSent.Clear();

        var payload = BuildSettle(spinId, actor, o.Kind, o.ItemId ?? "", o.Count, o.HpLost, o.Text ?? "");
        int peers = RemoteModels.SendToModPeers(log, _net, payload, $"settle{spinId}", 0);
        log.LogInfo($"{Tag} settle spin={spinId} actor={actor} type={o.Kind} item='{o.ItemId}' n={o.Count} " +
                    $"hpLost={o.HpLost} text='{o.Text}' -> {peers} peer(s) bytes={payload.Length}");
        if (requestId != 0)
        {
            string key = Key(actor, requestId);
            if (_ledger.TryGetValue(key, out var rec))
            {
                rec.Status = StatusSettled;
                rec.Text = o.Text ?? "";
                rec.SpinId = spinId;
            }
            SendVerdict(log, actor, requestId, StatusSettled, ReasonNone, spinId, o.Text ?? "");
        }
    }

    internal static uint NextSpinId() => ++_nextSpinId;

    // The booth asks for an id before it starts a HOST-LOCAL spin (E on the host), so a pull by the
    // host is broadcast and animates on the clients exactly like a request.
    internal static bool Broadcasting => _live && _isServer;

    internal static void BroadcastSpin(IHostContext log, uint spinId, ulong actor, string machineId, double startAt,
                                       float duration, int fromCell, int targetCell, int composeSeed, int shuffleSeed,
                                       int mask, int[] counts, ReelCard[] deck)
    {
        if (!_isServer || _net == null || spinId == 0) return;
        var payload = BuildSpin(spinId, actor, startAt, duration, fromCell, targetCell, composeSeed, shuffleSeed,
                                mask, counts, machineId, deck);
        int peers = RemoteModels.SendToModPeers(log, _net, payload, $"spin{spinId}", 0);
        if (peers > 0)
            log.LogInfo($"{Tag} spin {spinId} broadcast actor={actor} machine='{machineId}' cells={fromCell}->{targetCell} " +
                        $"seeds={composeSeed}/{shuffleSeed} mask=0x{mask:x2} " +
                        $"deck={(deck == null ? "none" : $"{deck.Length} cards sig=0x{ReelCanonical.Signature(deck):x8}")} " +
                        $"-> {peers} peer(s) bytes={payload.Length}");
    }

    // Re-send the live machine list with a bumped revision (the host moved something).
    internal static void Republish(IHostContext log)
    {
        if (!_isServer) return;
        _pubRev++;
        var booths = ReelPlacements.AutoBooths();
        _pubMachines.Clear();
        for (int i = 0; i < booths.Count; i++)
            _pubMachines.Add(new Machine { Id = booths[i].MachineId, Position = booths[i].Anchor, Yaw = booths[i].Yaw });
        var sb = new StringBuilder();
        for (int i = 0; i < _pubMachines.Count; i++) sb.Append(_pubMachines[i].Id).Append('@').Append(V(_pubMachines[i].Position)).Append(' ');
        log?.LogInfo($"{Tag} republish map='{_pubMap}' epoch={_pubEpoch} rev={_pubRev} [{sb}]");
        BroadcastSnapshot(log, "revision");
    }

    // Test hook: force the outcome of the NEXT request settled on this (host) instance.
    internal static void ForceNext(IHostContext log, string outcome)
    {
        _forceNextOutcome = outcome ?? "";
        log?.LogInfo($"{Tag} next request outcome forced to '{_forceNextOutcome}' (host test hook; one request)");
    }

    // ================================================================== encoders

    private static byte[] BuildPlace(uint epoch, uint rev, string map, List<Machine> machines)
    {
        var mapBytes = Utf8(map, 96);
        int total = 1 + 4 + 4 + 1 + mapBytes.Length + 1;
        for (int i = 0; i < machines.Count; i++) total += 1 + Utf8(machines[i].Id, MaxMachineIdBytes).Length + 16;
        var p = new byte[total];
        int at = 0;
        p[at++] = MsgPlace;
        W32(p, ref at, epoch);
        W32(p, ref at, rev);
        at = WStr(p, at, mapBytes);
        p[at++] = (byte)machines.Count;
        for (int i = 0; i < machines.Count; i++)
        {
            at = WStr(p, at, Utf8(machines[i].Id, MaxMachineIdBytes));
            Wf(p, ref at, machines[i].Position.x);
            Wf(p, ref at, machines[i].Position.y);
            Wf(p, ref at, machines[i].Position.z);
            Wf(p, ref at, machines[i].Yaw);
        }
        return p;
    }

    private static byte[] BuildSpin(uint spinId, ulong actor, double startAt, float duration, int fromCell, int targetCell,
                                    int composeSeed, int shuffleSeed, int mask, int[] counts, string machineId,
                                    ReelCard[] deck)
    {
        var machineBytes = Utf8(machineId, MaxMachineIdBytes);
        int deckBytes = deck == null ? 0 : ReelCanonical.WireBytes(deck.Length);
        var p = new byte[1 + 4 + 8 + 8 + 4 + 2 + 2 + 4 + 4 + 1 + 5 + 1 + machineBytes.Length + deckBytes];
        int at = 0;
        p[at++] = MsgSpin;
        W32(p, ref at, spinId);
        W64(p, ref at, actor);
        BinaryPrimitives.WriteDoubleLittleEndian(p.AsSpan(at, 8), startAt); at += 8;
        Wf(p, ref at, duration);
        p[at++] = (byte)(fromCell & 0xFF); p[at++] = (byte)((fromCell >> 8) & 0xFF);
        p[at++] = (byte)(targetCell & 0xFF); p[at++] = (byte)((targetCell >> 8) & 0xFF);
        W32(p, ref at, (uint)composeSeed);
        W32(p, ref at, (uint)shuffleSeed);
        p[at++] = (byte)(mask & 0xFF);
        for (int i = 0; i < 5; i++) p[at++] = (byte)(counts != null && i < counts.Length ? counts[i] : 0);
        at = WStr(p, at, machineBytes);
        if (deck != null) ReelCanonical.Write(p, at, deck);
        return p;
    }

    private static byte[] BuildSettle(uint spinId, ulong actor, byte kind, string item, int count, int hpLost, string text)
    {
        var itemBytes = Utf8(item, MaxHeldIdBytes);
        var textBytes = Utf8(text, MaxTextBytes);
        var p = new byte[1 + 4 + 8 + 1 + 1 + itemBytes.Length + 1 + 2 + 1 + textBytes.Length];
        int at = 0;
        p[at++] = MsgSettle;
        W32(p, ref at, spinId);
        W64(p, ref at, actor);
        p[at++] = kind;
        at = WStr(p, at, itemBytes);
        p[at++] = (byte)Mathf.Clamp(count, 0, 255);
        short hp = (short)Mathf.Clamp(hpLost, -32768, 32767);
        p[at++] = (byte)(hp & 0xFF); p[at++] = (byte)((hp >> 8) & 0xFF);
        WStr(p, at, textBytes);
        at += 1 + textBytes.Length;
        return p;
    }

    private static byte[] BuildInteract(uint requestId, uint epoch, string machineId, string held)
    {
        var machineBytes = Utf8(machineId, MaxMachineIdBytes);
        var heldBytes = Utf8(held, MaxHeldIdBytes);
        var p = new byte[1 + 4 + 4 + 1 + machineBytes.Length + 1 + heldBytes.Length];
        int at = 0;
        p[at++] = MsgInteract;
        W32(p, ref at, requestId);
        W32(p, ref at, epoch);
        at = WStr(p, at, machineBytes);
        WStr(p, at, heldBytes);
        return p;
    }

    private static void SendVerdict(IHostContext log, ulong clientId, uint requestId, byte status, byte reason, uint spinId, string text)
    {
        if (_net == null || clientId == 0) return;
        var textBytes = Utf8(text, MaxTextBytes);
        var p = new byte[1 + 4 + 1 + 1 + 4 + 1 + textBytes.Length];
        int at = 0;
        p[at++] = MsgVerdict;
        W32(p, ref at, requestId);
        p[at++] = status;
        p[at++] = reason;
        W32(p, ref at, spinId);
        WStr(p, at, textBytes);
        bool ok = false;
        try { ok = _net.Send(NetKind.App, clientId, p); } catch { }
        if (!ok) log?.LogWarning($"{Tag} verdict id={requestId} status={Word(status)} to client={clientId} NOT sent");
        else if (status != StatusAccepted)
            log.LogInfo($"{Tag} verdict id={requestId} -> client={clientId} status={Word(status)} " +
                        $"reason={ReasonWord(reason)} spin={spinId} text='{text}'");
    }

    // Client side of the verdict: log it, show a refusal, and remember the settled text so it can
    // ride the animation instead of arriving as a second toast.
    internal static void HandleVerdict(IHostContext log, byte[] payload)
    {
        int at = 1;
        if (!U32(payload, ref at, out uint requestId) || !U8(payload, ref at, out byte status) ||
            !U8(payload, ref at, out byte reason) || !U32(payload, ref at, out uint spinId) ||
            !Str(payload, ref at, out string text))
        {
            log.LogWarning($"{Tag} verdict malformed bytes={payload.Length}");
            return;
        }
        _ledger.TryGetValue(Key(_localId, requestId), out var rec);
        if (rec != null) { rec.Status = status; rec.Reason = reason; rec.SpinId = spinId; rec.Text = text; }

        switch (status)
        {
            case StatusAccepted:
                log.LogInfo($"{Tag} request {requestId} ACCEPTED by the host spin={spinId}; waiting for the spin");
                break;
            case StatusSettled:
                log.LogInfo($"{Tag} request {requestId} SETTLED spin={spinId} text='{text}' " +
                            "(applied by the host; this client applied nothing)");
                break;
            default:
                log.LogInfo($"{Tag} request {requestId} REJECTED reason={ReasonWord(reason)} text='{text}' (nothing consumed)");
                // The host refused: the same Error cue the host's machine made, from this client's
                // own booth, so the player who pressed E hears WHY on the side they are standing on.
                Vector3 rejectAt;
                if (BoothOrigin(rec != null ? rec.MachineId : null, out rejectAt))
                    ReelAudio.Cue(log, ReelCue.Error, rejectAt);
                if (rec == null || !rec.Shown) { ReelHud.Error(log, text); if (rec != null) rec.Shown = true; }
                break;
        }
    }

    // ================================================================== request-file hooks

    // reel.request lines this module owns. Called from LobbyPropRuntime.PollReelRequest before the
    // spin parsing, so `pull@1` is never mistaken for "spin".
    internal static bool HandleRequestLine(IHostContext log, string text)
    {
        if (log != null) _log = log;
        bool handled = false;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string key = line, val = "";
            int eq = line.IndexOf('=');
            if (eq > 0) { key = line.Substring(0, eq).Trim(); val = line.Substring(eq + 1).Trim(); }
            else
            {
                // `pull@2` (same @n booth addressing as interact.request) / `forge`.
                int at = key.LastIndexOf('@');
                if (at > 0)
                {
                    val = key.Substring(at + 1).Trim();
                    key = key.Substring(0, at).Trim();
                }
            }
            string lower = key.ToLowerInvariant();
            switch (lower)
            {
                case "pull": case "req": case "request":
                    handled = true;
                    SendRequestForLine(log, val, "request");
                    break;
                case "pull2": case "reqdup":
                    handled = true;
                    SendRequestForLine(log, val, "dup");
                    break;
                case "pullstale":
                    handled = true;
                    SendRequestForLine(log, val, "stale");
                    break;
                case "pullwrong":
                    handled = true;
                    SendRequestForLine(log, val, "wrongitem");
                    break;
                case "forge":
                    handled = true;
                    RequestVariant(log, "", "forge");
                    break;
                case "nextoutcome": case "force":
                    handled = true;
                    ForceNext(log, val.ToLowerInvariant());
                    break;
                case "inv":
                    handled = true;
                    ReelLogic.DebugInv(log, val);
                    break;
                case "respawn":
                    {
                        // Test hook (host only): run the game's OWN respawn for a client -
                        // Network.Spawners.DebugPlayerRespawner.Respawn(), the component the game
                        // itself uses to rebuild a player's objects. Used to prove that a custom
                        // body, its hat and its enhancement profile survive a respawn; nothing in
                        // the mod recreates the body by hand here.
                        handled = true;
                        if (!ulong.TryParse((val ?? "").Trim(), out ulong rcid))
                        {
                            log.LogWarning($"{Tag} respawn expects <clientId>");
                            break;
                        }
                        int seen = 0, fired = 0;
                        try
                        {
                            var arr = SceneCache.Scan(Il2CppType.Of<Network.Spawners.DebugPlayerRespawner>());
                            for (int i = 0; arr != null && i < arr.Length; i++)
                            {
                                var r = arr[i].TryCast<Network.Spawners.DebugPlayerRespawner>();
                                if (r == null) continue;
                                ulong whose = 0;
                                try { whose = r._clientId; } catch { }
                                seen++;
                                IntPtr ptr = IntPtr.Zero; try { ptr = r.Pointer; } catch { }
                                log.LogInfo($"{Tag} respawner[{seen}] '{r.name}' clientId={whose} active={(r.gameObject != null && r.gameObject.activeInHierarchy)} ptr=0x{ptr.ToInt64():X}");
                                if (whose != rcid)
                                {
                                    // The map ships one respawner (the local client's). Retarget it
                                    // instead of inventing a second respawn path: the component's own
                                    // Respawn() then rebuilds THE requested client's player objects.
                                    try { r._clientId = rcid; log.LogInfo($"{Tag} respawner[{seen}] clientId {whose}->{rcid}"); }
                                    catch (Exception e) { log.LogWarning($"{Tag} respawner[{seen}] retarget failed: {e.Message}"); continue; }
                                }
                                log.LogInfo($"{Tag} respawn client={rcid} found respawner='{r.name}' -> Respawn()");
                                try { r.Respawn(); fired++; }
                                catch (Exception e) { log.LogWarning($"{Tag} respawn client={rcid} threw: {e.Message}"); }
                            }
                        }
                        catch (Exception e) { log.LogWarning($"{Tag} respawn scan failed: {e.Message}"); }
                        log.LogInfo($"{Tag} respawn client={rcid} respawners={seen} fired={fired}");
                    }
                    break;
                case "giveremote":
                    {
                        // Test hook (host only): put one item in ANOTHER player's inventory through the
                        // game's own AddItemOrDropIfNoSpace, so a client's pull has a stake to bet and
                        // the host-side write is what the client sees (server-authoritative inventory).
                        handled = true;
                        var parts = val.Split(',');
                        if (parts.Length < 2) { log.LogWarning($"{Tag} giveremote expects <clientId>,<itemId>"); break; }
                        if (!ulong.TryParse(parts[0].Trim(), out ulong cid)) { log.LogWarning($"{Tag} giveremote: '{parts[0]}' is not a client id"); break; }
                        string itemId = parts[1].Trim();
                        var actor = FindActor(cid);
                        if (actor == null) { log.LogWarning($"{Tag} giveremote: no player for client={cid}"); break; }
                        ReelLogic.DebugGivePlayer(log, actor, itemId);
                    }
                    break;
                case "hurtremote": case "killremote":
                    {
                        // Test hook (host only): put ANOTHER player down through the game's own damage
                        // entry point - Health.Damage with a Debug world source, the exact call the reel's
                        // damage card makes. Needed because a joining client's Health is server-owned:
                        // a client cannot damage itself for a verification run, and a bare
                        // `health.Value = 0` write would skip the game's own death/respawn handling.
                        //   hurtremote=<clientId>            lethal (2x max)
                        //   hurtremote=<clientId>,<amount>   that much damage
                        handled = true;
                        var hp = val.Split(',');
                        if (hp.Length < 1 || !ulong.TryParse(hp[0].Trim(), out ulong hcid))
                        {
                            log.LogWarning($"{Tag} hurtremote expects <clientId>[,<damage>]");
                            break;
                        }
                        var victim = FindActor(hcid);
                        if (victim == null) { log.LogWarning($"{Tag} hurtremote: no player for client={hcid}"); break; }
                        var hurt = ReelLogic.FindHealth(victim);
                        if (hurt == null) { log.LogWarning($"{Tag} hurtremote: no Health on client={hcid}"); break; }
                        float amount = hurt.MaxValue * 2f;
                        if (hp.Length > 1 && float.TryParse(hp[1].Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float given) && given > 0f)
                            amount = given;
                        float hpBefore = hurt.Value, hpMax = hurt.MaxValue;
                        bool threw = false;
                        string why = "";
                        try { hurt.Damage(amount, HealthChangeContext.FromWorld(WorldHealthChangeSourceData.Type.Debug)); }
                        catch (Exception e) { threw = true; why = e.GetType().Name + ": " + e.Message; }
                        log.LogInfo($"{Tag} hurtremote client={hcid} damage={amount:F1} " +
                                    $"hp={hpBefore:F1}->{hurt.Value:F1}/{hpMax:F1} dead={hurt.IsDead} threw={threw} {why}");
                    }
                    break;
                case "placeat":
                    {
                        // Host test hook: stand a machine at a player's live server-side position and
                        // publish a new revision. This is the host's OWN placement change - clients
                        // follow it, which is the point of the authority.
                        handled = true;
                        var parts = val.Split(',');
                        if (parts.Length < 2 || !ulong.TryParse(parts[0].Trim(), out ulong cid))
                        { log.LogWarning($"{Tag} placeat expects <clientId>,<machineId>"); break; }
                        var actor = FindActor(cid);
                        if (actor == null) { log.LogWarning($"{Tag} placeat: no player for client={cid}"); break; }
                        Vector3 pos, fwd; string src;
                        bool havePose = cid == _localId
                            ? ReelLogic.TryGetPlayerPose(out pos, out fwd, out src)
                            : ReelLogic.TryGetPose(actor, out pos, out fwd, out src);
                        if (!havePose) { log.LogWarning($"{Tag} placeat: no pose for client={cid}"); break; }
                        var booth = ReelPlacements.FindBooth(parts[1].Trim());
                        if (booth == null) { log.LogWarning($"{Tag} placeat: no machine '{parts[1]}' (live=[{ReelPlacements.BoothIds()}])"); break; }
                        fwd.y = 0f;
                        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
                        fwd.Normalize();
                        // 1.5 m in front of the actor, floor-probed like any other booth.
                        Vector3 at = pos + fwd * 1.5f;
                        float floorY = LobbyPropFactory.GroundY(log, at.x, at.z, pos.y + 1.7f, 4.0f, pos.y);
                        float yaw = Mathf.Atan2(-fwd.x, -fwd.z) * Mathf.Rad2Deg;   // face the actor
                        booth.MoveTo(log, new Vector3(at.x, floorY, at.z), yaw);
                        Republish(log);
                        log.LogInfo($"{Tag} placeat client={cid} machine='{booth.MachineId}' actor={V(pos)} ({src}) " +
                                    $"-> machine={V(booth.Anchor)} yaw={yaw:F1} floorY={floorY:F3} rev={_pubRev}");
                    }
                    break;
                case "pose":
                    {
                        // Read-only test harness: sample the same actor/body the host validates.
                        // A teleport dispatch (particularly on a joiner) is not proof of movement.
                        handled = true;
                        if (!NetRole.IsServer || !ulong.TryParse(val, out ulong cid))
                        { log.LogWarning("[reel:pose] unavailable: expected host and a client ID"); break; }
                        var actor = FindActor(cid);
                        if (actor == null || !ReelLogic.TryGetPose(actor, out var pos, out var fwd, out var src))
                        { log.LogWarning($"[reel:pose] unavailable for actor={cid}"); break; }
                        log.LogInfo(FormattableString.Invariant($"[reel:pose] actor={cid} source={src} pos=({pos.x:F2},{pos.y:F2},{pos.z:F2}) fwd=({fwd.x:F2},{fwd.y:F2},{fwd.z:F2})"));
                    }
                    break;
                case "holdremote":
                    {
                        // Test hook (host side): report what the host sees the actor holding, so the
                        // request's expectedHeldItemId can be compared with the server's own read.
                        handled = true;
                        if (ulong.TryParse(val.Trim(), out ulong cid))
                        {
                            var actor = FindActor(cid);
                            log.LogInfo($"{Tag} hold(remote) client={cid} actor={(actor == null ? "null" : actor.name)} " +
                                        $"held='{ReelLogic.HeldItemId(actor)}'");
                        }
                    }
                    break;
                case "reelstate":
                    handled = true;
                    log.LogInfo($"{Tag} {StatusText()} authority={Authority} localMap='{_localMap}'");
                    for (int i = 0; i < _pubMachines.Count; i++)
                        log.LogInfo($"{Tag}   host machine '{_pubMachines[i].Id}' {V(_pubMachines[i].Position)} yaw={_pubMachines[i].Yaw:F1}");
                    for (int i = 0; i < _hostMachines.Count; i++)
                        log.LogInfo($"{Tag}   client machine '{_hostMachines[i].Id}' {V(_hostMachines[i].Position)} yaw={_hostMachines[i].Yaw:F1}");
                    break;
            }
        }
        // If the same body also carries a line the older parsers own (spin/give/hold/...), let them
        // run too: this method has already executed its own lines.
        if (handled && HasClassicLine(text)) return false;
        return handled;
    }

    private static bool HasClassicLine(string text)
    {
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim().ToLowerInvariant();
            if (line.Length == 0) continue;
            if (line.StartsWith("pull") || line.StartsWith("req") || line.StartsWith("forge") ||
                line.StartsWith("nextoutcome") || line.StartsWith("force") || line.StartsWith("inv") ||
                line.StartsWith("reelstate") || line.StartsWith("giveremote") || line.StartsWith("holdremote") ||
                line.StartsWith("placeat") || line.StartsWith("pose=")) continue;
            return true;
        }
        return false;
    }

    // `pull@2` / `pull=2` names the booth (1-based, same convention as interact.request's @n).
    private static void SendRequestForLine(IHostContext log, string value, string variant)
    {
        string v = (value ?? "").Trim();
        int index = 1;
        if (v.Length > 0)
        {
            string first = v.Split(' ')[0];
            if (first.StartsWith("booth", StringComparison.OrdinalIgnoreCase)) first = first.Substring(5);
            int.TryParse(first, out index);
            if (index <= 0) index = 1;
        }
        var booth = ReelPlacements.BoothIndexed(index);
        if (booth == null)
        {
            log?.LogWarning($"{Tag} pull: no booth {index} (live=[{ReelPlacements.BoothIds()}])");
            return;
        }
        if (variant == "request") Request(log, booth.MachineId, "request");
        else RequestVariant(log, booth.MachineId, variant);
    }

    // ================================================================== helpers

    private static string Word(byte status) =>
        status == StatusAccepted ? "accepted" : status == StatusSettled ? "settled"
        : status == StatusRejected ? "rejected" : "pending";

    private static string ReasonWord(byte reason)
    {
        switch (reason)
        {
            case ReasonNone: return "-";
            case ReasonWrongItem: return "wrong-item";
            case ReasonOutOfRange: return "out-of-range";
            case ReasonBusy: return "busy";
            case ReasonStaleEpoch: return "stale-epoch";
            case ReasonForbiddenItem: return "forbidden-item";
            case ReasonNoItem: return "no-item";
            case ReasonIneligible: return "ineligible";
            case ReasonUnknownMachine: return "unknown-machine";
            case ReasonDuplicate: return "duplicate";
            case ReasonMalformed: return "malformed";
            case ReasonNotHost: return "not-host";
            default: return "internal";
        }
    }

    private static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

    private static byte[] Utf8(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
        int n = Encoding.UTF8.GetByteCount(s);
        if (n > max) return Array.Empty<byte>();
        return Encoding.UTF8.GetBytes(s);
    }

    private static int WStr(byte[] p, int at, byte[] bytes)
    {
        p[at++] = (byte)Math.Min(255, bytes.Length);
        for (int i = 0; i < bytes.Length; i++) p[at++] = bytes[i];
        return at;
    }

    private static void W32(byte[] p, ref int at, uint v) { BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(at, 4), v); at += 4; }
    private static void W64(byte[] p, ref int at, ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(at, 8), v); at += 8; }
    private static void Wf(byte[] p, ref int at, float v) { BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(at, 4), v); at += 4; }

    private static bool U8(byte[] p, ref int at, out byte v)
    {
        v = 0;
        if (at + 1 > p.Length) return false;
        v = p[at++];
        return true;
    }

    private static bool U16(byte[] p, ref int at, out int v)
    {
        v = 0;
        if (at + 2 > p.Length) return false;
        v = p[at] | (p[at + 1] << 8);
        at += 2;
        return true;
    }

    private static bool I16(byte[] p, ref int at, out int v)
    {
        v = 0;
        if (at + 2 > p.Length) return false;
        v = (short)(p[at] | (p[at + 1] << 8));
        at += 2;
        return true;
    }

    private static bool U32(byte[] p, ref int at, out uint v)
    {
        v = 0;
        if (at + 4 > p.Length) return false;
        v = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(at, 4));
        at += 4;
        return true;
    }

    private static bool U64(byte[] p, ref int at, out ulong v)
    {
        v = 0;
        if (at + 8 > p.Length) return false;
        v = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(at, 8));
        at += 8;
        return true;
    }

    private static bool F32(byte[] p, ref int at, out float v)
    {
        v = 0f;
        if (at + 4 > p.Length) return false;
        v = BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(at, 4));
        at += 4;
        return true;
    }

    private static bool F64(byte[] p, ref int at, out double v)
    {
        v = 0.0;
        if (at + 8 > p.Length) return false;
        v = BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(at, 8));
        at += 8;
        return true;
    }

    private static bool Str(byte[] p, ref int at, out string s)
    {
        s = "";
        if (at >= p.Length) return false;
        int n = p[at++];
        if (at + n > p.Length) return false;
        s = n == 0 ? "" : Encoding.UTF8.GetString(p, at, n);
        at += n;
        return true;
    }
}
