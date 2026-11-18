using System;
using System.Collections.Generic;
using System.Text;

namespace AlterumModelSwap.Hot;

// The strip *is* the outcome table.
//
// A card's type is fixed when the deck is composed; the only random event at spin time is which
// cell the strip comes to rest on, and the settle reads whatever card is under the marker and
// applies that. There is no second roll anywhere.
//
// --------------------------------------------------------------------------------------------
// THE TABLE: every rate in this feature is here, plus the item-category weights in the generated
// ReelPool.g.cs. Nothing below this comment block hard-codes a proportion.
//
// The numbers are LethalCasino 1.1.3's shipped `FUN` preset (decompiled; the analysis is
// the LethalCasino rate analysis, kept outside the repo), mapped onto our five card faces, quantised to 64 cells:
//
//     blank 22   damage 10   debuff 6   item 21   double 5        = 64 cells
//     34.4 %     15.6 %      9.4 %      32.8 %     7.8 %
//     |____________ 59.4 % lose ____________|  |___ 40.6 % win ___|
//
// Their slots table is skulls 11.0 / no-match 48.5 / pair 30.3 / fruits 5.5 / bars-bells 4.0 /
// sevens 0.7 (99.70 % RTP, 59.5 % of spins lose). The two loss buckets that map onto a card face:
//   * no-match 48.5 %  -> BLANK 22 cells: the stake is gone and nothing else happens. Before this
//                         change our only losses were damage and debuff, which made the deck read
//                         as far harsher than the reference: a blank is the polite majority.
//   * skulls 11.0 %    -> DAMAGE 10 cells (their total loss), with DBF carrying the rest.
//   * pair 30.3 %      -> item 21 + double 5 (a small win / the stake back and more).
// Sevens are 0.7 %; one cell in 64 is already 1.5625 %, so the jackpot is deliberately NOT
// modelled on their rarity: X2 is 5 cells / 7.8 % and that is a stated deviation, not an accident.
// --------------------------------------------------------------------------------------------
//
// Item cards are drawn over the usable pool items with the per-category weights compiled into
// ReelPool.g.cs. Runes and the X-selected lantern are NOT in the pool at all (weight 0, an
// Exclusion reason on the row, and ReelExclusions refuses them as the inserted item as well).
//
// Two things are deliberately separable here:
//   * `ReelTable`   - the numbers.
//   * `ReelDraw`    - ONE algorithm function, with four modes behind one call:
//                       Independent               DEFAULT. Roll the outcome class from the
//                                                 cumulative table, THEN fabricate a display that
//                                                 shows it - the reference's own mechanism.
//                       FixedMultisetReshuffled  a uniform cell of the fixed 64-card multiset
//                                                 (what every previous build did; kept for
//                                                 comparison, reachable with algorithm=fixed)
//                       BagWithoutReplacement     cards leave the bag as they are drawn; a new
//                                                 deck + bag is composed when it empties
//                       PityCounter                independent weighted draws whose item weight
//                                                 rises after a run of non-item pulls
//                     Switching modes touches no caller: LobbyProp asks ReelDraw.Next for a cell.
//
// This file is deliberately free of UnityEngine so the exact production deck can be compiled and
// sampled headlessly (tools/reel-dist).
//
// Why there is no bag and no pity timer: the reference has neither. LethalCasino keeps no state
// between spins at all (nothing is removed from a pool, no streak counter, no forced win), so a
// bag or a pity counter here would be a design of ours pretending to be theirs. Independent is the
// default for that reason; `algorithm=bag|pity` still exists only so the difference can be seen.
internal enum ReelCardType { Item = 0, Damage = 1, Debuff = 2, Double = 3, Blank = 4 }

// The real effects a debuff card can name. Each maps to a family of the game's own
// ImposedEffectVariantData (see ReelLogic.TryApplyDebuff); the card never says one thing and
// applies another - if a family has no loaded variant, that kind is not composed into the deck.
internal enum ReelDebuffKind { Slow = 0, Fatigue = 1, Blind = 2 }

