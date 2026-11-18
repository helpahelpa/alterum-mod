using System;
using System.Collections.Generic;

namespace AlterumModelSwap.Hot;

// Shared model resources belong to their consumers, NOT to the most recent selection. Unity
// renderers do not keep a Material alive after Object.Destroy, even with sharedMaterials assigned.
// The cache limit is soft while bodies are alive: no local/remote/preview role needs special cases.
// Main-thread only. Each successful body owns one Lease until its renderer is disabled/destroyed.
//
// The bottom of this file is the read-only diagnostics surface (`alterum.test.assets` /
// `alterum.test.assert-assets` in GameConsole): a snapshot copies this bookkeeping, and a registry
// of the caches alive in THIS hot generation lets the console reach them at all. Neither can mutate
// a cache, and nothing in the cache's behaviour depends on either.
internal sealed class ModelAssetCache<T> : ModelAssetCacheDiagnostics, IDisposable where T : class
{
    internal sealed class Entry
    {
        internal string Key;
        internal T Value;
        internal int Users;
        internal long Used;
    }

    internal sealed class Lease : IDisposable
    {
        private ModelAssetCache<T> _owner;
        private Entry _entry;
        internal Lease(ModelAssetCache<T> owner, Entry entry) { _owner = owner; _entry = entry; entry.Users++; }
        internal T Value => _entry?.Value ?? throw new ObjectDisposedException(nameof(Lease));
        internal Lease Retain()
        {
            if (_owner == null) throw new ObjectDisposedException(nameof(Lease));
            return new Lease(_owner, _entry);
        }
        public void Dispose()
        {
            var owner = _owner;
            var entry = _entry;
            _owner = null; _entry = null;
            if (owner == null) return;
            entry.Users--;
            entry.Used = ++owner._clock;
            owner.Trim();
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _limit;
    private readonly Action<T> _destroy;
    private long _clock;
    private bool _retired;

    internal ModelAssetCache(int limit, Action<T> destroy)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
        _destroy = destroy ?? throw new ArgumentNullException(nameof(destroy));
        // Registered (weakly) so a read-only diagnostic can find the caches of this generation. An
        // EMPTY cache is a fact too, and a retired one must stay visible while a lease still holds it.
        _instance = ModelAssetCacheRegistry.Register(this);
    }

    internal int Count => _entries.Count;
    internal IEnumerable<T> Values { get { foreach (var e in _entries.Values) yield return e.Value; } }

    // ---- read-only diagnostics (see the section at the bottom of this file) ------------------
    private readonly int _instance;

    internal override int Instance => _instance;
    internal override string Owner => OwnerName;
    internal override string ValueType => OwnerName + "." + typeof(T).Name;

    // The value type names its owner: both caches are keyed on a private nested `Asset`
    // (SahurRig.Asset / SahurModel.Asset), and a cache never needs to know more than that.
    private static string OwnerName
        => typeof(T).DeclaringType != null ? typeof(T).DeclaringType.Name : typeof(T).Name;

    // A copy, taken with no side effects: no touch stamp is written, no Trim runs, no value is
    // loaded or destroyed. Values are handed out by reference for the caller to inspect.
    internal override ModelAssetCacheSnapshot Capture()
    {
        var s = new ModelAssetCacheSnapshot
        {
            Instance = _instance, Owner = Owner, ValueType = ValueType,
            Limit = _limit, Clock = _clock, Retired = _retired,
        };
        foreach (var e in _entries.Values)
        {
            s.Entries.Add(new ModelAssetCacheEntrySnapshot
            {
                Key = e.Key ?? "", Value = e.Value, Users = e.Users, Used = e.Used, Age = _clock - e.Used,
            });
            s.Users += e.Users;
            if (e.Users > 0) s.Leased++; else s.Idle++;
        }
        s.Count = s.Entries.Count;
        // Most recently touched first, key as the tie-break: the printed order must not depend on the
        // dictionary's internal order, or two identical reads could disagree.
        s.Entries.Sort((a, b) => a.Used != b.Used
            ? b.Used.CompareTo(a.Used)
            : string.CompareOrdinal(a.Key, b.Key));
        return s;
    }

