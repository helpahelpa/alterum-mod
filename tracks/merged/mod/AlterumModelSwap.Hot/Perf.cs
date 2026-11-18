using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using AlterumModelSwap.Contracts;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace AlterumModelSwap.Hot;

// Temporary diagnosis instrumentation: per-subsystem wall-clock cost for one frame, plus a count
// of the engine's own Error/Exception/Assert log entries per frame. Logs a breakdown every slow
// frame and an aggregate every 30 frames otherwise. Wall clock (Stopwatch), not Time.unscaledTime:
// a stalled frame clamps the engine clock, which is exactly what this has to measure.
internal static class Perf
{
    internal const double SlowMs = 120.0;
    private const int WindowFrames = 30;

    private static readonly Dictionary<string, double> _ms = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> _n = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, double> _winMs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> _winN = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> _errs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> _errsAll = new(StringComparer.Ordinal);

    private static long _frameTs;
    private static long _prevFrameTs;
    private static double _prevWallMs;
    private static double _winCostMs;
    private static int _winFrames;
    private static int _frames;
    private static bool _hooked;
    private static Application.LogCallback _cb;
    // Off by default: this is a diagnosis aid, not a shipping feature. Set true (or re-enable from
    // a request) to get the [perf] frame breakdown. SceneCache keeps working either way.
    private static bool _enabled = false;
    private static string _tag = "update";

    internal static bool Enabled { get => _enabled; set => _enabled = value; }

    internal static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        try { _cb = DelegateSupport.ConvertDelegate<Application.LogCallback>(
            new Action<string, string, LogType>(OnLog));
            Application.add_logMessageReceived(_cb); } catch { }
    }

    internal static void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        try { if (_cb != null) Application.remove_logMessageReceived(_cb); _cb = null; } catch { }
    }

    private static void OnLog(string msg, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        string key = msg ?? "";
        int nl = key.IndexOf('\n');
        if (nl > 0) key = key.Substring(0, nl);
        if (key.Length > 150) key = key.Substring(0, 150);
        lock (_errs)
        {
            _errs.TryGetValue(key, out int c); _errs[key] = c + 1;
            _errsAll.TryGetValue(key, out int d); _errsAll[key] = d + 1;
        }
    }

    // ---- per-call timing ----------------------------------------------------------------

    internal static void BeginFrame()
    {
        if (!_enabled) return;
        long now = Stopwatch.GetTimestamp();
        _prevWallMs = _prevFrameTs == 0 ? 0 : (now - _prevFrameTs) * 1000.0 / Stopwatch.Frequency;
        _prevFrameTs = now;
        _frameTs = now;
        _ms.Clear(); _n.Clear();
        _errs.Clear();
    }

    internal static void W(string name, Action a)
    {
        if (!_enabled) { a(); return; }
        long t = Stopwatch.GetTimestamp();
        try { a(); }
        finally { Add(name, t); }
    }

    internal static void Add(string name, long startTicks)
    {
        if (!_enabled) return;
        double ms = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        _ms.TryGetValue(name, out double v); _ms[name] = v + ms;
        _n.TryGetValue(name, out int c); _n[name] = c + 1;
    }

    internal static void Phase(string tag) { _tag = tag; }

    // Every scene-wide object-table scan goes through here: the tag is the scanned type, so the
    // frame report names the scan that costs the memory, not the subsystem that happened to call it.
    internal static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object> Scan(Il2CppSystem.Type t)
    {
        if (!_enabled) return Resources.FindObjectsOfTypeAll(t);
        long s = Stopwatch.GetTimestamp();
        try { return Resources.FindObjectsOfTypeAll(t); }
        finally { Add("scan:" + (t != null ? t.Name : "?"), s); }
    }

    internal static void Hit(string name)
    {
        if (!_enabled) return;
        _ms.TryGetValue(name, out double v); _ms[name] = v;
        _n.TryGetValue(name, out int c); _n[name] = c + 1;
    }

    // ---- frame report -------------------------------------------------------------------

    internal static void EndFrame(IHostContext log, float dt)
    {
        if (!_enabled || log == null) return;
        try
        {
            double cost = (Stopwatch.GetTimestamp() - _frameTs) * 1000.0 / Stopwatch.Frequency;
            _frames++;
            _winFrames++;
            _winCostMs += cost;

            int errThisFrame;
            lock (_errs) errThisFrame = _errs.Count;

            bool perFrame = cost > SlowMs || _prevWallMs > SlowMs * 2 || errThisFrame > 0;
            if (!perFrame && _winFrames < WindowFrames) return;

            foreach (var kv in _ms)
            {
                _winMs.TryGetValue(kv.Key, out double v); _winMs[kv.Key] = v + kv.Value;
                _n.TryGetValue(kv.Key, out int c); _winN.TryGetValue(kv.Key, out int wc); _winN[kv.Key] = wc + c;
            }

            IDictionary<string, double> ms = perFrame ? _ms : _winMs;
            IDictionary<string, int> n = perFrame ? _n : _winN;
            int frames = perFrame ? 1 : _winFrames;
            double shownCost = perFrame ? cost : _winCostMs / Math.Max(1, _winFrames);

            var sb = new StringBuilder();
            sb.Append("[perf] phase=").Append(_tag).Append(" n=").Append(_frames)
              .Append(" engineFrame=").Append(Time.frameCount)
              .Append(" fps=").Append(_prevWallMs > 1 ? (1000.0 / _prevWallMs).ToString("F2") : "burst")
              .Append(" wall=").Append(_prevWallMs.ToString("F1")).Append("ms")
              .Append(" dt=").Append(dt.ToString("F3")).Append("s")
              .Append(" cost=").Append(shownCost.ToString("F2")).Append("ms")
              .Append(" over=").Append(frames).Append("f")
              .Append(" errs=").Append(errThisFrame)
              .Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1"));
            if (errThisFrame > 0)
            {
                sb.Append(" errTop=[");
                int shown = 0;
                lock (_errs)
                {
                    foreach (var kv in _errs)
                    {
                        sb.Append('"').Append(kv.Key).Append("\"x").Append(kv.Value).Append("; ");
                        if (++shown >= 4) break;
                    }
                }
                sb.Append(']');
            }
            sb.Append(" rows=[");
            var list = new List<KeyValuePair<string, double>>(ms);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            int k = 0;
            foreach (var kv in list)
            {
                if (kv.Value < 0.05 && k >= 8) continue;
                n.TryGetValue(kv.Key, out int cnt);
                sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("F2"));
                if (cnt > 1) sb.Append('x').Append(cnt);
                sb.Append(' ');
                if (++k >= 16) break;
            }
            sb.Append(']');
            log.LogInfo(sb.ToString());

            _winFrames = 0; _winCostMs = 0;
            _winMs.Clear(); _winN.Clear();
        }
        catch (Exception e)
        {
            try { log.LogWarning($"[perf] end frame failed: {e.Message}"); } catch { }
        }
    }

    internal static string AllErrors()
    {
        var sb = new StringBuilder();
        lock (_errsAll)
        {
            foreach (var kv in _errsAll) sb.Append('"').Append(kv.Key).Append("\"x").Append(kv.Value).Append("; ");
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }
}
