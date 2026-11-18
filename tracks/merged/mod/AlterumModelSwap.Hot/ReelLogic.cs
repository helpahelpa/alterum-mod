using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using GameLobby;
using Gameplay.Books;
using Gameplay.ImposedEffects;
using Gameplay.PlayerSystem;
using HandheldSystem.ActiveSlotController;
using HandheldSystem.Core;
using HandheldSystem.Implementation.Properties;
using HealthSystem;
using Il2CppInterop.Runtime;
using InventorySystem.Core;
using InventorySystem.Core.Item;
using InventorySystem.Realization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// In-game half of the Reel of Fortune: consume the held item, spin, then apply *the card that is
// under the marker*. The strip is the outcome table (ReelDeck + ReelPool); there is no second roll
// and no "settle on a symbol, then pick an outcome" split any more. The card that stops under the
// marker is the effect, and the settle log names both so they can be checked against each other:
//
//   [reel] settle card=37 type=item id='healthflask' -> granted='healthflask' n=1 ...
//   [reel] settle card=12 type=double id='healthflask' -> granted='healthflask' n=2 ...
//   [reel] settle card=5  type=damage -> hp=... damage=... dead=... clamped=...
//   [reel] settle card=9  type=debuff -> effect='...' via=...
//   [reel] settle card=3  type=blank  -> consumed=1 returned=0 penalty=none
//
// The five card types and their composition (blank 22 / damage 10 / debuff 6 / item 21 / double 5)
// are LethalCasino 1.1.3's `FUN` slot table quantised to 64 cells (analysis kept outside the repo).
// BLANK is that reference's largest bucket - "no match: you lose the stake and nothing else
// happens" - and exists so the majority outcome stops being damage or a debuff. Item rarity plays
// no part in the odds, exactly as in the reference, where a $2 and a $60 scrap spin the same table.
//
// Flow (map run only):
//   1. find the local Player and the currently held Handheld;
//   2. take exactly one NetworkInventoryItem out of its InventorySlot (no grant without that);
//   3. spin; the deck order is reshuffled first, so the landing cell is the only random event;
//   4. read the card under the marker and apply it.
internal static class ReelLogic
{
    private static readonly Dictionary<string, InventoryItemData> _grantable = new Dictionary<string, InventoryItemData>();
    private static bool _scanned;
    private static string _scanSummary = "not scanned";

    internal sealed class Pull
    {
        public Player Player;
        public string HeldId;
        public string HeldCategory;
        public InventoryItemData HeldData;
        public bool Consumed;
        // False when this pull settles for ANOTHER player (a network request): the host applies the
        // outcome to that player, but the result toast belongs to the actor's own client, which
        // receives it in the settle broadcast. Showing it here would put a stranger's result on
        // the host's HUD.
        public bool ShowHud = true;
    }

    // What a settle did, in wire-sized terms: the host broadcasts exactly this so the actor's client
    // can show the same result line without applying anything itself.
    internal struct Outcome
    {
        public byte Kind;        // ReelCardType as a byte
        public string ItemId;    // granted item id, "" when none
        public int Count;        // items granted (0 for blank/damage/debuff)
        public int HpLost;       // damage card, 0 otherwise
        public string Text;      // the result line the local path shows
        public bool Ok;
    }

    internal static Outcome LastOutcome;

    // The result line the last settle produced, so a host can broadcast it. One place sets this, so
    // the text on the actor's client is byte-identical to the text the local path shows.
    private static void Report(IHostContext log, Pull pull, string text, byte kind,
                               string itemId, int count, int hpLost, bool ok, bool error = false)
    {
        LastOutcome = new Outcome
        {
            Kind = kind, ItemId = itemId ?? "", Count = count, HpLost = hpLost,
            Text = text ?? "", Ok = ok,
        };
        if (pull == null || !pull.ShowHud) return;
        if (error) ReelHud.Error(log, text); else ReelHud.Result(log, text);
    }