internal struct ReelCard
{
    internal ReelCardType Type;
    internal int Item;      // index into ReelPool.All for Item cards, -1 otherwise
    internal ReelDebuffKind Debuff;   // meaning of a Debuff card
    internal bool HasDebuff;          // false for non-debuff cards
}

// One debuff card family: the 3-5 character code on the card face and the plain-words label that
// goes on the colour band and into the result message. Colours live in ReelArt (UnityEngine).
internal sealed class ReelDebuffInfo
{
    internal readonly ReelDebuffKind Kind;
    internal readonly string Code;    // card code, e.g. SLOW
    internal readonly string Label;   // band text / result line stem, e.g. "SLOW 50 %"
    internal readonly string Plain;   // result wording after the value, e.g. "Slowed 50 %"
    internal ReelDebuffInfo(ReelDebuffKind kind, string code, string label, string plain)
    {
        Kind = kind; Code = code; Label = label; Plain = plain;
    }
}

// xorshift32: deterministic for a given seed on every runtime, so a headless sample of this code
// is the same distribution the game draws.
internal struct ReelRng
{
    private uint _s;
    internal ReelRng(int seed)
    {
        _s = unchecked((uint)seed);
        if (_s == 0u) _s = 0x9E3779B9u;
    }
    internal uint NextUInt()
    {
        uint x = _s;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _s = x;
        return x;
    }
    internal int Next(int maxExclusive) => maxExclusive <= 1 ? 0 : (int)(NextUInt() % (uint)maxExclusive);
}

internal static class ReelTable
{
    // ---- THE NUMBERS (LethalCasino `FUN`, see the file header) --------------------------
    // These are cells of the 64-card deck AND the weights of the Independent roll: one number, so
    // the strip a player sees and the odds they are paid cannot drift apart. Sum = 64 = cells.
    internal const int WeightBlank = 22, WeightDamage = 10, WeightDebuff = 6;
    internal const int WeightItem = 21, WeightDouble = 5;

    // Which selection scheme is in force: the reference rolls the outcome first and then shows it.
    internal static ReelDrawMode Mode = ReelDrawMode.Independent;

    // Rolling order for Independent: the order of the design table. Boundaries only decide a
    // measure-zero exact hit, so this is arbitrary but fixed (the reference accumulates in the
    // order its six config keys were bound, which is equally arbitrary).
    internal static readonly ReelCardType[] RollOrder =
    {
        ReelCardType.Blank, ReelCardType.Damage, ReelCardType.Debuff,
        ReelCardType.Item, ReelCardType.Double,
    };

    // PityCounter knobs (only read when Mode == PityCounter). Provisional placeholders whose one
    // purpose is to make the scheme expressible without a redesign.
    internal const int PityAfterNonItem = 8;      // non-item pulls before the item weight starts rising
    internal const int PityItemBoostPercent = 40; // added to the item type weight per missed pull
    internal const int PityMaxBoostPercent = 200; // ceiling on that bonus

    internal static readonly ReelDebuffInfo[] Debuffs =
    {
        new ReelDebuffInfo(ReelDebuffKind.Slow,    "SLOW",  "SLOW 50 %",  "Slowed 50 %"),
        new ReelDebuffInfo(ReelDebuffKind.Fatigue, "FATIG", "FATIGUE",    "Tired: stamina drains faster"),
        new ReelDebuffInfo(ReelDebuffKind.Blind,   "BLIND", "BLIND",      "Blinded"),
    };

    internal static ReelDebuffInfo Debuff(ReelDebuffKind kind)
    {
        for (int i = 0; i < Debuffs.Length; i++) if (Debuffs[i].Kind == kind) return Debuffs[i];
        return Debuffs[0];
    }

    internal static int WeightOf(ReelCardType type)
    {
        switch (type)
        {
            case ReelCardType.Blank: return WeightBlank;
            case ReelCardType.Damage: return WeightDamage;
            case ReelCardType.Debuff: return WeightDebuff;
            case ReelCardType.Double: return WeightDouble;
            default: return WeightItem;
        }
    }

