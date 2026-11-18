using System;
using System.Text;

namespace AlterumModelSwap.Hot;

// The emote catalogue for this build: exactly two Lethal Company ports, in this fixed order.
//
//   Twerk   Lethal Company port, 0.250 s, looping, 16 frames
//   Salute  Lethal Company port, 1.783 s, non-looping, 108 frames
//
// Both are `clip` entries of the shipped AlterumEmotes pack, so neither id carries the
// `native:` prefix: no clip of the game's own animancer graph is offered at all.
//
// This class is the single allowlist. Every path that can name, enumerate, start, publish or
// accept an emote resolves its id through it, so a clip that is still inside the shipped
// manifest/blob is nonetheless unreachable:
//
//   EmotePack.ByName/IndexOf      the only clip lookup the hot module has
//   EmoteRuntime.PickerEntries    the picker's catalogue (this order)
//   EmoteRuntime.TotalEmotes/IdAt the index space of F9, the picker and the request file
//   EmoteRuntime.TryIndexOfEmote  request-file ids
//   PosePlayer.Start/StartNative  the two playback entry points
//   RemoteModels.PublishLocalEmote / the MsgEmote receive path
//   RemoteEmotes.Start/Play       incoming start ids
//
// The pack is NOT repacked: the full manifest and float blob ship as before, offsets and all,
// and the removed clips are simply never exposed (see EmotePack.TryLoad).
internal static class EmoteCatalog
{
    // Declaration order IS catalogue order: the picker row, the F9 cycle and IdAt/IndexOf all use
    // it. Never derive it from the pack (the pack still carries the removed clips).
    private static readonly string[] Order = { "Twerk", "Salute" };

    // Native (game) emotes offered by this build. Zero, by design.
    internal const int NativeCount = 0;

    // The emote selected when nothing has been persisted yet, and what a saved id falls back to when
    // this build no longer offers it: the first catalogue entry (Twerk), resolved BY ID. Only a
    // selection is restored - restoring never starts playback.
    internal static string DefaultSelectionId => Order[0];

    internal static int Count => Order.Length;

    // A copy, so a caller cannot edit the allowlist through the returned array.
    internal static string[] Ids
    {
        get
        {
            var copy = new string[Order.Length];
            Array.Copy(Order, copy, Order.Length);
            return copy;
        }
    }

    // "Twerk,Salute" / "TWERK,SALUTE" - for log lines and the picker header, so every one of them
    // states the whole catalogue. Cached: the picker redraws share one string.
    private static readonly string ListText = string.Join(",", Order);
    private static readonly string UpperListText = ListText.ToUpperInvariant();

    internal static string List() => ListText;

    internal static string UpperList() => UpperListText;

    internal static bool Contains(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        for (int i = 0; i < Order.Length; i++)
            if (string.Equals(Order[i], id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The allowlist's own spelling for an id (a peer may send "twerk"), or null when the id is not
    // in the catalogue. Callers play and publish the canonical spelling, never the received one.
    internal static string Canonical(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < Order.Length; i++)
            if (string.Equals(Order[i], id, StringComparison.OrdinalIgnoreCase)) return Order[i];
        return null;
    }

    // Index in catalogue order, or -1. -1 is the only "not found": index 0 is a real clip (Twerk)
    // and must never be returned as a fallback.
    internal static int IndexOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; i < Order.Length; i++)
            if (string.Equals(Order[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    internal static string IdAt(int index) =>
        index >= 0 && index < Order.Length ? Order[index] : null;

    // Selection restore: by id, and a miss is the first catalogue entry rather than a substitution
    // of some other clip. `reset` reports the repair so the caller can log it; nothing is played.
    internal static int ResolveSelection(EmotePack pack, string savedId, out bool reset)
    {
        reset = false;
        int index = pack == null ? -1 : pack.IndexOf(savedId);
        if (index >= 0) return index;
        reset = true;
        return pack == null || pack.Clips.Count == 0 ? -1 : 0;
    }

    // One line that states the whole catalogue: the entry points that log a count use this.
    internal static string Describe(EmotePack pack)
    {
        var sb = new StringBuilder();
        sb.Append("ids=[");
        if (pack != null)
        {
            for (int i = 0; i < pack.Clips.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pack.Clips[i].Name);
            }
        }
        sb.Append("] count=").Append(pack == null ? 0 : pack.Clips.Count)
          .Append(" native=").Append(NativeCount)
          .Append(" allowlist=[")
          .Append(List())
          .Append(']');
        return sb.ToString();
    }
}