    internal static bool IsMapRun(out string scene)
    {
        scene = "";
        try { scene = SceneManager.GetActiveScene().name ?? ""; } catch { }
        if (scene.Length == 0) return false;
        if (scene.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (scene.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (scene.IndexOf("Bootstrap", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }

    internal static Player FindLocalPlayerEntity()
    {
        try
        {
            var provs = SceneCache.Scan(Il2CppType.Of<CurrentPlayerProvider>());
            for (int i = 0; i < provs.Length; i++)
            {
                var prov = provs[i].TryCast<CurrentPlayerProvider>();
                if (prov == null) continue;
                var player = prov.CurrentPlayer;
                if (player != null && player.gameObject != null && !string.IsNullOrEmpty(player.gameObject.scene.name))
                    return player;
            }
        }
        catch { }

        try
        {
            var players = SceneCache.Scan(Il2CppType.Of<Player>());
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i].TryCast<Player>();
                if (player == null || !player.IsOwner) continue;
                if (player.gameObject == null || string.IsNullOrEmpty(player.gameObject.scene.name)) continue;
                if (!player.gameObject.activeInHierarchy) continue;
                return player;
            }
        }
        catch { }
        return null;
    }

    // The Netcode Player root sits at the origin in a map; the visible body is a
    // ThirdViewPlayerModel under it. Placement and range both use the body's world pose.
    internal static bool TryGetPlayerPose(out Vector3 pos, out Vector3 fwd, out string source)
    {
        pos = Vector3.zero; fwd = Vector3.forward; source = "none";
        var player = FindLocalPlayerEntity();
        if (player == null) return false;
        if (TryGetPose(player, out pos, out fwd, out source) && source == "ThirdViewPlayerModel") return true;
        try
        {
            var cam = Camera.main;
            if (cam != null)
            {
                pos = cam.transform.position;
                fwd = cam.transform.forward;
                source = "Camera.main";
                return true;
            }
        }
        catch { }
        return TryGetPose(player, out pos, out fwd, out source);
    }

    // Same body-vs-root resolution as the local pose, for an explicit actor: the host validates a
    // request from the requesting player's own body, never from its own camera.
    internal static bool TryGetPose(Player player, out Vector3 pos, out Vector3 fwd, out string source)
    {
        pos = Vector3.zero;
        fwd = Vector3.forward;
        source = "none";
        if (player == null) return false;

        try
        {
            var models = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; models != null && i < models.Length; i++)
            {
                var m = models[i].TryCast<ThirdViewPlayerModel>();
                if (m == null) continue;
                var go = m.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                if (!Under(go.transform, player.transform)) continue;
                pos = go.transform.position;
                fwd = go.transform.forward;
                source = "ThirdViewPlayerModel";
                return true;
            }
        }
        catch { }

        try
        {
            pos = player.transform.position;
            fwd = player.transform.forward;
            source = "Player.transform";
            return true;
        }
        catch { return false; }
    }

    // The id of the item currently activated in the player's hands, "" when there is none. The host
    // compares this with the request's expectedHeldItemId; the client puts the same value on the
    // wire, so an item swapped between the request and the host's read is caught, not charged.
    internal static string HeldItemId(Player player)
    {
        if (player == null) return "";
        try
        {
            var hands = FindHands(player);
            var handheld = hands != null ? hands.CurrentHandheld : null;
            if (handheld == null) return "";
            var props = handheld.GetProperties<HandheldInventoryProperties>();
            if (props == null || props.InventoryItemWrapper == null) return "";
            var data = props.InventoryItemWrapper._itemData;
            return data == null ? "" : ItemId(data);
        }
        catch { return ""; }
    }

    // Evidence line for a verification run: the actor's health and how many of `id` their inventory
    // holds. Read-only; never grants, consumes or heals. Runs on whichever instance asks.
    internal static void DebugInv(IHostContext log, string id)
    {
        var player = FindLocalPlayerEntity();
        if (player == null) { log.LogWarning("[reel:inv] no local player"); return; }
        var health = FindHealth(player);
        string hp = health == null ? "missing" : $"{health.Value:F1}/{health.MaxValue:F1} dead={health.IsDead}";
        string held = HeldItemId(player);
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(id))
        {
            InventoryItemData data;
            if (TryGetData(id, log, out data)) sb.Append($" count('{id}')={CountItem(player, data)}");
            else sb.Append($" count('{id}')=?");
        }
        log.LogInfo($"[reel:inv] player='{PrettyName(player.name)}' hp={hp} held='{held}'{sb} " +
                    $"id='{id}' slot={(FindHands(player) != null ? "hands" : "none")}");
    }

    private static bool Under(Transform child, Transform parent)
    {
        int guard = 0;
        for (var t = child; t != null && guard++ < 16; t = t.parent)
            if (t == parent) return true;
        return false;
    }

    // Called by LobbyPropRuntime before a map-run spin starts. Consumes one item. Returns false
    // when the spin must not start (wrong scene, no held item, inventory refused the take).
    internal static bool TryBeginPull(IHostContext log, out Pull pull) =>
        TryBeginPull(log, null, out pull);

    // The host's request path passes the ACTOR explicitly: every read below (hands, held item,
    // inventory) is that player's, and the consume is the game's own server-side take, which
    // replicates to the owner. `actor == null` means the local player (the solo/host path).
    internal static bool TryBeginPull(IHostContext log, Player actor, out Pull pull)
    {
        pull = null;
        string scene;
        if (!IsMapRun(out scene))
        {
            log.LogInfo($"[reel] pull skipped reason=not-a-map-run scene='{scene}'");
            return false;
        }

        var player = actor ?? FindLocalPlayerEntity();
        if (player == null)
        {
            log.LogWarning("[reel] pull skipped reason=no-local-player");
            return false;
        }

        var hands = FindHands(player);
        var handheld = hands != null ? hands.CurrentHandheld : null;
        if (handheld == null)
        {
            log.LogInfo($"[reel] pull rejected reason=no-held-item scene='{scene}' message='You need an item to bet'");
            if (actor == null) ReelHud.Error(log, "You need an item to bet");
            ReelAudio.Cue(log, ReelCue.Error);
            return false;
        }

        HandheldInventoryProperties props = null;
        NetworkInventoryItem item = null;
        InventoryItemData data = null;
        try
        {
            props = handheld.GetProperties<HandheldInventoryProperties>();
            item = props != null ? props.Item : null;
            data = props != null && props.InventoryItemWrapper != null ? props.InventoryItemWrapper._itemData : null;
        }
        catch (Exception e)
        {
            log.LogWarning($"[reel] pull rejected reason=held-item-unreadable: {e.Message}");
            return false;
        }
        if (item == null || data == null)
        {
            log.LogInfo("[reel] pull rejected reason=held-item-not-an-inventory-item");
            return false;
        }

        string heldId = ItemId(data);
        // Never bet a quest rune or the X-selected lantern: refuse before ANY inventory change, so
        // a refusal costs the player nothing. Identification is id+name based (ReelExclusions),
        // with the generated pool rows (weight 0) as the second opinion.
        string refusal = ReelExclusions.ReasonFor(heldId, DisplayNameOf(heldId));
        if (refusal.Length > 0)
        {
            log.LogWarning($"[reel] pull refused reason={refusal} held='{heldId}' name='{DisplayNameOf(heldId)}' " +
                           "consumed=0 (quest/special item is never bettable or winnable)");
            if (actor == null) ReelHud.Error(log, RefusalText(refusal, heldId));
            ReelAudio.Cue(log, ReelCue.Error);
            return false;
        }

        EnsureDebuffScan(log);

        int before = CountItem(player, data);
        if (!ConsumeOne(log, player, item, data, heldId, before))
        {
            log.LogWarning($"[reel] pull rejected reason=consume-failed held='{heldId}' before={before}");
            return false;
        }
        int after = CountItem(player, data);

        pull = new Pull
        {
            Player = player,
            HeldId = heldId,
            HeldCategory = CategoryOf(heldId),
            HeldData = data,
            Consumed = true,
            ShowHud = ReferenceEquals(actor, null),
        };

        log.LogInfo($"[reel] pull start held='{heldId}' category={pull.HeldCategory} consumed=1 " +
                    $"inventory={before}->{after} deck=[{ReelDeck.Census(ReelDeck.Compose(ReelDeck.Cells, 0))}] " +
                    $"{ReelTable.Summary()} debuffMask={ReelDeck.DebuffMask}");
        return true;
    }

