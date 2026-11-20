using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

namespace AlterumModelSwap.Hot;

// Local developer transport, not a player feature. One expiring, identity-bound request at a time.
// Observe is read-only. Mutations use only native pause/UI/input-field paths, never network APIs,
// scene loads, model setters or reservation writes. A response acknowledges dispatch, NOT readiness.
internal static class SessionControl
{
    internal const string RequestName = "session.request";
    internal const string ExitPath = "Lobby Global/User Interface/Canvases/Master Canvas/Pause Menu/Viewport/Game Menu Tabs/Menu Tabs/Exit to Menu Button Variant";
    internal const string ConfirmPath = "Lobby Global/User Interface/Canvases/Game Confirmation Dialogs Canvas/Two Answer Confirmation Dialog/Content/Buttons/Right Button";
    internal const string CodePath = "UI/Menu Canvas/Main Menu Tabs/Menu Tabs/Code Menu Button";
    internal const string ConnectPath = "UI/Menu Canvas/Content/Connect via code/Gothic Yellow Button Variant";
    internal const string FieldPath = "UI/Menu Canvas/Content/Connect via code/Gothic InputField (TMP)";
    private static float _next;
    private static readonly string StartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static readonly string Generation = Guid.NewGuid().ToString("N");
    private static string Mvid => typeof(SessionControl).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    internal static void Tick(IHostContext host, ModelSwapRuntime runtime)
    {
        if (Time.unscaledTime < _next || host == null) return;
        _next = Time.unscaledTime + .2f;
        string path = Path.Combine(host.ScreensDir, RequestName);
        if (!File.Exists(path)) return;
        // Do not execute if consumption failed. There is no retry loop around a mutation.
        string text = File.ReadAllText(path);
        File.Delete(path);
        string[] p = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 10 || p[0] != "v1" || !Regex.IsMatch(p[1], "^[a-f0-9]{32}$"))
        { host.LogWarning("[session] malformed request refused"); return; }
        string reply = Path.Combine(host.ScreensDir, "session-" + p[1] + ".json");
        if (File.Exists(reply)) return; // replay of an already answered request
        string status = "observed", error = "";
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long expires = long.Parse(p[2], CultureInfo.InvariantCulture);
            if (expires < now || expires > now + 15000) throw new InvalidOperationException("expired/invalid request deadline");
            if (p[3] != Environment.ProcessId.ToString() || p[4] != StartUtc)
                throw new InvalidOperationException("process identity changed");
            if (p[7] != "observe")
            {
                if (p[5] != Mvid + ":" + Generation) throw new InvalidOperationException("hot generation changed");
                if (runtime == null || runtime.SequenceActive || UiDriver.Pending || File.Exists(Path.Combine(host.ScreensDir, "ui.request")))
                    throw new InvalidOperationException("UI driver/sequence busy");
                Act(host, runtime, p[7], p[6], p[8], p[9]);
                status = "dispatched";
            }
        }
        catch (Exception e) { status = "error"; error = e.GetType().Name + ": " + e.Message; }
        string snapshot;
        try { snapshot = Observe(host, runtime); }
        catch (Exception e) { snapshot = "null"; status = "error"; error += " observation failed: " + e.Message; }
        string json = "{\"schema\":\"alterum.session/1\",\"requestId\":" + Q(p[1]) + ",\"action\":" + Q(p[7]) +
            ",\"status\":" + Q(status) + ",\"error\":" + Q(error) + ",\"state\":" + snapshot + "}";
        File.WriteAllText(reply + ".tmp", json, new UTF8Encoding(false));
        File.Move(reply + ".tmp", reply);
        host.LogInfo($"[session] id={p[1]} action={p[7]} status={status} {error}");
    }

    private static void Act(IHostContext host, ModelSwapRuntime runtime, string action, string lobby, string code, string client)
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer) throw new InvalidOperationException("refusing to cycle a host");
        string scene = SceneManager.GetActiveScene().name;
        bool member = Steam.SteamLobbyManager.IsInLobby;
        string current = member ? ((ulong)Steam.SteamLobbyManager.CurrentLobby.Id).ToString() : "0";
        if (action == "pause" || action == "leave" || action == "confirm")
        {
            if (scene != "Lobby" || !member || current != lobby || nm == null || !nm.IsConnectedClient || nm.LocalClientId.ToString() != client)
                throw new InvalidOperationException("not the expected live client membership");
        }
        else if (scene != "Menu" || member || (nm != null && nm.IsListening))
            throw new InvalidOperationException("not disconnected in Menu");
        switch (action)
        {
            case "pause":
                var pausers = Find<Pause.Pauser>(null);
                if (pausers.Count != 1) throw new InvalidOperationException("no unique active scene Pauser");
                pausers[0].Pause();
                return;
            case "leave": Click(runtime, ExitPath); return;
            case "confirm":
                // Confirmation is only accepted after this membership's leave action, with its
                // exact dialog visible. Python also confirms the dialog before sending this action.
                if (_armedClient != client || _armedLobby != lobby || Time.unscaledTime > _confirmUntil)
                    throw new InvalidOperationException("no recent matching leave confirmation armed");
                _confirmUntil = 0;
                Click(runtime, ConfirmPath);
                return;
            case "open-code": Click(runtime, CodePath); return;
            case "fill-code":
                if (!Regex.IsMatch(code, "^[A-Z0-9]{4,16}$")) throw new InvalidOperationException("unsafe lobby code");
                var fields = Find<TMPro.TMP_InputField>(FieldPath);
                if (fields.Count != 1 || !fields[0].interactable) throw new InvalidOperationException("no unique interactable code field");
                fields[0].text = code;
                fields[0].onValueChanged.Invoke(code);
                fields[0].onEndEdit.Invoke(code);
                return;
            case "connect":
                var inputs = Find<TMPro.TMP_InputField>(FieldPath);
                if (inputs.Count != 1 || inputs[0].text != code) throw new InvalidOperationException("code field does not match intended lobby");
                Click(runtime, ConnectPath); return;
            default: throw new InvalidOperationException("unknown session action");
        }
    }

    private static string _armedClient, _armedLobby;
    private static float _confirmUntil;
    private static void Click(ModelSwapRuntime runtime, string path)
    {
        if (!ButtonReady(path)) throw new InvalidOperationException("exact UI button unavailable/not interactable: " + path);
        // Same machinery as the existing successful manual UI harness, but exact-path only.
        if (path == ExitPath)
        {
            _armedClient = NetworkManager.Singleton.LocalClientId.ToString();
            _armedLobby = ((ulong)Steam.SteamLobbyManager.CurrentLobby.Id).ToString();
            _confirmUntil = Time.unscaledTime + 15f;
        }
        if (runtime.ActivateOrClick(path, true) < 1) throw new InvalidOperationException("UI dispatch found no target");
    }

    private static List<T> Find<T>(string path) where T : Component
    {
        var found = new List<T>();
        // Explicit requests need a fresh census, not SceneCache's possibly pre-panel snapshot.
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var item = all[i].TryCast<T>();
            if (item == null || item.gameObject == null || !item.gameObject.scene.IsValid() || !item.gameObject.activeInHierarchy) continue;
            if (path == null || UiPath.Full(item.transform) == path) found.Add(item);
        }
        return found;
    }

    private static bool ButtonReady(string path)
    {
        // IL2CPP's Button type census is unreliable; scan MonoBehaviour and cast instead.
        var all = Find<MonoBehaviour>(path);
        UI.Elements.Button button = null;
        int count = 0;
        foreach (var mb in all)
        {
            var b = mb.TryCast<UI.Elements.Button>();
            if (b != null) { button = b; count++; }
        }
        return count == 1 && button.IsInteractable && button.CanClick();
    }

    private static string Observe(IHostContext host, ModelSwapRuntime runtime)
    {
        var nm = NetworkManager.Singleton;
        bool member = Steam.SteamLobbyManager.IsInLobby;
        ulong lobby = member ? (ulong)Steam.SteamLobbyManager.CurrentLobby.Id : 0;
        var identity = ModelPresentation.ResolveLocalReadOnly(host);
        // Reported so a coordinator can tell a diagnostics-only peer (no custom roster registered)
        // from one that merely failed to register its roster. Observation only; nothing here reads it.
        bool diagnosticsOnly = AlterumModelSwap.DiagnosticsMode.Enabled;
        var players = new List<string>();
        foreach (var player in Find<Player>(null))
        {
            if (!player.IsSpawned) continue;
            players.Add("{\"id\":" + Q(player.OwnerClientId.ToString()) + ",\"owned\":" + B(player.IsOwner) +
                ",\"connected\":" + B(player.IsConnected) + ",\"scene\":" + Q(player.gameObject.scene.name) + "}");
        }
        players.Sort(StringComparer.Ordinal);
        var fields = Find<TMPro.TMP_InputField>(FieldPath);
        string start = StartUtc;
        return "{\"pid\":" + Environment.ProcessId + ",\"startUtc\":" + Q(start) + ",\"exe\":" + Q(Environment.ProcessPath) +
            ",\"instance\":" + Q(host.InstanceName ?? "") + ",\"mvid\":" + Q(Mvid) + ",\"generation\":" + Q(Generation) + ",\"utc\":" + Q(DateTime.UtcNow.ToString("o")) +
            ",\"frame\":" + Time.frameCount + ",\"scene\":" + Q(SceneManager.GetActiveScene().name) +
            ",\"sceneLoaded\":" + B(SceneManager.GetActiveScene().isLoaded) + ",\"inLobby\":" + B(member) +
            ",\"lobbyId\":" + Q(lobby.ToString()) + ",\"lobbyCode\":" + Q(lobby == 0 ? "" : Steam.LobbyCode.Encode(lobby)) +
            ",\"listening\":" + B(nm != null && nm.IsListening) + ",\"server\":" + B(nm != null && nm.IsServer) +
            ",\"connected\":" + B(nm != null && nm.IsConnectedClient) + ",\"clientId\":" + Q(nm == null ? "" : nm.LocalClientId.ToString()) +
            ",\"players\":[" + string.Join(",", players) + "],\"rosterReady\":" + B(SahurWardrobe.RosterReady) +
            ",\"diagnosticsOnly\":" + B(diagnosticsOnly) +
            ",\"modelRow\":" + Q(identity?.AppliedRow ?? "") + ",\"modelDb\":" + (identity?.AppliedDb ?? -1) +
            ",\"modelSlug\":" + Q(identity?.CanonicalSlug ?? "") + ",\"modelReadiness\":" + Q(identity?.Readiness ?? "unknown") +
            ",\"bodyState\":" + Q(identity?.BodyState ?? "unknown") +
            ",\"uiBusy\":" + B(runtime == null || runtime.SequenceActive || UiDriver.Pending || File.Exists(Path.Combine(host.ScreensDir, "ui.request"))) +
            ",\"exitReady\":" + B(ButtonReady(ExitPath)) + ",\"confirmReady\":" + B(ButtonReady(ConfirmPath)) +
            ",\"codeReady\":" + B(ButtonReady(CodePath)) + ",\"connectReady\":" + B(ButtonReady(ConnectPath)) +
            ",\"fieldReady\":" + B(fields.Count == 1 && fields[0].interactable) +
            ",\"codeField\":" + Q(fields.Count == 1 ? fields[0].text : "") + "}";
    }

    private static string B(bool value) => value ? "true" : "false";
    private static string Q(string value)
    {
        var b = new StringBuilder("\"");
        foreach (char c in value ?? "")
        {
            if (c == '\\' || c == '"') b.Append('\\').Append(c);
            else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }
}
