using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Gameplay.Wardrobe;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AlterumModelSwap.Hot;

// One canonical resolver for "which model is this actor wearing", shared by every DISPLAY surface.
//
// The bug this exists to prevent (2026-11-17, actor B with Golubiro selected): the local body came
// from the wanted entry, the bottom-right HUD portrait came from `PlayerModelData.BustPortraitSprite`
// of the game's applied row (Ryan), and player A's wardrobe marked B on the Ryan row because that
// row is what the native `PlayersModelSetuper` index says. Four consumers, four sources, one actor.
//
// The resolver answers with a `ModelIdentity`: the canonical slug, the LOCALLY resolved
// `PlayerModelData`, its database index, provenance and readiness. What it may do with that:
//
//   * LOCAL BODY  - read the canonical row and let SahurWardrobe keep driving the body module. The
//                   explicit-wanted guard may legitimately leave the body and the game's own row
//                   different; the resolver does not force them together.
//   * HUD         - DISPLAY ONLY: the portrait Image is given the canonical row's bust sprite. The
//                   shared `PlayerModelData` is never mutated, `SetupModel` is never called and no
//                   selection RPC is ever sent just to refresh an icon.
//   * WARDROBE    - DISPLAY ONLY: a remote actor's occupancy/nickname is moved to the canonical
//                   row's tab and the stale row's entry for that actor is cleared.
//
// Indices here are LOCAL lookups into `PlayerModelDatabase.Models`. They are never persisted and
// never cross the wire - the wire carries slugs only (RemoteModels), and every peer resolves them
// against its own database.
//
// Refreshes are EDGES, never per-frame scene scans: canonical-selection change (SahurWardrobe's
// TouchDatabase/Invalidate), accepted remote state (RemoteModels.TouchPresentation), database
// generation change, and a UI reattach (SahurWardrobe.NoteViewAttached). A slow reconcile timer
// runs only while a correction is known to DRIFT (the game re-writing the HUD sprite or the tab
// nickname), so a steady state costs nothing.
internal static class ModelPresentation
{
    private const string IdentityTag = "[model:identity]";
    private const string HudTag = "[model:hud]";
    private const string WardrobeTag = "[model:wardrobe]";
    private const float ReconcileSeconds = 2f;
    // A correction only proves the surface was wrong ONCE. The game rewrites the HUD sprite and the
    // tab nickname from its own state, so a correction arms a BOUNDED watch: it survives
    // `WatchChecks` quiet passes (2 s apart) or ends `WatchSeconds` after the correction, whichever
    // comes first, and is never extended past that deadline. That is what catches a rewrite that
    // arrives with no new identity edge - without turning the resolver into a poll.
    private const int WatchChecks = 4;
    private const float WatchSeconds = 20f;

    internal sealed class ModelIdentity
    {
        internal string Actor = "local";
        internal ulong ClientId;
        internal bool Remote;

        internal string WantedSlug = "-";       // local: the wanted RECORD's slug ("" native, "-" none)
        internal string AdvertisedSlug = "-";   // the slug this actor publishes (peer state / ours)
        internal string CanonicalSlug = "";     // the canonical row: "" = a native row
        internal string CanonicalLabel = "?";
        internal PlayerModelData CanonicalData; // local lookup; null when native/unresolved
        internal int CanonicalDb = -1;          // local lookup, never persisted/sent

        internal string AppliedRow = "?";       // the game's applied PlayerModelData name
        internal string AppliedSlug = "";       // its slug when it is one of ours
        internal int AppliedDb = -1;
        internal string BodySlug = "";          // what the body module / remote build actually shows
        internal string BodyState = "";         // native | live | pending | failed (the EFFECTIVE body)

        internal string Provenance = "unknown";
        internal string Readiness = "unknown";
        internal bool Explicit;

        internal string Signature => string.Join("|", new[]
        {
            Actor, WantedSlug, AdvertisedSlug, CanonicalSlug, CanonicalLabel,
            CanonicalDb.ToString(), AppliedRow, AppliedSlug, BodySlug, BodyState, Provenance, Readiness,
        });
    }

    private static IHostContext _log;
    private static bool _dirty = true;
    private static string _dirtyWhy = "start";
    private static int _dbGen = int.MinValue;
    private static int _remoteRev = int.MinValue;
    private static int _viewTabCount = -1;
    private static float _nextReconcile;
    private static bool _hudDrift, _wardrobeDrift;   // "a correction was written in this pass"
    private static int _watchChecks;                 // quiet reconcile passes left before the watch ends
    private static float _watchUntil;                // hard end of the current watch
    private static string _localSig = "";
    private static readonly Dictionary<string, string> _remoteSigs = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _once = new(StringComparer.Ordinal);
    private static int _hudWrites, _wardrobeMoves;

    // ---- remote occupancy ledger (display only)
    // Per actor: the row whose tab currently carries this actor's nickname entry, and the EXACT text
    // of that entry. Entries - never whole fields - are what moves, so a tab that carries two actors'
    // names is never cleared as a whole, and the move is reversible in both directions (X -> Y -> the
    // native row, which is `applied`).
    private sealed class Projection
    {
        internal PlayerModelData Row;     // the row we last put this actor's entry on
        internal string Entry = "";        // the entry text that belongs to this actor
    }
    private static readonly Dictionary<string, Projection> _projected = new(StringComparer.Ordinal);
    private static string _entrySep = ", ";   // how the game joins two entries on one tab
    // The canonical roster entry from the last refresh. A cache read, never a re-resolve, so a
    // consumer inside the wardrobe's own pass cannot recurse into the resolver.
    private static int _cachedLocalEntry = -1;
    internal static int CachedLocalEntry => _cachedLocalEntry;
    internal static string CachedLocalSlug { get; private set; } = "";

