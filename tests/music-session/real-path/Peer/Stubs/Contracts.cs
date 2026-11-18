using System;
using System.Collections.Generic;

namespace AlterumModelSwap.Contracts
{
    // The subset of the permanent host contract the music sources touch. Kept deliberately small:
    // everything that is not part of the music code path is out of scope for this harness.
    public interface IHostContext
    {
        string ScreensDir { get; }
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
    }

    public enum NetKind
    {
        App = 0,
    }

    public struct NetInboundFrame
    {
        public ulong SenderClientId;
        public NetKind Kind;
        public byte Flags;
        public uint SessionEpoch;
        public uint Sequence;
        public byte[] Payload;
    }

    public interface INetBridge
    {
        bool Send(NetKind kind, ulong targetClientId, ReadOnlySpan<byte> payload);
        int Peers(List<ulong> into);
    }
}