    internal static int TotalWeight =>
        WeightBlank + WeightDamage + WeightDebuff + WeightItem + WeightDouble;

    internal static double PercentOf(ReelCardType type) =>
        100.0 * WeightOf(type) / TotalWeight;

    internal static string Summary() =>
        $"weights=blank{WeightBlank}/damage{WeightDamage}/debuff{WeightDebuff}/item{WeightItem}/double{WeightDouble} " +
        $"cells={TotalWeight} win={(WeightItem + WeightDouble) * 100.0 / TotalWeight:F1}% " +
        $"loss={(WeightBlank + WeightDamage + WeightDebuff) * 100.0 / TotalWeight:F1}% mode={Mode}";
}

internal enum ReelDrawMode
{
    FixedMultisetReshuffled = 0,
    BagWithoutReplacement = 1,
    PityCounter = 2,
    Independent = 3,          // default: class first, display second (the reference's mechanism)
}

// Everything a selection scheme needs to remember between pulls. Lives with the prop runtime; a
// new prop generation (or a hot reload) starts a fresh state, which the log says out loud.
internal sealed class ReelDrawState
{
    internal ReelDrawMode Mode = ReelTable.Mode;
    internal readonly List<int> Bag = new List<int>();
    internal int Pulls, SinceItem, SinceDebuff;

    internal void Reset(ReelDrawMode mode)
    {
        Mode = mode;
        Bag.Clear();
        Pulls = SinceItem = SinceDebuff = 0;
    }
}

internal struct ReelDrawResult
{
    internal int Cell;
    internal ReelCardType Type;
    internal string Why;     // one-line reason for the [reel] spin log
}

// ONE algorithm function. A caller that only knows `ReelDraw.Next(...)` is already compatible with
// bag-without-replacement and pity-counter schemes; the numbers live in ReelTable.
internal static class ReelDraw
{
    internal static ReelDrawResult Next(ReelDrawState st, ReelCard[] cards, int seed)
    {
        if (st == null) st = new ReelDrawState();
        st.Pulls++;
        ReelDrawResult d;
        switch (st.Mode)
        {
            case ReelDrawMode.Independent:
                d = RollThenDisplay(cards, seed);
                break;
            case ReelDrawMode.BagWithoutReplacement:
                d = FromBag(st, cards, seed);
                break;
            case ReelDrawMode.PityCounter:
                d = WithPity(st, cards, seed);
                break;
            default:
                d = new ReelDrawResult
                {
                    Cell = new ReelRng(unchecked(seed * 31 + st.Pulls)).Next(cards.Length),
                    Why = "uniform cell of the fixed multiset",
                };
                break;
        }
        d.Type = cards[d.Cell].Type;
        if (d.Type == ReelCardType.Item) st.SinceItem = 0; else st.SinceItem++;
        if (d.Type == ReelCardType.Debuff) st.SinceDebuff = 0; else st.SinceDebuff++;
        return d;
    }

    // The reference's mechanism, verbatim: ONE roll against the cumulative class table decides the
    // outcome, and only then is a display fabricated that shows it. It never spins a strip and tests
    // the strip for matches, so the landing cell carries no information - any cell of the rolled
    // type will do, and one is picked uniformly. No bag, no pity, no memory of previous pulls.
    //
    // The card under the marker is still the outcome (that rule is not negotiable): the cell is
    // chosen from cells carrying the rolled type, and `Next` re-reads the type from the card, so a
    // strip that somehow lacked that type would surface as the card it actually landed on.
    private static ReelDrawResult RollThenDisplay(ReelCard[] cards, int seed)
    {
        var rng = new ReelRng(unchecked(seed * 40503 + 7919));

        long total = 0;
        for (int i = 0; i < ReelTable.RollOrder.Length; i++)
        {
            var type = ReelTable.RollOrder[i];
            // A class with no cell on the strip cannot be displayed, so it is not rolled either.
            if (ReelDeck.CellOfType(cards, type, 0) < 0) continue;
            total += ReelTable.WeightOf(type);
        }
        if (total <= 0)
            return new ReelDrawResult { Cell = 0, Why = "empty table -> cell 0" };

        long roll = (long)((ulong)rng.NextUInt() % (ulong)total);
        ReelCardType chosen = ReelCardType.Blank;
        for (int i = 0; i < ReelTable.RollOrder.Length; i++)
        {
            var type = ReelTable.RollOrder[i];
            if (ReelDeck.CellOfType(cards, type, 0) < 0) continue;
            long w = ReelTable.WeightOf(type);
            if (roll < w) { chosen = type; break; }
            roll -= w;
        }

        int cell = ReelDeck.CellOfType(cards, chosen, unchecked(seed ^ 0x5F3759DF));
        if (cell < 0) cell = 0;
        return new ReelDrawResult
        {
            Cell = cell,
            Why = $"rolled type={ReelDeck.TypeName(chosen)} p={ReelTable.PercentOf(chosen):F1}% then displayed cell {cell}",
        };
    }

