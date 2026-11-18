using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AlterumModelSwap.Contracts;

// Stage-1 mod networking surface. The host owns the il2cpp side (Unity Netcode named messages);
// the hot module only ever sees these types, so a module swap can never disturb the registration.
//
// Everything here is plain managed code with no Unity/IL2CPP types: it crosses the collectible
// AssemblyLoadContext boundary like the rest of Contracts.

/// <summary>Wire constants. Shared by the host (which encodes/validates) and the hot side.</summary>
public static class NetProtocol
{
    /// <summary>Bumped whenever this file's shape changes in a way the host must know about.</summary>
    public const int ApiVersion = 1;

    /// <summary>"ASW1" little-endian: first four bytes of every frame.</summary>
    public const uint Magic = 0x31575341;

    public const byte Major = 1;
    public const byte Minor = 0;

    /// <summary>magic(4) major(1) minor(1) kind(1) flags(1) payloadLen(2) epoch(4) sequence(4).</summary>
    public const int HeaderBytes = 18;

    /// <summary>Application-frame ceiling: the largest payload the bridge will carry.</summary>
    public const int MaxPayloadBytes = 1024;

    public const int MaxFrameBytes = HeaderBytes + MaxPayloadBytes;

    /// <summary>Namespaced named-message name registered on the game's CustomMessagingManager.</summary>
    public const string MessageName = "com.user0.alterum.modelswap/v1";
}

/// <summary>Frame kind. Unknown kinds are dropped by the receiver, never dispatched.</summary>
public enum NetKind : byte
{
    App = 1,
    Echo = 2,
    EchoReply = 3,
}

public enum NetState
{
    /// <summary>The Unity Netcode API is not present on this build; the bridge stays idle forever.</summary>
    Unsupported = 0,
    /// <summary>No NetworkManager / CustomMessagingManager yet (main menu, lobby teardown).</summary>
    WaitingForNetwork = 1,
    /// <summary>Handler registered on a live session; sends and receives are possible.</summary>
    Registered = 2,
    /// <summary>Switched off by configuration or after repeated faults.</summary>
    Disabled = 3,
}

[Flags]
public enum NetCaps
{
    None = 0,
    Send = 1,
    Receive = 2,
    Broadcast = 4,
}

public struct NetBridgeConfig
{
    /// <summary>False mutes outbound frames (inbound is still queued and counted).</summary>
    public bool Enabled;
    /// <summary>Answer an inbound Echo with an EchoReply. This is the proven round-trip path.</summary>
    public bool AutoEcho;
    /// <summary>Log every frame instead of only the echo/test lines.</summary>
    public bool Verbose;
    /// <summary>Bounded inbound ring size; clamped to 1..256 by the host.</summary>
    public int InboundCapacity;

    public static NetBridgeConfig Default => new NetBridgeConfig
    {
        Enabled = true,
        AutoEcho = true,
        Verbose = false,
        InboundCapacity = 64,
    };
}

public struct NetBridgeStatus
{
    public NetState State;
    public NetCaps Caps;

    public ushort ApiVersionMajor;
    public ushort ApiVersionMinor;

    public ulong LocalClientId;
    public ulong ServerClientId;
    public bool IsServer;
    public bool IsClient;
    public bool IsListening;

    /// <summary>Peers currently connected, excluding this instance (0 when unknown).</summary>
    public int PeerCount;
    public uint SessionEpoch;
    public uint Generation;
    public int InboundQueued;
    public int InboundCapacity;

    public long FramesSent;
    public long FramesReceived;
    public long FramesDropped;
    public long FramesOversized;
    public long FramesMalformed;
    public long FramesVersionRejected;
    public long SequencesRejected;
    public string LastError;
}

public struct NetInboundFrame
{
    public ulong SenderClientId;
    public NetKind Kind;
    public byte Flags;
    public uint SessionEpoch;
    public uint Sequence;
    /// <summary>Exact copy of the application payload (never a view into il2cpp memory).</summary>
    public byte[] Payload;
}

public struct NetEnvelopeHeader
{
    public byte Major;
    public byte Minor;
    public NetKind Kind;
    public byte Flags;
    public ushort PayloadBytes;
    public uint SessionEpoch;
    public uint Sequence;
}