    // Pool display names are asset-derived (`Axe_03_serrated`). For the player-facing line, drop
    // the numbering and separators: "Axe serrated".
    internal static string PrettyName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        foreach (var raw in name.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            bool numeric = true;
            for (int i = 0; i < raw.Length; i++) if (!char.IsDigit(raw[i])) { numeric = false; break; }
            if (numeric) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(raw);
        }
        string s = sb.Length > 0 ? sb.ToString() : name;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    internal static string DisplayNameOf(string id)
    {
        int idx = ReelPool.IndexOf(id ?? "");
        return idx >= 0 ? ReelPool.All[idx].Name : (id ?? "");
    }

    private static string RefusalText(string reason, string id)
    {
        string name = DisplayNameOf(id);
        switch (reason)
        {
            // ASCII only: the card font has no em dash, and the drawn line is a 5x7 bitmap.
            case "quest-rune": return $"Not the {name}: quest item";
            case "quest-lantern": return $"Not the {name}: quest light";
            default: return $"Not the {name}";
        }
    }

    private static Hands FindHands(Player player)
    {
        try
        {
            var hands = player.GetComponentsInChildren<Hands>(true);
            for (int i = 0; i < hands.Length; i++)
                if (hands[i] != null && hands[i].gameObject.activeInHierarchy) return hands[i];
            if (hands.Length > 0) return hands[0];
        }
        catch { }
        return null;
    }

    private static bool ConsumeOne(IHostContext log, Player player, NetworkInventoryItem item,
                                   InventoryItemData data, string heldId, int before)
    {
        var slot = item.SlotItIsIn;
        if (slot != null)
        {
            try
            {
                Il2CppSystem.Collections.Generic.List<NetworkInventoryItem> taken;
                bool ok = slot.TryTake(1, out taken);
                int n = taken != null ? taken.Count : 0;
                if (ok && n == 1)
                {
                    log.LogInfo($"[reel] consumed held='{heldId}' count=1 slot={slot.GetHashCode()} from={before}");
                    return true;
                }
                log.LogWarning($"[reel] consume slot.TryTake(1) ok={ok} taken={n}");
            }
            catch (Exception e) { log.LogWarning($"[reel] consume slot.TryTake threw: {e.Message}"); }
        }

        try
        {
            var inventory = FindWritableInventory(player);
            if (inventory != null)
            {
                var taken = new Il2CppSystem.Collections.Generic.List<NetworkInventoryItem>();
                bool ok = inventory.TryTake(data, 1, taken);
                if (ok && taken != null && taken.Count > 0)
                {
                    log.LogInfo($"[reel] consumed held='{heldId}' count={taken.Count} via=inventory.Take from={before}");
                    return true;
                }
                log.LogWarning($"[reel] consume inventory.TryTake ok={ok} taken={(taken != null ? taken.Count : 0)}");
            }
        }
        catch (Exception e) { log.LogWarning($"[reel] consume inventory.TryTake threw: {e.Message}"); }
        return false;
    }

    // ---- settle: read the card, apply the card ------------------------------------------

    // `cell` is the strip cell that came to rest under the marker and `card` is the card in that
    // cell - the caller read it with the same accessor the renderer used, so what the log names and
    // what the picture shows are the same card.
    internal static void Resolve(IHostContext log, Pull pull, int cell, ReelCard card)
    {
        if (pull == null) return;
        LastOutcome = new Outcome { Kind = (byte)card.Type, Ok = false, ItemId = "", Text = "" };
        try
        {
            switch (card.Type)
            {
                case ReelCardType.Item: ResolveItem(log, pull, cell, card); return;
                case ReelCardType.Double: ResolveDouble(log, pull, cell, card); return;
                case ReelCardType.Damage: ResolveDamage(log, pull, cell, card); return;
                case ReelCardType.Debuff: ResolveDebuff(log, pull, cell, card); return;
                case ReelCardType.Blank: ResolveBlank(log, pull, cell, card); return;
            }
        }
        catch (Exception e)
        {
            log.LogError($"[reel] settle card={cell} type={ReelDeck.TypeName(card.Type)} failed: {e}");
        }
    }

