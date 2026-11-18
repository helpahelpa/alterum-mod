using System;
using AlterumModelSwap.Hot;

class Resource { internal int Destroyed; }
static class Program
{
    static int _cases;
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static ModelAssetCache<Resource> Cache(int limit = 2) => new(limit, r => r.Destroyed++);
    static void Run(string name, Action test) { test(); _cases++; Console.WriteLine("PASS " + name); }
    static void Main()
    {
        Run("B_switches_twice_A_materials_survive", () => {
            using var cache = Cache();
            using var a = cache.Acquire("soldier", () => new Resource());
            var resource = a.Value;
            using (var b = cache.Acquire("peter", () => new Resource())) { }
            using var b2 = cache.Acquire("saxton", () => new Resource());
            Check(resource.Destroyed == 0, "B's selection destroyed A's material");
        });
        Run("all_live_models_exceed_soft_limit", () => {
            using var cache = Cache(1);
            using var a = cache.Acquire("A", () => new Resource());
            using var b = cache.Acquire("B", () => new Resource());
            using var preview = cache.Acquire("preview", () => new Resource());
            Check(cache.Count == 3 && a.Value.Destroyed == 0 && b.Value.Destroyed == 0, "live assets evicted");
        });
        Run("same_slug_local_remote_preview_independent_release", () => {
            using var cache = Cache(0);
            var local = cache.Acquire("same", () => new Resource()); var r = local.Value;
            var remote = cache.Acquire("same", () => throw new Exception("duplicate load"));
            var preview = remote.Retain();
            local.Dispose(); remote.Dispose();
            Check(r.Destroyed == 0, "preview lost its shared resources");
            preview.Dispose(); preview.Dispose();
            Check(r.Destroyed == 1, "resource not destroyed exactly once");
        });
        Run("external_build_cannot_evict_saved_local_snapshot", () => {
            using var cache = Cache(1);
            using var saved = cache.Acquire("local", () => new Resource());
            using var external = cache.Acquire("remote", () => new Resource());
            Check(saved.Value.Destroyed == 0, "temporarily swapped-out local asset evicted");
        });
        Run("failed_build_releases_borrow_without_dropping_live_peer", () => {
            using var cache = Cache(1);
            using var a = cache.Acquire("A", () => new Resource()); Resource failed = null;
            try { using var build = cache.Acquire("B", () => failed = new Resource()); throw new Exception("injected"); }
            catch { }
            Check(a.Value.Destroyed == 0 && failed.Destroyed == 1, "failure cleanup leaked or destroyed peer");
            Check(cache.Acquire("missing", () => null) == null && cache.Count == 1, "failed load was cached");
        });
        Run("retired_generation_waits_for_last_preview", () => {
            var cache = Cache(); var preview = cache.Acquire("old", () => new Resource()); var r = preview.Value;
            cache.Dispose(); cache.Dispose(); Check(r.Destroyed == 0, "shutdown destroyed active preview");
            preview.Dispose(); Check(r.Destroyed == 1 && cache.Count == 0, "last release did not destroy");
            bool threw = false; try { cache.Acquire("new", () => new Resource()); } catch (ObjectDisposedException) { threw = true; }
            Check(threw, "retired cache accepted new asset");
        });
        Run("LRU_only_evicts_idle_resources", () => {
            using var cache = Cache(); Resource old, recent;
            using (var a = cache.Acquire("old", () => new Resource())) old = a.Value;
            using (var a = cache.Acquire("recent", () => new Resource())) recent = a.Value;
            using var newest = cache.Acquire("new", () => new Resource());
            Check(old.Destroyed == 1 && recent.Destroyed == 0, "wrong idle victim");
        });
        Run("stress_churn_keeps_four_peers_and_reclaims_after_leave", () => {
            using var cache = Cache();
            var peers = new ModelAssetCache<Resource>.Lease[4]; var resources = new Resource[4];
            for (int i = 0; i < 4; i++) { peers[i] = cache.Acquire("peer"+i, () => new Resource()); resources[i] = peers[i].Value; }
            for (int n = 0; n < 1000; n++) {
                using var local = cache.Acquire("local"+n, () => new Resource());
                foreach (var r in resources) Check(r.Destroyed == 0, "churn destroyed peer");
            }
            foreach (var p in peers) p.Dispose();
            Check(cache.Count <= 2, "idle cache did not shrink after leave");
            cache.Dispose(); foreach (var r in resources) Check(r.Destroyed == 1, "peer resource leaked/double destroyed");
        });
        Console.WriteLine($"ASSET LIFETIME OK ({_cases} cases)");
    }
}