/// <summary>
/// The one envelope implementation. The host encodes outbound frames and validates inbound ones
/// with it; a hot module that wants to inspect raw frames uses the same code, so both ends are
/// bounds-checked by construction.
/// </summary>
public static class NetEnvelope
{
    /// <summary>
    /// Write a frame into <paramref name="destination"/>. Returns the number of bytes written, or
    /// -1 when the payload is over the ceiling or the destination is too small.
    /// </summary>
    public static int Encode(NetKind kind, uint sessionEpoch, uint sequence, ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        if (payload.Length > NetProtocol.MaxPayloadBytes) return -1;
        var total = NetProtocol.HeaderBytes + payload.Length;
        if (destination.Length < total) return -1;

        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), NetProtocol.Magic);
        destination[4] = NetProtocol.Major;
        destination[5] = NetProtocol.Minor;
        destination[6] = (byte)kind;
        destination[7] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(8, 2), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(10, 4), sessionEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(14, 4), sequence);
        payload.CopyTo(destination.Slice(NetProtocol.HeaderBytes));
        return total;
    }

    /// <summary>
    /// Validate a whole frame and split it. Never throws: every failure mode returns false with a
    /// short machine-readable reason in <paramref name="error"/>.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out NetEnvelopeHeader header, out ReadOnlySpan<byte> payload, out string error)
    {
        header = default;
        payload = default;

        if (frame.Length < NetProtocol.HeaderBytes)
        {
            error = "short-header";
            return false;
        }
        if (frame.Length > NetProtocol.MaxFrameBytes)
        {
            error = "oversized";
            return false;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(0, 4)) != NetProtocol.Magic)
        {
            error = "bad-magic";
            return false;
        }

        header.Major = frame[4];
        header.Minor = frame[5];
        var kind = frame[6];
        header.Flags = frame[7];
        header.PayloadBytes = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2));
        header.SessionEpoch = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(10, 4));
        header.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(14, 4));

        if (kind != (byte)NetKind.App && kind != (byte)NetKind.Echo && kind != (byte)NetKind.EchoReply)
        {
            error = "bad-kind";
            return false;
        }
        header.Kind = (NetKind)kind;

        if (header.Major != NetProtocol.Major)
        {
            error = "version-major";
            return false;
        }
        if (header.PayloadBytes > NetProtocol.MaxPayloadBytes)
        {
            error = "payload-over-ceiling";
            return false;
        }
        if (header.SessionEpoch == 0)
        {
            error = "no-epoch";
            return false;
        }
        if (frame.Length != NetProtocol.HeaderBytes + header.PayloadBytes)
        {
            error = "length-mismatch";
            return false;
        }

        payload = frame.Slice(NetProtocol.HeaderBytes, header.PayloadBytes);
        error = null;
        return true;
    }

    /// <summary>Lowercase hex of at most <paramref name="maxBytes"/> bytes, with a ".." tail marker.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes, int maxBytes = 32)
    {
        var n = Math.Min(bytes.Length, maxBytes);
        var chars = new char[n * 2 + (bytes.Length > n ? 2 : 0)];
        const string digits = "0123456789abcdef";
        for (int i = 0; i < n; i++)
        {
            chars[i * 2] = digits[bytes[i] >> 4];
            chars[i * 2 + 1] = digits[bytes[i] & 0xF];
        }
        if (bytes.Length > n)
        {
            chars[n * 2] = '.';
            chars[n * 2 + 1] = '.';
        }
        return new string(chars);
    }
}

/// <summary>
/// A generation-scoped activation. The host initialises a new hot generation *before* disposing
/// the old one, so a stale lease must never be able to mute its successor. Disposing a lease whose
/// generation is no longer current is a logged no-op.
/// </summary>
public interface INetBridgeLease : IDisposable
{
    uint Generation { get; }
    bool IsActive { get; }
    /// <summary>Change settings for this generation only; ignored when stale.</summary>
    void Reconfigure(in NetBridgeConfig config);
}

/// <summary>
/// Implemented once by the permanent host assembly. Lives exactly as long as the process, so the
/// named-message registration survives every module swap.
/// </summary>
public interface INetBridge
{
    /// <summary>
    /// Start (or replace) the consumer for this module generation. Safe to call repeatedly: each
    /// call bumps the generation and invalidates every earlier lease.
    /// </summary>
    INetBridgeLease Activate(in NetBridgeConfig config);

    NetBridgeStatus Status { get; }

    /// <summary>Pull one inbound frame. False when the ring is empty.</summary>
    bool TryTake(out NetInboundFrame frame);

    /// <summary>Pull up to <paramref name="max"/> frames into <paramref name="into"/>; returns how many.</summary>
    int Drain(List<NetInboundFrame> into, int max);

    /// <summary>Send an application frame. False when not registered, target invalid, or too large.</summary>
    bool Send(NetKind kind, ulong targetClientId, ReadOnlySpan<byte> payload);

    /// <summary>Send to every connected peer. Server/host only.</summary>
    bool Broadcast(NetKind kind, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Ask a peer to echo a token back. The host logs the match; this proves the
    /// managed -> IL2CPP delegate bridge end to end without any feature code.
    /// </summary>
    bool Echo(ulong targetClientId);

    /// <summary>Connected peer ids (excluding this instance), copied into <paramref name="into"/>.</summary>
    int Peers(List<ulong> into);

    void ClearInbound();
}