    // blank -> the stake is gone and nothing is returned: no item, no damage, no debuff. This is the
    // reference's 48.5 % "no match" bucket, the largest single outcome it has, and the reason a
    // losing spin is normally a quiet one rather than a hit. The hosted item was already consumed by
    // TryBeginPull before the strip turned, so there is nothing to take here.
    private static void ResolveBlank(IHostContext log, Pull pull, int cell, ReelCard card)
    {
        log.LogInfo($"[reel] settle card={cell} type=blank -> consumed=1 returned=0 penalty=none");
        Report(log, pull, "Nothing. It keeps your item.", (byte)card.Type, "", 0, 0, true);
        ReelAudio.Cue(log, ReelCue.Settle);
    }

    // item -> grant that one item (1 instance).
    private static void ResolveItem(IHostContext log, Pull pull, int cell, ReelCard card)
    {
        if (card.Item < 0 || card.Item >= ReelPool.All.Length)
        {
            log.LogWarning($"[reel] settle card={cell} type=item id='' -> no item index (deck bug)");
            return;
        }
        var item = ReelPool.All[card.Item];
        bool ok = Grant(log, pull, cell, "item", item.Id, 1);
        Report(log, pull, ok
            ? $"You won 1 x {PrettyName(item.Name)}"
            : $"Card says {PrettyName(item.Name)}, but the item is not loaded",
            (byte)card.Type, item.Id, ok ? 1 : 0, 0, ok);
        ReelAudio.Cue(log, ReelCue.Win);
    }

    // double -> grant two instances of the item that was INSERTED (the consumed one).
    private static void ResolveDouble(IHostContext log, Pull pull, int cell, ReelCard card)
    {
        if (string.IsNullOrEmpty(pull.HeldId))
        {
            log.LogWarning($"[reel] settle card={cell} type=double -> no inserted item recorded");
            return;
        }
        bool ok = Grant(log, pull, cell, "double", pull.HeldId, 2);
        string name = PrettyName(DisplayNameOf(pull.HeldId));
        Report(log, pull, ok
            ? $"Double! 2 x {name}"
            : $"Double! But {name} could not be granted",
            (byte)card.Type, pull.HeldId, ok ? 2 : 0, 0, ok);
        ReelAudio.Cue(log, ReelCue.Double);
    }

    private static bool Grant(IHostContext log, Pull pull, int cell, string type, string id, int count)
    {
        InventoryItemData data;
        if (!TryGetData(id, log, out data))
        {
            log.LogInfo($"[reel] settle card={cell} type={type} id='{id}' -> granted='{id}' n=0 unavailable=1");
            return false;
        }

        int before = CountItem(pull.Player, data);
        bool threw = false;
        string error = "";
        try { PlayerInventoryUtilities.AddItemOrDropIfNoSpace(pull.Player, data, count); }
        catch (Exception e) { threw = true; error = e.GetType().Name + ": " + e.Message; }
        int after = CountItem(pull.Player, data);
        int delta = after - before;

        log.LogInfo($"[reel] settle card={cell} type={type} id='{id}' -> granted='{id}' n={count} " +
                    $"consumed=1 inventory={before}->{after} delta={delta} ok={!threw} " +
                    $"{(delta == count ? "" : "dropped-or-clipped=1 ")}{error}");
        return !threw;
    }

    // damage -> lose 25-45 % of max HP (clamped by the game's Health; can kill).
    private static void ResolveDamage(IHostContext log, Pull pull, int cell, ReelCard card)
    {
        var health = FindHealth(pull.Player);
        if (health == null)
        {
            log.LogWarning($"[reel] settle card={cell} type=damage -> no Health on the local player");
            log.LogInfo($"[reel] settle card={cell} type=damage -> hp=missing dead=False");
            return;
        }

        float max = health.MaxValue;
        float before = health.Value;
        float pct = UnityEngine.Random.Range(0.25f, 0.45f);
        float damage = Mathf.Max(1f, max * pct);

        bool threw = false;
        string error = "";
        try
        {
            health.Damage(damage, HealthChangeContext.FromWorld(WorldHealthChangeSourceData.Type.Debug));
        }
        catch (Exception e) { threw = true; error = e.Message; }

        float after = health.Value;
        if (!threw && after < health.MinValue) after = health.MinValue;
        bool clamped = after <= health.MinValue;
        bool dead = health.IsDead;
        log.LogInfo($"[reel] settle card={cell} type=damage -> hp={before:F1}->{after:F1}/{max:F1} " +
                    $"damage={damage:F1} pct={pct:F2} dead={dead} clamped={clamped} consumed=1 threw={threw} {error}");
        int lost = Mathf.Max(0, Mathf.RoundToInt(before - after));
        Report(log, pull, dead ? $"You lost {lost} HP - you went down" : $"You lost {lost} HP",
               (byte)card.Type, "", 0, lost, true);
        ReelAudio.Cue(log, ReelCue.Damage);
    }