    private static ReelDrawResult FromBag(ReelDrawState st, ReelCard[] cards, int seed)
    {
        if (st.Bag.Count == 0)
        {
            // New bag: every cell once. The caller reshuffles the strip at spin start, so a bag
            // that repeats a cell across bags is not memorisable.
            for (int i = 0; i < cards.Length; i++) st.Bag.Add(i);
            var rng = new ReelRng(unchecked(seed * 7919 + st.Pulls));
            for (int i = st.Bag.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int t = st.Bag[i]; st.Bag[i] = st.Bag[j]; st.Bag[j] = t;
            }
        }
        int last = st.Bag.Count - 1;
        int cell = st.Bag[last];
        st.Bag.RemoveAt(last);
        return new ReelDrawResult { Cell = cell, Why = $"bag pop ({st.Bag.Count} left in bag)" };
    }

    private static ReelDrawResult WithPity(ReelDrawState st, ReelCard[] cards, int seed)
    {
        long[] w = new long[cards.Length];
        long total = 0;
        int boost = st.SinceItem >= ReelTable.PityAfterNonItem
            ? Math.Min(ReelTable.PityMaxBoostPercent,
                       ReelTable.PityItemBoostPercent * (st.SinceItem - ReelTable.PityAfterNonItem + 1))
            : 0;
        for (int i = 0; i < cards.Length; i++)
        {
            long v = 100;
            if (cards[i].Type == ReelCardType.Item) v += boost;
            w[i] = v;
            total += v;
        }
        var rng = new ReelRng(unchecked(seed * 104729 + st.Pulls));
        long roll = (long)((ulong)rng.NextUInt() % (ulong)total);
        for (int i = 0; i < cards.Length; i++)
        {
            if (roll < w[i])
                return new ReelDrawResult { Cell = i, Why = $"pity weighted draw (misses={st.SinceItem} boost={boost}%)" };
            roll -= w[i];
        }
        return new ReelDrawResult { Cell = cards.Length - 1, Why = "pity fallback last cell" };
    }
}

// Never bet, never won. Runes and crucifixes are quest items; the lantern is the X-selected special
// light. All are out of the card pool (ReelPool.g.cs weight 0) and refused as inserted items.
internal static class ReelExclusions
{
    internal static string ReasonFor(string id, string name)
    {
        string i = (id ?? "").ToLowerInvariant();
        string n = (name ?? "").ToLowerInvariant();

        if (i == "lantern" || n.Contains("lantern")) return "quest-lantern";
        if (i.Contains("rune") || n.Contains("rune") || n.Contains("curse dispelling")) return "quest-rune";
        if (i.Contains("crucifix") || n.Contains("crucifix") || n.Contains("сrucifix")) return "quest-crucifix";

        int idx = ReelPool.IndexOf(id ?? "");
        if (idx >= 0 && rowIsExcluded(idx)) return ReelPool.All[idx].Exclusion.Split(':')[0];
        return "";
    }