    // ---- read-only resolve, for the qc.request `test.*` diagnostics -------------------------
    //
    // The console's test directives need the SAME answer every display surface uses, not a second
    // resolver: these two wrappers expose the canonical resolve above and nothing else. They run no
    // HUD/wardrobe projection and change nothing the game shows; the only write is the local-entry
    // cache `ResolveLocal` already refreshes on every resolve, with the value the next refresh would
    // compute anyway. `ResolveRemote` is also the only place that knows a remote's advertised-vs-built
    // slug distinction, which is why the accessor exists instead of the console re-deriving it from
    // RemoteModels.
    internal static ModelIdentity ResolveLocalReadOnly(IHostContext log)
    {
        try { return ResolveLocal(log); }
        catch (Exception e)
        {
            log?.LogWarning($"{IdentityTag} read-only local resolve failed: {e.Message}");
            return null;
        }
    }

    internal static ModelIdentity ResolveRemoteReadOnly(IHostContext log, ulong clientId)
    {
        try { return ResolveRemote(clientId); }
        catch (Exception e)
        {
            log?.LogWarning($"{IdentityTag} read-only remote resolve failed for client={clientId}: {e.Message}");
            return null;
        }
    }

    // Observe the sprite actually drawn in the local HUD, never the intended canonical row.
    // Missing/unknown/conflicting surfaces must not produce a successful assert-model verdict.
    // Remote actors have no local HUD slot; their canonical identity is separate evidence only.
    internal static string HudRow(ModelIdentity id)
    {
        if (id == null) return "<unobserved>";
        if (id.Remote) return "<remote-no-hud>";
        string observed = null;
        try
        {
            // Explicit observation must not reuse a scene snapshot taken before the HUD spawned.
            // This runs on diagnostic/identity edges, never as a per-frame scanner.
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<global::UI.Portrait.PortraitCompositeView>());
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var cv = all[i].TryCast<global::UI.Portrait.PortraitCompositeView>();
                if (cv == null || !cv.gameObject.activeInHierarchy ||
                    !SafePath(cv.transform).Contains("/HUD/")) continue;
                var image = cv._portraitImage;
                if (image == null || !image.isActiveAndEnabled || !image.gameObject.activeInHierarchy) continue;
                string row = ObservedBustRow(image.sprite);
                if (observed != null && observed != row) return "<conflict>";
                observed = row;
            }
            if (observed == null)
            {
                var legacy = Resources.FindObjectsOfTypeAll(Il2CppType.Of<HUDLayerElements.PortraitHUDElement>());
                for (int i = 0; legacy != null && i < legacy.Length; i++)
                {
                    var element = legacy[i].TryCast<HUDLayerElements.PortraitHUDElement>();
                    if (element == null || !element.gameObject.activeInHierarchy) continue;
                    var images = element.GetComponentsInChildren(Il2CppType.Of<Image>(), false);
                    for (int k = 0; images != null && k < images.Length; k++)
                    {
                        var image = images[k].TryCast<Image>();
                        if (image == null || !image.isActiveAndEnabled) continue;
                        string row = ObservedBustRow(image.sprite);
                        if (row == "<unknown-sprite>") continue;
                        if (observed != null && observed != row) return "<conflict>";
                        observed = row;
                    }
                }
            }
        }
        catch { return "<unobserved>"; }
        return observed ?? "<unobserved>";
    }

    private static string ObservedBustRow(Sprite sprite)
    {
        if (sprite == null) return "<unknown-sprite>";
        string observed = null;
        var models = PlayerModelDatabase.Models;
        for (int i = 0; models != null && i < models.Length; i++)
        {
            var row = models[i];
            Sprite bust = row?._bustPortraitSprite;
            // Hot reload may replace the row's Sprite while the live HUD holds its previous copy.
            // Match an unambiguous bust asset name as well as Unity identity, not the intended row.
            if (bust == null || (!Same(sprite, bust) &&
                (string.IsNullOrEmpty(sprite.name) || sprite.name != bust.name))) continue;
            string slug = SahurWardrobe.SlugOfData(row);
            string value = string.IsNullOrEmpty(slug) ? row.name : slug;
            if (observed != null && observed != value) return "<ambiguous-sprite>";
            observed = value;
        }
        return observed ?? "<unknown-sprite>";
    }

    internal static string WardrobeRow(ModelIdentity id)
        => id == null ? "-"
         : id.Remote ? (id.CanonicalSlug.Length > 0 ? id.CanonicalSlug : "-")
         : (id.AppliedRow == "-" ? "-" : id.AppliedRow);

    // One identity line for the console, in the resolver's own field vocabulary. Same format as
    // LogIdentity so a diagnostic line and a resolver line can be diffed directly.
    internal static string DescribeIdentity(ModelIdentity id)
    {
        if (id == null) return "unresolved";
        string hudRow = HudRow(id);
        return $"actor={id.Actor} wantedSlug={Disp(id.WantedSlug)} advertisedSlug={Disp(id.AdvertisedSlug)} " +
               $"canonicalSlug={Disp(id.CanonicalSlug)} canonicalLabel={id.CanonicalLabel} " +
               $"resolvedDb={(id.CanonicalDb >= 0 ? id.CanonicalDb.ToString() : "-")} " +
               $"appliedRow={id.AppliedRow} appliedDb={(id.AppliedDb >= 0 ? id.AppliedDb.ToString() : "-")} " +
               $"appliedSlug={Disp(id.AppliedSlug)} bodySlug={Disp(id.BodySlug)} hudRow={hudRow} " +
               $"provenance={id.Provenance} readiness={id.Readiness} " +
               $"bodyState={(string.IsNullOrEmpty(id.BodyState) ? "native" : id.BodyState)} explicit={id.Explicit}";
    }

    internal static void Reset()
    {
        _dirty = true;
        _cachedLocalEntry = -1;
        CachedLocalSlug = "";
        _hudDrift = _wardrobeDrift = false;
        _watchChecks = 0;
        _watchUntil = 0f;
        _projected.Clear();
        _localSig = "";
        _remoteSigs.Clear();
        _once.Clear();
        _viewTabCount = -1;
        _nextReconcile = 0f;
    }

    // A display-relevant edge. Called from SahurWardrobe (selection/database/view) and never from a
    // per-frame path.
    internal static void Invalidate(IHostContext log, string reason)
    {
        _log = log;
        _dirty = true;
        _dirtyWhy = string.IsNullOrEmpty(reason) ? "edge" : reason;
    }

    internal static void Tick(IHostContext log, float now)
    {
        if (log == null) return;
        _log = log;

        // Cheap edge checks only (two ints and a tab count): no scene access on the common frame.
        int gen;
        try { gen = SahurWardrobe.DatabaseGeneration; } catch { gen = _dbGen; }
        if (gen != _dbGen) { _dbGen = gen; _dirty = true; _dirtyWhy = "database generation"; }

        int rev;
        try { rev = RemoteModels.PresentationRevision; } catch { rev = _remoteRev; }
        if (rev != _remoteRev) { _remoteRev = rev; _dirty = true; _dirtyWhy = "accepted remote state"; }

        int tabs = 0;
        try { tabs = SahurWardrobe.ViewTabCount; } catch { }
        if (tabs > 0 && tabs != _viewTabCount) { _viewTabCount = tabs; _dirty = true; _dirtyWhy = "wardrobe UI attached"; }

        if (_dirty)
        {
            _dirty = false;
            string why = _dirtyWhy;
            _dirtyWhy = "edge";
            try { Refresh(log, why, now); }
            catch (Exception e) { log.LogWarning($"[model:identity] refresh failed: {e.Message}"); }
            return;
        }

        // Slow reconcile: a BOUNDED post-correction watch, and nothing else. It exists only because the
        // game was observed rewriting a surface this resolver corrected, and it always stops:
        // `WatchChecks` quiet passes (2 s apart) or `WatchSeconds` after the correction, whichever
        // comes first. No correction, no watch - the steady state still costs two integer reads.
        if (now < _nextReconcile || _watchChecks <= 0 || now > _watchUntil) return;
        _nextReconcile = now + ReconcileSeconds;
        try { Refresh(log, "reconcile-watch", now); }
        catch (Exception e) { log.LogWarning($"[model:identity] reconcile failed: {e.Message}"); }
    }

    private static void Refresh(IHostContext log, string why, float now)
    {
        var local = ResolveLocal(log);
        LogIdentity(log, local);

        // 1. HUD: display-only projection of the canonical row's bust sprite.
        _hudDrift = ProjectHud(log, local);

        // 2. Remote actors: body identity line + display-only wardrobe occupancy.
        var ids = new List<ulong>(4);
        try { RemoteModels.CollectRemoteIds(ids); } catch { }
        _wardrobeDrift = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Which row each actor's own game index names, and how many actors share it: an occupancy
        // field that carries two actors cannot be attributed to either of them (TryAdoptEntry).
        var applied = new Dictionary<ulong, PlayerModelData>();
        var occupants = new Dictionary<IntPtr, int>();
        for (int i = 0; i < ids.Count; i++)
        {
            PlayerModelData row = null;
            try { row = AppliedRowOfRemote(ids[i]); } catch { }
            if (row == null) continue;
            applied[ids[i]] = row;
            IntPtr key = IntPtr.Zero;
            try { key = row.Pointer; } catch { }
            occupants[key] = occupants.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        // The LOCAL player's row shares a tab with a remote actor's whenever both sit on the same
        // model: the field then carries both names and cannot be attributed to the remote alone.
        PlayerModelData localRow = null;
        try { localRow = SahurWardrobe.AppliedData(log); } catch { }
        if (localRow != null)
        {
            IntPtr key = IntPtr.Zero;
            try { key = localRow.Pointer; } catch { }
            occupants[key] = occupants.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        for (int i = 0; i < ids.Count; i++)
        {
            ModelIdentity id;
            try { id = ResolveRemote(ids[i]); } catch { continue; }
            seen.Add(id.Actor);
            LogIdentity(log, id);
            PlayerModelData row = applied.TryGetValue(ids[i], out var r) ? r : null;
            int shared = 1;
            if (row != null)
            {
                IntPtr key = IntPtr.Zero;
                try { key = row.Pointer; } catch { }
                occupants.TryGetValue(key, out shared);
            }
            try { if (ReconcileWardrobe(log, id, row, shared)) _wardrobeDrift = true; }
            catch (Exception e) { log.LogWarning($"{WardrobeTag} occupancy pass failed: {e.Message}"); }
        }
        var stale = new List<string>();
        foreach (var k in _remoteSigs.Keys) if (!seen.Contains(k)) stale.Add(k);
        for (int i = 0; i < stale.Count; i++) _remoteSigs.Remove(stale[i]);
        // Actors that left: their entry comes off the field and the ledger entry goes with them.
        var gone = new List<string>();
        foreach (var k in _projected.Keys) if (!seen.Contains(k)) gone.Add(k);
        for (int i = 0; i < gone.Count; i++)
        {
            try { ReleaseProjection(log, gone[i]); } catch { }
            _projected.Remove(gone[i]);
        }

        ArmWatch(now, _hudDrift || _wardrobeDrift);
        log.LogInfo($"[model:identity] refresh why='{why}' hudDrift={(_hudDrift ? "yes" : "no")} " +
                    $"wardrobeDrift={(_wardrobeDrift ? "yes" : "no")} watch={_watchChecks}/{WatchChecks} " +
                    $"hudWrites={_hudWrites} wardrobeMoves={_wardrobeMoves} " +
                    $"dbGen={_dbGen} remoteRev={_remoteRev}");
    }

    // Arm (or age) the bounded watch. A correction re-arms the CHECK COUNTER but never extends the
    // deadline, so a surface the game keeps rewriting is still given up on after `WatchSeconds`; a
    // correction after the watch ended opens one new window.
    private static void ArmWatch(float now, bool corrected)
    {
        if (corrected)
        {
            if (_watchChecks <= 0 || now > _watchUntil) _watchUntil = now + WatchSeconds;
            _watchChecks = WatchChecks;
            return;
        }
        if (_watchChecks > 0) _watchChecks--;
    }

    // ------------------------------------------------------------------ resolve

    private static ModelIdentity ResolveLocal(IHostContext log)
    {
        var id = new ModelIdentity { Actor = "local", Remote = false };
        SahurWardrobe.WantSnapshot want;
        try { want = SahurWardrobe.Snapshot(); }
        catch { want = default; }

        id.WantedSlug = want.Kind == 0 || want.Slug == "-" ? "-" : (want.Slug ?? "");
        id.Explicit = want.Explicit;
        id.Provenance = want.Provenance;

        PlayerModelData applied = null;
        try { applied = SahurWardrobe.AppliedData(log); } catch { }
        id.AppliedRow = applied != null && !string.IsNullOrEmpty(SafeName(applied)) ? SafeName(applied) : "-";
        id.AppliedDb = SafeIndex(applied);
        try { id.AppliedSlug = SahurWardrobe.SlugOfData(applied); } catch { }

        if (want.Kind == 1)
        {
            var data = SahurWardrobe.DataAtEntry(want.Entry);
            id.CanonicalSlug = want.Slug ?? "";
            id.CanonicalLabel = want.Label ?? "?";
            id.CanonicalData = data;
            id.CanonicalDb = SafeIndex(data);
            id.Readiness = want.Pending || data == null || id.CanonicalDb < 0 ? "pending" : "ready";
            if (id.Readiness == "pending" && string.IsNullOrEmpty(id.Provenance)) id.Provenance = "wanted:pending";
        }
        else if (want.Kind == 2)
        {
            // A deliberate native choice is canonical ON PURPOSE: the body follows the game's row.
            id.CanonicalSlug = "";
            id.CanonicalLabel = "<native>";
            id.CanonicalData = null;
            id.CanonicalDb = id.AppliedDb;
            id.Readiness = "native";
            if (string.IsNullOrEmpty(id.Provenance) || id.Provenance == "unknown") id.Provenance = "native";
        }
        else
        {
            // Not decided yet: the game's applied row is the best available answer, marked as such.
            // This is NOT written back to the wanted record (no display read persists anything).
            id.CanonicalSlug = id.AppliedSlug;
            id.CanonicalLabel = id.AppliedSlug.Length == 0 ? "<native>" : (id.AppliedRow ?? "?");
            id.CanonicalData = applied;
            id.CanonicalDb = id.AppliedDb;
            id.Readiness = id.AppliedSlug.Length == 0 ? "native" : "ready";
            if (string.IsNullOrEmpty(id.Provenance) || id.Provenance == "unknown")
                id.Provenance = want.From == "invalidated" ? "withdrawn" : "undecided";
        }

        id.BodySlug = LiveBodySlug(out string bodyState);
        // A wanted CUSTOM row that is not on screen is `pending`, never `native`: `native` would
        // claim the game's own body is the intended answer, which is the opposite of what the
        // record says and the exact confusion the incident produced (Demoman row, James body).
        // Module off + no custom record is a genuine native state.
        if (bodyState == "native" && want.Kind == 1) bodyState = "pending";
        id.BodyState = bodyState;
        _cachedLocalEntry = want.Kind == 1 ? want.Entry : -1;
        CachedLocalSlug = id.CanonicalSlug ?? "";
        return id;
    }

    private static ModelIdentity ResolveRemote(ulong clientId)
    {
        var id = new ModelIdentity { Actor = "remote=" + clientId.ToString(), Remote = true, ClientId = clientId };
        id.WantedSlug = "-";
        // The advertised slug is VOUCHED by RemoteModels (handshake + server address); it is the
        // only thing a peer's model identity travels as. No index crosses the wire.
        string slug = "";
        try { slug = RemoteModels.RemoteSlug(clientId); } catch { }
        id.AdvertisedSlug = slug ?? "";
        id.BodySlug = SafeString(() => RemoteModels.RemoteBodySlug(clientId));
        // Truthful remote state: the advertised slug is the peer's vouched WANT, the built slug is
        // what THIS instance actually put on screen for them. An advertised row with no build is
        // `pending`, never `live`.
        {
            var rep = ModelWantedPolicy.EffectiveRemoteBody(id.AdvertisedSlug, id.BodySlug, false);
            id.BodySlug = rep.Slug;
            id.BodyState = rep.State;
        }

        var spec = string.IsNullOrEmpty(id.AdvertisedSlug) ? null : ModelRoster.BySlug(id.AdvertisedSlug);
        int entry = spec == null ? -1 : SahurWardrobe.EntryIndexForSlug(spec.Slug);
        PlayerModelData data = entry >= 0 ? SahurWardrobe.DataAtEntry(entry) : null;

        id.CanonicalSlug = spec == null ? "" : spec.Slug;
        id.CanonicalLabel = spec == null ? "<native>" : spec.Label;
        id.CanonicalData = data;
        id.CanonicalDb = SafeIndex(data);
        id.Readiness = spec == null ? "native" : (data == null || id.CanonicalDb < 0 ? "pending" : "ready");
        id.Provenance = spec == null ? "remote:native" : "remote:vouched";

        // The game's own row for that actor (its native PlayersModelSetuper index). This is what the
        // wardrobe list and the native HUD slot use; the correction uses it only to find which row
        // must be CLEARED.
        var applied = AppliedRowOfRemote(clientId);
        id.AppliedRow = applied == null ? "-" : (SafeName(applied) ?? "-");
        id.AppliedDb = SafeIndex(applied);
        try { id.AppliedSlug = SahurWardrobe.SlugOfData(applied); } catch { }
        return id;
    }

    private static string LiveBodySlug(out string state)
    {
        // The EFFECTIVE body, decided by the shipping policy, from the module's own state: a module
        // that is ON but bound/retrying/failed is NOT a body, and naming its spec here is exactly
        // the "Demoman portrait over the native James body" false pass. One decision for both kinds.
        bool on = false, live = false, failed = false;
        string spec = "";
        try
        {
            if (SahurRig.Active)
            {
                on = true;
                live = SahurRig.BodyLive;
                failed = SahurRig.BodyStateName == "failed";
                spec = SahurRig.Spec != null ? SahurRig.Spec.Slug : "";
            }
            else if (SahurModel.Active)
            {
                on = true;
                live = SahurModel.BodyLive;
                failed = SahurModel.BodyStateName == "failed";
                spec = SahurModel.Spec != null ? SahurModel.Spec.Slug : "";
            }
        }
        catch { on = live = failed = false; spec = ""; }
        var rep = ModelWantedPolicy.EffectiveBody(on, live, failed, spec);
        state = rep.State;
        return rep.Slug;
    }

    // ------------------------------------------------------------------ diagnostic

    // One line per actor. `resolvedDb`/`hudRow`/`wardrobeRow` are LOCAL names/indices for the
    // evidence export; nothing in this line is ever transmitted.
    private static void LogIdentity(IHostContext log, ModelIdentity id)
    {
        string sig = id.Signature;
        if (id.Remote)
        {
            if (_remoteSigs.TryGetValue(id.Actor, out var prev) && prev == sig) return;
            _remoteSigs[id.Actor] = sig;
        }
        else
        {
            if (_localSig == sig) return;
            _localSig = sig;
        }

        // The rows the display surfaces SHOULD present, from the one derivation the console's
        // observation verbs also read (`HudRow`/`WardrobeRow`).
        string hudRow = HudRow(id);
        string wardrobeRow = WardrobeRow(id);
        log.LogInfo($"{IdentityTag} actor={id.Actor} wantedSlug={Disp(id.WantedSlug)} " +
                    $"advertisedSlug={Disp(id.AdvertisedSlug)} resolvedDb={(id.CanonicalDb >= 0 ? id.CanonicalDb.ToString() : "-")} " +
                    $"appliedRow={id.AppliedRow} bodySlug={Disp(id.BodySlug)} hudRow={hudRow} wardrobeRow={wardrobeRow} " +
                    $"provenance={id.Provenance} readiness={id.Readiness} bodyState={(string.IsNullOrEmpty(id.BodyState) ? "native" : id.BodyState)}");
    }

    private static string Disp(string s) => string.IsNullOrEmpty(s) ? "-" : s;

    // ------------------------------------------------------------------ HUD (display only)

    // Give the bottom-right portrait the CANONICAL row's bust sprite when the game's own slot is
    // showing a different row's. Display only: `PlayerModelData` is not written, no SetupModel and no
    // selection RPC. Returns true when a correction was needed in this pass, which arms the slow
    // reconcile watch (the game rewrites the slot from its own state).
    //
    // WHY THE TARGET CHANGED (2026-11-17 08:59Z real rejoin): the old scan looked for
    // `HUDLayerElements.PortraitHUDElement` and only rewrote an Image holding the APPLIED row's bust.
    // The live lobby HUD does not expose that element - it is
    // `Lobby Global/User Interface/Canvases/HUD/HUD Canvas/Portrait(Clone)` with a
    // `UI.Portrait.PortraitCompositeView` and its `_portraitImage` - and the sprite in the slot was
    // neither the applied row's (Sarah) nor the canonical row's (capuchino): it was a third row's
    // (the menu selection), so "no portrait Image carries the applied row's bust" was logged and the
    // custom body stood under a native portrait (reported screenshot). The composite's own portrait
    // image is the slot; any model-bust sprite in it is replaceable, and only that image is touched.
    private static bool ProjectHud(IHostContext log, ModelIdentity local)
    {
        if (local.CanonicalData == null) return false;

        Sprite want = null;
        try { want = local.CanonicalData._bustPortraitSprite; } catch { }
        if (want == null)
        {
            Once(log, "hud:nobust", $"{HudTag} canonical row '{local.CanonicalSlug}' has no bust sprite; " +
                                    "no HUD projection is possible (the slot keeps the game's own feed)");
            return false;
        }
        PlayerModelData appliedRow = null;
        try { appliedRow = local.AppliedRow == "-" ? null : SahurWardrobe.AppliedData(log); } catch { }
        Sprite appliedSprite = null;
        try { appliedSprite = appliedRow?._bustPortraitSprite; } catch { }

        // The live HUD composite first (the object the game actually draws), then the legacy element
        // scan as a fallback for older prefabs. Dedupe by instance id so one image is written once.
        var images = new List<Image>(8);
        var seen = new HashSet<int>();
        var composites = new List<string>(2);
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<global::UI.Portrait.PortraitCompositeView>());
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var cv = arr[i].TryCast<global::UI.Portrait.PortraitCompositeView>();
                if (cv == null || cv.gameObject == null || string.IsNullOrEmpty(cv.gameObject.scene.name)) continue;
                Image portrait = null;
                try { portrait = cv._portraitImage; } catch { }
                if (portrait == null) continue;
                string row = "?";
                try { row = cv.CurrentModelData != null ? (cv.CurrentModelData.name ?? "?") : "<none>"; } catch { }
                composites.Add($"'{SafePath(cv.transform)}' row='{row}' sprite='{SpriteName(portrait)}'");
                AddImage(images, seen, portrait);
            }
        }
        catch (Exception e) { log.LogWarning($"{HudTag} composite scan failed: {e.Message}"); }
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<HUDLayerElements.PortraitHUDElement>());
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var e = arr[i].TryCast<HUDLayerElements.PortraitHUDElement>();
                if (e == null || e.gameObject == null || string.IsNullOrEmpty(e.gameObject.scene.name)) continue;
                var imgs = e.GetComponentsInChildren(Il2CppType.Of<Image>(), true);
                for (int k = 0; imgs != null && k < imgs.Length; k++)
                    AddImage(images, seen, imgs[k].TryCast<Image>());
            }
        }
        catch { }

        var busts = BustSpriteIds();
        int wrote = 0, already = 0, foreign = 0;
        for (int i = 0; i < images.Count; i++)
        {
            var im = images[i];
            Sprite have;
            try { have = im.sprite; } catch { continue; }
            if (have == null) continue;
            if (Same(have, want)) { already++; continue; }
            // Is this the MODEL PORTRAIT? Either the applied row's bust, or any roster row's bust.
            // A frame, a cave backdrop or an icon that is not a bust is left alone; the third-row
            // case (menu row over a lobby body) is only reachable through the roster set.
            bool modelBust = (appliedSprite != null && Same(have, appliedSprite)) || busts.Contains(ImageKey(have));
            if (!modelBust) { foreign++; continue; }
            try { im.sprite = want; wrote++; } catch { }
        }

        if (wrote > 0) _hudWrites++;
        if (wrote > 0)
            log.LogInfo($"{HudTag} presented canonical row '{local.CanonicalSlug}' db={(local.CanonicalDb >= 0 ? local.CanonicalDb.ToString() : "-")} " +
                        $"in {wrote} portrait image(s) (game applied row='{local.AppliedRow}', " +
                        $"composites={composites.Count} [{string.Join("; ", composites)}]); " +
                        "display only - PlayerModelData untouched, no SetupModel, no selection RPC");
        else if (already == 0)
            Once(log, "hud:nomatch:" + local.CanonicalSlug,
                 $"{HudTag} no portrait Image could be identified (applied='{local.AppliedRow}' " +
                 $"canonical='{local.CanonicalSlug}' images={images.Count} foreign={foreign} " +
                 $"composites={composites.Count} [{string.Join("; ", composites)}]); nothing written");
        else
            Once(log, "hud:ok:" + local.CanonicalSlug + ":" + local.AppliedRow,
                 $"{HudTag} portrait already canonical ('{local.CanonicalSlug}') " +
                 $"(applied='{local.AppliedRow}', images={images.Count}, composites={composites.Count})");
        return wrote > 0;
    }

    private static void AddImage(List<Image> list, HashSet<int> seen, Image im)
    {
        if (im == null) return;
        int key;
        try { key = im.GetInstanceID(); } catch { return; }
        if (seen.Add(key)) list.Add(im);
    }

    private static int ImageKey(Sprite s)
    {
        try { return s.GetInstanceID(); } catch { return 0; }
    }

    // Every roster row's bust sprite, as instance ids. This is how a third row's sprite in the slot
    // (the game's stored menu row) is recognised as a model portrait rather than an ordinary Image.
    private static HashSet<int> BustSpriteIds()
    {
        var set = new HashSet<int>();
        try
        {
            var models = PlayerModelDatabase.Models;
            for (int i = 0; models != null && i < models.Length; i++)
            {
                var d = models[i];
                if (d == null) continue;
                Sprite b = null;
                try { b = d._bustPortraitSprite; } catch { }
                if (b == null) continue;
                int key = ImageKey(b);
                if (key != 0) set.Add(key);
            }
        }
        catch { }
        return set;
    }

    private static string SpriteName(Image im)
    {
        try { return im == null || im.sprite == null ? "null" : (im.sprite.name ?? "?"); } catch { return "?"; }
    }

    private static string SafePath(Transform t)
    {
        try
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
        catch { return "?"; }
    }

    // ------------------------------------------------------------------ wardrobe (display only)

    // A remote actor's occupancy is shown on the row the game's native `PlayersModelSetuper` index
    // names (B on 'Ryan') while the body is the canonical row ('Golubiro'). The correction moves THAT
    // ACTOR'S ENTRY - the exact string the ledger holds for it - from the tab it was last projected on
    // to the tab of the row it belongs on now, and never the field as a whole: a tab that carries two
    // actors' names loses one name and keeps the other. The nickname is copied, not re-derived: it is
    // the game's own display string, so no name source is invented here. No `PlayerModelData` is
    // mutated; nothing is selected.
    //
    // `applied` is the row the game's own index names for this actor (its native home) and `occupants`
    // is how many remote actors share that row. A canonical row of null means the actor is native
    // again, and the entry goes HOME to `applied` - that is the X -> Y -> native leg.
    private static bool ReconcileWardrobe(IHostContext log, ModelIdentity remote, PlayerModelData applied, int occupants)
    {
        _projected.TryGetValue(remote.Actor, out var proj);
        if (proj == null)
        {
            // Nothing was ever projected for this actor, so there is nothing to move or to send home.
            if (remote.CanonicalData == null) return false;
            proj = new Projection { Row = applied };
            _projected[remote.Actor] = proj;
        }

        if (!SafeViewAttached())
        {
            Once(log, "wardrobe:noview", $"{WardrobeTag} no wardrobe view in this scene (lobby-only) - " +
                                         "occupancy correction deferred to the next UI attach");
            return false;
        }

        // 1. Learn this actor's entry - ONCE. Everything after this moves the same string, so another
        //    actor's text cannot be picked up on the way (that is the whole fix: the field is never
        //    moved as a whole, and the ledger keeps the entry across X -> Y -> native).
        if (proj.Entry.Length == 0)
        {
            var srcTab = TabForRow(proj.Row);
            string src = Nickname(srcTab);
            if (src.Length == 0)
            {
                Once(log, "wardrobe:empty:" + remote.Actor + ":" + remote.CanonicalSlug,
                     $"{WardrobeTag} actor={remote.Actor} has no occupancy text to move to '{remote.CanonicalSlug}'");
                return false;
            }
            if (!TryAdoptEntry(remote.Actor, proj.Row, src, occupants, out string entry))
            {
                Once(log, "wardrobe:shared:" + remote.Actor + ":" + SafeName(proj.Row),
                     $"{WardrobeTag} actor={remote.Actor} occupancy field on '{SafeName(proj.Row)}' " +
                     $"carries '{Short(src)}' and {occupants} actor(s) share that row: it is not " +
                     "attributable to one actor, so nothing is moved (another actor's text is never " +
                     "moved or cleared)");
                return false;
            }
            proj.Entry = entry;
        }

        // 2. Where the entry belongs now: the canonical row, or - native again - the row the game's own
        //    index names.
        bool toNative = remote.CanonicalData == null;
        var target = toNative ? applied : remote.CanonicalData;
        if (target == null)
        {
            // Native and the game has not placed this actor on a row yet: keep the entry where it is
            // and retry on the next pass (the ledger makes that safe).
            return false;
        }
        var toTab = TabForRow(target);
        if (toTab == null)
        {
            if (!toNative)
                Once(log, "wardrobe:nocanon:" + remote.Actor + ":" + remote.CanonicalSlug,
                     $"{WardrobeTag} actor={remote.Actor} canonical row '{remote.CanonicalSlug}' has no tab yet - deferred");
            return false;
        }
        if (toTab._nicknameNameTMP == null)
        {
            // No field to write: nothing is removed from the old tab and the ledger is NOT advanced,
            // so the entry is still where the ledger says it is and the next pass retries.
            Once(log, "wardrobe:nofield:" + remote.Actor + ":" + SafeName(target),
                 $"{WardrobeTag} actor={remote.Actor} row '{SafeName(target)}' has no nickname field - deferred");
            return false;
        }

        bool wrote;
        try { wrote = MoveEntry(proj, TabForRow(proj.Row), toTab); }
        catch (Exception e) { log.LogWarning($"{WardrobeTag} occupancy move failed: {e.Message}"); return false; }
        proj.Row = target;

        if (wrote)
        {
            _wardrobeMoves++;
            string toLabel = toNative ? SafeName(target) : remote.CanonicalSlug;
            log.LogInfo($"{WardrobeTag} actor={remote.Actor} occupancy '{remote.AppliedRow}' -> " +
                        $"'{toLabel}' nickname='{proj.Entry}' (display only; the game's applied row is " +
                        "unchanged)");
        }
        // Written = a correction this pass, which arms the bounded watch (see ArmWatch). A pass that
        // found everything already correct is NOT proof that it stays correct, so it does not disarm
        // the watch either - the watch ends on its own budget.
        return wrote;
    }

    // Move ONE entry: take this actor's exact entry off `from` and put it on `to`, touching nothing
    // else in either field. Returns true when a field was written.
    private static bool MoveEntry(Projection proj, WardrobeModelButtonedTab from, WardrobeModelButtonedTab to)
    {
        string entry = proj.Entry;
        if (string.IsNullOrEmpty(entry) || to == null || to._nicknameNameTMP == null) return false;
        bool wrote = false;

        if (Same(from, to))
        {
            // Already where it belongs: only re-assert after the game rewrote the field.
            string here = Nickname(to);
            if (!ContainsEntry(here, entry))
            {
                to._nicknameNameTMP.text = AddEntry(here, entry);
                wrote = true;
            }
            return wrote;
        }

        if (from != null && from._nicknameNameTMP != null)
        {
            string field = Nickname(from);
            string rest = RemoveEntry(field, entry, out string sep);
            if (sep != null && sep.Length > 0) _entrySep = sep;
            if (!string.Equals(rest, field, StringComparison.Ordinal))
            {
                from._nicknameNameTMP.text = rest;
                wrote = true;
            }
        }
        string dest = Nickname(to);
        if (!ContainsEntry(dest, entry))
        {
            to._nicknameNameTMP.text = AddEntry(dest, entry);
            wrote = true;
        }
        return wrote;
    }

    // The text this actor contributes to the field it sits in. Only ever:
    //   * the whole field, when this actor is the only occupant of that row (`occupants == 1`), or
    //   * what remains after subtracting the entries of the OTHER occupants this ledger already holds
    //     on that same row - and only when that subtraction actually removed something and the
    //     remainder is a single token.
    // A field that still carries a name this ledger cannot account for is NOT attributable and is left
    // untouched rather than half-moved. That is exactly how two actors on one tab are told apart: by
    // the recorded entry of every other occupant, never by guessing the game's separator, and a field
    // we cannot attribute is never moved or cleared.
    private static bool TryAdoptEntry(string actor, PlayerModelData row, string field, int occupants, out string entry)
    {
        entry = "";
        string rest = field;
        bool subtracted = false;
        if (occupants > 1)
        {
            foreach (var kv in _projected)
            {
                if (string.Equals(kv.Key, actor, StringComparison.Ordinal)) continue;
                var p = kv.Value;
                if (p.Entry.Length == 0 || !Same(p.Row, row)) continue;
                string next = RemoveEntry(rest, p.Entry, out _);
                if (string.Equals(next, rest, StringComparison.Ordinal)) continue;
                rest = next;
                subtracted = true;
            }
            // More than one actor on the row and no other occupant identified: the field cannot be
            // attributed to this actor, so it is not touched at all.
            if (!subtracted) return false;
        }
        rest = rest.Trim();
        if (rest.Length == 0) return false;
        for (int i = 0; i < rest.Length; i++) if (IsFieldSep(rest[i])) return false;   // more than one name left
        entry = rest;
        return true;
    }

    // An actor left: its entry comes off the field it was projected on (nothing else in it is touched).
    private static void ReleaseProjection(IHostContext log, string actor)
    {
        if (!_projected.TryGetValue(actor, out var proj) || proj.Entry.Length == 0) return;
        var tab = TabForRow(proj.Row);
        if (tab == null || tab._nicknameNameTMP == null) return;
        string field = Nickname(tab);
        string rest = RemoveEntry(field, proj.Entry, out _);
        if (string.Equals(rest, field, StringComparison.Ordinal)) return;
        tab._nicknameNameTMP.text = rest;
        log?.LogInfo($"{WardrobeTag} actor={actor} left: occupancy entry '{Short(proj.Entry)}' removed from " +
                     $"'{SafeName(proj.Row)}' (display only)");
    }

    // Token-exact removal of ONE entry, taking the separator that joined it with it. A field that does
    // not contain the entry is returned unchanged.
    private static string RemoveEntry(string field, string entry, out string sep)
    {
        sep = null;
        if (string.IsNullOrEmpty(field) || string.IsNullOrEmpty(entry)) return field;
        int i = field.IndexOf(entry, StringComparison.Ordinal);
        if (i < 0) return field;
        int start = i, end = i + entry.Length;
        int gapEnd = end;
        while (gapEnd < field.Length && IsGap(field[gapEnd])) gapEnd++;
        if (gapEnd > end)
        {
            sep = field.Substring(end, gapEnd - end);
            return field.Substring(0, start) + field.Substring(gapEnd);
        }
        int gapStart = start;
        while (gapStart > 0 && IsGap(field[gapStart - 1])) gapStart--;
        if (gapStart < start)
        {
            sep = field.Substring(gapStart, start - gapStart);
            return field.Substring(0, gapStart) + field.Substring(end);
        }
        return field.Substring(0, start) + field.Substring(end);
    }

    // Token-exact addition: an entry that is already on the field is not added twice.
    private static string AddEntry(string field, string entry)
    {
        if (string.IsNullOrEmpty(entry)) return field;
        if (string.IsNullOrEmpty(field)) return entry;
        if (ContainsEntry(field, entry)) return field;
        string sep = string.IsNullOrEmpty(_entrySep) ? ", " : _entrySep;
        return field.TrimEnd() + sep + entry;
    }

    // Whole-token containment: the occurrence must be bounded by separators (or the field's ends), so
    // "Ry" is not found inside "Ryan" and one actor's name never counts as another's.
    private static bool ContainsEntry(string field, string entry)
    {
        if (string.IsNullOrEmpty(field) || string.IsNullOrEmpty(entry)) return false;
        int i = 0;
        while ((i = field.IndexOf(entry, i, StringComparison.Ordinal)) >= 0)
        {
            int end = i + entry.Length;
            bool left = i == 0 || IsGap(field[i - 1]);
            bool right = end >= field.Length || IsGap(field[end]);
            if (left && right) return true;
            i = end;
        }
        return false;
    }

    // What the game puts between two entries on one tab: a separator character and optional padding.
    private static bool IsFieldSep(char c) => c == ',' || c == '/' || c == ';' || c == '|' || c == '\n' || c == '\t';
    private static bool IsGap(char c) => IsFieldSep(c) || c == ' ' || c == '\u00a0';

    private static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
    }

    private static bool SafeViewAttached()
    {
        try { return SahurWardrobe.ViewAttached; } catch { return false; }
    }

    private static WardrobeModelButtonedTab TabForRow(PlayerModelData row)
    {
        if (row == null) return null;
        int n;
        try { n = SahurWardrobe.ViewTabCount; } catch { return null; }
        for (int i = 0; i < n; i++)
        {
            var t = SahurWardrobe.ViewTabAt(i);
            if (t == null) continue;
            PlayerModelData d;
            try { d = t.PlayerModelData; } catch { continue; }
            if (Same(d, row)) return t;
        }
        return null;
    }

    private static string Nickname(WardrobeModelButtonedTab tab)
    {
        if (tab == null) return "";
        try { return tab._nicknameNameTMP == null ? "" : (tab._nicknameNameTMP.text ?? ""); }
        catch { return ""; }
    }

    // The actor's native applied row, from the setuper under that actor's root. Local lookup and
    // read-only; nothing about it is written anywhere.
    private static PlayerModelData AppliedRowOfRemote(ulong clientId)
    {
        try
        {
            var root = RemoteModels.RemoteRoot(clientId);
            if (root == null) return null;
            var all = SceneCache.Scan(Il2CppType.Of<PlayerModelSetuper>());
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var s = all[i].TryCast<PlayerModelSetuper>();
                if (s == null || s.gameObject == null || !s.gameObject.activeInHierarchy) continue;
                if (!Under(s.transform, root)) continue;
                return s._playerModelData?.Value;
            }
        }
        catch { }
        return null;
    }

    private static bool Under(Transform t, Transform root)
    {
        for (int i = 0; t != null && i < 12; i++, t = t.parent) if (t == root) return true;
        return false;
    }

    // ------------------------------------------------------------------ helpers

    private static bool Same(UnityEngine.Object a, UnityEngine.Object b)
    {
        if (a == null || b == null) return a == null && b == null;
        try { return a.GetInstanceID() == b.GetInstanceID(); } catch { return false; }
    }

    private static bool Same(PlayerModelData a, PlayerModelData b)
    {
        if (a == null || b == null) return a == null && b == null;
        try { return a.Pointer == b.Pointer; } catch { return false; }
    }

    private static string SafeName(PlayerModelData d)
    {
        try { return d == null ? "" : (d.name ?? ""); } catch { return ""; }
    }

    private static int SafeIndex(PlayerModelData d)
    {
        try { return d == null ? -1 : d.Index; } catch { return -1; }
    }

    private static string SafeString(Func<string> f)
    {
        try { return f() ?? ""; } catch { return ""; }
    }

    private static void Once(IHostContext log, string key, string line)
    {
        if (!_once.Add(key)) return;
        log?.LogInfo(line);
    }

    internal static string Summary =>
        $"hudWrites={_hudWrites} wardrobeMoves={_wardrobeMoves} hudDrift={(_hudDrift ? 1 : 0)} " +
        $"wardrobeDrift={(_wardrobeDrift ? 1 : 0)} dbGen={_dbGen} remoteRev={_remoteRev}";
}