    // debuff -> the real imposed status effect the card names (Slow/Fatigue/Blind, each the game's
    // own ImposedEffectVariantData family, applied through PlayersImposedEffects exactly like the
    // game's RPC handler does). A card never acts as something other than what it shows: if the
    // named family has no loaded variant, nothing is applied and the log says so - no silent fold
    // into damage any more.
    private static void ResolveDebuff(IHostContext log, Pull pull, int cell, ReelCard card)
    {
        var info = ReelTable.Debuff(card.Debuff);
        string name, via;
        if (TryApplyDebuff(log, pull.Player, card.Debuff, out name, out via))
        {
            log.LogInfo($"[reel] settle card={cell} type=debuff kind={info.Code} effect='{name}' via={via} consumed=1");
            Report(log, pull, info.Plain, (byte)card.Type, "", 0, 0, true);
            ReelAudio.Cue(log, ReelCue.Debuff);
            return;
        }
        log.LogWarning($"[reel] settle card={cell} type=debuff kind={info.Code} -> no loaded variant for this effect; " +
                       "nothing applied (a card must not act as another effect)");
        Report(log, pull, $"Misfire: {info.Code} unavailable", (byte)card.Type, "", 0, 0, false, true);
    }

    // Which debuff families have at least one usable variant loaded in this scene. Recomposes the
    // deck when it changes, so a card type that cannot fire is never composed into the strip.
    private static int _debuffMask = -1;
    private static string _debuffSummary = "not scanned";

    internal static void EnsureDebuffScan(IHostContext log)
    {
        if (_debuffMask >= 0) return;
        int mask = 0;
        var sb = new StringBuilder();
        int slow = CountSlowVariants(log);
        if (slow > 0) { mask |= 1 << (int)ReelDebuffKind.Slow; }
        sb.Append("slow=").Append(slow);
        int fatigue = CountVariants<FatigueImposedEffectVariantData>(log);
        if (fatigue > 0) { mask |= 1 << (int)ReelDebuffKind.Fatigue; }
        sb.Append(" fatigue=").Append(fatigue);
        int blind = CountVariants<BlindnessImposedEffectVariantData>(log);
        if (blind > 0) { mask |= 1 << (int)ReelDebuffKind.Blind; }
        sb.Append(" blind=").Append(blind);
        _debuffMask = mask;
        ReelDeck.DebuffMask = mask == 0 ? 7 : mask;
        _debuffSummary = (mask == 0 ? "none-loaded(deck keeps all, resolve refuses) " : "") + sb;
        log.LogInfo($"[reel] debuff scan {_debuffSummary} active={ReelDeck.Census(ReelDeck.Compose(ReelDeck.Cells, 0, ReelDeck.DebuffMask))}");
    }

    internal static int DebuffMask => _debuffMask;
    internal static string DebuffScanSummary => _debuffSummary;