    private static bool rowIsExcluded(int idx) =>
        idx >= 0 && idx < ReelPool.All.Length && !string.IsNullOrEmpty(ReelPool.All[idx].Exclusion);

    internal static bool IsRefused(string id, string name) => ReasonFor(id, name).Length > 0;

    // The rows the generated table refuses, for the [reel] census log.
    internal static string TableSummary()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ReelPool.All.Length; i++)
        {
            var it = ReelPool.All[i];
            if (string.IsNullOrEmpty(it.Exclusion)) continue;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(it.Id).Append(" id='").Append(it.Id).Append("' name='").Append(it.Name)
              .Append("' reason=").Append(it.Exclusion);
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }
}

internal static class ReelDeck
{
    // 64 cards: the smallest strip that represents the table to within one card of each weight,
    // and 64 x 128 px = 8192 px of texture - inside the maximum texture width of every GPU this
    // build runs on.
    internal const int Cells = 64;

    // Kept as the names ReelLogic/LobbyProp log with.
    internal const int WeightItem = ReelTable.WeightItem, WeightDamage = ReelTable.WeightDamage;
    internal const int WeightDebuff = ReelTable.WeightDebuff, WeightDouble = ReelTable.WeightDouble;
    internal const int WeightBlank = ReelTable.WeightBlank;

    // Bitmask of ReelDebuffKind values that have a loaded variant in this scene. Set once by
    // ReelLogic after its effect scan; a deck composed while the mask is 0 keeps all three kinds
    // (the resolve step re-checks and refuses rather than pretending).
    internal static int DebuffMask = 7;

    // Indices into ReelPool.All that an item card may name. null = the whole usable pool, which is
    // what the lobby and the headless sampler (tools/reel-dist, which never sets it) get. In a map
    // ReelEligible replaces it with the ids that live spawn table can actually place, so the deck's
    // item cards cannot name an item this map cannot spawn. Type weights are untouched: whatever
    // the ceiling, 21 of the 64 cells are item cards.
    internal static int[] AllowedItems;

    internal static int CountKinds(int mask)
    {
        int n = 0;
        for (int k = 0; k < ReelTable.Debuffs.Length; k++) if ((mask & (1 << k)) != 0) n++;
        return n;
    }

    internal static ReelCard[] Compose(int cells, int seed) => Compose(cells, seed, DebuffMask);

    internal static ReelCard[] Compose(int cells, int seed, int debuffMask)
    {
        var rng = new ReelRng(seed);
        var cards = new ReelCard[cells];

        // Indexed by ReelCardType, so the largest-remainder pass below can write straight back into
        // the per-type counts. Sum = 64 at the shipped table, which makes the quantisation exact.
        int[] w =
        {
            ReelTable.WeightItem, ReelTable.WeightDamage, ReelTable.WeightDebuff,
            ReelTable.WeightDouble, ReelTable.WeightBlank,
        };
        int sum = 0;
        for (int i = 0; i < w.Length; i++) sum += w[i];

        var n = new int[w.Length];
        var rem = new long[w.Length];
        int used = 0;
        for (int i = 0; i < w.Length; i++)
        {
            long exact = (long)cells * w[i];
            n[i] = (int)(exact / sum);
            rem[i] = exact % sum;
            used += n[i];
        }
        // Largest remainder: the leftover cards go to the weights with the biggest fractions.
        while (used < cells)
        {
            int best = 0;
            for (int i = 1; i < w.Length; i++) if (rem[i] > rem[best]) best = i;
            n[best]++; rem[best] = -1; used++;
        }

        int at = 0;
        for (int i = 0; i < n[(int)ReelCardType.Blank]; i++)
            cards[at++] = new ReelCard { Type = ReelCardType.Blank, Item = -1 };
        for (int i = 0; i < n[(int)ReelCardType.Damage]; i++)
            cards[at++] = new ReelCard { Type = ReelCardType.Damage, Item = -1 };
        var kinds = ActiveKinds(debuffMask);
        for (int i = 0; i < n[(int)ReelCardType.Debuff]; i++)
            cards[at++] = new ReelCard
            {
                Type = ReelCardType.Debuff,
                Item = -1,
                Debuff = kinds[i % kinds.Length],
                HasDebuff = true,
            };
        for (int i = 0; i < n[(int)ReelCardType.Double]; i++)
            cards[at++] = new ReelCard { Type = ReelCardType.Double, Item = -1 };
        int itemTotal = AllowedWeight();
        for (int i = 0; i < n[(int)ReelCardType.Item]; i++)
        {
            int pick = itemTotal > 0 ? WeightedItem(rng.Next(itemTotal)) : -1;
            cards[at++] = new ReelCard { Type = ReelCardType.Item, Item = pick };
        }
        Shuffle(cards, unchecked(seed ^ 0x5F3759DF));
        return cards;
    }

