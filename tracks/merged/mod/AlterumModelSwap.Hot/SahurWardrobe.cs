using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Reflection;
using AlterumModelSwap.Contracts;
using Gameplay.Appearance;
using Gameplay.PlayerSystem;
using Gameplay.Wardrobe;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using HarmonyLib;
using TMPro;
using UI.Elements;
using UI.MasterLayer.Lobby;
using UnityEngine;
using UnityEngine.UI;

namespace AlterumModelSwap.Hot;

// Custom models as selectable entries in the wardrobe model list. "Sahur" is the rigged variant
// (SahurRig): the CC BY 4.0 Roblox model with its own 71-bone Mixamo skeleton retargeted from this
// rig. The former rigid Sahur entry is no longer offered; the other roster rows (Golubiro,
// Capuchino, ...) are data in ModelRoster and use whichever renderer path their kind names.
//
// Each is a real `PlayerModelData` entry appended to `PlayerModelDatabase.Models`. The game's own
// selection path then applies, and `SyncSelection` maps "which entry is applied" to "which module
// shows its body", so exactly one entry is ever visible.
//
// The wardrobe builds one `WardrobeModelButtonedTab` per `PlayerModelData` and the list it walks is
// `PlayerModelDatabase.Models` (a static `PlayerModelData[]`; the wardrobe's model display keeps a
// `Dictionary<PlayerModelData, ThirdViewPlayerModel>` of previews). A model is selected by index:
// `WardrobeOptionalViewController.OnSelectButtonClicked` -> `ModelSelected(clientId, index)` ->
// `WardrobeController.OnModelSelectedServerRpc` -> `PlayersAppearanceManager.SetBaseModelIndex` ->
// `ApplyEffectiveModel` -> `PlayerModelSetuper.SetupModel(PlayerModelDatabase.Models[index])`.
// That is the game's own path, and it is also what persists the choice lobby -> map.
//
// So each entry is a runtime `PlayerModelData` cloned from one of the game's own (its third-view
// prefab is a real player body, so every native system keeps working), renamed, given a rendered
// portrait sprite, and appended. Everything visible is then replaced by the matching module, so the
// game body that got spawned is hidden exactly like the normal one.
//
// Nothing here runs before the lobby: in a map the wardrobe view controller does not exist, so the
// data/registration part is all that happens, which is what makes the entries appear when the lobby
// loads (the tabs are built from the list).
internal static class SahurWardrobe
{
    private const float DataSeconds = 5.0f;
    private const float SyncSeconds = 0.5f;
    private const float UiSeconds = 1.0f;
    private const float WireRetrySeconds = 10.0f;

    // Name marker of a row this mod inserted by hand (the game's own rows are 'Model Selectable(Clone)').
    private const string InsertMarker = "WardrobeModelButton_Sahur";

    // Tag of every line that says what a wardrobe pick became. Provenance is the subject of those
    // lines: the pick itself, the record it produced, and what was rejected instead.
    private const string ChoiceTag = "[sahur:choice]";

    private sealed class Entry
    {
        public string Label;                 // what the tab says and what the data object is named
        public string[] AdoptNames;          // names to adopt on a hot reload (includes old labels)
        public ModelSpec Spec;               // roster data: kind, assets, bone/piece table, licence
        public bool Rigged;
        public string BustSuffix, FullSuffix;
        public PlayerModelData Data;
        public Sprite Bust, Full;
        public Texture2D TexBust, TexFull;
        public int Index = -1;
        public bool Selected;
        public readonly List<WardrobeModelButtonedTab> Tabs = new();
    }

    // Order = registration order = ModelRoster.All (the game persists the selected index, so the
    // retained entries keep their leading positions). Every other difference between the entries -
    // which renderer path drives the body, which blob/portraits are embedded, which bone table
    // applies - is data in ModelRoster, not a branch here.
    private static readonly Entry[] Entries = Build();

    private static Entry[] Build()
    {
        var list = new List<Entry>();
        foreach (var spec in ModelRoster.All)
            list.Add(new Entry
            {
                Label = spec.Label, AdoptNames = spec.AdoptNames, Spec = spec,
                Rigged = spec.Kind == ModelKind.Rigged,
                BustSuffix = spec.BustSuffix, FullSuffix = spec.PortraitSuffix,
            });
        return list.ToArray();
    }

    // The native PlayerModelData every entry was cloned from. Each entry's own data is a key in the
    // displayer's instance map (SahurPreview.EnsureInstances adds the rows the displayer predates),
    // so the preview shows the entry's own instance; the donor is only the source of the clones.

    internal static ModelSpec SpecAt(int index) => index >= 0 && index < Entries.Length ? Entries[index].Spec : null;
    internal static int EntryCount => Entries.Length;

    // The game's sampler sees every custom row once registration completes.
    internal static bool RosterReady
    {
        get
        {
            foreach (var e in Entries) if (e.Data == null || e.Index < 0) return false;
            return Entries.Length > 0;
        }
    }

    // Diagnostic count of explicit wardrobe/request selections; never used to assign a model.
    internal static int ManualSelections { get; private set; }
    internal static void MarkManualSelection() => ManualSelections++;

    internal static string EntryLabel(int entryIndex) =>
        entryIndex >= 0 && entryIndex < Entries.Length ? Entries[entryIndex].Label : "?";

    internal static int EntryDatabaseIndex(int entryIndex) =>
        entryIndex >= 0 && entryIndex < Entries.Length ? Entries[entryIndex].Index : -1;

    // Apply one of our entries through the game's own path (ApplyNative -> SetBaseModelIndex).
    // Returns false when the row is not registered yet, so the caller can retry a moment later.
    // Nothing is written to disk; the pick is recorded in the same in-memory wanted state a
    // wardrobe confirm writes (see ApplyIndex), so an explicit programmatic pick survives the
    // native-wire remap instead of being read back as "the game's row".
    internal static bool ApplyEntry(IHostContext log, int entryIndex, string why = null)
    {
        if (entryIndex < 0 || entryIndex >= Entries.Length) return false;
        var entry = Entries[entryIndex];
        if (entry == null || entry.Data == null || entry.Index < 0) return false;
        return ApplyNative(log, entry, entry.Index, why);
    }

    private static PlayerModelSetuper _setuper;
    private static WardrobeOptionalViewController _view;
    private static Harmony _clickProbe;

    // Harmony owner id, unique per hot generation (the same convention MenuScope, LobbyNaming,
    // SceneSyncTrace and QcGate use). A FIXED id made a hot reload fail open: the candidate
    // generation installs its guard from HotModule.Initialize, and only THEN is the retiring
    // generation disposed - its UnpatchSelf would remove every patch owned by that shared id,
    // including the prefixes the fresh generation had just installed, while the fresh generation's
    // _wireGuard still said "installed". With a per-generation id, the retiring generation can only
    // withdraw its own hooks.
    private static readonly string ProbeId = "alterum.modelswap.wardrobeclick." + Guid.NewGuid().ToString("N");

    // The native-wire guard is a PRECONDITION for touching PlayerModelDatabase.Models at all, not a
    // diagnostic. True only between a COMPLETE InstallClickProbe and Dispose; every append is gated
    // on it AND on the proven native prefix (see EnsureNativePrefix), and every release path
    // withdraws the rows first (see WithdrawCustomRows).
    private static bool _wireGuard;
    private static int _wireAttempts;
    private static float _nextWireAttempt;

    // This generation's proof of the native prefix: the number of shipped rows at the FRONT of
    // `PlayerModelDatabase.Models`, i.e. the rows the game itself loaded from Resources. It is
    // proven from the shipped resource set (see EnsureNativePrefix), not read from a host-owned
    // member: a running host may predate that member, and the resource proof also works on a
    // database a previous generation already expanded. It is never derived from the current array
    // length, from where the first row this generation recognises sits, or from PlayerModelData.Index.
    // `_prefixKnown` is one-way in practice (the shipped set cannot change inside a process), so a
    // true is safe to cache; `_prefixFailed` records the one-way fail-closed proof.
    private static bool _prefixKnown;
    private static bool _prefixFailed;
    private static int _nativeCount;

    // The process's SHIPPED rows, once loaded: the PlayerModelData assets the game itself loads from
    // Resources "Data/Gameplay/Player" (alterum-re docs/PLAYER.md §3). Cached because the asset set
    // cannot change inside a process; compared by native pointer, never by name. Null means "not
    // loaded yet" and is retried, not "empty".
    private static IntPtr[] _shippedRows;
    private static float _nextShippedLoad;

    // The last few fail-closed / deferred-prune lines written, so a 0.5 s pass does not repeat them.
    // A small ring rather than one slot: two distinct errors (an unknown prefix and an unshrinkable
    // session) can alternate on every pass, and a single slot would log both again each time. The
    // wire hooks run on the game/network thread while Tick runs on the main thread, hence the locks.
    private static readonly List<string> WireNoteRing = new List<string>();
    private const int WireNoteRingSize = 8;
    private static readonly List<string> ShrinkNoteRing = new List<string>();
    private static readonly object ShrinkNoteLock = new object();
    private static IHostContext _probeLog;
    private static bool _lobbyShot;
    private static int _registers, _tabs;
    internal static int Rebuilds;

    // Bumps whenever the LOCAL model database mapping can have changed: a row appended or pruned,
    // an entry adopted its row, the view was (re)attached. ModelPresentation keys its HUD/wardrobe
    // projection off this counter, so a display refresh is an edge, never a per-frame scene scan.
    private static int _dbGeneration;
    internal static int DatabaseGeneration => _dbGeneration;
    internal static void TouchDatabase() => _dbGeneration++;
    private static float _nextRebuild;
    private static float _nextData, _nextSync, _nextUi, _nextLabels;

    internal static bool Selected { get { foreach (var e in Entries) if (e.Selected) return true; return false; } }

    // True while this module's own selection pass is writing the game's row. The wardrobe RPC hook
    // and the applied-row reader both use it to tell "this module applied the wanted entry" from
    // "the player clicked a tab": a selection this class drives must not be persisted as a
    // deliberate pick, and must not be read back as a new decision.
    internal static bool OurApply => _ourApply;

    // Which roster entry is the applied model, or -1. The wardrobe preview binds to this.
    internal static int SelectedIndex
    {
        get
        {
            for (int i = 0; i < Entries.Length; i++) if (Entries[i].Selected && Entries[i].Data != null) return i;
            return -1;
        }
    }

    internal static PlayerModelData DataAt(int index)
    {
        return index >= 0 && index < Entries.Length ? Entries[index].Data : null;
    }

    internal static bool SameData(PlayerModelData a, PlayerModelData b) => Same(a, b);

    // The row the wardrobe is currently *previewing* (the active TabGroup tab), as an entry index.
    // The preview body must follow a click immediately, before the Select button is pressed; using
    // the applied index made a click on a native row snap back to the applied model.
    internal static int PreviewIndex
    {
        get
        {
            try
            {
                var vc = _view;
                if (vc == null || vc.gameObject == null || !vc.gameObject.activeInHierarchy) return -1;
                var group = vc._modelsButtonsTabGroup;
                if (group == null) return -1;
                var tab = group.ActiveTab == null ? null : group.ActiveTab.TryCast<WardrobeModelButtonedTab>();
                if (tab == null) return -1;
                var data = tab.PlayerModelData;
                for (int i = 0; i < Entries.Length; i++)
                    if (Entries[i].Data != null && Same(data, Entries[i].Data)) return i;
            }
            catch { }
            return -1;
        }
    }
    internal static int Index { get { foreach (var e in Entries) if (e.Data != null) return e.Index; return -1; } }
    internal static bool HasData { get { foreach (var e in Entries) if (e.Data != null) return true; return false; } }
    internal static int Registers => _registers;

    // ================================================================== tick

    private static bool _credited;

    // Written ONLY by RecordWanted: the wardrobe's own confirm and the programmatic explicit
    // selectors record the same scoped intent. Never a preview, a join row or a random roll.
    private static string _wantedSlug; // null: follow game; empty: explicit native; otherwise roster slug
    private static ulong _wantedLobby, _wantedClient;

    // A guest's selection RPC has NO return value and NO acknowledgement: invoking it (or even the
    // game's own click handler returning) says only that a send was attempted. When the only
    // evidence behind the wanted record is such a send, the record is marked PENDING here and is
    // cleared only by ConfirmPending, once this client's own applied row is the row the send named
    // (or our entry's row, when a modded host applied it directly).
    private static bool _wantedPending;
    private static ushort _wantedPendingRow;      // the shipped row the pending send carried
    private static IntPtr _wantedPendingFrom;     // the applied row at send time, so a no-op is visible

    internal static void Tick(IHostContext log, float dt)
    {
        if (log == null) return;
        CheckWantedScope(log);
        // Prove the native prefix as soon as both the live database and the shipped resource set are
        // readable: it is the one value every later decision needs. The proof is the resource set, so
        // it also works on a database a previous generation already expanded; an unprovable front
        // fails closed instead of guessing a boundary.
        if (!_prefixKnown) EnsureNativePrefix(log);
        if (!_credited)
        {
            // Credits live here as well as in the csproj next to the embedded resources and in
            // notes/wardrobe-variants2.md - the same three places the Sahur credits live.
            _credited = true;
            try
            {
                log.LogInfo($"[sahur:wardrobe] roster: {Entries.Length} entries ({Entries.Length - 0} of " +
                            $"{PlayerModelDatabase.Models?.Length ?? 0} PlayerModelData rows are ours)");
                foreach (var e in Entries)
                {
                    var spec = e.Spec;
                    if (spec == null) continue;
                    log.LogInfo($"[sahur:wardrobe] entry '{e.Label}' index={e.Index} kind={spec.Kind} " +
                                $"asset={spec.BlobSuffix} credit: {spec.Credit}");
                    if (spec.DemoOnly)
                        log.LogWarning($"[sahur:wardrobe] '{e.Label}' is DEMO ONLY - NOT FOR DISTRIBUTION " +
                                       $"({(string.IsNullOrEmpty(spec.DemoReason) ? "no licence" : spec.DemoReason)}): " +
                                       "do not ship " +
                                       spec.BlobSuffix + ", " + spec.AlbedoSuffix + ", " +
                                       spec.PortraitSuffix + ", " + spec.BustSuffix);
                }
            }
            catch (Exception e2) { log.LogWarning($"[sahur:wardrobe] credit log failed: {e2.Message}"); }
        }
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }

        if (now >= _nextData)
        {
            _nextData = now + DataSeconds;
            foreach (var e in Entries)
            {
                if (e.Data != null) continue;
                try
                {
                    EnsureData(log, e);
                    // A freshly built row must be visible immediately, not on the next 0.5 s pass.
                    if (e.Data != null) EnsureRegistered(log, e);
                }
                catch (Exception ex) { log.LogWarning($"[sahur:wardrobe] data build failed for '{e.Label}': {ex.Message}"); }
            }
        }
        // Registration and retirement only change when the database or roster changes, so they run
        // at the selection-sync rate. Doing both every frame was ~0.5 ms/frame in the lobby.
        if (now >= _nextSync)
        {
            _nextSync = now + SyncSeconds;
            // A failed install must not leave custom rows in the sender's index space. Retry here
            // as well as from Attach: the wardrobe view (Attach's caller) only exists in the lobby,
            // so a failure in a map would otherwise never recover. Paced by InstallClickProbe.
            // The withdrawal is the failsafe, not hygiene, but it never shrinks a live session:
            // with the game's dictionaries live, removing a row can retarget or truncate an index
            // that is still held. In that state it reports CANNOT PROTECT PEERS instead.
            if (!_wireGuard)
            {
                try { InstallClickProbe(log); } catch (Exception e2) { log.LogWarning($"[sahur:click] retry failed: {e2.Message}"); }
                if (!_wireGuard)
                    try { WithdrawCustomRows(log, "native-wire guard is not installed"); }
                    catch (Exception e2) { log.LogWarning($"[model:native-wire] withdraw failed: {e2.Message}"); }
            }
            foreach (var e in Entries)
            {
                if (e.Data == null) continue;
                try { EnsureRegistered(log, e); } catch (Exception ex) { log.LogWarning($"[sahur:wardrobe] register failed for '{e.Label}': {ex.Message}"); }
            }
            try { PruneRetired(log); } catch (Exception ex) { log.LogWarning($"[sahur:wardrobe] retire failed: {ex.Message}"); }
            try { SyncSelection(log); } catch (Exception e2) { log.LogWarning($"[sahur:wardrobe] selection read failed: {e2.Message}"); }
        }
        if (now >= _nextUi)
        {
            _nextUi = now + UiSeconds;
            // The wardrobe view only exists in the lobby. In a map this used to walk the object
            // table once a second looking for it and finding nothing.
            bool lobby = false;
            try
            {
                string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
                lobby = sc.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }
            if (lobby) { try { Attach(log); } catch (Exception e2) { log.LogWarning($"[sahur:wardrobe] ui failed: {e2.Message}"); } }
            else _view = null;
        }
        else if (_view != null && now >= _nextLabels)
        {
            // The tab's localization coroutine writes the donor's name once per enable, so the label
            // is re-asserted while the wardrobe is up. 0.25 s is far faster than a user can see and
            // avoids walking every tab on every frame (the old cost was ~0.5 ms/frame).
            _nextLabels = now + 0.25f;
            try { AssertLabels(); } catch { }
        }
    }

    internal static void Dispose(IHostContext log)
    {
        // The rows this module appended are about to lose their wire guard (UnpatchSelf below), and
        // PlayerModelDatabase.Models is the game's whole random-assignment index space: a row left
        // in it would be selectable by an unmodified peer that cannot resolve the index
        // (alterum-re docs/NETWORKING.md §5). Withdraw first, while every mandatory prefix is still
        // live - but a LIVE SESSION forbids the shrink (the game's dictionaries can still hold an
        // index above the prefix), so on a reload the withdrawal reports that it cannot protect
        // peers and the rows stay for the next generation to adopt. The next generation re-registers
        // only after a complete install, and drops rows once the session is over.
        bool hadGuard = _wireGuard, hadPrefix = _prefixKnown;
        int prefixCount = _nativeCount;
        try { WithdrawCustomRows(log, "module disposed"); }
        catch (Exception e) { log?.LogWarning($"[model:native-wire] withdraw on dispose failed: {e.Message}"); }
        foreach (var e in Entries) e.Tabs.Clear();
        try { _clickProbe?.UnpatchSelf(); }
        catch (Exception e)
        {
            // The rows are already withdrawn where that was safe; an unpatch that failed leaves
            // prefixes attached with a disposed owner. Say so: "no guard" is a claim this
            // generation can no longer verify.
            log?.LogWarning($"[model:native-wire] UnpatchSelf failed while disposing: {e.Message}; " +
                            "some native-wire prefixes may still be installed by this generation");
        }
        _clickProbe = null;
        _wireGuard = false;
        _probeLog = null;
        // The proof is this generation's; the next generation proves the same shipped set again.
        _prefixKnown = false;
        _view = null;
        _setuper = null;
        if (log != null)
            try
            {
                var sb = new StringBuilder();
                foreach (var e in Entries)
                    sb.Append($"'{e.Label}' data='{(e.Data == null ? "none" : e.Data.name)}' index={e.Index} ");
                log.LogInfo($"[sahur:wardrobe] state kept {sb}registers={_registers} " +
                            $"wire={(hadGuard ? "guard" : "NO-GUARD")} " +
                            $"prefix={(hadPrefix ? prefixCount.ToString() : "unknown")}");
            }
            catch { }
    }


    // Diagnostic probe for the acceptance evidence: logs every call the game's own wardrobe RPC
    // reaches. Purely observational (a postfix), removed with the generation on hot reload.
    internal static void Install(IHostContext log) => InstallClickProbe(log);

    // Register at the join boundary before the game's spawn/assignment implementation runs.
    // Unlike hooking a tiny sampler, this also covers native builds that inline random selection.
    // Reading the original availability result below never adds occupied rows back to it.
    private static void BeforeGameSpawn(ulong clientId)
    {
        foreach (var e in Entries)
        {
            if (e.Data == null) EnsureData(_probeLog, e);
            if (e.Data != null) EnsureRegistered(_probeLog, e);
        }
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<PersistentData.PlayersModelsNetworkData>());
            for (int k = 0; all != null && k < all.Length; k++)
            {
                var network = all[k].TryCast<PersistentData.PlayersModelsNetworkData>();
                if (network == null || !network.IsSpawned || !network.IsServer) continue;
                var available = network.GetAvailablesModelsIndexes();
                var rows = new List<ushort>();
                int custom = 0;
                for (int i = 0; available != null && i < available.Count; i++)
                {
                    ushort index = available[i];
                    rows.Add(index);
                    if (!string.IsNullOrEmpty(SlugOfData(DatabaseRowAt(index)))) custom++;
                }
                _probeLog?.LogInfo($"[model:game-pool] client={clientId} database={DatabaseCount} " +
                                   $"available=[{string.Join(",", rows)}] customAvailable={custom} " +
                                   "source=original-game (read only; no mod roll or availability rewrite)");
                break;
            }
        }
        catch (Exception e) { _probeLog?.LogWarning($"[model:game-pool] trace failed: {e.Message}"); }
    }

    private static void InstallClickProbe(IHostContext log)
    {
        _probeLog = log;
        if (_clickProbe != null) return;
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (_wireAttempts > 0 && now < _nextWireAttempt) return;
        _wireAttempts++;
        _nextWireAttempt = now + WireRetrySeconds;
        try
        {
            var m = AccessTools.DeclaredMethod(typeof(PlayersAppearanceManager), "SetBaseModelIndex",
                                               new[] { typeof(ulong), typeof(ushort) });
            if (m == null) throw new MissingMethodException("PlayersAppearanceManager.SetBaseModelIndex");
            var post = typeof(SahurWardrobe).GetMethod(nameof(OnSetBaseModelIndex), BindingFlags.Static | BindingFlags.NonPublic);
            _clickProbe = new Harmony(ProbeId);
            _clickProbe.Patch(m, prefix: new HarmonyMethod(typeof(SahurWardrobe), nameof(SafeModelIndex)), postfix: new HarmonyMethod(post));
            var save = AccessTools.DeclaredMethod(typeof(PersistentData.PlayersModelsNetworkData), "SaveModel", new[] { typeof(ulong), typeof(ushort) });
            if (save == null) throw new MissingMethodException("PlayersModelsNetworkData.SaveModel");
            _clickProbe.Patch(save, prefix: new HarmonyMethod(typeof(SahurWardrobe), nameof(SafeSavedModelIndex)));
            var link = AccessTools.DeclaredMethod(typeof(Network.Spawners.PlayersModelSetuper), "LinkPlayerModelToPlayer_ServerRpc", new[] { typeof(ulong), typeof(ushort) });
            if (link == null) throw new MissingMethodException("PlayersModelSetuper.LinkPlayerModelToPlayer_ServerRpc");
            _clickProbe.Patch(link, prefix: new HarmonyMethod(typeof(SahurWardrobe), nameof(SafeModelIndex)));
            var confirm = AccessTools.DeclaredMethod(typeof(Gameplay.Wardrobe.WardrobeController), "OnModelSelected", new[] { typeof(ulong), typeof(ushort) });
            if (confirm == null) throw new MissingMethodException("WardrobeController.OnModelSelected");
            _clickProbe.Patch(confirm, prefix: new HarmonyMethod(typeof(SahurWardrobe), nameof(OnLocalModelConfirmed)));
            var spawn = AccessTools.DeclaredMethod(typeof(Network.Spawners.PlayersModelSetuper),
                                                   "SpawnPlayerModelOnServer", new[] { typeof(ulong) });
            if (spawn == null) throw new MissingMethodException("PlayersModelSetuper.SpawnPlayerModelOnServer");
            _clickProbe.Patch(spawn, prefix: new HarmonyMethod(typeof(SahurWardrobe), nameof(BeforeGameSpawn)));
            // Only a COMPLETE install opens the gate. Every row this module appends is an index the
            // game may replicate (SaveModel, LinkPlayerModelToPlayer_ServerRpc, SetBaseModelIndex),
            // and a stock receiver indexes its own array with it without a bounds check. A partial
            // install (one prefix missing) is therefore as unsafe as no install at all.
            _wireGuard = true;
            // Opening the gate is not enough to append: EnsureRegistered ALSO needs the proven
            // native prefix, so a process whose database is not readable (or whose front is not the
            // shipped resource set) still registers nothing. Asking here makes the fail-closed
            // reason appear once, at the install line, instead of on the first registration pass.
            bool prefixKnown = EnsureNativePrefix(log);
            log.LogInfo("[sahur:wardrobe] original-game assignment: custom rows registered before spawn; " +
                        "no saved-choice restore, mod reroll, or shared occupied rows; native-wire guard " +
                        $"installed; shipped prefix={(prefixKnown ? _nativeCount.ToString() : "not known")}");
        }
        catch (Exception e)
        {
            _wireGuard = false;
            string message = $"[sahur:click] patch install failed (attempt {_wireAttempts}): {e.Message}";
            if (_wireAttempts == 1)
                log.LogWarning(message + " - FAIL CLOSED: no custom row is registered until every " +
                                         "mandatory native-wire prefix is installed");
            else log.LogInfo(message + " - still failing closed");
            try { _clickProbe?.UnpatchSelf(); }
            catch (Exception w)
            {
                log.LogWarning($"[model:native-wire] UnpatchSelf failed after a partial install: {w.Message}; " +
                               "some native-wire prefixes may remain installed while _wireGuard is false");
            }
            _clickProbe = null;
            // A previous generation (or an earlier partial attempt) may already have appended rows.
            // They are selectable by the game's random assignment right now, so they cannot wait for
            // the next sync pass. The withdrawal cuts only the provable tail of our own rows and
            // never a shipped row - and if a live session makes even that unsafe it reports that the
            // peers cannot be protected rather than shifting a live index.
            try { WithdrawCustomRows(log, "native-wire patch install failed"); }
            catch (Exception w) { log.LogWarning($"[model:native-wire] withdraw failed: {w.Message}"); }
        }
    }

    // Native code can inline the random sampler. Guard the model map, appearance
    // snapshot, and direct model-link RPC; custom identity travels separately as a slug.
    private static void CheckWantedScope(IHostContext log)
    {
        if (_wantedSlug == null) return;
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null && nm.IsListening && nm.IsConnectedClient && Steam.SteamLobbyManager.IsInLobby &&
                (ulong)Steam.SteamLobbyManager.CurrentLobby.Id == _wantedLobby && nm.LocalClientId == _wantedClient) return;
        }
        catch { }
        _wantedSlug = null;
        _wantedPending = false;
        _wantedPendingRow = 0;
        _wantedPendingFrom = IntPtr.Zero;
        InvalidatePresentation(log, "selection session ended");
    }

    // This local delegate is fired only on confirmed wardrobe selection, never on preview or join.
    // A custom choice stays mod-side; the RPC carries the player's current native donor instead.
    //
    // The index is checked against the proven shipped prefix, NOT against a scan of our rows:
    // the row may be one we appended, a shipped row, or a STALE index whose row no longer exists
    // (a shrink, a row list rebuilt by a previous generation, a saved value). Only the first
    // nativeCount rows are resolvable on a peer without the mod, so everything else is clamped.
    //
    // THE CLAMP DOES NOT DEPEND ON THE SESSION, THE WANTED RECORD OR _ourApply. A record can fail (no
    // scoped lobby, a transient read error) and this module's own apply raises _ourApply - the old
    // order returned on either before rewriting the index, so the raw custom row went to the game's
    // RPC and a stock host resolved it with no bounds check. The pure plan runs first, unconditionally;
    // the gates below only decide whether the intent is recorded.
    private static void OnLocalModelConfirmed(ulong localClientId, ref ushort chosenModelIndex)
    {
        try
        {
            ushort requested = chosenModelIndex;
            bool prefixKnown = KnownPrefix(_probeLog, out int nativeCount);
            var row = prefixKnown ? DatabaseRowAt(requested) : null;
            var slug = SlugOfData(row);
            // The donor is only consulted for an index past the prefix; a shipped-row click needs no
            // object-table scan at all.
            int donor = prefixKnown && requested >= nativeCount ? NativeDonorRow(_probeLog, nativeCount) : -1;

            var plan = NativeWirePolicy.PlanConfirm(prefixKnown, nativeCount, requested, row != null,
                                                     slug, donor, localClientId);
            // THE CLAMP IS FIRST AND UNCONDITIONAL. It used to sit behind the session/lobby/own-apply
            // gates, so a confirmation outside a scoped lobby - and this module's OWN apply - carried
            // the raw index to the game's RPC. Whether the intent may be RECORDED is decided below;
            // nothing below may put plan.Index back.
            chosenModelIndex = plan.Index;
            if (plan.Branch == ConfirmBranch.NoPrefix)
            {
                // No shipped boundary: no index can be shown to be resolvable on a stock peer. Row 0
                // exists in every loaded database, so it is the only safe value to confirm.
                NoNativePrefix(_probeLog, "local confirmation");
                if (requested != 0)
                    _probeLog?.LogError($"[model:wanted] client={localClientId} confirmed={requested} -> 0 " +
                                        "(no captured native prefix; row 0 is the only guaranteed row)");
            }

            // Intent gating starts here. It can suppress the record; it cannot change the wire value.
            if (_ourApply)
            {
                _probeLog?.LogInfo($"[model:wanted] client={localClientId} confirmed={requested} -> " +
                                   $"{chosenModelIndex} is this module's own apply: the wire is clamped, " +
                                   "the wanted intent is unchanged");
                return;
            }
            if (!ConfirmSession(localClientId, out string session))
            {
                _probeLog?.LogInfo($"[model:wanted] client={localClientId} confirmed={requested} -> " +
                                   $"{chosenModelIndex}: the wire is clamped, but the intent is unchanged " +
                                   $"({session})");
                return;
            }
            if (plan.Record && !RecordWanted(_probeLog, plan.Slug, "local wardrobe confirm", !NetRole.IsServer, plan.Index))
                _probeLog?.LogWarning($"[model:wanted] client={localClientId} could not record wanted=" +
                                      $"'{(plan.Slug.Length == 0 ? "<native>" : plan.Slug)}'; the wire index " +
                                      "is still clamped to a shipped row");
            else if (plan.Record && !NetRole.IsServer)
                // On a client the confirm is an RPC SEND to a host that owns the row. The pick is the
                // player's (so it is recorded), but it is not a landed selection: the record stays
                // pending until this body carries the row the send named.
                _probeLog?.LogInfo($"[model:wanted] client={localClientId} " +
                                   $"slug='{(plan.Slug.Length == 0 ? "<native>" : plan.Slug)}' " +
                                   $"wireRow={plan.Index} state=PENDING (the confirm RPC was sent; only " +
                                   "this body carrying the row confirms it)");

            switch (plan.Branch)
            {
                case ConfirmBranch.NoPrefix:
                    // Reported above, before the intent gate: the clamp never waits for it.
                    break;
                case ConfirmBranch.StaleRow:
                    // A stale index: the row it named is gone. Nothing to record, and the index itself
                    // may be past every peer's table.
                    _probeLog?.LogWarning($"[model:wanted] client={localClientId} confirmed={requested} names no " +
                                          $"current row -> donor={chosenModelIndex}");
                    break;
                case ConfirmBranch.NativeRow:
                    // A shipped row inside the captured prefix: it is what the player picked.
                    _probeLog?.LogInfo($"[model:wanted] client={localClientId} native={chosenModelIndex}");
                    break;
                case ConfirmBranch.ForeignRow:
                    // Past the shipped prefix and not ours: a foreign or stale row, unresolvable on a
                    // stock peer. Clamp it like one of ours and keep the deliberate-native intent.
                    _probeLog?.LogWarning($"[model:wanted] client={localClientId} confirmed={requested} is past the " +
                                          $"{nativeCount} shipped row(s) and is not ours -> donor={chosenModelIndex}");
                    break;
                default:
                    _probeLog?.LogInfo($"[model:wanted] client={localClientId} slug={slug} donor={chosenModelIndex}");
                    break;
            }
        }
        catch (Exception e) { _probeLog?.LogWarning($"[model:wanted] confirm failed: {e.Message}"); }
    }

    // Is this confirmation a decision of the LOCAL player inside a lobby session the wanted record
    // can be scoped to? Intent only: it never decides what travels to the host.
    private static bool ConfirmSession(ulong localClientId, out string why)
    {
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null) { why = "no NetworkManager"; return false; }
            if (!nm.IsListening) { why = "no network session"; return false; }
            if (!nm.IsConnectedClient) { why = "not a connected client"; return false; }
            if (nm.LocalClientId != localClientId) { why = $"confirmation for client {localClientId}, local is {nm.LocalClientId}"; return false; }
            if (!Steam.SteamLobbyManager.IsInLobby) { why = "not in a lobby"; return false; }
            why = "lobby client " + localClientId;
            return true;
        }
        catch (Exception e) { why = "unreadable session (" + e.Message + ")"; return false; }
    }

    // The ONE write path for the wanted intent: which model THIS client deliberately picked, kept
    // apart from the native donor row that travels over the wire (RemapModelIndex rewrites our row,
    // because a peer without the mod cannot resolve it). Scoped to the lobby + client that recorded
    // it; CheckWantedScope drops the record as soon as either changes.
    //
    // Both the wardrobe's own confirm (OnLocalModelConfirmed) and the programmatic explicit
    // selectors (`select=`, `apply`, `index=`, and `alterum.test.select-model ... protocol`, all via
    // ApplyNative/ApplyIndex) write here, because they mean the same thing by the same scoped state.
    // Recording the intent only on the UI confirm made an explicit programmatic pick come back as
    // "follow the game's row" after the native-wire remap replaced it with a donor: the picked
    // custom body was released again and `apply`/`index=`/`select=` looked like they did nothing.
    // Preview, the game's own join/random row and the mod's random roll are not choices and never
    // call this.
    //
    // `slug` is the entry's roster slug, or "" for a deliberate native row. `pending` marks a
    // record whose only evidence is a guest's selection RPC (see _wantedPending). False when there
    // is no local lobby session to scope the record to; the wanted state is then left untouched.
    private static bool RecordWanted(IHostContext log, string slug, string reason, bool pending = false,
                                     ushort pendingRow = 0)
    {
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsConnectedClient || !Steam.SteamLobbyManager.IsInLobby)
                return false;
            _wantedLobby = (ulong)Steam.SteamLobbyManager.CurrentLobby.Id;
            _wantedClient = nm.LocalClientId;
            _wantedSlug = slug ?? "";
            _wantedPending = pending;
            _wantedPendingRow = pendingRow;
            _wantedPendingFrom = pending ? AppliedPointer(log) : IntPtr.Zero;
            InvalidatePresentation(log, reason);
            return true;
        }
        catch (Exception e) { log?.LogWarning($"[model:wanted] record failed: {e.Message}"); return false; }
    }

    private static void SafeSavedModelIndex(ulong clientId, ref ushort modelId) => RemapModelIndex(clientId, ref modelId);
    private static void SafeModelIndex(ulong clientId, ref ushort modelIndex) => RemapModelIndex(clientId, ref modelIndex);

    private static void RemapModelIndex(ulong clientId, ref ushort index)
    {
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !nm.IsListening) return;
            // The shipped prefix is a proven fact about the resource set. Deriving it here from the
            // first row this generation recognises would call an older generation's row native (its
            // pointer is not in this generation's Entries until adoption runs) and put that index on
            // the wire - the exact replication a stock peer cannot resolve.
            if (!KnownPrefix(_probeLog, out int nativeCount))
            {
                NoNativePrefix(_probeLog, "wire remap");
                if (index != 0)
                {
                    _probeLog?.LogError($"[model:native-wire] client={clientId} requested={index} -> 0 " +
                                        "(no captured native prefix; row 0 is the only guaranteed row)");
                    index = 0;
                }
                return;
            }
            if (index < nativeCount) return;
            ushort original = index;
            // Preserve the shipped row this player already carries - only for the LOCAL client: the
            // applied row of any other client cannot be read here, and this host's own body is not
            // that client's donor. A body rig is built over THAT native actor; an arbitrary
            // `clientId % nativeCount` row can be a different model with no relation to the pick.
            int donor = clientId == nm.LocalClientId ? NativeDonorRow(_probeLog, nativeCount) : -1;
            index = NativeWirePolicy.ShippedWireIndex(true, nativeCount, original, donor, clientId);
            _probeLog?.LogInfo($"[model:native-wire] client={clientId} requested={original} native={index} " +
                               $"nativeCount={nativeCount} donor={donor}");
        }
        catch (Exception e) { _probeLog?.LogWarning($"[model:native-wire] failed: {e.Message}"); }
    }

    private static void OnSetBaseModelIndex(ulong clientId, ushort modelIndex)
    {
        // Observational only - no adoption. A row CHANGE is not a decision by itself: the join-time
        // row the lobby re-applies (`SpawnPlayerModelOnClient` -> SetupModel) satisfies "not our own
        // apply and this client's id" exactly as well as a click does, so recording a choice here
        // recorded the game's stale row as the player's (and rewrote the cfg with it). Provenance
        // now comes from the wardrobe's own select RPC, which IS the click: NoteLocalWardrobePick.
        try
        {
            _probeLog?.LogInfo($"[sahur:click] PlayersAppearanceManager.SetBaseModelIndex(client={clientId}, index={modelIndex}) " +
                               $"(the native wardrobe selection RPC, {(_ourApply ? "this module's own force" : "the game's own call")})");
        }
        catch { }
    }

    // ================================================================== data

    private static PlayerModelData Donor(IHostContext log)
    {
        PlayerModelData first = null;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<PlayerModelData>());
            for (int i = 0; i < arr.Length; i++)
            {
                var d = arr[i].TryCast<PlayerModelData>();
                if (d == null) continue;
                if (d.ThirdViewPlayerModel == null) continue;
                if (first == null) first = d;
                string n = d.name ?? "";
                if (n.IndexOf("James", StringComparison.OrdinalIgnoreCase) >= 0) return d;
            }
        }
        catch (Exception e) { log?.LogWarning($"[sahur:wardrobe] donor scan failed: {e.Message}"); }
        return first;
    }

    private static bool IsTaken(string name, Entry self)
    {
        foreach (var e in Entries)
        {
            if (e == self || e.Data == null) continue;
            if (string.Equals(e.Data.name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void EnsureData(IHostContext log, Entry entry)
    {
        var donor = Donor(log);
        if (donor == null) { log.LogWarning("[sahur:wardrobe] no PlayerModelData with a third-view prefab; cannot build the entry"); return; }

        Sprite bust = Icon(log, entry.BustSuffix, out entry.TexBust);
        Sprite full = Icon(log, entry.FullSuffix, out entry.TexFull);
        entry.Bust = bust;
        entry.Full = full;

        // Hot reloads keep the native PlayerModelDatabase array but lose our managed state, so a
        // previous generation's entry is still there by name: adopt it instead of appending twice.
        PlayerModelData d = null;
        string adoptedAt = null, adoptedAs = null;
        var existing = PlayerModelDatabase.Models;
        for (int n = 0; n < entry.AdoptNames.Length && d == null; n++)
        {
            for (int i = 0; existing != null && i < existing.Length; i++)
            {
                var e = existing[i];
                if (e == null || (e.name ?? "") != entry.AdoptNames[n]) continue;
                if (IsTaken(e.name, entry)) continue;            // another entry already owns this one
                d = e;
                adoptedAt = $"index {i}";
                adoptedAs = e.name;
                break;
            }
        }
        if (d != null)
        {
            log.LogInfo($"[sahur:wardrobe] adopted the existing '{adoptedAs}' entry at {adoptedAt} as " +
                        $"'{entry.Label}' (previous generation)");
        }
        else
        {
            var cloneObj = UnityEngine.Object.Instantiate(donor);
            d = cloneObj?.TryCast<PlayerModelData>();
            if (d == null) { log.LogWarning("[sahur:wardrobe] PlayerModelData clone failed"); return; }
            log.LogInfo($"[sahur:wardrobe] entry '{entry.Label}' cloned from '{donor.name}'");
        }
        try { d.name = entry.Label; } catch { }
        try { d._bustPortraitSprite = bust ?? full; } catch (Exception e) { log.LogWarning($"[sahur:wardrobe] portrait assign failed: {e.Message}"); }
        // Do NOT null _modelNameLocalizedString: WardrobeModelButtonedTab.OnEnabled starts a coroutine
        // that localizes that string and dereferences it, so a null one throws a NullReferenceException
        // every time the wardrobe opens. Entries adopted from a generation that did null it get the
        // donor's back here; our own label is asserted over it (StyleOurs / AssertLabels).
        try
        {
            if (d._modelNameLocalizedString == null) d._modelNameLocalizedString = donor._modelNameLocalizedString;
            if (d._modelNameLocalizedString == null)
                log.LogWarning($"[sahur:wardrobe] '{entry.Label}' has no localized name to fall back on; the tab's name coroutine will throw");
        }
        catch (Exception e) { log.LogWarning($"[sahur:wardrobe] localized name restore failed: {e.Message}"); }
        try
        {
            // The donor's portrait layers (hair, eyes, accessories drawn over the bust) belong to
            // the donor's face. An empty layer set keeps the composite view to our own portrait.
            var layers = d._portraitLayerSet;
            if (layers != null)
            {
                var empty = UnityEngine.Object.Instantiate(layers)?.TryCast<CharacterCustomization.PortraitLayerSet>();
                if (empty != null)
                {
                    empty._layers = new Il2CppReferenceArray<CharacterCustomization.PortraitLayerEntry>(0);
                    empty.name = entry.Label.Replace(" ", "") + "_PortraitLayerSet";
                    d._portraitLayerSet = empty;
                    log.LogInfo($"[sahur:wardrobe] portrait layer set '{layers.name}' -> empty clone for '{entry.Label}'");
                }
            }
        }
        catch (Exception e) { log.LogWarning($"[sahur:wardrobe] portrait layer set failed: {e.Message}"); }

        entry.Data = d;
        TouchDatabase();
        ModelPresentation.Invalidate(log, $"entry '{entry.Label}' adopted its row");
        log.LogInfo($"[sahur:wardrobe] entry '{d.name}' from '{donor.name}' " +
                    $"rigged={entry.Rigged} gender={d.Gender} " +
                    $"thirdPerson='{(d.ThirdViewPlayerModel == null ? "null" : d.ThirdViewPlayerModel.gameObject.name)}' " +
                    $"portraitBust={(bust == null ? "null" : ((int)bust.rect.width) + "x" + ((int)bust.rect.height))} " +
                    $"portraitFull={(full == null ? "null" : ((int)full.rect.width) + "x" + ((int)full.rect.height))}");
    }

    // The portraits are the same meshes the runtime builds, rasterised offline by
    // tracks/merged/tools/sahur_roblox_portrait.py (rigged) and the variants2 tools, RGBA with a
    // transparent background and embedded.
    private static Sprite Icon(IHostContext log, string suffix, out Texture2D tex)
    {
        tex = null;
        try
        {
            var asm = typeof(SahurWardrobe).Assembly;
            string name = null;
            foreach (var n in asm.GetManifestResourceNames())
                if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = n; break; }
            if (name == null) { log.LogWarning($"[sahur:wardrobe] no embedded '{suffix}'"); return null; }
            byte[] png;
            using (var s = asm.GetManifestResourceStream(name)) png = ReadAll(s);
            if (png == null) { log.LogWarning($"[sahur:wardrobe] '{name}' empty"); return null; }
            int w, h;
            var rgba = PngDecoder.DecodeRgbaBottomUp(png, out w, out h);
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.name = "Sahur_Portrait_" + Path.GetFileNameWithoutExtension(suffix);
            tex.LoadRawTextureData(new Il2CppStructArray<byte>(rgba));
            tex.Apply(false, false);
            var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "Sahur_Portrait_" + Path.GetFileNameWithoutExtension(suffix);
            log.LogInfo($"[sahur:wardrobe] portrait '{name}' {w}x{h} -> sprite {(int)sprite.rect.width}x{(int)sprite.rect.height}");
            return sprite;
        }
        catch (Exception e) { log.LogWarning($"[sahur:wardrobe] portrait decode failed: {e.Message}"); return null; }
    }

    private static byte[] ReadAll(Stream s)
    {
        if (s == null) return null;
        var bytes = new byte[s.Length];
        int read = 0;
        while (read < bytes.Length)
        {
            int n = s.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return bytes;
    }

    // ================================================================== registration

    private static bool Same(PlayerModelData a, PlayerModelData b)
    {
        try { return a != null && b != null && a.Pointer == b.Pointer; } catch { return false; }
    }

    // Is this one of our runtime rows? Used by the [mapbody] trace to tell "the game applied a
    // native row" from "the game applied ours" without duplicating the pointer comparison.
    internal static bool IsOurData(PlayerModelData d)
    {
        if (d == null) return false;
        foreach (var e in Entries) if (Same(e.Data, d)) return true;
        return false;
    }

    // One line for the [mapbody] census: roster readiness, the applied row and every entry's state.
    internal static string MapBodyState()
    {
        var sb = new StringBuilder(160);
        sb.Append("roster=").Append(RosterReady ? "ready" : "NOT-ready").Append(' ');
        try
        {
            var models = PlayerModelDatabase.Models;
            sb.Append("models=").Append(models == null ? -1 : models.Length).Append(' ');
        }
        catch { }
        sb.Append("entries=");
        bool first = true;
        for (int i = 0; i < Entries.Length; i++)
        {
            var e = Entries[i];
            if (e.Index < 0) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append(i).Append(':').Append(e.Index);
            if (e.Selected) sb.Append('*');
        }
        return sb.ToString();
    }

    private static void EnsureRegistered(IHostContext log, Entry entry)
    {
        // FAIL CLOSED. Every row of PlayerModelDatabase.Models is an index the game may put on the
        // wire: GetRandomModelIndex walks the array (and, when the availability pool is empty, hands
        // out a DUPLICATE RANDOM ROW from the whole array - `RandomUtilities.GetRandom(models.Length)`,
        // not an index past the end), SaveModel/LinkPlayerModelToPlayer_ServerRpc replicate the value,
        // and the receiving SpawnPlayerModelOnClient/OnServer index their own array with no bounds
        // check (alterum-re docs/NETWORKING.md §5). Two things keep our rows off that wire: the mandatory
        // prefixes in InstallClickProbe (which rewrite the value) and the resource-proven shipped
        // prefix (which says where the safe range ends). Without BOTH there is no safe row to add;
        // the entries keep building data and are appended by a later guarded pass.
        if (!_wireGuard) return;
        if (entry == null || entry.Data == null) return;
        var models = PlayerModelDatabase.Models;
        if (models == null)
        {
            try { PlayerModelDatabase.Initialize(); } catch { }
            models = PlayerModelDatabase.Models;
        }
        if (models == null || models.Length == 0)
        {
            // Appending into an empty array would make OUR row index 0 with no native prefix to
            // stand on; every peer's row 0 is a shipped model and ours is not.
            log?.LogWarning($"[sahur:wardrobe] refusing to register '{entry.Label}': " +
                            "PlayerModelDatabase.Models has no native rows to stand on");
            return;
        }
        // No append can happen before this: EnsureNativePrefix proves the FRONT of the live array
        // against the shipped resource set, which stays true when a previous generation already
        // appended rows (they follow the shipped ones). A front that does not match fails closed
        // and keeps the gate shut (see EnsureNativePrefix).
        if (!EnsureNativePrefix(log)) return;
        if (models.Length < _nativeCount)
        {
            // The array is shorter than the shipped set this generation proved, so it is not the
            // array the proof describes (re-initialised, replaced, or truncated elsewhere). Neither
            // the prefix nor a new row at the end can be trusted here.
            NoteWire(log, $"[model:native-wire] refusing to register: PlayerModelDatabase.Models has " +
                           $"{models.Length} rows, fewer than the {_nativeCount} shipped row(s) proven " +
                           "from the resource set; leaving the database untouched");
            return;
        }
        int n = models.Length;
        for (int i = 0; i < n; i++)
        {
            if (!Same(models[i], entry.Data)) continue;
            if (entry.Index != i)
            {
                if (entry.Index >= 0 && log != null)
                    log.LogWarning($"[sahur:wardrobe] model list was rebuilt; '{entry.Label}' moved {entry.Index} -> {i}");
                entry.Index = i;
                try { entry.Data._index = (ushort)i; } catch { }
            }
            PruneDuplicates(log, entry, models, i);
            return;
        }

        var grown = new Il2CppReferenceArray<PlayerModelData>(n + 1);
        for (int i = 0; i < n; i++) grown[i] = models[i];
        grown[n] = entry.Data;
        PlayerModelDatabase._models = grown;
        entry.Index = n;
        try { entry.Data._index = (ushort)n; } catch { }
        _registers++;
        TouchDatabase();
        var names = new StringBuilder();
        for (int i = 0; i <= n; i++)
        {
            var d = grown[i];
            names.Append(i == n ? " *" : " ").Append(i).Append(":").Append(d == null ? "null" : d.name);
        }
        log.LogInfo($"[sahur:wardrobe] registered '{entry.Label}' at index {n} of {n + 1} PlayerModelData " +
                    $"[{names}] (register #{_registers})");
    }

    // A generation that appended before the adoption path existed (or any duplicate the label
    // catches) is dropped here, keeping the entry the game's model index points at. Shrinking is
    // gated by CanShrink: a removed row shifts every index above it, and only a state in which no
    // live index can name one of our rows makes that safe.
    private static void PruneDuplicates(IHostContext log, Entry entry, Il2CppReferenceArray<PlayerModelData> models, int keep)
    {
        var drop = new List<int>();
        for (int i = 0; i < models.Length; i++)
            if (i != keep && models[i] != null && (models[i].name ?? "") == entry.Label) drop.Add(i);
        if (drop.Count == 0) return;
        if (!CanShrink(log, $"duplicate '{entry.Label}' rows", drop)) return;
        var kept = new Il2CppReferenceArray<PlayerModelData>(models.Length - drop.Count);
        int k = 0;
        for (int i = 0; i < models.Length; i++)
        {
            if (drop.Contains(i)) continue;
            kept[k++] = models[i];
        }
        PlayerModelDatabase._models = kept;
        TouchDatabase();
        // every entry's index may have shifted; let the next pass re-resolve them
        foreach (var e in Entries) e.Index = -1;
        log.LogInfo($"[sahur:wardrobe] pruned {drop.Count} duplicate '{entry.Label}' entr{(drop.Count == 1 ? "y" : "ies")} " +
                    $"[{string.Join(",", drop.ToArray())}] -> {kept.Length} models");
        foreach (var e in Entries)
            if (e.Data != null) EnsureRegistered(log, e);
    }

    // Drop entries a previous generation registered and this one no longer offers (ModelRoster.Retired).
    // The game's applied model index may point at one; it is moved back to the donor's row first so
    // the selection never dangles, and the wardrobe rebuilds its rows on the next Attach.
    // Shrinking is gated by CanShrink for the same reason as PruneDuplicates.
    private static void PruneRetired(IHostContext log)
    {
        if (ModelRoster.Retired.Length == 0) return;
        var models = PlayerModelDatabase.Models;
        if (models == null) return;
        var drop = new List<int>();
        for (int i = 0; i < models.Length; i++)
            if (models[i] != null && ModelRoster.IsRetired(models[i].name ?? "") && !OwnedByAnEntry(models[i]))
                drop.Add(i);
        if (drop.Count == 0) return;
        if (!CanShrink(log, "retired entries", drop)) return;
        // The fallback selection must land on a SHIPPED row: it stands in for an applied row, and a
        // custom row is not one a stock peer can resolve (nor the native actor the body was built
        // over). CanShrink above already required a proven prefix, so the bound is real.
        int donorBound = KnownPrefix(log, out int donorNativeCount) ? Math.Min(donorNativeCount, models.Length) : 0;
        int donorAt = -1;
        for (int i = 0; i < donorBound; i++)
            if (models[i] != null && !drop.Contains(i) && (models[i].name ?? "").IndexOf("James", StringComparison.OrdinalIgnoreCase) >= 0) { donorAt = i; break; }
        if (donorAt < 0)
            for (int i = 0; i < donorBound; i++)
                if (models[i] != null && !drop.Contains(i)) { donorAt = i; break; }
        // move the selection off the retired row before it disappears
        var s = LocalSetuper(null);
        PlayerModelData applied = null;
        try { applied = s?._playerModelData?.Value; } catch { }
        if (applied != null && drop.Contains(applied.Index) && donorAt >= 0)
        {
            ulong clientId = 0;
            try { clientId = Unity.Netcode.NetworkManager.Singleton != null ? Unity.Netcode.NetworkManager.Singleton.LocalClientId : 0; } catch { }
            try
            {
                var managers = SceneCache.Scan(Il2CppType.Of<Gameplay.Appearance.PlayersAppearanceManager>());
                var m = managers != null && managers.Length > 0 ? managers[0].TryCast<Gameplay.Appearance.PlayersAppearanceManager>() : null;
                if (m != null) m.SetBaseModelIndex(clientId, (ushort)donorAt);
                else s?.SetupModel(models[donorAt]);
                log.LogInfo($"[sahur:wardrobe] retired entry '{applied.name}' was the applied model -> back to index {donorAt} '{models[donorAt].name}'");
            }
            catch (Exception e) { log.LogWarning($"[sahur:wardrobe] moving off the retired entry failed: {e.Message}"); }
        }
        var kept = new Il2CppReferenceArray<PlayerModelData>(models.Length - drop.Count);
        int k = 0;
        for (int i = 0; i < models.Length; i++) if (!drop.Contains(i)) kept[k++] = models[i];
        PlayerModelDatabase._models = kept;
        TouchDatabase();
        foreach (var e in Entries) e.Index = -1;
        log.LogInfo($"[sahur:wardrobe] retired {drop.Count} withdrawn entr{(drop.Count == 1 ? "y" : "ies")} " +
                    $"[{string.Join(",", drop.ConvertAll(i => (i + ":" + models[i].name).ToString()).ToArray())}] -> {kept.Length} models");
        Rebuilds = 0;
    }

    private static bool OwnedByAnEntry(PlayerModelData d)
    {
        foreach (var e in Entries) if (Same(e.Data, d)) return true;
        return false;
    }

    // ================================================================== native prefix (resource proof)

    // The number of SHIPPED rows at the front of PlayerModelDatabase.Models, proven from the
    // shipped resource set BEFORE any row this process appends. Every decision that needs "where
    // does the safe wire range end" reads it here. None may answer the same question from the
    // current array length (that array grows), from where the first row this generation recognises
    // sits (a per-generation pointer is blind to the rows an older generation appended), or from
    // PlayerModelData.Index.
    //
    // The proof is re-derived by every hot generation from Resources, so it needs nothing from the
    // host beyond the old IHostContext surface and also works on a database a previous generation
    // already expanded. A host-owned holder would be the alternative, but a running host that
    // predates that contract member must not break the guard, so no host member is consulted.
    private static bool KnownPrefix(IHostContext log, out int nativeCount)
    {
        nativeCount = _prefixKnown ? _nativeCount : 0;
        return _prefixKnown;
    }

    // FAIL CLOSED, once per distinct line. An unknown prefix means no index can be shown to name a
    // shipped row: nothing is appended, nothing is pruned on a guess, and the wire remap clamps.
    private static bool NoNativePrefix(IHostContext log, string why)
    {
        NoteWire(log, $"[model:native-wire] FAIL CLOSED ({why}): the shipped model prefix is unknown" +
                       (_prefixFailed
                            ? " (the live database front did not match the shipped resource set)"
                            : " and has not been proven yet") +
                       "; no model index can be shown to be a shipped row");
        return false;
    }

    // The wire hooks run on the game/network thread while Tick runs on the main thread, so the ring
    // is guarded; the log call itself stays outside the lock.
    private static readonly object WireNoteLock = new object();

    private static void NoteWire(IHostContext log, string message)
    {
        lock (WireNoteLock)
        {
            if (WireNoteRing.Contains(message)) return;
            WireNoteRing.Add(message);
            if (WireNoteRing.Count > WireNoteRingSize) WireNoteRing.RemoveAt(0);
        }
        log?.LogError(message);
    }

    private static void NoteShrink(IHostContext log, string message)
    {
        lock (ShrinkNoteLock)
        {
            if (ShrinkNoteRing.Contains(message)) return;
            ShrinkNoteRing.Add(message);
            if (ShrinkNoteRing.Count > WireNoteRingSize) ShrinkNoteRing.RemoveAt(0);
        }
        log?.LogInfo(message);
    }

    // The SHIPPED rows are the game's own resource set, loaded exactly the way
    // PlayerModelDatabase.Initialize loads them (alterum-re docs/PLAYER.md §3):
    // `Resources.LoadAll<PlayerModelData>(ResourcePathes.PlayerModelDatas)`. That set is a property of
    // the build, not of this process's live array, so it is the only proof of the native boundary that
    // does not depend on the roster labels this generation happens to know. Unity caches resource
    // assets, so the live database holds the same native objects; identity is compared by pointer
    // (`Il2CppObjectBase.Pointer`), never by name. Null means "not loaded yet" and is retried; an
    // empty result, a null row or an exception is the fail-closed state (never guessed).
    private static IntPtr[] ShippedRows(IHostContext log)
    {
        if (_shippedRows != null) return _shippedRows;
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _nextShippedLoad) return null;
        _nextShippedLoad = now + 5f;
        string path = global::Constants.ResourcePathes.PlayerModelDatas;
        try
        {
            var shipped = Resources.LoadAll<PlayerModelData>(path);
            if (shipped == null || shipped.Length == 0)
            {
                NoteWire(log, $"[model:native-wire] FAIL CLOSED (shipped rows): Resources.LoadAll<PlayerModelData>" +
                               $"(\"{path}\") returned no row; the shipped prefix cannot be proven and no custom " +
                               "row is appended");
                return null;
            }
            var ids = new IntPtr[shipped.Length];
            for (int i = 0; i < shipped.Length; i++)
            {
                ids[i] = Identity(shipped[i]);
                if (ids[i] == IntPtr.Zero)
                {
                    NoteWire(log, $"[model:native-wire] FAIL CLOSED (shipped rows): row {i} of " +
                                   $"Resources.LoadAll<PlayerModelData>(\"{path}\") is null; the shipped prefix " +
                                   "cannot be proven");
                    return null;
                }
            }
            _shippedRows = ids;
            log?.LogInfo($"[model:native-wire] shipped set: {ids.Length} PlayerModelData row(s) from Resources " +
                         $"'{path}' (identity is the native pointer, not a name)");
            return ids;
        }
        catch (Exception e)
        {
            NoteWire(log, $"[model:native-wire] FAIL CLOSED (shipped rows): Resources.LoadAll<PlayerModelData>" +
                           $"(\"{path}\") failed: {e.Message}");
            return null;
        }
    }

    private static IntPtr Identity(PlayerModelData d)
    {
        if (d == null) return IntPtr.Zero;
        try { return d.Pointer; } catch { return IntPtr.Zero; }
    }

    // Prove the shipped prefix against that resource set, once per generation. Rows
    // [0, shipped.Length) of the live array must be EXACTLY the shipped set (same pointers, no
    // duplicate, no stranger); the proven count is then shipped.Length, which stays true when the
    // live array already carries rows appended by a previous generation - the case the old label
    // inference could get wrong in both directions. A row inside the candidate prefix that is not
    // shipped, a missing shipped row or a null row proves nothing, so the failure is one-way and
    // every consumer fails closed. A database that is merely shorter than the resource set is still
    // initializing: wait.
    private static bool EnsureNativePrefix(IHostContext log)
    {
        if (_prefixKnown) return true;
        if (_prefixFailed) return false;
        var models = PlayerModelDatabase.Models;
        if (models == null || models.Length == 0)
        {
            // Not a native table yet. This is a wait, not a failure: the first Tick runs before the
            // game has loaded its model database.
            return false;
        }
        var shipped = ShippedRows(log);
        if (shipped == null) return false;                  // resource set not loadable yet: wait
        if (models.Length < shipped.Length) return false;    // database still initializing: wait
        var candidate = new IntPtr[shipped.Length];
        for (int i = 0; i < shipped.Length; i++) candidate[i] = Identity(models[i]);
        var verdict = NativeWirePolicy.ProvePrefix(shipped, candidate);
        switch (verdict.Proof)
        {
            case PrefixProof.Proven:
                _nativeCount = verdict.Count;
                _prefixKnown = true;
                log?.LogInfo($"[model:native-wire] shipped prefix {verdict.Count} proven from " +
                             $"Resources '{(global::Constants.ResourcePathes.PlayerModelDatas)}': " +
                             $"models[0..{verdict.Count}) is exactly the shipped resource set " +
                             $"({models.Length} live row(s)); every index >= {verdict.Count} is " +
                             "remapped to a shipped row before it is written to the wire");
                return true;
            case PrefixProof.FailClosed:
                _prefixFailed = true;
                return NoNativePrefix(log, "first database observation: " + verdict.Detail);
            default:
                return false;   // Wait: nothing observed yet that could prove or disprove a prefix
        }
    }

    // Is this row one this module appended? Used by WithdrawCustomRows to decide what may be CUT, and
    // nowhere to derive the shipped prefix: a label is only evidence of ownership, never a boundary.
    private static bool IsOurRow(PlayerModelData row)
    {
        if (row == null) return false;
        if (IsOurData(row)) return true;
        string name = "";
        try { name = row.name ?? ""; } catch { }
        if (name.Length == 0) return false;
        foreach (var e in Entries)
        {
            if (string.Equals(e.Label, name, StringComparison.OrdinalIgnoreCase)) return true;
            for (int n = 0; e.AdoptNames != null && n < e.AdoptNames.Length; n++)
                if (string.Equals(e.AdoptNames[n], name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return ModelRoster.IsRetired(name);
    }

    // Removing a row SHIFTS every index above it, so a shrink can silently retarget a live index at
    // a different body (or past the end). Three facts make a prune safe:
    //   * the shipped prefix is captured, so "rows this module appended" (index >= prefix) is well
    //     defined and a shipped row can never be in the set being removed;
    //   * no network session is live. The guard rewrites WRITES, but the game's own dictionaries
    //     (`_playersModelsIndexes`, the client mirror, PlayerPrefs) can already hold an index at or
    //     above the prefix, and `SpawnPlayerModelOnClient` indexes the receiver's array with such a
    //     value and no bounds check (alterum-re docs/NETWORKING.md §5). A shrink while those
    //     dictionaries are live can retarget or truncate an index this module never sees, so it is
    //     deferred until the session ends, even when the guard is installed;
    //   * the native-wire guard is installed, so no new value in the game's model-index space can
    //     name a row at or above the prefix (SaveModel, LinkPlayerModelToPlayer_ServerRpc,
    //     SetBaseModelIndex all pass through RemapModelIndex, which clamps to a shipped row).
    // A custom index written by an OLDER build before the guard existed is not covered (see
    // WithdrawCustomRows' residual note); the remap still clamps it on the next write.
    private static bool CanShrink(IHostContext log, string what, List<int> drop)
    {
        bool prefixKnown = KnownPrefix(log, out int nativeCount) && nativeCount > 0;
        bool sessionActive = SessionActive(out string session);
        switch (NativeWirePolicy.CanPrune(prefixKnown, _wireGuard, sessionActive, nativeCount, drop))
        {
            case ShrinkVerdict.DeferSession:
                NoteShrink(log, $"[model:native-wire] deferring prune of {what}: a live {session} session can " +
                                 "still hold a model index at or above the shipped prefix in the game's own " +
                                 "dictionaries; removing a row could retarget or truncate it unchecked");
                return false;
            case ShrinkVerdict.DeferGuard:
                NoteShrink(log, $"[model:native-wire] deferring prune of {what}: the native-wire guard is not " +
                                 "installed, so a live model index could still name one of our rows");
                return false;
            case ShrinkVerdict.RefuseShippedRow:
                for (int k = 0; drop != null && k < drop.Count; k++)
                {
                    if (drop[k] < nativeCount) continue;
                    NoteWire(log, $"[model:native-wire] refusing to prune {what}: row {drop[k]} is inside the " +
                                   $"{nativeCount} shipped row(s); only rows this module appended may be removed");
                    break;
                }
                return false;
            case ShrinkVerdict.WaitForPrefix:
                return NoNativePrefix(log, "prune " + what);
            default:
                return true;
        }
    }

    // A live NetworkManager session is the one state in which shrinking PlayerModelDatabase.Models
    // cannot be proven safe: the game's model dictionaries may still hold an index that named a row at
    // or above the cut, and the spawn reader indexes the array with it unchecked. Unreadable network
    // state counts as ACTIVE (the conservative answer is "do not shrink").
    private static bool SessionActive(out string detail)
    {
        detail = null;
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return false;
            detail = nm.IsServer ? "host" : (nm.IsConnectedClient ? "client" : "listening");
            return true;
        }
        catch (Exception e) { detail = "unreadable (" + e.Message + ")"; return true; }
    }

    // The invariant InstallClickProbe and Dispose maintain: PlayerModelDatabase.Models contains a
    // custom row only while every mandatory native-wire prefix is installed. When that cannot be
    // guaranteed the provable tail of our own rows is withdrawn. Hiding a row from availability is
    // NOT enough: GetRandomModelIndex hands out a DUPLICATE RANDOM ROW from the whole array when the
    // pool is empty (`RandomUtilities.GetRandom(models.Length)` - a row index, not an index past the
    // end; alterum-re docs/NETWORKING.md §5), and every index in the array can be replicated raw.
    // Removing the rows is the only state in which the game's own random assignment can pick nothing
    // but a shipped model. The PlayerModelData objects stay referenced by Entries; a later guarded
    // pass re-appends.
    //
    // Only a CONTIGUOUS SUFFIX that is provably this module's is ever cut:
    //   * strictly ABOVE a captured prefix, so a shipped row is never a candidate. With no captured
    //     prefix nothing can be shown to be above every shipped row, so NOTHING is cut and the rows
    //     that stay are reported - a shrink on an unknown boundary could delete a shipped row;
    //   * identified as ours by pointer or roster label, so a row another module appended is never
    //     deleted;
    //   * cut from the END, so every surviving index keeps its value. A stale saved index that used
    //     to name a removed row is >= the prefix and is therefore clamped by RemapModelIndex - it can
    //     never resolve to a different body.
    //
    // A LIVE SESSION SUPPRESSES THE CUT. This is the one place where "remove the unprotected rows"
    // and "never shift a live index" conflict; the dictionary risk wins, because a shrink can make
    // an index the game already holds resolve to a different body or past the end on every peer. The
    // rows therefore stay and the inability to protect stock peers is reported as an error instead
    // of being guessed away. Outside a session the game's session dictionaries are gone, so the cut
    // is safe for them and happens without waiting for CanShrink (the guard may be exactly what
    // failed). It does NOT make a stale SAVED index safe: PlayerPrefs can still hold a row this cut
    // removed, and with the guard missing nothing remaps it before the game's own spawn reader
    // indexes the shorter array. That residual is inherent to withdrawing at all - the rows cannot
    // be removed from a peer's resolving path otherwise - and it is reported, not hidden.
    //
    // Residuals, not repairable here:
    //   * every non-shipped row that stays is reported, including the ones this generation cannot
    //     name as its own: another module's row and a renamed row from an older roster are just as
    //     unresolvable on a stock peer, and they are not ours to delete;
    //   * an index an OLDER build replicated (and the per-client PlayerPrefs/save value) before this
    //     guard existed is native state this module never saw; withdrawal cannot retract it;
    //   * during a hot reload there is an instant in which the game still holds one of our
    //     PlayerModelData objects as the applied local row while the array no longer contains it; the
    //     object stays valid, its Index mirror is stale, and the next generation re-appends.
    private static void WithdrawCustomRows(IHostContext log, string reason)
    {
        var models = PlayerModelDatabase.Models;
        if (models == null || models.Length == 0) return;

        if (SessionActive(out string session))
        {
            bool sessionPrefix = KnownPrefix(log, out int sessionNativeCount) && sessionNativeCount > 0;
            int ours = 0, foreign = 0;
            var listed = new StringBuilder();
            var listedForeign = new StringBuilder();
            for (int i = 0; i < models.Length; i++)
            {
                if (IsOurRow(models[i]))
                {
                    ours++;
                    if (listed.Length < 200)
                        listed.Append(listed.Length == 0 ? "" : ",").Append(i).Append(':').Append(RowName(models[i]));
                }
                else if (sessionPrefix && i >= sessionNativeCount)
                {
                    // Non-shipped and NOT ours. It stays for the same reason and stock peers cannot
                    // resolve it either, so the report must not be limited to recognised ownership.
                    foreign++;
                    if (listedForeign.Length < 200)
                        listedForeign.Append(listedForeign.Length == 0 ? "" : ",").Append(i).Append(':')
                                     .Append(RowName(models[i]));
                }
            }
            if (ours > 0)
                NoteWire(log, $"[model:native-wire] CANNOT PROTECT PEERS ({reason}): {ours} custom row(s) " +
                               $"[{listed}] stay in PlayerModelDatabase.Models because a live {session} session " +
                               "makes shrinking it unsafe (the game's model dictionaries can still hold an index " +
                               "at or above the shipped prefix, and the spawn reader indexes with it unchecked). " +
                               "They cannot be withdrawn while the session is live; until then the native-wire " +
                               "guard is the only protection left for these indexes - if it is not installed, " +
                               "the installer keeps retrying, and a restart is the sure fallback");
            if (foreign > 0)
                NoteWire(log, $"[model:native-wire] CANNOT PROTECT PEERS ({reason}): {foreign} non-shipped " +
                               $"row(s) at or above the {sessionNativeCount} shipped row(s) [{listedForeign}] " +
                               "are not this module's and stay too, because a live " + session + " session makes " +
                               "the shrink unsafe. Their indexes are just as unresolvable on a stock peer; only " +
                               "the native-wire prefixes keep them off the wire");
            return;
        }

        bool havePrefix = KnownPrefix(log, out int nativeCount) && nativeCount > 0;
        var isOurs = new bool[models.Length];
        for (int i = 0; i < models.Length; i++) isOurs[i] = IsOurRow(models[i]);
        var plan = NativeWirePolicy.PlanWithdrawal(havePrefix, isOurs, nativeCount);
        int cut = plan.Cut;

        if (cut < models.Length)
        {
            var dropped = new StringBuilder();
            for (int i = cut; i < models.Length; i++)
                dropped.Append(i == cut ? "" : ",").Append(i).Append(':').Append(RowName(models[i]));
            var kept = new Il2CppReferenceArray<PlayerModelData>(cut);
            for (int i = 0; i < cut; i++) kept[i] = models[i];
            PlayerModelDatabase._models = kept;
            foreach (var e in Entries) e.Index = -1;
            Rebuilds = 0;
            TouchDatabase();
            log?.LogError($"[model:native-wire] FAIL CLOSED ({reason}): withdrew {models.Length - cut} custom " +
                          $"row(s) [{dropped}] -> {cut} row(s); a stock peer cannot resolve an index past the shipped " +
                          "prefix (SpawnPlayerModelOnClient/OnServer have no bounds check)");
        }

        // Rows of ours that could NOT be cut (a row after them is not provably ours, or the prefix is
        // unknown so no row can be proven to be above every shipped row) are still selectable. This
        // runs even when NOTHING was cut: a silent "nothing to do" is exactly how a residual custom
        // row stayed on the wire before.
        if (plan.Residual.Length > 0)
        {
            var listed = new StringBuilder();
            foreach (int i in plan.Residual)
                listed.Append(listed.Length == 0 ? "" : ",").Append(i).Append(':').Append(RowName(models[i]));
            NoteWire(log, $"[model:native-wire] could not withdraw {plan.Residual.Length} custom row(s) [{listed}]: " +
                           (plan.Bounded
                                ? "a row after them is not provably ours, so cutting there would delete another " +
                                  "module's data"
                                : "the shipped prefix is unknown, so no row can be proven to be above every " +
                                  "shipped row and nothing may be cut on that boundary") +
                           "; these indexes stay and remain selectable, so a peer without the mod can " +
                           "receive them; only the native-wire guard (when it is installed) keeps them off " +
                           "the wire");
        }

        // EVERY non-shipped row that stays, not only the ones this generation recognises as its own.
        // Another module's row, a row from an older roster this build cannot name, and a row blocked
        // behind a foreign entry are just as unresolvable on a stock peer - and they are NOT ours to
        // delete. Reporting only `isOurs` rows was the silent case: the tail looked clean to the
        // ownership scan while a stock peer still could not resolve it.
        if (plan.Unprotected.Length > 0)
        {
            var listed = new StringBuilder();
            foreach (int i in plan.Unprotected)
                listed.Append(listed.Length == 0 ? "" : ",").Append(i).Append(':').Append(RowName(models[i]));
            NoteWire(log, $"[model:native-wire] CANNOT PROTECT PEERS ({reason}): {plan.Unprotected.Length} " +
                           $"row(s) at or above the {plan.Floor} shipped row(s) [{listed}] stay and are not " +
                           "provably this module's, so the withdrawal cannot remove them. A stock peer resolves " +
                           "an index past its shipped rows with no bounds check (SpawnPlayerModelOnClient/" +
                           "OnServer throw); only installed native-wire prefixes keep these indexes off the " +
                           "wire, and the installer keeps retrying while the guard is down");
        }
    }

    // ================================================================== selection

    // Compatibility diagnostic names below now describe the game's applied row only.
    // No saved wanted choice is loaded or written, and no sync path selects a model.
    private static IntPtr _lastAppliedPtr;
    private static bool _lastAppliedSeen;
    private static bool _ourApply;
    private static Entry EntryAt(int i) => i >= 0 && i < Entries.Length ? Entries[i] : null;
    internal static string WantedState() => Snapshot().Label;
    private static int SafeIndex(PlayerModelData d) { try { return d == null ? -1 : d.Index; } catch { return -1; } }

    private static void InvalidatePresentation(IHostContext log, string reason)
    {
        try { ModelPresentation.Invalidate(log, reason); } catch { }
    }

    // The wardrobe's view event: tabs were (re)built, so any occupancy text ModelPresentation moved
    // is on objects that no longer exist. Same edge the inventory work already treats as a rebuild.
    internal static void NoteViewAttached(IHostContext log)
    {
        TouchDatabase();
        InvalidatePresentation(log, "wardrobe view attached");
    }

    // ---- read-only accessors for the canonical resolver (ModelPresentation) ----------------

    // What this player wants, after the repair rules. Kind: 0 = not decided yet, 1 = roster entry,
    // 2 = native row. `Pending` means the entry is known but the game has not registered its row.
    internal readonly struct WantSnapshot
    {
        internal readonly int Kind;
        internal readonly int Entry;
        internal readonly string Slug;        // "" = native, "-" = none/unknown
        internal readonly string Label;
        internal readonly bool Explicit;
        internal readonly bool Pending;
        internal readonly string Provenance;
        internal readonly string From;

        internal WantSnapshot(int kind, int entry, string slug, string label, bool explicitFlag, bool pending,
                              string provenance, string from)
        {
            Kind = kind; Entry = entry; Slug = slug ?? ""; Label = label ?? "?"; Explicit = explicitFlag;
            Pending = pending; Provenance = provenance ?? "unknown"; From = from ?? "none";
        }
    }

    internal static WantSnapshot Snapshot()
    {
        PlayerModelData applied = AppliedData(_probeLog);
        if (_wantedSlug != null)
        {
            int wantedIndex = EntryIndexForSlug(_wantedSlug);
            var wanted = EntryAt(wantedIndex);
            bool unregistered = !string.IsNullOrEmpty(_wantedSlug) &&
                                (wanted == null || wanted.Data == null || wanted.Index < 0);
            return new WantSnapshot(string.IsNullOrEmpty(_wantedSlug) ? 2 : 1, wantedIndex,
                _wantedSlug, wanted?.Label ?? (string.IsNullOrEmpty(_wantedSlug) ? "<native>" : "?"),
                true, unregistered || _wantedPending,
                _wantedPending ? "wanted:guest-send" : "wanted:confirm", "wardrobe");
        }
        var e = applied == null ? null : EntryOwningRow(_probeLog, applied);
        int entry = e?.Spec == null ? -1 : EntryIndexForSlug(e.Spec.Slug);
        return new WantSnapshot(applied == null ? 0 : (e == null ? 2 : 1), entry,
                                e?.Spec?.Slug ?? "", e?.Label ?? (applied == null ? "?" : "<native>"),
                                false, false, "game:applied", "game");
    }

    // Slug -> roster entry. Never an index: slugs are the only identity that survives a roster change.
    internal static int EntryIndexForSlug(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return -1;
        for (int i = 0; i < Entries.Length; i++)
        {
            var spec = Entries[i].Spec;
            if (spec != null && string.Equals(spec.Slug, slug, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    internal static PlayerModelData DataAtEntry(int entry) => EntryAt(entry)?.Data;

    // The entry that owns a database row, as a slug, or "" when the row is not ours. Pointer
    // identity, never the row index: rows shift between generations.
    internal static string SlugOfData(PlayerModelData row)
    {
        if (row == null) return "";
        foreach (var e in Entries)
            if (Same(e.Data, row)) return e.Spec != null ? e.Spec.Slug : "";
        return "";
    }

    // The LOCAL player's applied row, read from the held setuper (no scan when it is alive).
    internal static PlayerModelData AppliedData(IHostContext log)
    {
        try { return LocalSetuper(log)?._playerModelData?.Value; } catch { return null; }
    }

    private static IntPtr AppliedPointer(IHostContext log)
    {
        try { var d = AppliedData(log); return d == null ? IntPtr.Zero : d.Pointer; } catch { return IntPtr.Zero; }
    }

    // The shipped row the local body ALREADY carries, or -1. Cheap by design: the wire hooks run on
    // the game/network thread and must not walk the object table.
    private static int AppliedShippedRow(IHostContext log, int nativeCount)
    {
        if (nativeCount <= 0) return -1;
        var models = PlayerModelDatabase.Models;
        if (models == null) return -1;
        var applied = AppliedData(log);
        int bound = Math.Min(nativeCount, models.Length);
        for (int i = 0; i < bound; i++)
            if (Same(models[i], applied)) return i;
        return -1;
    }

    // The best native row this client can PROVE is a shipped row, used as the value that stands in
    // for one of our rows on the wire: the row the local body already carries when it is shipped
    // (the player is looking at it), else the row this module clones its custom bodies from - the
    // native actor the body rig replaces. -1 when neither can be proven, so the caller falls back to
    // the deterministic per-client row. Only rows inside the captured prefix qualify: past it is one
    // of our rows (or another module's), which a stock peer cannot resolve.
    private static int NativeDonorRow(IHostContext log, int nativeCount)
    {
        int applied = AppliedShippedRow(log, nativeCount);
        if (applied >= 0) return applied;
        if (nativeCount <= 0) return -1;
        var models = PlayerModelDatabase.Models;
        if (models == null) return -1;
        var donor = Donor(log);
        int bound = Math.Min(nativeCount, models.Length);
        for (int i = 0; i < bound; i++)
            if (Same(models[i], donor)) return i;
        return -1;
    }

    // A database row by index, or null. This is a LOCAL lookup: the index is never persisted and
    // never sent over the wire (see RemoteModels: only slugs travel).
    internal static PlayerModelData DatabaseRowAt(int index)
    {
        var models = PlayerModelDatabase.Models;
        if (models == null || index < 0 || index >= models.Length) return null;
        return models[index];
    }

    internal static int DatabaseCount
    {
        get { try { var m = PlayerModelDatabase.Models; return m == null ? -1 : m.Length; } catch { return -1; } }
    }

    // The wardrobe view's tab list, for the display-only occupancy correction. Null outside the
    // lobby (the controller is lobby-only).
    internal static int ViewTabCount => Count(_view?._instantiatedWardrobeModelButtons);
    internal static WardrobeModelButtonedTab ViewTabAt(int i)
    {
        var list = _view?._instantiatedWardrobeModelButtons;
        if (list == null || i < 0 || i >= list.Count) return null;
        try { return list[i]; } catch { return null; }
    }
    internal static bool ViewAttached { get { try { return _view != null && _view.gameObject != null; } catch { return false; } } }

    // Which entry (if any) owns a row of the CURRENT PlayerModelDatabase.Models. Identity is the
    // native PlayerModelData POINTER, never the numeric index: rows are appended, pruned and shifted
    // between generations, so an index captured earlier does not mean the same row.
    //
    // After a hot reload the entries exist before they have adopted their row (`Entry.Data` is null
    // for up to DataSeconds), so a row whose NAME is one the entry may adopt resolves too - and the
    // pointer is bound, which is the adoption EnsureData would perform on its next pass.
    private static Entry EntryOwningRow(IHostContext log, PlayerModelData row)
    {
        if (row == null) return null;
        foreach (var e in Entries) if (e.Data != null && Same(e.Data, row)) return e;

        string name = "";
        try { name = row.name ?? ""; } catch { }
        if (name.Length == 0) return null;

        foreach (var e in Entries)
        {
            bool match = string.Equals(e.Label, name, StringComparison.OrdinalIgnoreCase);
            for (int n = 0; !match && e.AdoptNames != null && n < e.AdoptNames.Length; n++)
                match = string.Equals(e.AdoptNames[n], name, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            if (e.Data == null)
            {
                e.Data = row;
                log?.LogInfo($"{ChoiceTag} '{name}' is the row of entry '{e.Label}', which this generation had not " +
                             "adopted yet -> adopting it now (same object EnsureData would take)");
            }
            return e;
        }
        return null;
    }

    private static PlayerModelSetuper LocalSetuper(IHostContext log)
    {
        if (_setuper != null && _setuper.gameObject != null && _setuper.gameObject.activeInHierarchy) return _setuper;
        _setuper = null;
        // The owner comes from the same resolution every other local-body consumer uses (provider,
        // then Netcode IsOwner). No owner, no setuper: the old fallback accepted ANY setuper whose
        // path contained "Player", which on a joining client picked the HOST's body and made this
        // module read and drive another player's model.
        Transform owner = null;
        try { var player = ReelLogic.FindLocalPlayerEntity(); if (player != null && player.transform != null) owner = player.transform; } catch { }
        if (owner == null) return null;
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<PlayerModelSetuper>());
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i].TryCast<PlayerModelSetuper>();
                if (s == null || s.gameObject == null || !s.gameObject.activeInHierarchy) continue;
                if (!Under(s.transform, owner)) continue;
                _setuper = s;
                if (log != null) log.LogInfo($"[sahur:wardrobe] local PlayerModelSetuper '{PathOf(s.transform)}'");
                break;
            }
        }
        catch (Exception e) { if (log != null) log.LogWarning($"[sahur:wardrobe] setuper scan failed: {e.Message}"); }
        return _setuper;
    }

    private static bool Under(Transform t, Transform root)
    {
        for (int i = 0; t != null && i < 12; i++, t = t.parent)
            if (t == root) return true;
        return false;
    }

    // Direct body requests are diagnostic only. The next sync follows the game's actual row.
    internal static void NoteBodyRequest(IHostContext log, ModelSpec spec, bool on)
    {
        InvalidatePresentation(log, "diagnostic body request");
    }

    private static void SyncSelection(IHostContext log)
    {
        // An explicit choice - a wardrobe confirm, or a programmatic `select=`/`apply`/`index=` -
        // is kept separately from the game's native donor row. With no explicit choice, continue
        // to follow the game's applied row.
        PlayerModelData applied = AppliedData(log);
        // A guest's PENDING pick has exactly one confirmation available: the applied row itself.
        ConfirmPending(log, applied);
        try
        {
            IntPtr p = applied == null ? IntPtr.Zero : applied.Pointer;
            if (!_lastAppliedSeen || p != _lastAppliedPtr)
            {
                _lastAppliedSeen = true;
                _lastAppliedPtr = p;
                InvalidatePresentation(log, "game applied row changed");
            }
        }
        catch { }
        Entry hit = _wantedSlug == null ? (applied == null ? null : EntryOwningRow(log, applied))
            : EntryAt(EntryIndexForSlug(_wantedSlug));
        ApplySelection(log, hit, applied, _wantedSlug == null ? "game assigned row" : "confirmed wanted");
        Publish(log, hit);
    }

    // Release first, acquire second, both in roster order. SahurRig/SahurModel are one singleton
    // per KIND, so the entry being left behind must hand its module back BEFORE the incoming entry
    // of the same kind takes it over. In one pass in roster order, going from a later entry to an
    // earlier one of the same kind ran the later entry's "off" after the earlier entry's "on",
    // which destroyed the body the same tick it was built (Peter Griffin -> TC2 Scout left no body
    // at all). Note: an "off" for an entry that is not the module's current spec is a no-op inside
    // the module, so the release pass cannot touch the incoming entry.
    private static void ApplySelection(IHostContext log, Entry hit, PlayerModelData applied, string why)
    {
        string appliedName = applied == null ? "?" : (applied.name ?? "?");
        int appliedIndex = -1;
        try { appliedIndex = applied?.Index ?? -1; } catch { }
        foreach (var e in Entries)
        {
            // `Selected` is the wardrobe's own bit, but a body module can also be ON without it
            // (F1/F3, or `sahur.request on`). Reading the module's real state here is what stops
            // TWO modules from writing the same skeleton - the divergence path.
            bool moduleOn = false;
            try { moduleOn = e.Rigged ? (SahurRig.Active && ReferenceEquals(SahurRig.Spec, e.Spec))
                                      : (SahurModel.Active && ReferenceEquals(SahurModel.Spec, e.Spec)); }
            catch { }
            if (e == hit || (!e.Selected && !moduleOn)) continue;
            e.Selected = false;
            log.LogInfo($"[sahur:wardrobe] wardrobe selection is NOT '{e.Label}' " +
                        $"(applied '{appliedName}' index={appliedIndex}, why={why}) -> body off");
            try
            {
                if (e.Rigged) SahurRig.SetFromWardrobe(log, e.Spec, false);
                else SahurModel.SetFromWardrobe(log, e.Spec, false);
            }
            catch (Exception ex) { log.LogWarning($"[sahur:wardrobe] body set failed for '{e.Label}': {ex.Message}"); }
        }
        if (hit == null) return;
        if (hit.Selected) return;
        hit.Selected = true;
        log.LogInfo($"[sahur:wardrobe] wardrobe selection IS '{hit.Label}' " +
                    $"(applied '{appliedName}' index={appliedIndex}, why={why}) -> body on");
        try
        {
            if (hit.Rigged) SahurRig.SetFromWardrobe(log, hit.Spec, true);
            else SahurModel.SetFromWardrobe(log, hit.Spec, true);
        }
        catch (Exception ex) { log.LogWarning($"[sahur:wardrobe] body set failed for '{hit.Label}': {ex.Message}"); }
    }

    // Publish the actually applied entry's stable slug, or "" for a native row.
    // The sender coalesces and rate-limits; rejected requests never become a peer claim.
    private static void Publish(IHostContext log, Entry hit)
    {
        try { RemoteModels.PublishLocal(log, hit == null || hit.Spec == null ? "" : hit.Spec.Slug); }
        catch (Exception ex) { log.LogWarning($"[swap:net] publish failed: {ex.Message}"); }
    }

    // ================================================================== wardrobe UI

    private static void Attach(IHostContext log)
    {
        var vcs = SceneCache.ScanPolled(Il2CppType.Of<WardrobeOptionalViewController>(), 1.0);
        for (int i = 0; vcs != null && i < vcs.Length; i++)
        {
            var vc = vcs[i].TryCast<WardrobeOptionalViewController>();
            if (vc == null || vc.gameObject == null) continue;
            if (vc.gameObject.scene.name == null || vc.gameObject.scene.name.Length == 0) continue;
            bool wasNull = _view == null;
            _view = vc;
            if (wasNull) NoteViewAttached(log);
            InstallClickProbe(log);
            if (wasNull)
                log.LogInfo($"[sahur:wardrobe] wardrobe view '{PathOf(vc.transform)}' active={vc.gameObject.activeInHierarchy} " +
                            $"tabs={Count(vc._instantiatedWardrobeModelButtons)} prefab={(vc._wardrobeModelButtonedTabPrefab == null ? "null" : "yes")} " +
                            $"parent={(vc._wardrobeModelButtonedTabsParent == null ? "null" : PathOf(vc._wardrobeModelButtonedTabsParent))}");
            if (!_lobbyShot && vc.gameObject.activeInHierarchy)
            {
                _lobbyShot = true;
                // full-screen capture (ScreenCapture, not the game camera): the wardrobe UI is a
                // screen-space overlay canvas, so a camera render would miss the tab list
                HudShot(log, "sahur_wardrobe_lobby");
            }

            // The tab list is built from PlayerModelDatabase.Models when the view starts, which can
            // happen before our entries are appended. Its own create/destroy pair then rebuilds the
            // whole list, with the game's click wiring and its active tabs (an inserted clone of the
            // disabled prefab stays inactive unless it is explicitly activated - that is what hid
            // the previous attempt's rows).
            bool haveData = true;
            foreach (var e in Entries) if (e.Data == null) haveData = false;
            if (haveData)
            {
                string why = SyncReason(vc);
                if (why != null) RebuildTabs(log, why, vc);
                foreach (var e in Entries) EnsureTab(log, vc, e);
                PruneOrphans(log, vc);
                SyncTabGroup(log, vc, "attach");
            }
            StyleOurs(log, vc);
            return;
        }
        _view = null;
    }

    // Why the view's tab list does not match the model database, or null when it does. An empty list
    // is not a mismatch: the view has not built its tabs yet and will build them from the (already
    // extended) database on its own.
    private static string SyncReason(WardrobeOptionalViewController vc)
    {
        var models = PlayerModelDatabase.Models;
        int want = models == null ? 0 : models.Length;
        var list = vc._instantiatedWardrobeModelButtons;
        int have = Count(list);
        if (have <= 0) return null;
        if (have != want) return $"tabs {have} != models {want}";
        for (int i = 0; i < have; i++)
        {
            var t = list[i];
            if (t == null) return $"tab[{i}] is null";
            try { if (t.PlayerModelData == null) return $"tab[{i}] '{t.name}' has no model data"; } catch { }
            try { if (!t.gameObject.activeSelf) return $"tab[{i}] '{t.name}' is inactive"; } catch { }
        }
        return null;
    }

    // Rows this mod inserted by hand in an earlier generation are named after their entry; the
    // game's own are 'Model Selectable(Clone)'. Anything carrying our marker that is no longer in
    // the view's list is a leftover and is destroyed, so a reload cannot stack rows.
    private static void PruneOrphans(IHostContext log, WardrobeOptionalViewController vc)
    {
        var parent = vc._wardrobeModelButtonedTabsParent;
        if (parent == null) return;
        var list = vc._instantiatedWardrobeModelButtons;
        int kids = parent.childCount;
        for (int i = kids - 1; i >= 0; i--)
        {
            Transform child = null;
            try { child = parent.GetChild(i); } catch { }
            if (child == null) continue;
            string n = child.name ?? "";
            if (n.IndexOf(InsertMarker, StringComparison.Ordinal) < 0) continue;   // not ours
            bool kept = false;
            for (int k = 0; k < Count(list) && !kept; k++)
            {
                var t = list[k];
                if (t == null) continue;
                try { if (t.transform == child) kept = true; } catch { }
            }
            if (kept) continue;
            log.LogInfo($"[sahur:wardrobe] pruning leftover '{n}' (not in the view's list)");
            try { UnityEngine.Object.Destroy(child.gameObject); } catch { }
        }
    }

    private static int Count(Il2CppSystem.Collections.Generic.List<WardrobeModelButtonedTab> list)
    {
        try { return list == null ? -1 : list.Count; } catch { return -1; }
    }


    // The view's TabGroup is populated by its loader when the list is first built. Replacing the
    // rows with the view's own CreateWardrobeModelButtonedTabs() does not re-run that loader, so the
    // new rows would have no GroupOwner: Tab.Activate() is a no-op and the row is inert even though
    // it renders and raycasts. Register exactly those rows through the game's own TabGroup API.
    private static void SyncTabGroup(IHostContext log, WardrobeOptionalViewController vc, string why)
    {
        var group = vc == null ? null : vc._modelsButtonsTabGroup;
        var list = vc == null ? null : vc._instantiatedWardrobeModelButtons;
        int n = Count(list);
        if (group == null || n <= 0) return;
        int removed = 0, added = 0, kept = 0;
        try
        {
            for (int i = group._tabs.Count - 1; i >= 0; i--)
            {
                var gt = group._tabs[i];
                bool live = false;
                for (int k = 0; k < n && !live; k++)
                {
                    var t = list[k];
                    if (t != null && gt != null && t == gt) live = true;
                }
                if (live) { kept++; continue; }
                string old = gt == null ? "null" : $"{gt.name} data='{(gt.TryCast<WardrobeModelButtonedTab>()?.PlayerModelData?.name ?? "?")}'";
                group.RemoveTab(gt);
                removed++;
                log.LogInfo($"[sahur:ui] tabgroup drop stale '{old}'");
            }
            for (int i = 0; i < n; i++)
            {
                var t = list[i];
                if (t == null) continue;
                bool already = false;
                try
                {
                    for (int k = 0; k < group._tabs.Count && !already; k++)
                        if (group._tabs[k] == t) already = true;
                }
                catch { }
                if (already && t.GroupOwner != null) continue;
                group.AddTab(t);
                added++;
            }
            if (added > 0 || removed > 0)
                log.LogInfo($"[sahur:ui] tabgroup sync ({why}): rows={n} tabs={group._tabs.Count} kept={kept} dropped={removed} added={added} " +
                            $"active='{(group.ActiveTab == null ? "null" : group.ActiveTab.name)}'");
            if (added > 0 || removed > 0)
            {
                try { vc.SelectModelButtonForLocalPlayer(); log.LogInfo("[sahur:ui] tabgroup: SelectModelButtonForLocalPlayer() re-applied the native row selection"); }
                catch (Exception e) { log.LogWarning($"[sahur:ui] tabgroup: SelectModelButtonForLocalPlayer failed: {e.Message}"); }
            }
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] tabgroup sync failed: {e.Message}"); }
    }


    private static void SelectButtonInfo(IHostContext log)
    {
        var vc = _view;
        var b = vc == null ? null : vc._selectButton;
        if (b == null) { log.LogWarning("[sahur:cmp] select button is null"); return; }
        var go = b.gameObject;
        var rt = b.transform.TryCast<RectTransform>();
        bool interactable = false, canClick = false;
        string safe = "?", safety = "?", unsafeCount = "?";
        try { interactable = b.IsInteractable; } catch { }
        try { canClick = b.CanClick(); } catch { }
        try { safe = b.ClickedSafe == null ? "null" : "set"; } catch { }
        try { safety = b.ClickedSafeReferenced == null ? "null" : "set"; } catch { }
        try { var ev = b.ClickedUnsafe; unsafeCount = ev == null ? "null" : ev.GetPersistentEventCount().ToString(); } catch { }
        var im = Comp<Image>(go);
        log.LogInfo($"[sahur:cmp] Select Button '{PathOf(b.transform)}' activeSelf={go.activeSelf} active={go.activeInHierarchy} layer={go.layer} " +
                    $"enabled={b.enabled} interactable={interactable} canClick={canClick} ClickedSafe={safe} ClickedSafeReferenced={safety} " +
                    $"ClickedUnsafe.events={unsafeCount} image={(im == null ? "no" : $"raycast={im.raycastTarget}")} {RectText(rt)}");
        try
        {
            var all = go.GetComponents(Il2CppType.Of<UnityEngine.Component>());
            var sb = new StringBuilder();
            for (int k = 0; all != null && k < all.Length; k++) if (all[k] != null) sb.Append(all[k].GetIl2CppType().Name).Append(' ');
            log.LogInfo($"[sahur:cmp] Select Button components = {sb}");
        }
        catch { }
        try
        {
            var ph = Comp<UI.Elements.PointerHolder>(go);
            bool phi = false, canHold = false;
            string down = "?", up = "?", downU = "?", upU = "?";
            if (ph != null)
            {
                try { phi = ph.IsInteractable; } catch { }
                try { canHold = ph.CanHold(UnityEngine.EventSystems.PointerEventData.InputButton.Left); } catch { }
                try { down = ph.PointerDownedSafe == null ? "null" : "set"; } catch { }
                try { up = ph.PointerReleasedSafe == null ? "null" : "set"; } catch { }
                try { downU = ph.PointerDownedUnsafe == null ? "null" : ph.PointerDownedUnsafe.GetPersistentEventCount().ToString(); } catch { }
                try { upU = ph.PointerUppedUnsafe == null ? "null" : ph.PointerUppedUnsafe.GetPersistentEventCount().ToString(); } catch { }
            }
            var cg2 = Comp<CanvasGroup>(go);
            log.LogInfo($"[sahur:cmp] Select Button PointerHolder={(ph == null ? "no" : $"interactable={phi} canHoldLeft={canHold} downSafe={down} upSafe={up} downUnsafe={downU} upUnsafe={upU}")} " +
                        $"CanvasGroup={(cg2 == null ? "no" : $"interactable={cg2.interactable} blocks={cg2.blocksRaycasts}")} " +
                        $"text='{(vc._selectButtonTMP == null ? "null" : vc._selectButtonTMP.text)}'");
        }
        catch (Exception e) { log.LogWarning($"[sahur:cmp] Select Button pointer info failed: {e.Message}"); }
    }

    // ================================================================== layout probe

    private static T Comp<T>(GameObject go) where T : Il2CppObjectBase
    {
        try { return go == null ? null : go.GetComponent(Il2CppType.Of<T>())?.TryCast<T>(); } catch { return null; }
    }

    private static string RectText(RectTransform rt)
    {
        if (rt == null) return "rt=null";
        try
        {
            var r = rt.rect;
            return $"size={rt.sizeDelta.ToString("F1")} pos={rt.anchoredPosition.ToString("F1")} pivot={rt.pivot.ToString("F2")} " +
                   $"anchorMin={rt.anchorMin.ToString("F2")} anchorMax={rt.anchorMax.ToString("F2")} rect=({r.width:F1}x{r.height:F1})";
        }
        catch { return "rt=?（exception)"; }
    }

    // Screen-space rectangle of a RectTransform in pixels, bottom-left origin like the UI system.
    private static bool ScreenRect(RectTransform rt, out float x0, out float y0, out float x1, out float y1)
    {
        x0 = y0 = x1 = y1 = 0f;
        if (rt == null) return false;
        try
        {
            var c = new Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(c);
            var canvas = rt.GetComponentInParent(Il2CppType.Of<Canvas>())?.TryCast<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            bool any = false;
            for (int k = 0; k < 4; k++)
            {
                var sp = RectTransformUtility.WorldToScreenPoint(cam, c[k]);
                if (!any) { x0 = x1 = sp.x; y0 = y1 = sp.y; any = true; }
                else { x0 = Math.Min(x0, sp.x); x1 = Math.Max(x1, sp.x); y0 = Math.Min(y0, sp.y); y1 = Math.Max(y1, sp.y); }
            }
            return any;
        }
        catch { return false; }
    }

    private static void ScanLayout(IHostContext log, GameObject go, string tag)
    {
        if (go == null) return;
        string p;
        try { p = PathOf(go.transform); } catch { p = "?"; }
        try
        {
            var sr = Comp<ScrollRect>(go);
            if (sr != null)
            {
                float ch = 0f, vh = 0f;
                try { ch = sr.content == null ? 0f : sr.content.rect.height; } catch { }
                try { vh = sr.viewport == null ? 0f : sr.viewport.rect.height; } catch { }
                float vpos = -1f;
                try { vpos = sr.verticalNormalizedPosition; } catch { }
                log.LogInfo($"[sahur:ui] {tag} ScrollRect '{p}' content='{PathOf(sr.content)}' viewport='{PathOf(sr.viewport)}' " +
                            $"vertical={sr.vertical} horizontal={sr.horizontal} movement={sr.movementType} inertia={sr.inertia} " +
                            $"sensitivity={sr.scrollSensitivity:F1} vbar='{(sr.verticalScrollbar == null ? "null" : PathOf(sr.verticalScrollbar.transform))}' vpos={vpos:F3} " +
                            $"contentH={ch:F1} viewportH={vh:F1} overflow={(ch - vh):F1}");
            }
            var vlg = Comp<VerticalLayoutGroup>(go);
            if (vlg != null)
                log.LogInfo($"[sahur:ui] {tag} VerticalLayoutGroup '{p}' spacing={vlg.spacing:F1} align={vlg.childAlignment} " +
                            $"controlH={vlg.childControlHeight} forceH={vlg.childForceExpandHeight} controlW={vlg.childControlWidth} " +
                            $"forceW={vlg.childForceExpandWidth} pad=({vlg.padding.left},{vlg.padding.top},{vlg.padding.right},{vlg.padding.bottom})");
            var hlg = Comp<HorizontalLayoutGroup>(go);
            if (hlg != null)
                log.LogInfo($"[sahur:ui] {tag} HorizontalLayoutGroup '{p}' spacing={hlg.spacing:F1} align={hlg.childAlignment} " +
                            $"controlW={hlg.childControlWidth} forceW={hlg.childForceExpandWidth}");
            var grid = Comp<GridLayoutGroup>(go);
            if (grid != null)
                log.LogInfo($"[sahur:ui] {tag} GridLayoutGroup '{p}' cell={grid.cellSize.ToString("F1")} spacing={grid.spacing.ToString("F1")} " +
                            $"constraint={grid.constraint} count={grid.constraintCount}");
            var fit = Comp<ContentSizeFitter>(go);
            if (fit != null)
                log.LogInfo($"[sahur:ui] {tag} ContentSizeFitter '{p}' horizontal={fit.horizontalFit} vertical={fit.verticalFit} enabled={fit.enabled}");
            var le = Comp<LayoutElement>(go);
            if (le != null)
                log.LogInfo($"[sahur:ui] {tag} LayoutElement '{p}' minH={le.minHeight:F1} prefH={le.preferredHeight:F1} flexibleH={le.flexibleHeight:F1} ignore={le.ignoreLayout}");
            var rm = Comp<RectMask2D>(go);
            if (rm != null) log.LogInfo($"[sahur:ui] {tag} RectMask2D '{p}' enabled={rm.enabled}");
            var mask = Comp<Mask>(go);
            if (mask != null) log.LogInfo($"[sahur:ui] {tag} Mask '{p}' enabled={mask.enabled} showGraphic={mask.showMaskGraphic}");
            var img = Comp<Image>(go);
            if (img != null)
                log.LogInfo($"[sahur:ui] {tag} Image '{p}' sprite='{(img.sprite == null ? "null" : img.sprite.name)}' " +
                            $"raycast={img.raycastTarget} enabled={img.enabled} color={img.color.ToString("F2")}");
            var cg = Comp<CanvasGroup>(go);
            if (cg != null)
                log.LogInfo($"[sahur:ui] {tag} CanvasGroup '{p}' alpha={cg.alpha:F2} interactable={cg.interactable} blocks={cg.blocksRaycasts}");
            var canvas = Comp<Canvas>(go);
            if (canvas != null)
                log.LogInfo($"[sahur:ui] {tag} Canvas '{p}' mode={canvas.renderMode} sorting={canvas.sortingOrder} " +
                            $"scale={canvas.scaleFactor:F2} pixelPerfect={canvas.pixelPerfect}");
            var scaler = Comp<CanvasScaler>(go);
            if (scaler != null)
                log.LogInfo($"[sahur:ui] {tag} CanvasScaler '{p}' mode={scaler.uiScaleMode} ref={scaler.referenceResolution.ToString("F0")} " +
                            $"matchW={scaler.matchWidthOrHeight:F2} screen={Screen.width}x{Screen.height}");
            var raycaster = Comp<GraphicRaycaster>(go);
            if (raycaster != null)
                log.LogInfo($"[sahur:ui] {tag} GraphicRaycaster '{p}' enabled={raycaster.enabled} blocking={raycaster.blockingObjects}");
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] scan '{p}' failed: {e.Message}"); }
    }

    private static void DumpLayout(IHostContext log)
    {
        var vc = _view;
        if (vc == null || vc.gameObject == null) { log.LogWarning("[sahur:ui] no wardrobe view in this scene"); return; }
        var list = vc._instantiatedWardrobeModelButtons;
        var parent = vc._wardrobeModelButtonedTabsParent;
        var models = PlayerModelDatabase.Models;
        log.LogInfo($"[sahur:ui] view='{PathOf(vc.transform)}' active={vc.gameObject.activeInHierarchy} tabs={Count(list)} " +
                    $"models={(models == null ? -1 : models.Length)} parent='{PathOf(parent)}' children={(parent == null ? -1 : parent.childCount)} " +
                    $"prefab={(vc._wardrobeModelButtonedTabPrefab == null ? "null" : "ok")} group={(vc._modelsButtonsTabGroup == null ? "null" : vc._modelsButtonsTabGroup.GetType().Name)} " +
                    $"modelDisplayer={(vc._modelDisplayer == null ? "null" : "ok")}");

        // every child of the tabs parent, in sibling order, and whether the model data is in the DB
        try
        {
            var sb = new StringBuilder();
            for (int i = 0; parent != null && i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var tab = child == null ? null : child.gameObject.GetComponent(Il2CppType.Of<WardrobeModelButtonedTab>())?.TryCast<WardrobeModelButtonedTab>();
                var rt = child == null ? null : child.TryCast<RectTransform>();
                bool active = child != null && child.gameObject.activeSelf;
                float cx0, cy0, cx1, cy1;
                ScreenRect(rt, out cx0, out cy0, out cx1, out cy1);
                sb.AppendLine($"    child[{i}] '{child.name}' activeSelf={active} tab={(tab == null ? "no" : "yes")} " +
                              $"data='{(tab == null || tab.PlayerModelData == null ? "-" : tab.PlayerModelData.name)}' {RectText(rt)} " +
                              $"screen=({cx0:F0},{cy0:F0})-({cx1:F0},{cy1:F0}) center=({(cx0 + cx1) / 2f:F0},{(cy0 + cy1) / 2f:F0})");
            }
            int kids = parent == null ? -1 : parent.childCount;
            log.LogInfo($"[sahur:ui] tabs parent children ({kids}):\n{sb}");
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] child dump failed: {e.Message}"); }

        // the ancestor chain of the tabs parent, with layout components and their real values
        int depth = 0;
        for (Transform t = parent; t != null && depth < 9; t = t.parent, depth++)
        {
            var rt = t.TryCast<RectTransform>();
            float x0, y0, x1, y1;
            ScreenRect(rt, out x0, out y0, out x1, out y1);
            log.LogInfo($"[sahur:ui] ^{depth} '{t.name}' children={t.childCount} {RectText(rt)} screen=({x0:F0},{y0:F0})-({x1:F0},{y1:F0})");
            ScanLayout(log, t.gameObject, "^" + depth);
        }

        // the tabs themselves: list index, sibling index, screen rect, and what a click would need
        try
        {
            var sb = new StringBuilder();
            float vx0 = 0, vy0 = 0, vx1 = 0, vy1 = 0;
            var viewportRt = parent == null ? null : parent.parent?.TryCast<RectTransform>();
            bool haveViewport = ScreenRect(viewportRt, out vx0, out vy0, out vx1, out vy1);
            for (int i = 0; i < Count(list); i++)
            {
                var tab = list[i];
                if (tab == null) { sb.AppendLine($"    [{i}] null"); continue; }
                var rt = tab.transform.TryCast<RectTransform>();
                float a, b, c, d;
                bool ok = ScreenRect(rt, out a, out b, out c, out d);
                bool inside = ok && haveViewport && a >= vx0 - 0.5f && c <= vx1 + 0.5f && b >= vy0 - 0.5f && d <= vy1 + 0.5f;
                var btn = Comp<UnityEngine.UI.Button>(tab.gameObject);
                var im = Comp<Image>(tab.gameObject);
                sb.AppendLine($"    [{i}] '{tab.name}' sib={tab.transform.GetSiblingIndex()} data='{(tab.PlayerModelData == null ? "null" : tab.PlayerModelData.name)}' " +
                              $"text='{(tab._modelNameTMP == null ? "null" : tab._modelNameTMP.text)}' {RectText(rt)} " +
                              $"screen=({a:F0},{b:F0})-({c:F0},{d:F0}) insideViewport={inside} " +
                              $"button={(btn == null ? "no" : "yes")} interactable={(btn == null ? "-" : btn.interactable.ToString())} " +
                              $"onClick={OnClickText(btn)} raycastTarget={(im == null ? "-" : im.raycastTarget.ToString())}");
            }
            log.LogInfo($"[sahur:ui] tabs ({Count(list)}) viewportScreen=({vx0:F0},{vy0:F0})-({vx1:F0},{vy1:F0}):\n{sb}");
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] tab dump failed: {e.Message}"); }

        // the view's own C# surface: its base chain and any show/hide entry point the game owns
        try
        {
            for (var ty = vc.GetType(); ty != null && ty != typeof(object); ty = ty.BaseType)
            {
                var names = new StringBuilder();
                var ms = ty.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                for (int k = 0; k < ms.Length; k++)
                {
                    string mn = ms[k].Name;
                    if (mn.IndexOf("Show", StringComparison.OrdinalIgnoreCase) < 0 &&
                        mn.IndexOf("Hide", StringComparison.OrdinalIgnoreCase) < 0 &&
                        mn.IndexOf("Enable", StringComparison.OrdinalIgnoreCase) < 0 &&
                        mn.IndexOf("View", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    names.Append(mn).Append(' ');
                }
                log.LogInfo($"[sahur:ui] type {ty.FullName}: {names}");
            }
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] type dump failed: {e.Message}"); }

        // the event system / canvas input path for the wardrobe canvas
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            bool over = false;
            try { over = es != null && es.IsPointerOverGameObject(); } catch { }
            string mouse = "?";
            try { var m = UnityEngine.InputSystem.Mouse.current; mouse = m == null ? "none" : m.position.ReadValue().ToString("F0"); } catch { }
            string cursor = "?";
            try { cursor = $"{Cursor.lockState}/{Cursor.visible}"; } catch { }
            log.LogInfo($"[sahur:ui] EventSystem={(es == null ? "null" : PathOf(es.transform))} " +
                        $"module={(es == null || es.currentInputModule == null ? "null" : es.currentInputModule.GetType().Name)} " +
                        $"pointerOverGameObject={over} cursor={cursor} mouse={mouse}");
            try
            {
                var mods = es.gameObject.GetComponents(Il2CppType.Of<UnityEngine.EventSystems.BaseInputModule>());
                for (int k = 0; mods != null && k < mods.Length; k++)
                {
                    var m2 = mods[k].TryCast<UnityEngine.EventSystems.BaseInputModule>();
                    if (m2 == null) continue;
                    log.LogInfo($"[sahur:ui] input module [{k}] {m2.GetType().Name} enabled={m2.enabled} active={m2.isActiveAndEnabled}");
                }
            }
            catch (Exception e) { log.LogWarning($"[sahur:ui] input module dump failed: {e.Message}"); }
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] event system read failed: {e.Message}"); }

        // selection state so a click can be checked against it
        var s = LocalSetuper(log);
        if (s != null)
        {
            PlayerModelData applied = null;
            try { applied = s._playerModelData?.Value; } catch { }
            log.LogInfo($"[sahur:ui] applied='{(applied == null ? "null" : applied.name)}' index={(applied == null ? -1 : applied.Index)}");
        }
    }

    // ================================================================== rebuild

    // The view's own tab creation walks PlayerModelDatabase.Models, so calling it after our entries
    // are in the array yields tabs wired exactly like the game's own (click, occupancy, nickname,
    // layout) instead of clones that only look right.
    private static void RebuildTabs(IHostContext log, string why, WardrobeOptionalViewController vc = null)
    {
        if (vc == null) vc = _view;
        if (vc == null) { log.LogWarning("[sahur:ui] rebuild: no wardrobe view"); return; }
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _nextRebuild) return;                 // one rebuild per pass, at most
        if (Rebuilds >= 4) return;                      // never churn: four attempts per generation is enough
        _nextRebuild = now + 5f;
        try
        {
            int before = Count(vc._instantiatedWardrobeModelButtons);
            var models = PlayerModelDatabase.Models;
            int n = models == null ? -1 : models.Length;
            var p0 = vc._wardrobeModelButtonedTabsParent;
            int kids0 = p0 == null ? -1 : p0.childCount;
            float h0 = ContentHeight(vc);
            log.LogInfo($"[sahur:ui] rebuild ({why}): tabs={before} models={n} children={kids0} contentH={h0:F1}");
            vc.DestroyWardrobeModelButtonedTabs();
            vc.CreateWardrobeModelButtonedTabs();
            var p1 = vc._wardrobeModelButtonedTabsParent;
            log.LogInfo($"[sahur:ui] rebuild done: tabs={Count(vc._instantiatedWardrobeModelButtons)} " +
                        $"children={(p1 == null ? -1 : p1.childCount)} contentH={ContentHeight(vc):F1}");
            foreach (var e in Entries) e.Tabs.Clear();
            Rebuilds++;
            NoteViewAttached(log);
        }
        catch (Exception e) { log.LogError($"[sahur:ui] rebuild failed: {e}"); }
    }

    private static float ContentHeight(WardrobeOptionalViewController vc)
    {
        try
        {
            var p = vc._wardrobeModelButtonedTabsParent;
            var rt = p == null ? null : p.TryCast<RectTransform>();
            return rt == null ? -1f : rt.rect.height;
        }
        catch { return -1f; }
    }

    // Diagnostic only: feeds a synthetic PointerEventData into the row's UI.Elements.Button. It does
    // NOT select anything, and neither does the same call on the game's own rows - OnPointerClick is
    // guarded by the EventSystem's press bookkeeping (pointerPress / pointerPressRaycast), which only
    // a real click through the active UI state fills in. Selection is therefore driven by
    // `wardrobe=select=...` (PlayersAppearanceManager.SetBaseModelIndex, the call the wardrobe's own
    // OnModelSelectedServerRpc makes).
    private static void ClickTab(IHostContext log, string name)
    {
        var entry = Find(name);
        if (entry == null) { log.LogWarning($"[sahur:ui] click: no entry '{name}'"); return; }
        var vc = _view;
        var list = vc == null ? null : vc._instantiatedWardrobeModelButtons;
        for (int i = 0; i < Count(list); i++)
        {
            var t = list[i];
            if (t == null || !Same(t.PlayerModelData, entry.Data)) continue;
            string text = "?";
            try { text = t._modelNameTMP == null ? "null" : t._modelNameTMP.text; } catch { }
            var btn = t._button;
            if (btn == null) { log.LogWarning($"[sahur:ui] click: tab '{t.name}' has no UI.Elements.Button"); return; }
            log.LogInfo($"[sahur:ui] click list[{i}] '{t.name}' text='{text}' active={t.gameObject.activeSelf} " +
                        $"button='{PathOf(btn.transform)}' interactable={btn.IsInteractable}");
            var es = UnityEngine.EventSystems.EventSystem.current;
            var ped = new UnityEngine.EventSystems.PointerEventData(es);
            try
            {
                var rt = t.transform.TryCast<RectTransform>();
                var world = rt == null ? Vector3.zero : rt.TransformPoint(rt.rect.center);
                var sp = RectTransformUtility.WorldToScreenPoint(null, world);
                ped.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
                ped.position = sp;
                ped.pressPosition = sp;
                ped.clickCount = 1;
                log.LogInfo($"[sahur:ui] click position={sp.ToString("F0")} (screen {Screen.width}x{Screen.height})");
            }
            catch (Exception e) { log.LogWarning($"[sahur:ui] click position failed: {e.Message}"); }
            btn.Click(ped);
            return;
        }
        log.LogWarning($"[sahur:ui] click: no tab for '{entry.Label}' in the list");
    }


    // ------------------------------------------------------------------ test layer: selection
    //
    // The two paths behind `alterum.test.select-model <slug> <ui|protocol>`. GameConsole gates both
    // before calling in (a selection is a mutation: AEZAKMI/QcAccess on the request transport, the
    // same unlock check inside the native row). The variant word is part of the command because the
    // two paths answer DIFFERENT questions, and a run must never be readable as the other one:
    //
    //   ui        the wardrobe's OWN row, pressed through the existing UI click machinery. It needs
    //             the native view OPEN (the tab only exists in the open view) and it writes no model
    //             row itself: the game's own click handler selects, which is what makes this the path
    //             a human takes. No ApplyEntry, no SetBaseModelIndex, no RPC from here.
    //   protocol  the module's existing authoritative selection helper (ApplyEntry -> ApplyNative ->
    //             the host's PlayersAppearanceManager.SetBaseModelIndex, the call
    //             WardrobeController.OnModelSelectedServerRpc makes), with test provenance. HOST ONLY:
    //             that write is the host's authority, and a client's real path is the wardrobe UI.
    //
    // Both fail loudly and neither falls back to the other path: a `ui` run that cannot find the
    // view, the slug or the tab reports why instead of applying the model directly, because a silent
    // fallback would make "the UI path works" unfalsifiable.

    // Diagnostic label for the explicit `protocol` test path; never persisted as a choice.
    internal const string ProtocolWhy = "test protocol";

    // `ui`: resolve the slug to the tab the OPEN view built for it, then press that tab's own
    // UI.Elements.Button with the same four calls the mod's existing UI click machinery makes for a
    // Button (ModelSwapRuntime.ActivateOrClick: Click with an EventSystem PointerEventData, then
    // ClickedSafe / ClickedSafeReferenced / ClickedUnsafe). It is not called through ActivateOrClick
    // itself because that is an instance method on the runtime this module does not hold, and it
    // matches by hierarchy-path substring across every active MonoBehaviour - which would press
    // whatever else shares the path. `detail` names what was pressed, or exactly what was missing.
    internal static bool TrySelectModelViaUi(IHostContext log, string slug, out string detail)
    {
        detail = "";
        slug = (slug ?? "").Trim();
        if (slug.Length == 0) { detail = "no slug given"; return false; }

        // 1 the native view. A tab list only exists once the view is attached, and a click path only
        //   exists while it is OPEN: pressing a hidden row would prove nothing about the UI.
        if (!ViewAttached)
        {
            detail = "the native wardrobe view is not attached in this scene (open it first: " +
                     "test.open-wardrobe), so there is no row to press";
            return false;
        }
        bool open = false;
        try { open = _view.gameObject.activeInHierarchy; } catch { }
        if (!open)
        {
            detail = "the native wardrobe view is attached but closed (open it first: test.open-wardrobe); " +
                     "a hidden row is not the click path";
            return false;
        }

        // 2 the slug, through the module's own resolver (label, squashed label or ModelSpec.Slug).
        var entry = Find(slug);
        if (entry == null)
        {
            detail = $"no wardrobe entry for '{slug}' (have {SlugList()})";
            return false;
        }
        if (entry.Data == null || entry.Index < 0)
        {
            detail = $"entry '{entry.Label}' (slug='{EntrySlug(entry)}') is not registered in " +
                     $"PlayerModelDatabase.Models yet (data={(entry.Data != null)} index={entry.Index})";
            return false;
        }

        // 3 the actual tab the view built for that row, and its own Button.
        var tab = FindTab(slug, out int index);
        if (tab == null)
        {
            detail = $"the open view has no tab for '{entry.Label}' (slug='{EntrySlug(entry)}', " +
                     $"tabs={ViewTabCount}); the rows may not have been rebuilt yet";
            return false;
        }
        var go = tab.gameObject;
        if (go == null || !go.activeInHierarchy)
        {
            detail = $"tab '{tab.name}' (list[{index}]) exists but is not on screen (activeInHierarchy=false)";
            return false;
        }
        var btn = tab._button;
        if (btn == null)
        {
            detail = $"tab '{tab.name}' (list[{index}]) has no UI.Elements.Button, so it has no click path";
            return false;
        }

        string tabPath = PathOf(tab.transform), btnPath = PathOf(btn.transform);
        bool interactable = false, canClick = false;
        try { interactable = btn.IsInteractable; } catch { }
        try { canClick = btn.CanClick(); } catch { }
        var appliedBefore = AppliedData(log);
        log.LogInfo($"[sahur:test] select-model ui: slug='{slug}' entry='{entry.Label}' dbIndex={entry.Index} " +
                    $"tab='{tabPath}' list[{index}] button='{btnPath}' interactable={interactable} canClick={canClick} " +
                    $"applied-before='{RowName(appliedBefore)}'");

        // 4 the press. PointerEventData from the live EventSystem, positioned at the row's own centre,
        //   exactly as ClickTab and ActivateOrClick build it.
        var es = UnityEngine.EventSystems.EventSystem.current;
        var ped = new UnityEngine.EventSystems.PointerEventData(es);
        try
        {
            var rt = tab.transform.TryCast<RectTransform>();
            var world = rt == null ? Vector3.zero : rt.TransformPoint(rt.rect.center);
            var sp = RectTransformUtility.WorldToScreenPoint(null, world);
            ped.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
            ped.position = sp;
            ped.pressPosition = sp;
            ped.clickCount = 1;
            log.LogInfo($"[sahur:test] select-model ui: pointer={sp.ToString("F0")} " +
                        $"screen={Screen.width}x{Screen.height} " +
                        $"EventSystem={(es == null ? "null" : PathOf(es.transform))}");
        }
        catch (Exception e) { log.LogWarning($"[sahur:test] select-model ui: pointer position failed: {e.Message}"); }

        try { btn.Click(ped); }
        catch (Exception e)
        {
            detail = $"UI.Elements.Button.Click on '{btnPath}' threw {e.GetType().Name}: {e.Message}";
            return false;
        }
        var safe = btn.ClickedSafe;
        if (safe != null) { try { safe.Invoke(); } catch (Exception e) { log.LogWarning($"[sahur:test] select-model ui: ClickedSafe threw {e.Message}"); } }
        var safeRef = btn.ClickedSafeReferenced;
        if (safeRef != null) { try { safeRef.Invoke(btn); } catch (Exception e) { log.LogWarning($"[sahur:test] select-model ui: ClickedSafeReferenced threw {e.Message}"); } }
        try { btn.ClickedUnsafe.Invoke(); }
        catch (Exception e) { log.LogWarning($"[sahur:test] select-model ui: ClickedUnsafe threw {e.Message}"); }

        // 5 the read-back, not a claim: the row the local body carries now. On a client the game's own
        //   RPC answers from the host, so an unchanged row here means "not landed yet", which the line
        //   says instead of pretending the click was ignored.
        var appliedAfter = AppliedData(log);
        bool landed = Same(appliedBefore, entry.Data) || Same(appliedAfter, entry.Data);
        detail = $"pressed the wardrobe's own row '{tabPath}' (list[{index}], button '{btnPath}', " +
                 $"interactable={interactable} canClick={canClick}) via Button.Click + ClickedSafe/Referenced/Unsafe; " +
                 $"applied '{RowName(appliedBefore)}' -> '{RowName(appliedAfter)}' " +
                 (landed
                     ? $"('{entry.Label}' is the row on the local body)"
                     : "(the click path ran; the game's own RPC/apply has not put the row on this body yet)");
        return true;
    }

    // `protocol`: the module's existing authoritative selection helper, with test provenance. No UI is
    // involved and none is required; the write is the host's, so a client is refused here rather than
    // driven down a server RPC by a test verb.
    internal static bool TrySelectModelViaProtocol(IHostContext log, string slug, out string detail)
    {
        detail = "";
        slug = (slug ?? "").Trim();
        if (slug.Length == 0) { detail = "no slug given"; return false; }

        if (!NetRole.IsServer)
        {
            detail = $"host only: this instance is '{NetRole.Word()}', and the selection protocol is the " +
                     "host's authority (use the ui variant on a client)";
            return false;
        }

        var entry = Find(slug);
        if (entry == null) { detail = $"no wardrobe entry for '{slug}' (have {SlugList()})"; return false; }
        int entryIndex = Array.IndexOf(Entries, entry);
        if (!ApplyEntry(log, entryIndex, ProtocolWhy))
        {
            detail = $"the module's selection path did not take '{entry.Label}' (slug='{EntrySlug(entry)}'): " +
                     $"data={(entry.Data != null)} index={entry.Index}";
            return false;
        }
        detail = $"'{entry.Label}' (slug='{EntrySlug(entry)}', dbIndex={entry.Index}) applied through the " +
                 $"module's existing selection path (ApplyEntry -> SetBaseModelIndex), provenance='{ProtocolWhy}'";
        return true;
    }

    // The failure text names what WOULD have been accepted, so a wrong slug is diagnosable from the
    // one line instead of needing a second command.
    private static string SlugList()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Entries.Length; i++)
        {
            sb.Append(i == 0 ? "" : ", ").Append('\'').Append(Entries[i].Label).Append("' [")
              .Append(EntrySlug(Entries[i])).Append(']');
        }
        return sb.ToString();
    }

    private static string EntrySlug(Entry e) => e == null || e.Spec == null ? "" : (e.Spec.Slug ?? "");

    private static string RowName(PlayerModelData d)
    {
        try { return d == null ? "none" : (d.name ?? ""); } catch { return "?"; }
    }


    // ================================================================== component report / harness

    // One row's full click surface, printed next to a native row: the custom UI.Elements.Button the
    // game actually wires (not UnityEngine.UI.Button), its delegates/guards, the graphic that receives
    // the raycast, and the EventSystem modules. `cmp` with no value compares row 9 (RYAN) and row 10
    // (our first added row); a label or index picks any other row.
    private static void RowComponents(IHostContext log, string name)
    {
        var vc = _view;
        var list = vc == null ? null : vc._instantiatedWardrobeModelButtons;
        if (list == null) { log.LogWarning("[sahur:cmp] no wardrobe tab list"); return; }
        var picks = new List<int>();
        if (string.IsNullOrEmpty(name))
        {
            picks.Add(9 < Count(list) ? 9 : 0);
            picks.Add(10 < Count(list) ? 10 : Count(list) - 1);
        }
        else
        {
            int idx;
            if (int.TryParse(name, out idx)) picks.Add(idx);
            else
            {
                var tab = FindTab(name, out idx);
                if (tab != null) picks.Add(idx);
            }
        }
        foreach (var i in picks)
        {
            if (i < 0 || i >= Count(list)) { log.LogWarning($"[sahur:cmp] no row {i}"); continue; }
            var t = list[i];
            if (t == null) { log.LogWarning($"[sahur:cmp] row {i} is null"); continue; }
            var go = t.gameObject;
            var rt = t.transform.TryCast<RectTransform>();
            log.LogInfo($"[sahur:cmp] row[{i}] '{PathOf(t.transform)}' name='{t.name}' activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy} " +
                        $"layer={go.layer}({LayerMask.LayerToName(go.layer)}) data='{SafeDataName(t)}' " +
                        $"text='{(t._modelNameTMP == null ? "null" : t._modelNameTMP.text)}' sibling={t.transform.GetSiblingIndex()} {RectText(rt)}");
            try
            {
                var all = go.GetComponents(Il2CppType.Of<UnityEngine.Component>());
                var sb = new StringBuilder();
                for (int k = 0; all != null && k < all.Length; k++)
                {
                    var c = all[k];
                    if (c == null) continue;
                    sb.Append(c.GetIl2CppType().Name).Append(k + 1 < all.Length ? "," : "");
                }
                log.LogInfo($"[sahur:cmp]   components({(all == null ? -1 : all.Length)}) = {sb}");
            }
            catch (Exception e) { log.LogWarning($"[sahur:cmp]   component list failed: {e.Message}"); }

            var b = t._button;
            if (b == null) log.LogWarning("[sahur:cmp]   _button = null (row has no game click handler)");
            else
            {
                bool interactable = false, canClick = false;
                string safe = "?", safeRef = "?", unsafeCount = "?";
                try { interactable = b.IsInteractable; } catch { }
                try { canClick = b.CanClick(); } catch { }
                try { safe = b.ClickedSafe == null ? "null" : "set"; } catch { }
                try { safeRef = b.ClickedSafeReferenced == null ? "null" : "set"; } catch { }
                try { var ev = b.ClickedUnsafe; unsafeCount = ev == null ? "null" : ev.GetPersistentEventCount().ToString(); } catch { }
                log.LogInfo($"[sahur:cmp]   UI.Elements.Button '{PathOf(b.transform)}' enabled={b.enabled} active={b.isActiveAndEnabled} " +
                            $"interactable={interactable} canClick={canClick} ClickedSafe={safe} ClickedSafeReferenced={safeRef} " +
                            $"ClickedUnsafe.events={unsafeCount} leftAllowed={(b.IsLeftClickAllowed ? "yes" : "no")}");
            }
            try
            {
                var ugui = Comp<UnityEngine.UI.Button>(go);
                log.LogInfo($"[sahur:cmp]   UnityEngine.UI.Button={(ugui == null ? "no" : "yes")} " +
                            $"{(ugui == null ? "" : $"interactable={ugui.interactable} onClickEvents={OnClickText(ugui)}")}");
            }
            catch { }
            var et = Comp<UnityEngine.EventSystems.EventTrigger>(go);
            log.LogInfo($"[sahur:cmp]   EventTrigger={(et == null ? "no" : "yes")} " +
                        $"{(et == null ? "" : $"triggers={et.triggers?.Count}")}");
            var im = Comp<Image>(go);
            var gr = Comp<UnityEngine.UI.Graphic>(go);
            log.LogInfo($"[sahur:cmp]   Image={(im == null ? "no" : "yes")} raycastTarget={(im == null ? "-" : im.raycastTarget.ToString())} " +
                        $"enabled={(im == null ? "-" : im.enabled.ToString())} Graphic={(gr == null ? "no" : gr.GetIl2CppType().Name)}");
            var cg = Comp<CanvasGroup>(go);
            log.LogInfo($"[sahur:cmp]   CanvasGroup={(cg == null ? "no" : $"alpha={cg.alpha:F2} interactable={cg.interactable} blocks={cg.blocksRaycasts}")}");
            try
            {
                var g = vc._modelsButtonsTabGroup;
                bool contains = false;
                int gtabs = -1;
                string active = "?", prev = "?";
                if (g != null)
                {
                    try { gtabs = g._tabs == null ? -1 : g._tabs.Count; } catch { }
                    try { active = g.ActiveTab == null ? "null" : (g.ActiveTab.TryCast<WardrobeModelButtonedTab>()?.PlayerModelData?.name ?? g.ActiveTab.name); } catch { }
                    try { prev = g.PreviousActiveTab == null ? "null" : g.PreviousActiveTab.name; } catch { }
                    try
                    {
                        for (int k = 0; g._tabs != null && k < g._tabs.Count; k++)
                            if (g._tabs[k] == t) { contains = true; break; }
                    }
                    catch { }
                }
                log.LogInfo($"[sahur:cmp]   Tab group={(g == null ? "null" : PathOf(g.transform))} tabs={gtabs} containsRow={contains} " +
                            $"active='{active}' previous='{prev}' rowGroupOwner={(t.GroupOwner == null ? "null" : "set")} rowIsActivated={t.IsActivated}");
            }
            catch (Exception e) { log.LogWarning($"[sahur:cmp]   tab group read failed: {e.Message}"); }
            try
            {
                var gr2 = go.GetComponentsInChildren(Il2CppType.Of<UnityEngine.UI.Graphic>(), true);
                var sb = new StringBuilder();
                for (int k = 0; gr2 != null && k < gr2.Length; k++)
                {
                    var g = gr2[k].TryCast<UnityEngine.UI.Graphic>();
                    if (g == null) continue;
                    sb.Append($"'{g.name}' raycast={g.raycastTarget} enabled={g.enabled}; ");
                }
                log.LogInfo($"[sahur:cmp]   child Graphics ({(gr2 == null ? -1 : gr2.Length)}): {sb}");
            }
            catch (Exception e) { log.LogWarning($"[sahur:cmp]   child graphic dump failed: {e.Message}"); }
        }
        DumpInputModules(log);
    }

    private static void DumpInputModules(IHostContext log)
    {
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) { log.LogInfo("[sahur:cmp] EventSystem=null"); return; }
            string curType = "null", curPath = "null";
            try
            {
                var cur = es.currentInputModule;
                if (cur != null) { curType = cur.GetIl2CppType().Name; curPath = PathOf(cur.transform); }
            }
            catch (Exception e) { curType = "err:" + e.Message; }
            log.LogInfo($"[sahur:cmp] EventSystem='{PathOf(es.transform)}' currentInputModule='{curType}' path='{curPath}' " +
                        $"sendNavigation={es.sendNavigationEvents} currentSelected='{(es.currentSelectedGameObject == null ? "null" : es.currentSelectedGameObject.name)}'");
            var all = es.gameObject.GetComponents(Il2CppType.Of<UnityEngine.Component>());
            var sb = new StringBuilder();
            for (int k = 0; all != null && k < all.Length; k++)
            {
                var c = all[k];
                if (c == null) continue;
                sb.Append(c.GetIl2CppType().Name);
                var beh = c.TryCast<Behaviour>();
                if (beh != null) sb.Append(beh.enabled ? "[on]" : "[off]");
                sb.Append(k + 1 < all.Length ? ", " : "");
            }
            log.LogInfo($"[sahur:cmp] EventSystem components = {sb}");
            var mods = es.gameObject.GetComponents(Il2CppType.Of<UnityEngine.EventSystems.BaseInputModule>());
            for (int k = 0; mods != null && k < mods.Length; k++)
            {
                var m = mods[k].TryCast<UnityEngine.EventSystems.BaseInputModule>();
                if (m == null) continue;
                log.LogInfo($"[sahur:cmp] module[{k}] '{m.GetIl2CppType().Name}' enabled={m.enabled} active={m.isActiveAndEnabled} " +
                            $"isCurrent={m == es.currentInputModule}");
            }
            try
            {
                var uis = SceneCache.Scan(Il2CppType.Of<UnityEngine.InputSystem.UI.InputSystemUIInputModule>());
                log.LogInfo($"[sahur:cmp] InputSystemUIInputModule instances={uis?.Length ?? 0}");
                for (int k = 0; uis != null && k < uis.Length; k++)
                {
                    var m = uis[k].TryCast<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                    if (m == null || m.gameObject == null) continue;
                    if (string.IsNullOrEmpty(m.gameObject.scene.name)) continue;
                    log.LogInfo($"[sahur:cmp]   uimodule[{k}] '{PathOf(m.transform)}' enabled={m.enabled} active={m.isActiveAndEnabled} " +
                                $"isCurrent={m == es.currentInputModule}");
                }
            }
            catch (Exception e) { log.LogWarning($"[sahur:cmp] uimodule scan failed: {e.Message}"); }
        }
        catch (Exception e) { log.LogWarning($"[sahur:cmp] input module dump failed: {e.Message}"); }
    }

    // The player's actual E path: Interactor.Interact(WardrobeInteractable) runs the interaction
    // constraints, the local open and the server request, unlike calling TryOpenWardrobe directly.
    private static void OpenInteract(IHostContext log)
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<InteractionSystem.Implementation.Interactables.WardrobeInteractable>());
            var owner = MirrorPlayerVisibility.Current == null ? null : MirrorPlayerVisibility.Current.Owner;
            var ints = SceneCache.Scan(Il2CppType.Of<InteractionSystem.Core.Interactor>());
            InteractionSystem.Core.Interactor local = null;
            for (int i = 0; ints != null && i < ints.Length; i++)
            {
                var it = ints[i].TryCast<InteractionSystem.Core.Interactor>();
                if (it == null || it.gameObject == null || !it.gameObject.activeInHierarchy) continue;
                if (owner != null && !Under(it.transform, owner)) continue;
                local = it;
                log.LogInfo($"[sahur:cmp] local Interactor '{PathOf(it.transform)}' current={(it.CurrentInteractable == null ? "null" : PathOf(it.CurrentInteractable.transform))}");
                break;
            }
            if (local == null) { log.LogWarning("[sahur:cmp] no local Interactor"); return; }
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var w = arr[i].TryCast<InteractionSystem.Implementation.Interactables.WardrobeInteractable>();
                if (w == null || w.gameObject == null || !w.gameObject.activeInHierarchy) continue;
                log.LogInfo($"[sahur:cmp] Interact(WardrobeInteractable '{PathOf(w.transform)}')");
                bool ok = false;
                try { ok = local.Interact(w); } catch (Exception e) { log.LogWarning($"[sahur:cmp] Interact threw: {e.Message}"); }
                log.LogInfo($"[sahur:cmp] Interact returned {ok}; viewActive={_view != null && _view.gameObject.activeInHierarchy}");
                DumpInputModules(log);
                return;
            }
            log.LogWarning("[sahur:cmp] no active WardrobeInteractable");
        }
        catch (Exception e) { log.LogWarning($"[sahur:cmp] open-interact failed: {e.Message}"); }
    }

    // The game's own click entry point on the row's UI.Elements.Button. Unlike Click(pointer) this
    // does not need the EventSystem's press bookkeeping, so it proves whether the row is wired to the
    // native selection. It is NOT a pointer click; the log says so.
    private static void InvokeSafe(IHostContext log, string name)
    {
        var tab = FindTab(name, out int idx);
        if (tab == null) { log.LogWarning($"[sahur:cmp] safe: no row '{name}'"); return; }
        var b = tab._button;
        if (b == null) { log.LogWarning($"[sahur:cmp] safe: row {idx} has no UI.Elements.Button"); return; }
        log.LogInfo($"[sahur:cmp] invoking row[{idx}] '{SafeDataName(tab)}' UI.Elements.Button.OnClickedSafe() " +
                    $"(handler invocation, not a pointer click)");
        try { b.OnClickedSafe(); log.LogInfo("[sahur:cmp] OnClickedSafe returned"); }
        catch (Exception e) { log.LogWarning($"[sahur:cmp] OnClickedSafe threw: {e.Message}"); }
    }


    // The row's UnityEvent (runtime listeners included) and Tab.Activate: the two paths the native
    // Button uses internally. Harness evidence only; the log names this as handler invocation.
    private static void InvokeUnsafe(IHostContext log, string name)
    {
        var tab = FindTab(name, out int idx);
        if (tab == null) { log.LogWarning($"[sahur:cmp] unsafe: no row '{name}'"); return; }
        var b = tab._button;
        if (b == null) { log.LogWarning($"[sahur:cmp] unsafe: row {idx} has no UI.Elements.Button"); return; }
        log.LogInfo($"[sahur:cmp] invoking row[{idx}] '{SafeDataName(tab)}' ClickedUnsafe.Invoke() (handler invocation, not a pointer click)");
        try { b.ClickedUnsafe.Invoke(); log.LogInfo("[sahur:cmp] ClickedUnsafe returned"); }
        catch (Exception e) { log.LogWarning($"[sahur:cmp] ClickedUnsafe threw: {e.Message}"); }
    }

    private static void InvokeActivate(IHostContext log, string name)
    {
        var tab = FindTab(name, out int idx);
        if (tab == null) { log.LogWarning($"[sahur:cmp] activate: no row '{name}'"); return; }
        log.LogInfo($"[sahur:cmp] invoking row[{idx}] '{SafeDataName(tab)}' Tab.Activate() (handler invocation, not a pointer click)");
        try { tab.Activate(); log.LogInfo("[sahur:cmp] Activate returned"); }
        catch (Exception e) { log.LogWarning($"[sahur:cmp] Activate threw: {e.Message}"); }
    }

    // The game's own open path: WardrobeInputHandler.TryOpenWardrobe (raycast + distance check to
    // the lobby's wardrobe object, then the optional view's own Show). Used only to put the UI into
    // a state a human could have reached, so a real mouse click can be tried against it.
    private static void OpenNative(IHostContext log)
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<InputSystem.UI.WardrobeInputHandler>());
            log.LogInfo($"[sahur:ui] open: {(arr == null ? 0 : arr.Length)} WardrobeInputHandler(s)");
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var h = arr[i].TryCast<InputSystem.UI.WardrobeInputHandler>();
                if (h == null || h.gameObject == null) continue;
                if (h.gameObject.scene.name == null || h.gameObject.scene.name.Length == 0) continue;
                string obj = "?";
                try { obj = h._wardrobeObject == null ? "null" : PathOf(h._wardrobeObject.transform); } catch { }
                log.LogInfo($"[sahur:ui] open handler '{PathOf(h.transform)}' wardrobeObject='{obj}' " +
                            $"view={(h._wardrobeViewController == null ? "null" : PathOf(h._wardrobeViewController.transform))}");
                try
                {
                    h.TryOpenWardrobe();
                    log.LogInfo($"[sahur:ui] open: TryOpenWardrobe() returned; view active={_view != null && _view.gameObject.activeInHierarchy}");
                }
                catch (Exception e) { log.LogWarning($"[sahur:ui] open: TryOpenWardrobe failed: {e.Message}"); }
            }
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] open failed: {e.Message}"); }
    }

    private static string SafeDataName(WardrobeModelButtonedTab t)
    {
        try { return t.PlayerModelData == null ? "?" : (t.PlayerModelData.name ?? "?"); } catch { return "?"; }
    }

    private static string PreviewName(WardrobeOptionalViewController vc)
    {
        try
        {
            var d = vc._modelDisplayer == null ? null : vc._modelDisplayer._currentplayerModeData;
            return d == null ? "null" : (d.name ?? "?");
        }
        catch { return "?"; }
    }

    // Click a row by its on-screen label (native rows included). Same Button.Click the native
    // click path uses, so the wardrobe's own OnModelClicked runs: preview swap + model index RPC.
    private static void Row(IHostContext log, string name)
    {
        var vc = _view;
        var list = vc == null ? null : vc._instantiatedWardrobeModelButtons;
        string want = (name ?? "").Trim();
        if (want.Length == 0) { log.LogWarning("[sahur:ui] row: give a label"); return; }
        for (int i = 0; i < Count(list); i++)
        {
            var t = list[i];
            if (t == null) continue;
            string text = null;
            try { text = t._modelNameTMP == null ? null : t._modelNameTMP.text; } catch { }
            string dname = SafeDataName(t);
            bool hit = (text != null && text.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) ||
                       (dname != null && dname.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hit) continue;
            var btn = t._button;
            if (btn == null) { log.LogWarning($"[sahur:ui] row: '{t.name}' has no UI.Elements.Button"); return; }
            var es = UnityEngine.EventSystems.EventSystem.current;
            var ped = new UnityEngine.EventSystems.PointerEventData(es);
            try
            {
                var rt = t.transform.TryCast<RectTransform>();
                var world = rt == null ? Vector3.zero : rt.TransformPoint(rt.rect.center);
                var sp = RectTransformUtility.WorldToScreenPoint(null, world);
                ped.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
                ped.position = sp; ped.pressPosition = sp; ped.clickCount = 1;
            }
            catch { }
            log.LogInfo($"[sahur:ui] row click list[{i}] '{t.name}' text='{text}' data='{dname}'");
            btn.Click(ped);
            return;
        }
        log.LogWarning($"[sahur:ui] row: no row matching '{want}' among {Count(list)} tabs");
    }

    private static void SelectButton(IHostContext log)
    {
        var vc = _view;
        if (vc == null) { log.LogWarning("[sahur:ui] selectbutton: no view"); return; }
        string path = "null", text = "null";
        try { path = vc._selectButton == null ? "null" : PathOf(vc._selectButton.transform); } catch { }
        try { text = vc._selectButtonTMP == null ? "null" : vc._selectButtonTMP.text; } catch { }
        log.LogInfo($"[sahur:ui] selectbutton '{path}' text='{text}' preview='{PreviewName(vc)}' -> OnSelectButtonClicked()");
        try { vc.OnSelectButtonClicked(); }
        catch (Exception e) { log.LogWarning($"[sahur:ui] selectbutton failed: {e.Message}"); }
    }

    // Screen-space hit test at the row's centre through the game's own EventSystem: what a click at
    // that pixel would land on. Proves the row is a raycast target in the scroll viewport.
    // Test harness only: the lobby EventSystem carries a bare BaseInputModule, so no UI pointer
    // event is processed at all (not even for the game's own rows). Adding the Input System module
    // makes the EventSystem deliver real clicks, which is what a mouse test needs; 'off' removes it.
    private static void RaycastRow(IHostContext log, string name)
    {
        var tab = FindTab(name, out _);
        var vc = _view;
        if (vc == null) { log.LogWarning($"[sahur:ui] raycast: no view"); return; }
        bool point = tab == null && name != null && name.IndexOf(',') > 0;
        if (tab == null && !point) { log.LogWarning($"[sahur:ui] raycast: no tab for '{name}'"); return; }
        try
        {
            Vector2 sp;
            int comma = name.IndexOf(',');
            if (comma > 0 &&
                float.TryParse(name.Substring(0, comma), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px) &&
                float.TryParse(name.Substring(comma + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var py))
            {
                sp = new Vector2(px, py);
            }
            else
            {
                var rt = tab.transform.TryCast<RectTransform>();
                var world = rt == null ? Vector3.zero : rt.TransformPoint(rt.rect.center);
                sp = RectTransformUtility.WorldToScreenPoint(null, world);
            }
            var es = UnityEngine.EventSystems.EventSystem.current;
            var ped = new UnityEngine.EventSystems.PointerEventData(es);
            ped.position = sp;
            ped.pressPosition = sp;
            ped.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
            var hits = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(ped, hits);
            log.LogInfo($"[sahur:ui] raycast '{(tab == null ? name : SafeDataName(tab))}' at {sp.ToString("F0")} -> {hits.Count} hit(s)");
            for (int i = 0; i < hits.Count && i < 6; i++)
            {
                var r = hits[i];
                var g = r.gameObject;
                string path = g == null ? "null" : PathOf(g.transform);
                bool ours = g != null && (PathOf(g.transform).IndexOf("Model Selectable", StringComparison.Ordinal) >= 0 ||
                                          PathOf(g.transform).IndexOf(InsertMarker, StringComparison.Ordinal) >= 0);
                log.LogInfo($"[sahur:ui] raycast   [{i}] depth={r.depth:F1} ours={ours} '{path}'");
            }
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] raycast failed: {e.Message}"); }
    }

    private static WardrobeModelButtonedTab FindTab(string name, out int index)
    {
        index = -1;
        var vc = _view;
        if (vc == null) return null;
        var list = vc._instantiatedWardrobeModelButtons;
        var entry = Find(name);
        int want = -1;
        if (entry == null && !int.TryParse(name, out want)) return null;
        for (int i = 0; i < Count(list); i++)
        {
            var t = list[i];
            if (t == null) continue;
            if (entry != null) { if (!Same(t.PlayerModelData, entry.Data)) continue; }
            else if (i != want) continue;
            index = i;
            return t;
        }
        return null;
    }

    // The game's own close path (WardrobeInputHandler.TryCloseWardrobe): it restores the lobby UI
    // state and the camera, which a bare ViewController.Hide() does not.
    private static void CloseNative(IHostContext log)
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<InputSystem.UI.WardrobeInputHandler>());
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var h = arr[i].TryCast<InputSystem.UI.WardrobeInputHandler>();
                if (h == null || h.gameObject == null) continue;
                if (h.gameObject.scene.name == null || h.gameObject.scene.name.Length == 0) continue;
                log.LogInfo($"[sahur:ui] close handler '{PathOf(h.transform)}'");
                try
                {
                    h.TryCloseWardrobe();
                    log.LogInfo($"[sahur:ui] close: TryCloseWardrobe() returned; view active={_view != null && _view.gameObject.activeInHierarchy}");
                }
                catch (Exception e) { log.LogWarning($"[sahur:ui] close: TryCloseWardrobe failed: {e.Message}"); }
            }
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] close failed: {e.Message}"); }
    }

    private static void ScrollTo(IHostContext log, string val)
    {
        var vc = _view;
        var p = vc == null ? null : vc._wardrobeModelButtonedTabsParent;
        if (p == null) { log.LogWarning("[sahur:ui] scroll: no tabs parent"); return; }
        var rt = p.TryCast<RectTransform>();
        var sr = p.GetComponentInParent(Il2CppType.Of<ScrollRect>())?.TryCast<ScrollRect>();
        if (sr == null) { log.LogWarning("[sahur:ui] scroll: no ScrollRect above the tabs parent"); return; }
        float v = 1f;
        if (val == "bottom" || val == "end" || val == "0") v = 0f;
        else if (val == "top" || val == "1" || val.Length == 0) v = 1f;
        else { float parsed; if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed)) v = parsed; }
        v = Math.Max(0f, Math.Min(1f, v));
        try
        {
            if (rt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            sr.verticalNormalizedPosition = v;
            log.LogInfo($"[sahur:ui] scroll verticalNormalizedPosition={v:F3} (contentH={ContentHeight(vc):F1} " +
                        $"viewportH={(sr.viewport == null ? -1f : sr.viewport.rect.height):F1})");
        }
        catch (Exception e) { log.LogWarning($"[sahur:ui] scroll failed: {e.Message}"); }
    }

    private static string OnClickText(UnityEngine.UI.Button btn)
    {
        try
        {
            if (btn == null) return "-";
            var ev = btn.onClick;
            int n = ev == null ? 0 : ev.GetPersistentEventCount();
            var sb = new StringBuilder();
            sb.Append(n).Append(':');
            for (int i = 0; i < n && i < 4; i++)
            {
                try { sb.Append(ev.GetPersistentMethodName(i)).Append('@').Append(ev.GetPersistentTarget(i) == null ? "null" : ev.GetPersistentTarget(i).name).Append(';'); }
                catch { }
            }
            return sb.ToString();
        }
        catch { return "?"; }
    }

    private static void EnsureTab(IHostContext log, WardrobeOptionalViewController vc, Entry entry)
    {
        var list = vc._instantiatedWardrobeModelButtons;
        if (list == null) return;
        int n = Count(list);
        for (int i = 0; i < n; i++)
        {
            var t = list[i];
            if (t == null) continue;
            if (Same(t.PlayerModelData, entry.Data)) { Track(log, vc, t, "native", entry); return; }
        }

        // Not in the game's list: the tabs were built before our entry existed. Insert one through
        // the wardrobe's own prefab/tab group so it behaves exactly like the others, and ACTIVATE it:
        // the prefab is the disabled template child ('Model Selectable', activeSelf=False), so an
        // unactivated clone is skipped by the layout group, never drawn and never raycastable.
        var prefab = vc._wardrobeModelButtonedTabPrefab;
        var parent = vc._wardrobeModelButtonedTabsParent;
        var group = vc._modelsButtonsTabGroup;
        if (prefab == null || parent == null || group == null)
        {
            log.LogWarning($"[sahur:wardrobe] cannot add the '{entry.Label}' tab: prefab={(prefab == null ? "null" : "ok")} " +
                           $"parent={(parent == null ? "null" : "ok")} group={(group == null ? "null" : "ok")}");
            return;
        }
        GameObject go = null;
        try
        {
            var obj = UnityEngine.Object.Instantiate(prefab.gameObject);
            go = obj?.TryCast<GameObject>();
            if (go == null) { log.LogWarning("[sahur:wardrobe] tab instantiate returned null"); return; }
            go.transform.SetParent(parent, false);
            go.name = InsertMarker + entry.Label.Replace(" ", "").Replace("(", "").Replace(")", "");
            go.SetActive(true);
            var tab = go.GetComponent(Il2CppType.Of<WardrobeModelButtonedTab>())?.TryCast<WardrobeModelButtonedTab>();
            if (tab == null) { log.LogWarning("[sahur:wardrobe] clone has no WardrobeModelButtonedTab"); UnityEngine.Object.Destroy(go); return; }
            tab.Initialize(entry.Data);
            group.AddTab(tab);
            list.Add(tab);
            _tabs++;
            log.LogInfo($"[sahur:wardrobe] inserted tab '{PathOf(go.transform)}' for '{entry.Label}' at sibling " +
                        $"{go.transform.GetSiblingIndex()} of {parent.childCount} (tabs now {Count(list)}, insert #{_tabs}, active={go.activeSelf})");
            Track(log, vc, tab, "inserted", entry);
        }
        catch (Exception e)
        {
            log.LogWarning($"[sahur:wardrobe] tab insert failed: {e.Message}");
            try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
        }
    }

    private static void Track(IHostContext log, WardrobeOptionalViewController vc, WardrobeModelButtonedTab tab, string how, Entry entry)
    {
        if (entry.Tabs.Contains(tab)) return;
        entry.Tabs.Add(tab);
        try
        {
            tab.ActivatedSafe += DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => OnOursActivated(log, entry)));
            log.LogInfo($"[sahur:wardrobe] tracking '{entry.Label}' tab ({how}) '{PathOf(tab.transform)}'");
        }
        catch (Exception e)
        {
            log.LogWarning($"[sahur:wardrobe] tab click hook failed ({how}): {e.Message}");
        }
        // the tab list of the whole wardrobe, once per entry, so the element paths are on record
        try
        {
            var sb = new StringBuilder();
            var list = vc._instantiatedWardrobeModelButtons;
            for (int i = 0; i < Count(list); i++)
            {
                var t = list[i];
                if (t == null) continue;
                var d = t.PlayerModelData;
                sb.AppendLine($"    [{i}] '{PathOf(t.transform)}' data='{(d == null ? "null" : d.name)}' index={(d == null ? -1 : d.Index)} " +
                              $"text='{(t._modelNameTMP == null ? "null" : t._modelNameTMP.text)}'");
                DumpImages(sb, t.transform, "        ");
            }
            log.LogInfo($"[sahur:wardrobe] wardrobe model tabs:\n{sb}");
        }
        catch (Exception e) { log.LogWarning($"[sahur:wardrobe] tab dump failed: {e.Message}"); }
    }

    private static void DumpImages(StringBuilder sb, Transform root, string pad)
    {
        try
        {
            var imgs = root.GetComponentsInChildren(Il2CppType.Of<Image>(), true);
            for (int i = 0; imgs != null && i < imgs.Length; i++)
            {
                var im = imgs[i].TryCast<Image>();
                if (im == null) continue;
                var rt = im.rectTransform;
                sb.AppendLine($"{pad}Image '{PathOf(im.transform)}' sprite='{(im.sprite == null ? "null" : im.sprite.name)}' " +
                              $"{(rt == null ? "" : $"size={rt.sizeDelta.ToString("F0")} anchored={rt.anchoredPosition.ToString("F0")} pivot={rt.pivot.ToString("F2")}")} " +
                              $"enabled={im.enabled} active={im.gameObject.activeInHierarchy}");
            }
        }
        catch { }
    }

    // The wanted intent a database row index names, for the explicit programmatic selectors
    // (`select=`, `apply`, `index=`, `revert`, and the protocol test path). The row index chosen
    // outside the map path is the wanted entry: one of ours -> its roster slug, a native row -> ""
    // (the wanted entry becomes native, so a map releases our body instead of forcing the previous
    // row back on - the "deliberate native choice is honoured" rule), and null when there is no
    // such row or it has no stable identity (then nothing is recorded and the game's row is
    // followed, exactly as before).
    private static string WantedSlugForRow(IHostContext log, int dbIndex)
    {
        var row = DatabaseRowAt(dbIndex);
        if (row == null) return null;
        var owner = EntryOwningRow(log, row);
        if (owner == null) return "";
        string slug = EntrySlug(owner);
        return string.IsNullOrEmpty(slug) ? null : slug;
    }

    // A guest's explicit pick: the game's selection RPC was SENT, which is all this client can
    // observe - the call has no return value and the host owns the row. The wanted record is kept
    // (the body follows the pick) but marked PENDING, and the line says so instead of claiming the
    // row landed. ConfirmPending marks it confirmed once this client's own applied row is the row
    // the send carried (or our entry's row, when a modded host applied it directly).
    private static void NotePendingWanted(IHostContext log, string slug, ushort wireRow, string what)
    {
        string slot = string.IsNullOrEmpty(slug) ? "native" : $"slug='{slug}'";
        if (!RecordWanted(log, slug, "local guest selection (unconfirmed)", true, wireRow))
        {
            log?.LogInfo($"[model:wanted] why={what} ({slot}): wanted state unchanged - no local lobby " +
                         "session to scope an explicit choice to; the game's row is followed");
            return;
        }
        log?.LogInfo($"[model:wanted] client={_wantedClient} {slot} why={what} wireRow={wireRow} " +
                     "state=PENDING (the guest selection RPC was sent; a send is not an acknowledgement, " +
                     "so the pick stays pending until this body carries the row)");
    }

    // The only confirmation a guest can have: the row this client's body carries is the row the send
    // named, or our own entry's row when the host applied it directly. Nothing here reads an RPC
    // result, because there is none. Called from the 0.5 s selection pass, after the applied row was
    // read.
    private static void ConfirmPending(IHostContext log, PlayerModelData applied)
    {
        if (!_wantedPending) return;
        if (applied == null) return;
        var entry = string.IsNullOrEmpty(_wantedSlug) ? null : EntryAt(EntryIndexForSlug(_wantedSlug));
        bool ourRow = entry != null && Same(entry.Data, applied);
        bool sentRow = Same(DatabaseRowAt(_wantedPendingRow), applied);
        var verdict = NativeWirePolicy.PendingConfirm(string.IsNullOrEmpty(_wantedSlug), ourRow, sentRow);
        if (verdict == PendingVerdict.Pending) return;
        bool changed = false;
        try { changed = applied.Pointer != _wantedPendingFrom; } catch { }
        _wantedPending = false;
        // The readiness/provenance fields change even when the applied row did not: give the resolver
        // that edge, so a cached identity cannot keep reading "pending" after the row arrived.
        InvalidatePresentation(log, "guest selection confirmed");
        log?.LogInfo($"[model:wanted] client={_wantedClient} slug='{_wantedSlug}' wireRow={_wantedPendingRow} " +
                     $"state=CONFIRMED by the applied row '{RowName(applied)}' " +
                     $"(source={(verdict == PendingVerdict.OurRow ? "our-entry-row" : "the-row-the-send-carried")}, " +
                     $"{(changed ? "the row changed" : "already carried, so the send changed nothing observable")})");
    }

    // Record a programmatic explicit pick: the same scoped intent a wardrobe confirm records, so the
    // native-wire remap cannot turn it back into "follow the game's row". Called by ApplyIndex for
    // a write that actually took, never for a refused one.
    private static void NoteExplicitWanted(IHostContext log, string slug, string what)
    {
        string slot = string.IsNullOrEmpty(slug) ? "native" : $"slug='{slug}'";
        if (RecordWanted(log, slug, "local explicit selection"))
        {
            log?.LogInfo($"[model:wanted] client={_wantedClient} {slot} why={what} " +
                         "(programmatic explicit pick; the wire still carries a native row)");
            return;
        }
        log?.LogInfo($"[model:wanted] why={what} ({slot}): wanted state unchanged - no local lobby " +
                     "session to scope an explicit choice to; the game's row is followed");
    }

    private static void OnOursActivated(IHostContext log, Entry entry)
    {
        try
        {
            log.LogInfo($"[sahur:wardrobe] '{entry.Label}' tab activated by the user -> body on");
            foreach (var e in Entries) e.Selected = e == entry;
            if (entry.Rigged) SahurRig.SetFromWardrobe(log, entry.Spec, true);
            else SahurModel.SetFromWardrobe(log, entry.Spec, true);
            if (_view != null) StyleOurs(log, _view);
        }
        catch (Exception e) { log.LogWarning($"[sahur:wardrobe] tab activate failed: {e.Message}"); }
    }

    private static void AssertLabels()
    {
        bool active = false;
        try { active = _view != null && _view.gameObject.activeInHierarchy; } catch { }
        if (!active) return;
        foreach (var e in Entries)
        {
            for (int i = 0; i < e.Tabs.Count; i++)
            {
                var t = e.Tabs[i];
                if (t == null) continue;
                var tmp = t._modelNameTMP;
                if (tmp == null || tmp.text == e.Label) continue;
                try { tmp.text = e.Label; } catch { }
            }
        }
    }

    private static void StyleOurs(IHostContext log, WardrobeOptionalViewController vc)
    {
        var list = vc._instantiatedWardrobeModelButtons;
        foreach (var entry in Entries)
        {
            for (int i = 0; i < Count(list); i++)
            {
                var t = list[i];
                if (t == null || !Same(t.PlayerModelData, entry.Data)) continue;
                // name: the tab's own localized string is the donor's, so it is overwritten here and
                // re-asserted once a second (the async localization update can land after Initialize)
                var tmp = t._modelNameTMP;
                if (tmp != null && tmp.text != entry.Label)
                {
                    try { tmp.text = entry.Label; } catch { }
                }
                // icon: PlayerModelData.BustPortraitSprite is the game's per-model portrait slot; a
                // UGUI Image on the tab with no sprite is the other place a per-model picture can go
                try
                {
                    var imgs = t.GetComponentsInChildren(Il2CppType.Of<Image>(), true);
                    for (int k = 0; imgs != null && k < imgs.Length; k++)
                    {
                        var im = imgs[k].TryCast<Image>();
                        if (im == null || im.sprite != null) continue;
                        var rt = im.rectTransform;
                        bool tall = rt != null && rt.sizeDelta.y > rt.sizeDelta.x;
                        im.sprite = tall && entry.Full != null ? entry.Full : (entry.Bust ?? entry.Full);
                        log.LogInfo($"[sahur:wardrobe] icon for '{entry.Label}' -> Image '{PathOf(im.transform)}' " +
                                    $"(was empty, {(tall ? "portrait" : "bust")} sprite)");
                    }
                }
                catch (Exception e) { log.LogWarning($"[sahur:wardrobe] icon assign failed: {e.Message}"); }
            }
        }
    }

    // ================================================================== report / commands

    internal static void Command(IHostContext log, string line)
    {
        try
        {
            string text = (line ?? "").Trim();
            string lower = text.ToLowerInvariant();
            int eq = lower.IndexOf('=');
            string key = eq > 0 ? lower.Substring(0, eq) : lower;
            string val = eq > 0 ? lower.Substring(eq + 1).Trim() : "";
            switch (key)
            {
                case "": case "1": case "dump":
                    Dump(log);
                    break;
                case "select":
                    // "select=sahur" / "select=golubiro" / "select=sahur:off" -- the unattended
                    // equivalent of clicking the wardrobe tab, through the game's own model index.
                    Select(log, val);
                    break;
                case "index":
                    // "index=<0..databaseCount-1>" -- any row, native included. The unattended path
                    // for a full-roster pass; the selection itself still goes through the game, and
                    // it is recorded as the wanted entry (a native index makes the wanted entry
                    // native, which is what makes a native pick stick in a map).
                    if (int.TryParse(val, out var idx)) ApplyIndex(log, idx, $"index {idx}", null, WantedSlugForRow(log, idx));
                    else log.LogWarning($"[sahur:wardrobe] index: '{val}' is not a number");
                    break;
                case "on": case "select-native":
                    SetBody(log, Entries[0], true);
                    break;
                case "off":
                    SetBody(log, Entries[0], false);
                    break;
                case "body":
                    // "body=<label or slug>:<on|off>" - the unattended body switch for any entry
                    {
                        string nm = val;
                        bool on = true;
                        int colon = val.IndexOf(':');
                        if (colon > 0) { nm = val.Substring(0, colon); on = val.Substring(colon + 1) != "off" && val.Substring(colon + 1) != "0"; }
                        var hit = Find(nm);
                        if (hit == null) log.LogWarning($"[sahur:wardrobe] body: no entry '{nm}'");
                        else SetBody(log, hit, on);
                    }
                    break;
                case "hud":
                    DumpHud(log);
                    break;
                case "preview":
                    // the wardrobe's own preview instance: which one it is, what it was built from,
                    // and the Sahur model attached to it (dump|attach|detach)
                    SahurPreview.Command(log, val);
                    break;
                case "apply":
                    ApplyNative(log, Entries[0], -1);
                    break;
                case "revert":
                    // leave the game's own model index on the donor (the model Sahur was cloned from)
                    ApplyNative(log, Entries[0], DonorIndex(log, Entries[0]));
                    break;
                case "hudshot":
                    HudShot(log, val.Length == 0 ? "sahur_wardrobe_hud" : val);
                    break;
                case "ui": case "layout":
                    DumpLayout(log);
                    break;
                case "open":
                    OpenNative(log);
                    break;
                case "openinteract":
                    OpenInteract(log);
                    break;
                case "cmp":
                    RowComponents(log, val);
                    break;
                case "safe":
                    InvokeSafe(log, val);
                    break;
                case "unsafe":
                    InvokeUnsafe(log, val);
                    break;
                case "activate":
                    InvokeActivate(log, val);
                    break;
                case "sync":
                    SyncTabGroup(log, _view, "request");
                    break;
                case "sel":
                    SelectButtonInfo(log);
                    break;
                case "close":
                    CloseNative(log);
                    break;
                case "show":
                    if (_view == null) log.LogWarning("[sahur:ui] show: no wardrobe view");
                    else
                    {
                        // The game's own open: ViewController<T>.Show(), which runs the optional view's
                        // OnShow (that is where a clickable wardrobe is wired) instead of a bare SetActive.
                        bool before = false;
                        try { before = _view.IsShown; } catch { }
                        try { _view.Show(); }
                        catch (Exception e) { log.LogWarning($"[sahur:ui] show: Show() failed: {e.Message}"); _view.gameObject.SetActive(true); }
                        bool after = false;
                        try { after = _view.IsShown; } catch { }
                        log.LogInfo($"[sahur:ui] view '{PathOf(_view.transform)}' Show() isShown {before} -> {after} active={_view.gameObject.activeInHierarchy}");
                    }
                    break;
                case "hide":
                    if (_view == null) log.LogWarning("[sahur:ui] hide: no wardrobe view");
                    else
                    {
                        bool before = false;
                        try { before = _view.IsShown; } catch { }
                        try { _view.Hide(); }
                        catch (Exception e) { log.LogWarning($"[sahur:ui] hide: Hide() failed: {e.Message}"); _view.gameObject.SetActive(false); }
                        bool after = false;
                        try { after = _view.IsShown; } catch { }
                        log.LogInfo($"[sahur:ui] view '{PathOf(_view.transform)}' Hide() isShown {before} -> {after} active={_view.gameObject.activeInHierarchy}");
                    }
                    break;
                case "stale":
                    // Test the automatic path: drop the last two rows from the view's own list and
                    // destroy them, which is exactly the state a lobby load leaves when the tabs were
                    // built from the 10-model database before our entries were appended.
                    if (_view == null) { log.LogWarning("[sahur:ui] stale: no wardrobe view"); break; }
                    {
                        var list = _view._instantiatedWardrobeModelButtons;
                        int n = Count(list);
                        for (int i = n - 1; i >= 10 && i >= 0; i--)
                        {
                            var t = list[i];
                            list.RemoveAt(i);
                            if (t != null) { try { UnityEngine.Object.Destroy(t.gameObject); } catch { } }
                        }
                        log.LogInfo($"[sahur:ui] stale: dropped rows 10+ -> tabs={Count(list)} models={(PlayerModelDatabase.Models == null ? -1 : PlayerModelDatabase.Models.Length)}");
                    }
                    break;
                case "rebuild":
                    RebuildTabs(log, "request");
                    break;
                case "click":
                    ClickTab(log, val);
                    break;
                case "raycast":
                    RaycastRow(log, val);
                    break;
                case "row":
                    Row(log, val);
                    break;
                case "selectbutton":
                    SelectButton(log);
                    break;
                case "scroll":
                    ScrollTo(log, val);
                    break;
                default:
                    log.LogWarning($"[sahur:wardrobe] unknown command '{line}'");
                    break;
            }
        }
        catch (Exception e) { log.LogError($"[sahur:wardrobe] command failed: {e}"); }
    }

    // Enter the game's transport RPC directly for an automatic guest re-assert. OnModelSelected
    // is a thin local UI callback that forwards to this RPC; neither a callback return nor an RPC
    // send is an acknowledgement. The applied row must subsequently converge on the guest.
    private static bool TryGuestSelect(IHostContext log, WardrobeController wc, ulong clientId, ushort index,
                                       out string how)
    {
        how = null;
        try
        {
            var rpc = AccessTools.DeclaredMethod(typeof(WardrobeController), "OnModelSelectedServerRpc",
                                                new[] { typeof(ushort), typeof(Unity.Netcode.RpcParams) });
            if (rpc != null)
            {
                object rpcParams = Activator.CreateInstance(typeof(Unity.Netcode.RpcParams));
                rpc.Invoke(wc, new[] { (object)index, rpcParams });
                how = "WardrobeController.OnModelSelectedServerRpc(ushort,RpcParams)";
                return true;
            }
            var legacy = AccessTools.DeclaredMethod(typeof(WardrobeController), "OnModelSelectedServerRpc",
                                                   new[] { typeof(ulong), typeof(ushort) });
            if (legacy != null)
            {
                legacy.Invoke(wc, new object[] { clientId, index });
                how = "WardrobeController.OnModelSelectedServerRpc(ulong,ushort)";
                return true;
            }
        }
        catch (Exception e) { log?.LogWarning($"[sahur:wardrobe] guest selection call failed: {e.Message}"); }
        return false;
    }

    // Any label (or its squashed form, or the model slug) resolves, so an unattended run can select
    // every entry by name without a hardcoded index.
    private static Entry Find(string name)
    {
        if (name == null) return Entries[0];
        string n = name.Trim().ToLowerInvariant();
        foreach (var e in Entries)
        {
            string label = e.Label.ToLowerInvariant();
            string slug = (e.Spec == null ? "" : e.Spec.Slug).ToLowerInvariant();
            if (label == n || label.Replace(" ", "").Replace("(", "").Replace(")", "") == n || slug == n) return e;
        }
        if (n == "roblox" || n == "rigged" || n == "sahur") return Entries[0];
        return null;
    }

    private static void Select(IHostContext log, string name)
    {
        MarkManualSelection();
        string body = name;
        int colon = name.IndexOf(':');
        if (colon > 0) body = name.Substring(0, colon);
        var entry = Find(body);
        if (entry == null)
        {
            var have = new StringBuilder();
            foreach (var e in Entries) have.Append(have.Length == 0 ? "" : ", ").Append(e.Label);
            log.LogWarning($"[sahur:wardrobe] select: no entry '{name}' (have {have})");
            return;
        }
        ApplyNative(log, entry, entry.Index);
        // The path is reported by ApplyIndex (host write, guest send, or local SetupModel); claiming
        // SetBaseModelIndex here was wrong on a client, where the game's own RPC carries the row.
        log.LogInfo($"[sahur:wardrobe] select '{entry.Label}' (index {entry.Index}) requested; " +
                    "ApplyIndex reports the path that carried it");
    }

    private static void SetBody(IHostContext log, Entry entry, bool on)
    {
        entry.Selected = on;
        if (entry.Rigged) SahurRig.SetFromWardrobe(log, entry.Spec, on);
        else SahurModel.SetFromWardrobe(log, entry.Spec, on);
        log.LogInfo($"[sahur:wardrobe] '{entry.Label}' body forced {(on ? "on" : "off")} by request");
    }

    // Full-screen capture: the HUD portrait (and the wardrobe's tab list) live on screen-space
    // overlay canvases, which a camera render does not see.
    private static void HudShot(IHostContext log, string name)
    {
        try
        {
            Directory.CreateDirectory(log.ScreensDir);
            string path = Path.Combine(log.ScreensDir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            log.LogInfo($"[sahur:wardrobe] full-screen capture -> {path}");
        }
        catch (Exception e) { log.LogWarning($"[sahur:wardrobe] full-screen capture failed: {e.Message}"); }
    }

    private static int DonorIndex(IHostContext log, Entry entry)
    {
        // Only inside the proven shipped prefix: past it is one of our rows (or another module's),
        // which a stock peer cannot resolve, so "the model Sahur was cloned from" must be looked up
        // where shipped rows live. No proven prefix -> no provable donor -> row 0, which the apply
        // path clamps/closes on anyway.
        bool prefixKnown = KnownPrefix(log, out int nativeCount);
        int bound = prefixKnown ? nativeCount : 0;
        try
        {
            var models = PlayerModelDatabase.Models;
            for (int i = 0; models != null && i < bound && i < models.Length; i++)
            {
                var d = models[i];
                if (d == null || Same(d, entry == null ? null : entry.Data)) continue;
                if ((d.name ?? "").IndexOf("James", StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            for (int i = 0; models != null && i < bound && i < models.Length; i++)
                if (models[i] != null && !Same(models[i], entry == null ? null : entry.Data)) return i;
        }
        catch { }
        return 0;
    }

    // The game's own selection path, without the wardrobe UI: PlayersAppearanceManager is what
    // WardrobeController.OnModelSelectedServerRpc calls, and it re-applies on every scene load,
    // which is what makes the choice survive lobby -> map. Falls back to the per-player setuper.
    private static bool ApplyNative(IHostContext log, Entry entry, int forcedIndex, string why = null)
    {
        if (entry == null || entry.Data == null || entry.Index < 0) { log.LogWarning("[sahur:wardrobe] apply: no entry registered yet"); return false; }
        // The index actually asked for is what the player wants from now on: our own row when the
        // forced index is the entry's (or unset), a native row when a caller reverts to the donor.
        int target = forcedIndex >= 0 ? forcedIndex : entry.Index;
        // Our own row keeps the entry's slug even if the index bookkeeping lags a database rebuild;
        // any other forced index is read as the row it names (the donor -> deliberate native intent).
        string want = forcedIndex < 0 || forcedIndex == entry.Index
            ? (string.IsNullOrEmpty(EntrySlug(entry)) ? null : EntrySlug(entry))
            : WantedSlugForRow(log, forcedIndex);
        return ApplyIndex(log, target, $"'{entry.Label}'", entry.Data, want);
    }

    // Any row of PlayerModelDatabase.Models by index, native or ours. Used by the unattended
    // selection pass; the landing body is still the game's own model setuper.
    // `wantSlug` is the explicit caller's wanted intent (a roster slug, "" for a deliberate native
    // row, null for no intent). It is recorded only once the write took, so a refused apply never
    // changes what this client wants. Preview and random paths pass none.
    //
    // The index that leaves this method is NEVER the raw target: one pure decision
    // (NativeWirePolicy.ShippedWireIndex) clamps both the host write and the guest RPC, and it prefers
    // the native donor this client can prove (the row the body already carries, else the row the
    // entry was cloned from) to an arbitrary per-client row. A guest's send is recorded as PENDING,
    // never as a landed row: an RPC send has no acknowledgement.
    private static bool ApplyIndex(IHostContext log, int target, string what, PlayerModelData known = null,
                                   string wantSlug = null)
    {
        _ourApply = true;          // do not read our own write as a deliberate selection
        bool applied = false;
        bool guestSend = false;
        ushort guestWireRow = 0;
        try
        {
            if (target < 0) { log.LogWarning("[sahur:wardrobe] apply: no target index"); return false; }
            ulong clientId = 0;
            bool listening = false, server = false;
            try
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                listening = nm != null && nm.IsListening;
                server = listening && nm.IsServer;
                if (nm != null) clientId = nm.LocalClientId;
            }
            catch { }

            // The wire value, decided once for every path below. A process with no proven prefix has
            // no provably shipped row, so it fails closed to row 0 and says so.
            ushort requestedRow = (ushort)target;
            bool prefixKnown = KnownPrefix(log, out int nativeCount);
            int donor = prefixKnown && requestedRow >= nativeCount ? NativeDonorRow(log, nativeCount) : -1;
            ushort wireRow = NativeWirePolicy.ShippedWireIndex(prefixKnown, nativeCount, requestedRow, donor, clientId);
            if (!prefixKnown) NoNativePrefix(log, "local selection");
            else if (wireRow != requestedRow)
                log.LogInfo($"[model:native-wire] local selection client={clientId} requested={requestedRow} " +
                            $"native={wireRow} nativeCount={nativeCount} donor={donor}");

            // A GUEST cannot write the author's row through its local PlayersAppearanceManager: that
            // manager is a NetworkBehaviour owned by the server, and the write this method used to do
            // there never reached the host (2026-11-17 08:59Z: apply 'Capuchino' index=13 on B, while
            // A's log had no such call and kept re-spawned rows at index 4). The game's own guest
            // path is WardrobeController.OnModelSelectedServerRpc - the exact call the wardrobe tab
            // makes - so the host applies the row, publishes the appearance snapshot, and every peer
            // (including this one) rebuilds the actor and its HUD from that row.
            if (listening && !server)
            {
                var controllers = SceneCache.Scan(Il2CppType.Of<Gameplay.Wardrobe.WardrobeController>());
                WardrobeController wc = null;
                for (int i = 0; controllers != null && i < controllers.Length; i++)
                {
                    var c = controllers[i].TryCast<WardrobeController>();
                    if (c == null || c.gameObject == null) continue;
                    if (string.IsNullOrEmpty(c.gameObject.scene.name)) continue;   // prefab asset
                    wc = c; break;
                }
                if (wc == null)
                {
                    log.LogWarning($"[sahur:wardrobe] apply {what}: guest has no wardrobe controller " +
                                   "- the game's row stays as the join spawned it; the body module still follows the wanted entry");
                    return false;
                }
                if (!TryGuestSelect(log, wc, clientId, wireRow, out string how))
                {
                    log.LogWarning($"[sahur:wardrobe] apply {what}: guest has no selection entry point " +
                                   "- the game's row stays as the join spawned it; the body module still follows the wanted entry");
                    return false;
                }
                // SENT, not applied. The host owns the row and the RPC has no acknowledgement, so the
                // intent is recorded as pending and only the applied row can confirm it.
                log.LogInfo($"[sahur:wardrobe] apply {what}: {how}(client={clientId}, {wireRow}) " +
                            $"on '{PathOf(wc.transform)}' (sent on the game's own guest selection path; " +
                            "the host owns the row and has not acknowledged it - the intent stays PENDING " +
                            "until this body carries the row)");
                guestSend = true;
                guestWireRow = wireRow;
                applied = true;
                return true;
            }

            var managers = SceneCache.Scan(Il2CppType.Of<Gameplay.Appearance.PlayersAppearanceManager>());
            if (managers != null && managers.Length > 0)
            {
                var m = managers[0].TryCast<Gameplay.Appearance.PlayersAppearanceManager>();
                if (m != null)
                {
                    m.SetBaseModelIndex(clientId, wireRow);
                    log.LogInfo($"[sahur:wardrobe] apply {what}: SetBaseModelIndex(client={clientId}, {wireRow}" +
                                $"{(wireRow == requestedRow ? "" : $" from requested {requestedRow}")}) " +
                                $"on '{PathOf(m.transform)}' (the wardrobe's own path; the row handed to the game " +
                                "is a shipped row a peer without the mod can resolve)");
                    applied = true;
                    return true;
                }
            }
            var s = LocalSetuper(log);
            if (s == null) { log.LogWarning("[sahur:wardrobe] apply: no local setuper"); return false; }
            var data = known;
            if (data == null)
            {
                var models = PlayerModelDatabase.Models;
                if (models == null || target >= models.Length) { log.LogWarning($"[sahur:wardrobe] apply {what}: no model at index {target}"); return false; }
                data = models[target];
            }
            // Local body only: SetupModel takes the row object, not an index, and nothing here is
            // replicated - so this is the one path where our own row is correct.
            s.SetupModel(data);
            log.LogInfo($"[sahur:wardrobe] apply {what}: SetupModel('{data.name}') on '{PathOf(s.transform)}' (no appearance manager found; local only)");
            applied = true;
            return true;
        }
        catch (Exception e) { log.LogError($"[sahur:wardrobe] apply failed: {e}"); return false; }
        finally
        {
            _ourApply = false;
            // The write landed, so the explicit caller's intent is the wanted entry from now on.
            // On the host the row above was remapped to a native donor by the wire guard; without
            // this record SyncSelection would read that donor as "the game's row" and drop the body.
            // A guest send has no such evidence, so it records the same intent as PENDING.
            if (applied && wantSlug != null)
            {
                if (guestSend) NotePendingWanted(log, wantSlug, guestWireRow, what);
                else NoteExplicitWanted(log, wantSlug, what);
            }
        }
    }

    // Every object this mod has in the scene, by name: the leak metric for the whole roster. One
    // body rig and one preview rig is the invariant; anything more is a leftover from a hot
    // generation (SahurModel/SahurRig sweep those on their next build) or a bug.
    private static void DumpOurs(IHostContext log)
    {
        try
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int total = 0;
            var all = SceneCache.Scan(Il2CppType.Of<Transform>());
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var t = all[i].TryCast<Transform>();
                if (t == null || t.gameObject == null) continue;
                var sc = t.gameObject.scene;
                if (string.IsNullOrEmpty(sc.name)) continue;                       // prefab asset
                string n = t.name ?? "";
                if (!ModelRoster.IsOurObjectName(n)) continue;
                // only the root of a subtree; a rig's children are bone transforms we count separately
                if (t.parent != null && ModelRoster.IsOurObjectName(t.parent.name ?? "")) continue;
                counts[n] = counts.TryGetValue(n, out var c) ? c + 1 : 1;
                total++;
            }
            var sb = new StringBuilder();
            foreach (var kv in counts) sb.Append($"{kv.Key}={kv.Value} ");
            log.LogInfo($"[sahur:count] our body/skeleton objects in the scene: {total} [{sb}] " +
                        $"rigidBuilds={SahurModel.BuildCount} riggedBuilds={SahurRig.BuildCount} " +
                        $"previewBuilds={SahurPreview.Builds} previewDisposes={SahurPreview.Disposes} " +
                        $"cache rigid={SahurModel.CacheCount}/{SahurModel.CacheBytes / 1048576.0:F2}MB " +
                        $"rigged={SahurRig.CacheCount}/{SahurRig.CacheBytes / 1048576.0:F2}MB");
        }
        catch (Exception e) { log.LogWarning($"[sahur:count] failed: {e.Message}"); }
    }

    internal static void Dump(IHostContext log)
    {
        try
        {
            var models = PlayerModelDatabase.Models;
            int n = models == null ? -1 : models.Length;
            var names = new StringBuilder();
            for (int i = 0; i < n; i++)
                if (i < 15) names.Append(i).Append(':').Append(models[i] == null ? "null" : models[i].name).Append(' ');
            foreach (var e in Entries)
            {
                int at = -1;
                for (int i = 0; i < n; i++) if (Same(models[i], e.Data)) at = i;
                log.LogInfo($"[sahur:wardrobe] entry '{e.Label}' rigged={e.Rigged} data='{(e.Data == null ? "none" : e.Data.name)}' " +
                            $"index={e.Index} inDatabase={at} selected={e.Selected} tabs={e.Tabs.Count} " +
                            $"portrait='{(e.Bust == null ? "none" : e.Bust.name)}' full='{(e.Full == null ? "none" : e.Full.name)}' " +
                            $"body={(e.Rigged ? SahurRig.On : SahurModel.On)}");
            }
            log.LogInfo($"[sahur:wardrobe] databaseCount={n} registers={_registers} inserts={_tabs} " +
                        $"rigidBody={SahurModel.On} riggedBody={SahurRig.On} view={(_view == null ? "none" : PathOf(_view.transform))}");
            log.LogInfo($"[sahur:wardrobe] PlayerModelDatabase.Models = [{names}]");
            DumpOurs(log);
            var s = LocalSetuper(null);
            if (s != null)
            {
                PlayerModelData applied = null;
                try { applied = s._playerModelData?.Value; } catch { }
                log.LogInfo($"[sahur:wardrobe] local setuper '{PathOf(s.transform)}' isSetup={s.IsSetup} " +
                            $"applied='{(applied == null ? "null" : applied.name)}' index={(applied == null ? -1 : applied.Index)}");
            }
            if (_view != null)
            {
                var list = _view._instantiatedWardrobeModelButtons;
                var sb = new StringBuilder();
                for (int i = 0; i < Count(list); i++)
                {
                    var t = list[i];
                    if (t == null) continue;
                    var d = t.PlayerModelData;
                    sb.AppendLine($"    [{i}] '{PathOf(t.transform)}' data='{(d == null ? "null" : d.name)}' " +
                                  $"text='{(t._modelNameTMP == null ? "null" : t._modelNameTMP.text)}'");
                    DumpImages(sb, t.transform, "        ");
                }
                log.LogInfo($"[sahur:wardrobe] live tabs ({Count(list)}):\n{sb}");
            }
            else log.LogInfo("[sahur:wardrobe] no wardrobe view in this scene (it is a lobby-only controller)");
        }
        catch (Exception e) { log.LogError($"[sahur:wardrobe] dump failed: {e}"); }
    }

    // Which on-screen slot the user means by "the icon on the bottom right": the HUD portrait is
    // HUDLayerElements.PortraitHUDElement -> UI.HUD.PortraitDisplayController -> UI.Portrait.
    // PortraitCompositeView, and its Image is fed from PlayerModelData.BustPortraitSprite.
    internal static void DumpHud(IHostContext log)
    {
        try
        {
            // The live lobby HUD is the composite (Portrait(Clone) -> PortraitCompositeView), not the
            // legacy PortraitHUDElement type; log it first so an evidence pass names the object the
            // game actually draws, its CurrentModelData and the sprite in its portrait slot.
            try
            {
                var cvs = SceneCache.Scan(Il2CppType.Of<global::UI.Portrait.PortraitCompositeView>());
                log.LogInfo($"[sahur:wardrobe] HUD composites: {(cvs == null ? 0 : cvs.Length)}");
                for (int i = 0; cvs != null && i < cvs.Length; i++)
                {
                    var cv = cvs[i].TryCast<global::UI.Portrait.PortraitCompositeView>();
                    if (cv == null || cv.gameObject == null || string.IsNullOrEmpty(cv.gameObject.scene.name)) continue;
                    string row = "?";
                    try { row = cv.CurrentModelData == null ? "<none>" : (cv.CurrentModelData.name ?? "?"); } catch { }
                    string sprite = "?";
                    try { sprite = cv._portraitImage == null || cv._portraitImage.sprite == null ? "null" : (cv._portraitImage.sprite.name ?? "?"); } catch { }
                    log.LogInfo($"[sahur:wardrobe] HUD composite '{PathOf(cv.transform)}' currentModelData='{row}' " +
                                $"portraitSprite='{sprite}' active={cv.gameObject.activeInHierarchy}");
                }
            }
            catch (Exception ce) { log.LogWarning($"[sahur:wardrobe] composite dump failed: {ce.Message}"); }

            var arr = SceneCache.Scan(Il2CppType.Of<HUDLayerElements.PortraitHUDElement>());
            if (arr == null || arr.Length == 0)
            {
                log.LogWarning("[sahur:wardrobe] no PortraitHUDElement in the scene (see the composite lines above)");
                return;
            }
            for (int i = 0; i < arr.Length; i++)
            {
                var e = arr[i].TryCast<HUDLayerElements.PortraitHUDElement>();
                if (e == null || e.gameObject == null) continue;
                if (e.gameObject.scene.name == null || e.gameObject.scene.name.Length == 0) continue;
                var rt = e.transform.TryCast<RectTransform>();
                string rect = "n/a";
                if (rt != null)
                {
                    var c = new Vector3[4];
                    rt.GetWorldCorners(c);
                    var canvas = e.GetComponentInParent(Il2CppType.Of<Canvas>())?.TryCast<Canvas>();
                    var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                    var sb = new StringBuilder();
                    for (int k = 0; k < 4; k++)
                    {
                        var sp = RectTransformUtility.WorldToScreenPoint(cam, c[k]);
                        sb.Append(k == 0 ? "" : ",").Append($"({sp.x:F0},{sp.y:F0})");
                    }
                    rect = $"corners(px)={sb} anchorMin={rt.anchorMin.ToString("F2")} anchorMax={rt.anchorMax.ToString("F2")} " +
                           $"size={rt.sizeDelta.ToString("F0")}";
                }
                log.LogInfo($"[sahur:wardrobe] HUD portrait '{PathOf(e.transform)}' active={e.gameObject.activeInHierarchy} {rect}");
                // which sprite is in the slot: names the answered question "is our portrait there"
                var sb2 = new StringBuilder();
                DumpImages(sb2, e.transform, "    ");
                log.LogInfo($"[sahur:wardrobe] HUD portrait images:\n{sb2}");
                // the slot's own feed: the applied PlayerModelData's bust sprite (what the composite
                // view draws). One sprite per entry is the requirement.
                try
                {
                    var sm = LocalSetuper(null);
                    var applied = sm?._playerModelData?.Value;
                    var bust = applied?._bustPortraitSprite;
                    log.LogInfo($"[sahur:wardrobe] HUD slot feed: applied='{(applied == null ? "null" : applied.name)}' " +
                                $"index={(applied == null ? -1 : applied.Index)} " +
                                $"bustPortraitSprite='{(bust == null ? "null" : bust.name)}' " +
                                $"{(bust == null ? "" : ((int)bust.rect.width) + "x" + ((int)bust.rect.height))}");
                }
                catch (Exception e2) { log.LogWarning($"[sahur:wardrobe] HUD slot feed read failed: {e2.Message}"); }
            }
        }
        catch (Exception e) { log.LogError($"[sahur:wardrobe] hud dump failed: {e}"); }
    }

    private static string PathOf(Transform t)
    {
        try
        {
            if (t == null) return "null";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
        catch { return "?"; }
    }
}

// ============================================================================================
// Native-wire decisions (pure)
//
// Everything below is a DECISION, not a call: no Il2Cpp, Unity, Harmony or game type may appear
// here. SahurWardrobe translates live rows into pointers / flags and applies the result, which is
// what lets the safety rules be exercised behaviorally outside the game
// (tests/native-wire-guard/behavior compiles this block verbatim from this file).
// ============================================================================================

// >>> native-wire-pure: begin (the text between these markers is compiled verbatim by the behavioral harness)

// The proof state of the shipped prefix. The only value taken from the live database is the
// candidate rows [0, shipped.Length); the shipped set itself always comes from Resources.
internal enum PrefixProof
{
    // Nothing to decide yet: the resource set or the database is not loaded, or the database is
    // still shorter than the resource set. Retry; never capture, never append, never mark unknown.
    Wait = 0,

    // Rows [0, shipped.Length) are exactly the game's shipped set, so that many rows are resolvable
    // on a peer without the mod. This is true even when the live array is longer (a previous
    // generation's appended rows follow the shipped ones), and the value is shipped.Length, never
    // the live length.
    Proven = 1,

    // A row inside the candidate prefix is not a shipped row (null, duplicate, foreign, or the
    // shipped set is incomplete). Nothing can be proven, so the prefix must become unknown and
    // every consumer must fail closed for the rest of the process.
    FailClosed = 2,
}

internal readonly struct PrefixVerdict
{
    internal readonly PrefixProof Proof;
    internal readonly int Count;
    internal readonly string Detail;

    internal PrefixVerdict(PrefixProof proof, int count, string detail)
    {
        Proof = proof;
        Count = count;
        Detail = detail;
    }
}

// Why a shrink may or may not run. A shrink is the only operation that can invalidate an index the
// game already holds, so every reason to refuse is named rather than collapsed into false.
internal enum ShrinkVerdict
{
    // Safe: prefix known, session over, guard installed, every removed row at or above the prefix.
    Allow = 0,

    // No shipped prefix: "rows above the prefix" is undefined, so nothing may be removed.
    WaitForPrefix = 1,

    // A live session can still hold an index at or above the prefix in the game's dictionaries and
    // read it without a bounds check. Refuse even when the guard is installed.
    DeferSession = 2,

    // Without the guard a new custom index can still be written and replicated.
    DeferGuard = 3,

    // A row inside the shipped prefix is in the drop set: it is not ours to remove.
    RefuseShippedRow = 4,
}

// The result of comparing the live array's front with the shipped resource set. `shipped` and
// `candidate` are native pointers; `candidate` must already be the first shipped.Length rows of the
// live array (the caller slices; a shorter array is Wait). Order is not part of the proof: a peer
// resolves an index, so what matters is that every index below the count is a shipped row.
internal static class NativeWirePolicy
{
    internal static PrefixVerdict ProvePrefix(IntPtr[] shipped, IntPtr[] candidate)
    {
        if (shipped == null || shipped.Length == 0)
            return new PrefixVerdict(PrefixProof.Wait, 0, "no shipped PlayerModelData rows are loaded");
        if (candidate == null || candidate.Length == 0)
            return new PrefixVerdict(PrefixProof.Wait, 0, "PlayerModelDatabase.Models has no rows yet");
        if (candidate.Length < shipped.Length)
            return new PrefixVerdict(PrefixProof.Wait, 0,
                $"only {candidate.Length} of {shipped.Length} shipped row(s) are loaded");

        var expected = new HashSet<IntPtr>();
        for (int i = 0; i < shipped.Length; i++) expected.Add(shipped[i]);

        var seen = new HashSet<IntPtr>();
        for (int i = 0; i < shipped.Length; i++)
        {
            IntPtr pointer = candidate[i];
            if (pointer == IntPtr.Zero)
                return new PrefixVerdict(PrefixProof.FailClosed, 0,
                    $"model row {i} is null; the shipped prefix cannot be proven");
            if (!expected.Contains(pointer))
                return new PrefixVerdict(PrefixProof.FailClosed, 0,
                    $"model row {i} is not one of the {shipped.Length} shipped PlayerModelData row(s)");
            if (!seen.Add(pointer))
                return new PrefixVerdict(PrefixProof.FailClosed, 0,
                    $"model row {i} repeats a shipped row; a shipped row is missing from the prefix");
        }

        return new PrefixVerdict(PrefixProof.Proven, shipped.Length,
            $"models[0..{shipped.Length}) is exactly the {shipped.Length} shipped row(s)");
    }

    internal static ShrinkVerdict CanPrune(bool prefixKnown, bool guardInstalled, bool sessionActive,
                                           int nativeCount, IReadOnlyList<int> drop)
    {
        if (!prefixKnown || nativeCount <= 0) return ShrinkVerdict.WaitForPrefix;
        if (sessionActive) return ShrinkVerdict.DeferSession;
        if (!guardInstalled) return ShrinkVerdict.DeferGuard;
        for (int i = 0; drop != null && i < drop.Count; i++)
            if (drop[i] < nativeCount) return ShrinkVerdict.RefuseShippedRow;
        return ShrinkVerdict.Allow;
    }

    // The contiguous tail that may be cut, the rows of ours that stay, and the non-shipped rows that
    // stay though they are not ours. `isOurs[i]` is the caller's ownership proof for row i;
    // `nativeCount` is the proven shipped prefix. A cut requires that proof: with no captured prefix
    // no row can be shown to be above every shipped row, so NOTHING is cut (a shrink on an unknown
    // boundary could delete a shipped row) and every ours row is reported. Ownership at or above the
    // cut is not assumed: the cut stops at the first row that is not ours, and every ours row BELOW
    // it is residual, even when the cut is empty. That is the difference between "nothing to do" and
    // "a row stayed on the wire".
    internal static WithdrawalPlan PlanWithdrawal(bool prefixKnown, bool[] isOurs, int nativeCount)
    {
        int count = isOurs == null ? 0 : isOurs.Length;
        bool bounded = prefixKnown && nativeCount > 0;
        int floor = 0;
        if (bounded)
        {
            floor = nativeCount;
            if (floor > count) floor = count;
        }

        int cut = count;
        if (bounded)
            while (cut > floor && isOurs[cut - 1]) cut--;

        var residual = new List<int>();
        for (int i = floor; i < cut; i++) if (isOurs[i]) residual.Add(i);

        // Rows at or above the proven prefix that are NOT ours: non-shipped by construction, and not
        // ours to delete. They stay, so no stock peer can resolve their indexes. Without a proven
        // prefix this set is unknown (a shipped row could be in it), so it stays empty rather than
        // claiming shipped rows are unprotected.
        var unprotected = new List<int>();
        if (bounded)
            for (int i = floor; i < count; i++) if (!isOurs[i]) unprotected.Add(i);

        return new WithdrawalPlan(cut, residual.ToArray(), unprotected.ToArray(), floor, bounded);
    }

    // A model index that may be written to the wire. With no captured prefix nothing is provably
    // shipped, so the only safe value is row 0 (fail closed). A shipped index passes through. Any
    // other index is replaced by a donor the caller can PROVE is a shipped row when it has one (the
    // row the body already carries, or the row it was cloned from), and only then by the
    // deterministic per-client row, which is the same value the server-side remap used to choose.
    internal static ushort ShippedWireIndex(bool prefixKnown, int nativeCount, ushort requested, int donor,
                                            ulong clientId)
    {
        if (!prefixKnown || nativeCount <= 0) return 0;
        if (requested < nativeCount) return requested;
        if (donor >= 0 && donor < nativeCount) return (ushort)donor;
        return NativeModelPolicy.Fallback(requested, nativeCount, clientId);
    }

    // What the local wardrobe confirm must do with the index that is about to travel to the host.
    // The index is ALWAYS returned; `Record` only says whether the wanted intent should also be
    // recorded. Nothing about the record can suppress or weaken the clamp.
    internal static ConfirmPlan PlanConfirm(bool prefixKnown, int nativeCount, ushort chosen, bool rowExists,
                                            string slug, int donor, ulong clientId)
    {
        if (!prefixKnown || nativeCount <= 0)
            return new ConfirmPlan(ConfirmBranch.NoPrefix, 0, false, null);
        // A donor outside the proven shipped range is not a shipped row; ignore it rather than let
        // a caller bug put it on the wire. The caller only scans [0, nativeCount), but the plan must
        // stay safe on its own inputs.
        if (donor < 0 || donor >= nativeCount) donor = -1;
        if (!rowExists)
            // The index names no row, so the requested value is not sent at all: keep the donor the
            // body already carries when there is one, and only then fall back deterministically.
            return new ConfirmPlan(ConfirmBranch.StaleRow,
                donor >= 0 ? (ushort)donor : ShippedWireIndex(prefixKnown, nativeCount, chosen, -1, clientId),
                false, null);
        if (string.IsNullOrEmpty(slug))
        {
            if (chosen < nativeCount)
                return new ConfirmPlan(ConfirmBranch.NativeRow, chosen, true, "");
            return new ConfirmPlan(ConfirmBranch.ForeignRow,
                ShippedWireIndex(prefixKnown, nativeCount, chosen, donor, clientId), true, "");
        }
        return new ConfirmPlan(ConfirmBranch.OurRow,
            ShippedWireIndex(prefixKnown, nativeCount, chosen, donor, clientId), true, slug);
    }

    // What an observed applied row says about a PENDING wanted record - the one confirmation a guest
    // has, because its selection RPC has no acknowledgement. `wantedNative` is the record's intent
    // (empty slug); `ourRow` is the applied row being the wanted entry's OWN row; `sentRow` is the
    // applied row being the row the send carried. A native intent is never confirmed by our row: the
    // player asked for the game's own model, so our body under it is not convergence.
    internal static PendingVerdict PendingConfirm(bool wantedNative, bool ourRow, bool sentRow)
    {
        if (!wantedNative && ourRow) return PendingVerdict.OurRow;
        return sentRow ? PendingVerdict.SentRow : PendingVerdict.Pending;
    }
}

// The three outcomes of observing an applied row against a pending guest selection.
internal enum PendingVerdict
{
    // Neither the wanted entry's row nor the row the send carried: nothing has confirmed the pick.
    Pending = 0,

    // The wanted entry's own row is applied (a host with the mod applied it directly).
    OurRow = 1,

    // The applied row is the shipped row the send carried (the host resolved the clamped value).
    SentRow = 2,
}

internal readonly struct WithdrawalPlan
{
    // Rows [Cut, Count) may be removed; Cut == Count means nothing may be cut.
    internal readonly int Cut;

    // Indices of rows this module owns that remain in [Floor, Cut). Non-empty means the withdrawal
    // could not protect them, even when Cut == Count.
    internal readonly int[] Residual;

    // Indices at or above Floor that are NOT this module's. They are non-shipped by construction
    // (Floor is the proven shipped prefix), they cannot be removed, and no stock peer can resolve
    // them. Empty when the prefix is unknown: a shipped row could be in the set.
    internal readonly int[] Unprotected;

    // The lowest index the plan was allowed to consider (the shipped prefix, or 0 when unknown).
    internal readonly int Floor;

    // True when the plan had a PROVEN, positive prefix, i.e. a cut was even considered.
    internal readonly bool Bounded;

    internal WithdrawalPlan(int cut, int[] residual, int[] unprotected, int floor, bool bounded)
    {
        Cut = cut;
        Residual = residual ?? Array.Empty<int>();
        Unprotected = unprotected ?? Array.Empty<int>();
        Floor = floor;
        Bounded = bounded;
    }
}

internal enum ConfirmBranch
{
    NoPrefix = 0,   // no shipped prefix: index 0, nothing recorded
    StaleRow = 1,   // the confirmed index names no current row: donor/fallback, nothing recorded
    NativeRow = 2,  // a shipped row inside the prefix: unchanged, record the deliberate-native intent
    ForeignRow = 3, // past the prefix and not ours: donor/fallback, record deliberate-native intent
    OurRow = 4,     // one of our rows: donor/fallback, record the entry's slug
}

internal readonly struct ConfirmPlan
{
    internal readonly ConfirmBranch Branch;
    internal readonly ushort Index;   // the value that may travel to the host
    internal readonly bool Record;    // whether to record the wanted intent (never gates Index)
    internal readonly string Slug;    // "" = deliberate native, otherwise the entry slug

    internal ConfirmPlan(ConfirmBranch branch, ushort index, bool record, string slug)
    {
        Branch = branch;
        Index = index;
        Record = record;
        Slug = slug;
    }
}

// <<< native-wire-pure: end