    internal Lease Acquire(string key, Func<T> load)
    {
        if (_retired) throw new ObjectDisposedException(nameof(ModelAssetCache<T>));
        if (!_entries.TryGetValue(key, out var entry))
        {
            var value = load();
            if (value == null) return null; // failed load never occupies a cache entry
            entry = new Entry { Key = key, Value = value };
            _entries.Add(key, entry);
        }
        entry.Used = ++_clock;
        var lease = new Lease(this, entry); // pin BEFORE trimming, including the newly loaded asset
        Trim();
        return lease;
    }

    private void Trim()
    {
        while (_entries.Count > (_retired ? 0 : _limit))
        {
            Entry victim = null;
            foreach (var e in _entries.Values)
                if (e.Users == 0 && (victim == null || e.Used < victim.Used)) victim = e;
            if (victim == null) return; // every remaining asset is in use; never destroy a live one
            _entries.Remove(victim.Key);
            _destroy(victim.Value);
        }
    }

    // Hot teardown may retire the cache before a preview detaches. Its resources survive until
    // that last lease releases; a late/double release cannot destroy another generation's assets.
    public void Dispose() { _retired = true; Trim(); }
}

// ==================================================================== read-only diagnostics
//
// `alterum.test.assets` / `alterum.test.assert-assets` (GameConsole) answer one question about the
// cache above: does every lease pin a live asset, and does the cache only ever drop what no body
// holds? Nothing here mutates a cache - a snapshot COPIES the bookkeeping fields and hands each
// value out by reference so the caller can inspect the Unity objects the value is made of. No entry
// is acquired, released, loaded, destroyed or trimmed from this section.
//
// The registry is what makes a cache reachable from the console at all: the caches are private
// statics of their modules, and this assembly is loaded once per hot generation (HotLoader uses a
// fresh collectible AssemblyLoadContext), so the registry sees exactly the caches of the generation
// that is RUNNING. Its references are weak: it can never keep a cache - or its assets - alive, and a
// previous generation's copy of this class lives in its own context with its own registry.

internal abstract class ModelAssetCacheDiagnostics
{
    internal abstract int Instance { get; }               // stable id of one cache in this generation
    internal abstract string Owner { get; }               // module that owns the value type ("SahurRig")
    internal abstract string ValueType { get; }           // "SahurRig.Asset"
    internal abstract ModelAssetCacheSnapshot Capture();  // a copy; never a live view
}

// One entry as the diagnostic reads it. `Value` is the entry's own object (never a copy), so the
// caller can walk its Unity references to answer "is this asset still alive".
internal sealed class ModelAssetCacheEntrySnapshot
{
    internal string Key = "";
    internal object Value;
    internal int Users;   // 0 = idle (evictable), >0 = leased by that many bodies
    internal long Used;   // the cache's own clock stamp; larger = touched more recently
    internal long Age;    // clock - Used at capture time
}

internal sealed class ModelAssetCacheSnapshot
{
    internal int Instance;
    internal string Owner = "?";
    internal string ValueType = "?";
    internal int Limit, Count, Leased, Idle, Users;
    internal long Clock;
    internal bool Retired;  // Dispose() ran: the cache now holds only leases that had not released
    internal readonly List<ModelAssetCacheEntrySnapshot> Entries = new(4);
}

internal static class ModelAssetCacheRegistry
{
    // Bounded by construction: one cache per module, created once per hot generation. Dead entries
    // are pruned on every read, so a cache that is collected never lingers here.
    private static readonly List<WeakReference<ModelAssetCacheDiagnostics>> _caches = new(4);
    private static int _instances;

    internal static int Register(ModelAssetCacheDiagnostics cache)
    {
        Prune();
        int id = ++_instances;
        _caches.Add(new WeakReference<ModelAssetCacheDiagnostics>(cache));
        return id;
    }

    internal static List<ModelAssetCacheDiagnostics> Live()
    {
        Prune();
        var live = new List<ModelAssetCacheDiagnostics>(_caches.Count);
        for (int i = 0; i < _caches.Count; i++)
            if (_caches[i].TryGetTarget(out var cache) && cache != null) live.Add(cache);
        return live;
    }

    private static void Prune()
    {
        for (int i = _caches.Count - 1; i >= 0; i--)
            if (!_caches[i].TryGetTarget(out var cache) || cache == null) _caches.RemoveAt(i);
    }
}