    // Debuff kinds that can be composed: the scanned mask, or all of them when nothing was scanned
    // yet (the lobby preview deck; a map run recomposes once the scan lands).
    internal static ReelDebuffKind[] ActiveKinds(int mask)
    {
        var outKinds = new List<ReelDebuffKind>();
        for (int k = 0; k < ReelTable.Debuffs.Length; k++)
            if ((mask & (1 << k)) != 0) outKinds.Add(ReelTable.Debuffs[k].Kind);
        if (outKinds.Count == 0) outKinds.Add(ReelTable.Debuffs[0].Kind);
        return outKinds.ToArray();
    }

    // The item table is ordered by id; walk it once per draw accumulating the per-item weight.
    // Runes and the lantern carry weight 0 in the generated table, so they can never be drawn.
    // When AllowedItems is set (a map run) the walk visits only those rows, in pool order, so the
    // relative weights inside the allowed set are exactly the generated category weights.
    private static int AllowedWeight()
    {
        var allowed = AllowedItems;
        if (allowed == null || allowed.Length == 0) return ReelPool.TotalWeight();
        int total = 0;
        for (int k = 0; k < allowed.Length; k++)
        {
            int i = allowed[k];
            if (i < 0 || i >= ReelPool.All.Length) continue;
            if (ReelPool.All[i].Weight > 0) total += ReelPool.All[i].Weight;
        }
        return total;
    }

    private static int WeightedItem(int roll)
    {
        var allowed = AllowedItems;
        if (allowed == null || allowed.Length == 0)
        {
            var all = ReelPool.All;
            for (int i = 0; i < all.Length; i++)
            {
                int wt = all[i].Weight;
                if (wt <= 0) continue;
                if (roll < wt) return i;
                roll -= wt;
            }
            for (int i = all.Length - 1; i >= 0; i--) if (all[i].Weight > 0) return i;
            return -1;
        }
        for (int k = 0; k < allowed.Length; k++)
        {
            int i = allowed[k];
            if (i < 0 || i >= ReelPool.All.Length) continue;
            int wt = ReelPool.All[i].Weight;
            if (wt <= 0) continue;
            if (roll < wt) return i;
            roll -= wt;
        }
        for (int k = allowed.Length - 1; k >= 0; k--)
        {
            int i = allowed[k];
            if (i >= 0 && i < ReelPool.All.Length && ReelPool.All[i].Weight > 0) return i;
        }
        return -1;
    }

    // Fisher-Yates. Called at every spin start, so the landing position cannot be memorised and a
    // card's neighbours are different each time.
    internal static void Shuffle(ReelCard[] cards, int seed)
    {
        var rng = new ReelRng(seed);
        for (int i = cards.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            var t = cards[i]; cards[i] = cards[j]; cards[j] = t;
        }
    }

    // A random cell carrying `type`, for the reel.request test hook. Landing on an existing card of
    // that type is the same code path a natural spin takes - nothing is injected or bypassed.
    internal static int CellOfType(ReelCard[] cards, ReelCardType type, int seed) =>
        CellOfType(cards, type, -1, seed);

