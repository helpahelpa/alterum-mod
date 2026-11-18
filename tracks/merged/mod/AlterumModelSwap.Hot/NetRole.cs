using System;

namespace AlterumModelSwap.Hot;

// One answer to "may this instance write a server-owned NetworkVariable".
//
// The game's NetworkVariables with `NetworkVariableWritePermission.Server` throw
//   |Client-N|Readiness Controller|_requiredPlayerNumberToStartGame| Write permissions (Server)
//   for this client instance is not allowed!
// on a join client, through UnityEngine.Debug.LogError - i.e. an `[Error]` line the verification
// gate counts, produced by the mod poking a value the client does not own. Every place that writes
// such a value asks this first; a client writes its own local state and nothing else, and the
// server's replicated value is what the client reads.
internal static class NetRole
{
    // True only when a NetworkManager exists AND this process is the server (host or dedicated).
    // Any throw counts as "not the server": a write attempt is the thing being avoided.
    internal static bool IsServer
    {
        get
        {
            try
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                return nm != null && nm.IsServer;
            }
            catch { return false; }
        }
    }

    // "server", "client" or "none" (no NetworkManager yet: menu, lobby teardown).
    internal static string Word()
    {
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null) return "none";
            return nm.IsServer ? "server" : "client";
        }
        catch { return "none"; }
    }
}