    private static int CountSlowVariants(IHostContext log)
    {
        int n = 0;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<SlowImposedEffectVariantData>());
            for (int i = 0; i < arr.Length; i++)
            {
                var s = arr[i].TryCast<SlowImposedEffectVariantData>();
                if (s != null && s.MoveSpeedMultiplier < 0.999f) n++;
            }
        }
        catch (Exception e) { log.LogWarning($"[reel] debuff scan slow failed: {e.Message}"); }
        return n;
    }

    private static int CountVariants<T>(IHostContext log) where T : UnityEngine.Object
    {
        try { return SceneCache.Scan(Il2CppType.Of<T>()).Length; }
        catch (Exception e) { log.LogWarning($"[reel] debuff scan {typeof(T).Name} failed: {e.Message}"); return 0; }
    }

    // Pick the family's variant (the strongest slow, the first fatigue/blind) and impose it. The
    // utility resolves a target through the network registry; the player's own component is the
    // same call the RPC handler ends in, so it is the direct fallback.
    private static bool TryApplyDebuff(IHostContext log, Player player, ReelDebuffKind kind,
                                       out string name, out string via)
    {
        name = null;
        via = "";
        try
        {
            ImposedEffectVariantData chosen = null;
            switch (kind)
            {
                case ReelDebuffKind.Fatigue:
                    chosen = FirstVariant<FatigueImposedEffectVariantData>();
                    if (chosen != null)
                    {
                        var f = chosen.TryCast<FatigueImposedEffectVariantData>();
                        name = SafeName(f) + ":stamina+" + f._staminaConsumptionIncreasePercent.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                    }
                    break;
                case ReelDebuffKind.Blind:
                    chosen = FirstVariant<BlindnessImposedEffectVariantData>();
                    if (chosen != null) name = SafeName(chosen);
                    break;
                default:
                    {
                        var slowed = PickSlow();
                        chosen = slowed;
                        if (slowed != null)
                            name = SafeName(slowed) + ":x" + slowed.MoveSpeedMultiplier.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
            }

            if (chosen == null)
            {
                log.LogInfo($"[reel] debuff {kind} found 0 usable variants");
                return false;
            }
            try
            {
                ImposedEffectsUtilities.ImposeEffect(player, chosen);
                via = "utility";
                log.LogInfo($"[reel] debuff {kind} applied variant='{name}' via=utility " +
                            $"duration={(chosen is ImposedTemporaryEffectVariantData t ? t.Duration : -1f):F1}");
                return true;
            }
            catch (Exception e)
            {
                log.LogWarning($"[reel] debuff {kind} utility apply failed: {e.Message}; trying PlayersImposedEffects directly");
            }

            var comps = player.GetComponentsInChildren<PlayersImposedEffects>(true);
            if (comps.Length > 0 && comps[0] != null)
            {
                comps[0].ImposeTheEffect(chosen);
                via = "PlayersImposedEffects";
                log.LogInfo($"[reel] debuff {kind} applied variant='{name}' via=PlayersImposedEffects " +
                            $"component='{comps[0].gameObject.name}'");
                return true;
            }
            log.LogInfo($"[reel] debuff {kind} has a variant but no PlayersImposedEffects on the player");
            return false;
        }
        catch (Exception e)
        {
            log.LogWarning($"[reel] debuff {kind} apply failed: {e.Message}");
            return false;
        }
    }

    private static SlowImposedEffectVariantData PickSlow()
    {
        SlowImposedEffectVariantData best = null;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<SlowImposedEffectVariantData>());
            for (int i = 0; i < arr.Length; i++)
            {
                var s = arr[i].TryCast<SlowImposedEffectVariantData>();
                if (s == null || s.MoveSpeedMultiplier >= 0.999f) continue;
                if (best == null || s.MoveSpeedMultiplier < best.MoveSpeedMultiplier) best = s;
            }
        }
        catch { }
        return best;
    }

    private static T FirstVariant<T>() where T : UnityEngine.Object
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<T>());
            for (int i = 0; i < arr.Length; i++)
            {
                var v = arr[i].TryCast<T>();
                if (v != null) return v;
            }
        }
        catch { }
        return null;
    }

    private static string SafeName(UnityEngine.Object o)
    {
        try { return o != null ? o.name : "?"; } catch { return "?"; }
    }

    // ---- runtime item registry -----------------------------------------------------------

    // Ensure the runtime registry is scanned once. Uses the game's live InventoryItemData objects,
    // not a hand-built ItemData->component guess; only ids in the generated pool are kept.
    private static void EnsureScanned(IHostContext log)
    {
        if (_scanned) return;
        _scanned = true;
        int total = 0;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<InventoryItemData>());
            for (int i = 0; i < arr.Length; i++)
            {
                var inv = arr[i].TryCast<InventoryItemData>();
                if (inv == null) continue;
                string id = ItemId(inv);
                if (string.IsNullOrEmpty(id)) continue;
                if (!_grantable.ContainsKey(id)) { _grantable[id] = inv; total++; }
            }
        }
        catch (Exception e) { log.LogWarning($"[reel] inventory-item scan failed: {e.Message}"); }

        int inPool = 0, missing = 0;
        var miss = new StringBuilder();
        for (int i = 0; i < ReelPool.All.Length; i++)
        {
            var it = ReelPool.All[i];
            if (!it.Usable) continue;
            inPool++;
            if (_grantable.ContainsKey(it.Id)) continue;
            missing++;
            if (miss.Length < 400) miss.Append(it.Id).Append(' ');
        }
        _scanSummary = $"runtimeInventoryItems={total} pool={inPool} missing={missing} " +
                       $"totalWeight={ReelPool.TotalWeight()}";
        log.LogInfo($"[reel] pool scan {_scanSummary} missing=[{miss}] generated={ReelPool.Generated}");
    }

    private static bool TryGetData(string id, IHostContext log, out InventoryItemData data)
    {
        data = null;
        if (string.IsNullOrEmpty(id)) return false;
        EnsureScanned(log);
        if (_grantable.TryGetValue(id, out data) && data != null) return true;
        // A scene that never loaded this item's asset would have missed it at scan time; rescan once.
        _scanned = false;
        EnsureScanned(log);
        return _grantable.TryGetValue(id, out data) && data != null;
    }

    internal static Health FindHealth(Player player)
    {
        try
        {
            var wrappers = player.GetComponentsInChildren<HealthWrapper>(true);
            for (int i = 0; i < wrappers.Length; i++)
            {
                var h = wrappers[i] != null ? wrappers[i].Health : null;
                if (h != null) return h;
            }
        }
        catch { }

        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<HealthWrapper>());
            var scene = player.gameObject.scene.name;
            for (int i = 0; i < arr.Length; i++)
            {
                var w = arr[i].TryCast<HealthWrapper>();
                if (w == null || w.Health == null) continue;
                if (!string.IsNullOrEmpty(scene) && w.gameObject.scene.name != scene) continue;
                return w.Health;
            }
        }
        catch { }
        return null;
    }

    private static Inventory FindWritableInventory(Player player)
    {
        try
        {
            var comps = player.GetComponentsInChildren<InventoryComponent>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null || !c.IsWritableInventory) continue;
                var inv = c.Inventory;
                if (inv != null) return inv;
            }
        }
        catch { }
        return null;
    }

    private static int CountItem(Player player, InventoryItemData data)
    {
        int n = 0;
        try
        {
            var comps = player.GetComponentsInChildren<InventoryComponent>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                var inv = comps[i] != null ? comps[i].Inventory : null;
                if (inv != null) n += inv.GetItemCount(data);
            }
        }
        catch { }
        return n;
    }

    private static string ItemId(InventoryItemData data)
    {
        try
        {
            var g = data != null ? data.GameItemData : null;
            return g != null ? g.ItemDataID : null;
        }
        catch { return null; }
    }

    private static string CategoryOf(string id)
    {
        int i = ReelPool.IndexOf(id);
        return i >= 0 ? ReelPool.All[i].Category : "?";
    }

    // Test-only hook for unattended verification. Starts the lobby's transition to a map so the
    // map-only pull can be exercised on a locked desktop. Not part of the player-facing feature;
    // remove the `startmap` token if the build is shipped as-is.
    internal static void StartMap(IHostContext log)
    {
        try
        {
            // Who is connected right now: the transition can only carry the clients the server
            // still has. Without this line a peer that dropped before the gate looks exactly like
            // a peer the loader refused to carry.
            try
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                var ids = nm != null ? nm.ConnectedClientsIds : null;
                // IReadOnlyList<ulong> is Il2CppSystem's projection class: Item only, no Count.
                var concrete = ids != null ? ids.TryCast<Il2CppSystem.Collections.Generic.List<ulong>>() : null;
                int count = concrete != null ? concrete.Count : -1;
                var sb = new System.Text.StringBuilder(32);
                for (int i = 0; concrete != null && i < concrete.Count; i++) { if (i > 0) sb.Append(','); sb.Append(concrete[i]); }
                log.LogInfo($"[reel:debug] startmap: server={(nm != null && nm.IsServer)} local={(nm != null ? nm.LocalClientId : 0)} " +
                            $"connected={count} [{sb}]");
            }
            catch (Exception e) { log.LogWarning($"[reel:debug] startmap: connection state unavailable: {e.Message}"); }
            var arr = SceneCache.Scan(Il2CppType.Of<GameSceneStarter>());
            log.LogInfo($"[reel:debug] GameSceneStarter objects={arr.Length}");
            for (int i = 0; i < arr.Length; i++)
            {
                var starter = arr[i].TryCast<GameSceneStarter>();
                if (starter == null) continue;
                var go = starter.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                var ready = starter._readinessController;
                bool can = ready != null && ready.CanStartExpedition != null && ready.CanStartExpedition.Value;
                string mapName = "-";
                try { var m = starter._mapSelector != null ? starter._mapSelector.SelectedGameMap : null; mapName = m != null ? m.name : "null"; } catch { }
                log.LogInfo($"[reel:debug] starter '{go.name}' scene='{go.scene.name}' mapSelector={(starter._mapSelector != null)} " +
                            $"map='{mapName}' scenesConfig={(starter._scenesConfig != null)} canStart={can} " +
                            $"ready={ready != null} readyCount={(ready != null ? ready.NumberOfReadyPlayers : -1)} " +
                            $"min={(ready != null ? ready.MinRequiredPlayers : -1)} max={(ready != null ? ready.MaxRequiredPlayers : -1)}");
                if (ready != null && !can)
                {
                    // Recompute only: readiness is the players' gate, and a diagnostic must not
                    // manufacture it (the deleted MakePlayerReadyRpc(0) marked client 0 ready who
                    // was never on the pad).
                    try { ready.UpdateCanStartExpedition(); } catch (Exception e) { log.LogWarning($"[reel:debug] readiness update failed: {e.Message}"); }
                    try { can = ready.CanStartExpedition != null && ready.CanStartExpedition.Value; } catch { can = false; }
                }
                try
                {
                    var book = starter._mapSelector != null ? starter._mapSelector.TryCast<MapBookNetworkMapSelector>() : null;
                    if (book != null)
                    {
                        log.LogInfo($"[reel:debug] map selector index={book.SelectedMapIndex.Value} effective={book.EffectiveMapIndex}");
                        if (book.SelectedMapIndex.Value < 0) book.SelectedMapIndex.Value = 0;
                    }
                }
                catch (Exception e) { log.LogWarning($"[reel:debug] map select failed: {e.Message}"); }
                if (!can)
                {
                    // No forced gate: the pad and the countdown are the players' own path.
                    log.LogWarning($"[reel:debug] startmap refused: CanStartExpedition is false " +
                                   $"(ready={(ready != null ? ready.NumberOfReadyPlayers : -1)}/" +
                                   $"{(ready != null ? ready.MinRequiredPlayers : -1)}); this diagnostic will not " +
                                   "force readiness or the gate");
                    return;
                }
                try { starter.StartTransitionToGameScene(); log.LogInfo("[reel:debug] StartTransitionToGameScene invoked"); }
                catch (Exception e) { log.LogWarning($"[reel:debug] start transition failed: {e.Message}"); }
                return;
            }
            log.LogWarning("[reel:debug] no active GameSceneStarter");
        }
        catch (Exception e) { log.LogError($"[reel:debug] startmap failed: {e}"); }
    }

    // Test-only: put one item in the local inventory through the same grant path the outcome uses.
    internal static bool DebugGive(IHostContext log, string id)
    {
        InventoryItemData data;
        if (!TryGetData(id, log, out data))
        {
            log.LogWarning($"[reel:debug] give '{id}' not in the runtime registry");
            return false;
        }
        var player = FindLocalPlayerEntity();
        if (player == null) { log.LogWarning("[reel:debug] give: no local player"); return false; }
        int before = CountItem(player, data);
        try { PlayerInventoryUtilities.AddItemOrDropIfNoSpace(player, data, 1); }
        catch (Exception e) { log.LogWarning($"[reel:debug] give threw: {e.Message}"); return false; }
        int after = CountItem(player, data);
        log.LogInfo($"[reel:debug] give id='{id}' inventory={before}->{after} ok={after > before}");
        return after > before;
    }

    // Test-only (host side): give one item to an EXPLICIT player - the game's own grant call, so a
    // remote client's inventory receives it from the server, exactly like a pickup would.
    internal static bool DebugGivePlayer(IHostContext log, Player player, string id)
    {
        if (player == null) { log.LogWarning("[reel:debug] give-player: no player"); return false; }
        InventoryItemData data;
        if (!TryGetData(id, log, out data)) { log.LogWarning($"[reel:debug] give-player '{id}' not in the runtime registry"); return false; }
        int before = CountItem(player, data);
        try { PlayerInventoryUtilities.AddItemOrDropIfNoSpace(player, data, 1); }
        catch (Exception e) { log.LogWarning($"[reel:debug] give-player threw: {e.Message}"); return false; }
        int after = CountItem(player, data);
        log.LogInfo($"[reel:debug] give-player id='{id}' player='{player.name}' owner={player.OwnerClientId} " +
                    $"inventory={before}->{after} ok={after > before}");
        return after > before;
    }

    // Test-only: restore the local player's health to max. Exists so a verification spin can be
    // taken without risking a lethal damage card on the live player; it is the same health object
    // the damage card writes to, and it logs the before/after like every other reel step.
    internal static bool DebugHeal(IHostContext log)
    {
        var player = FindLocalPlayerEntity();
        if (player == null) { log.LogWarning("[reel:debug] heal: no local player"); return false; }
        var health = FindHealth(player);
        if (health == null) { log.LogWarning("[reel:debug] heal: no Health on the local player"); return false; }
        try
        {
            float before = health.Value, max = health.MaxValue;
            health.Value = max;
            log.LogInfo($"[reel:debug] heal hp={before:F1}->{health.Value:F1}/{max:F1} dead={health.IsDead}");
            return true;
        }
        catch (Exception e) { log.LogWarning($"[reel:debug] heal threw: {e.Message}"); return false; }
    }

    // Test-only: activate the first slot holding `id` so Hands.CurrentHandheld becomes that item.
    internal static bool DebugHold(IHostContext log, string id)
    {
        var player = FindLocalPlayerEntity();
        if (player == null) { log.LogWarning("[reel:debug] hold: no local player"); return false; }
        InventoryItemData data;
        if (!TryGetData(id, log, out data))
        {
            log.LogWarning($"[reel:debug] hold '{id}' not in the runtime registry");
            return false;
        }

        InventoriesActiveSlotController controller = null;
        try
        {
            var cs = SceneCache.Scan(Il2CppType.Of<InventoriesActiveSlotController>());
            for (int i = 0; i < cs.Length; i++)
            {
                var c = cs[i].TryCast<InventoriesActiveSlotController>();
                if (c != null && c.gameObject != null && c.gameObject.activeInHierarchy) { controller = c; break; }
            }
            if (controller == null)
            {
                var local = player.GetComponentsInChildren<InventoriesActiveSlotController>(true);
                if (local.Length > 0) controller = local[0];
            }
        }
        catch { }
        if (controller == null) { log.LogWarning("[reel:debug] hold: no active-slot controller"); return false; }

        try
        {
            var comps = player.GetComponentsInChildren<InventoryComponent>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                var comp = comps[i];
                var inv = comp != null ? comp.Inventory : null;
                if (inv == null || !comp.IsWritableInventory) continue;
                var slot = inv.GetSlot(data);
                if (slot == null) continue;
                controller.ForceActivateSlot(comp, slot);
                log.LogInfo($"[reel:debug] hold id='{id}' component='{comp.gameObject.name}' slotIndex={slot.GetHashCode()}");
                return true;
            }
        }
        catch (Exception e) { log.LogWarning($"[reel:debug] hold threw: {e.Message}"); }
        log.LogWarning($"[reel:debug] hold id='{id}' found no slot");
        return false;
    }

    // For an unattended probe: report the deck as implemented and what the runtime exposes, without
    // consuming or granting anything.
    internal static void Probe(IHostContext log)
    {
        EnsureScanned(log);
        var deck = ReelDeck.Compose(ReelDeck.Cells, Environment.TickCount);
        var sb = new StringBuilder();
        for (int k = 0; k < deck.Length && sb.Length < 1500; k++)
            sb.Append(k).Append(':').Append(ReelDeck.Code(deck[k])).Append(' ');
        string scene; bool map = IsMapRun(out scene);
        var player = FindLocalPlayerEntity();
        var health = player != null ? FindHealth(player) : null;
        var hands = player != null ? FindHands(player) : null;
        log.LogInfo($"[reel:probe] map={map} scene='{scene}' player={(player != null)} " +
                    $"held='{(hands != null && hands.CurrentHandheld != null ? "yes" : "no")}' " +
                    $"health={(health != null ? health.Value.ToString("F1") + "/" + health.MaxValue.ToString("F1") : "none")} " +
                    $"scan={_scanSummary} pool={ReelPool.UsableCount()} icons=" +
                    $"{CountIcons()} {ReelTable.Summary()} debuffScan='{_debuffSummary}' " +
                    $"excluded=[{ReelExclusions.TableSummary()}] promptPath='{ReelHud.Path}' audio='{ReelAudio.Summary}'");
        log.LogInfo($"[reel:probe] deck {ReelDeck.Census(deck)} order=[{sb}]");
    }

    private static int CountIcons()
    {
        int n = 0;
        for (int i = 0; i < ReelPool.All.Length; i++) if (ReelPool.All[i].HasIcon) n++;
        return n;
    }
}