    internal static int CellOfType(ReelCard[] cards, ReelCardType type, int debuffKind, int seed)
    {
        int count = 0;
        for (int i = 0; i < cards.Length; i++)
            if (cards[i].Type == type && (debuffKind < 0 || (int)cards[i].Debuff == debuffKind)) count++;
        if (count == 0) return -1;
        var rng = new ReelRng(unchecked(seed * 31 + 17));
        int pick = rng.Next(count);
        for (int i = 0; i < cards.Length; i++)
        {
            if (cards[i].Type != type) continue;
            if (debuffKind >= 0 && (int)cards[i].Debuff != debuffKind) continue;
            if (pick-- == 0) return i;
        }
        return -1;
    }

    internal static int[] TypeCounts(ReelCard[] cards)
    {
        var n = new int[5];
        for (int i = 0; i < cards.Length; i++) n[(int)cards[i].Type]++;
        return n;
    }

    internal static string TypeName(ReelCardType t)
    {
        switch (t)
        {
            case ReelCardType.Item: return "item";
            case ReelCardType.Damage: return "damage";
            case ReelCardType.Debuff: return "debuff";
            case ReelCardType.Blank: return "blank";
            default: return "double";
        }
    }

    // Short code drawn on every card, and the short tag used in logs. A debuff card shows the
    // effect it applies (SLOW/FATIG/BLIND), never a generic tag. The blank card is deliberately the
    // plainest face in the deck: a dash, drawn from the hyphen the 3x5 card font actually has (the
    // font has no em dash, and an unknown glyph would draw a hollow box instead).
    // It is doubled so the dash reads at strip scale; see ReelArt.DrawCard.
    internal const string BlankCode = "--";

    internal static string Code(ReelCard c)
    {
        switch (c.Type)
        {
            case ReelCardType.Item:
                return c.Item >= 0 && c.Item < ReelPool.All.Length ? ReelPool.All[c.Item].Code : "---";
            case ReelCardType.Damage: return "DMG";
            case ReelCardType.Debuff: return ReelTable.Debuff(c.Debuff).Code;
            case ReelCardType.Blank: return BlankCode;
            default: return "X2";
        }
    }

    // The plain-words line for the card face / the result message. Empty for non-debuff cards.
    internal static string Label(ReelCard c) =>
        c.Type == ReelCardType.Debuff ? ReelTable.Debuff(c.Debuff).Label : "";

    internal static string DebuffName(ReelCard c) =>
        c.Type == ReelCardType.Debuff ? ReelTable.Debuff(c.Debuff).Code : "";

    internal static string IdOf(ReelCard c) =>
        c.Type == ReelCardType.Item && c.Item >= 0 && c.Item < ReelPool.All.Length ? ReelPool.All[c.Item].Id : "";

    internal static string NameOf(ReelCard c) =>
        c.Type == ReelCardType.Item && c.Item >= 0 && c.Item < ReelPool.All.Length ? ReelPool.All[c.Item].Name : "";

    // One-line census for the [reel] strip log, including the debuff split. `cells=` leads so the
    // composition total and the weights can be checked against each other in one glance.
    internal static string Census(ReelCard[] cards)
    {
        var n = TypeCounts(cards);
        var sb = new StringBuilder();
        sb.Append($"cells={cards.Length} blank={n[(int)ReelCardType.Blank]} ");
        sb.Append($"item={n[(int)ReelCardType.Item]} damage={n[(int)ReelCardType.Damage]} ");
        sb.Append($"debuff={n[(int)ReelCardType.Debuff]} double={n[(int)ReelCardType.Double]}");
        if (n[(int)ReelCardType.Debuff] > 0)
        {
            sb.Append(" debuffs=");
            bool first = true;
            for (int k = 0; k < ReelTable.Debuffs.Length; k++)
            {
                int c = 0;
                for (int i = 0; i < cards.Length; i++)
                    if (cards[i].Type == ReelCardType.Debuff && (int)cards[i].Debuff == k) c++;
                if (c == 0) continue;
                if (!first) sb.Append('/');
                sb.Append(ReelTable.Debuffs[k].Code).Append(':').Append(c);
                first = false;
            }
        }
        return sb.ToString();
    }
}
