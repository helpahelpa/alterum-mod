#!/usr/bin/env python3
"""Menu arm analysis: DXGI present-to-present deltas (external) + the in-process bench CSV.

Same method as REPORT-2: event Microsoft-Windows-DXGI / ID 42 (Present start), filtered by the
arm's PID, Clock-Time in 100 ns, deltas between consecutive presents.
"""
import csv, glob, gzip, io, os, statistics, sys

def read_rows(path):
    op = gzip.open if path.endswith('.gz') else open
    with op(path, 'rt', encoding='utf-8', errors='replace') as f:
        for line in f:
            parts = [p.strip() for p in line.split(',')]
            if len(parts) < 17: continue
            if parts[0] != 'Microsoft-Windows-DXGI': continue
            if parts[2] != '42': continue
            try:
                pid = int(parts[9], 16)
                ct = int(parts[16])
            except ValueError:
                continue
            yield pid, ct

def series(path):
    rows = list(read_rows(path))
    if not rows: return None, []
    counts = {}
    for pid, _ in rows: counts[pid] = counts.get(pid, 0) + 1
    pid = max(counts, key=counts.get)
    ts = sorted(ct for p, ct in rows if p == pid)
    d = [(b - a) / 10000.0 for a, b in zip(ts, ts[1:]) if b > a]
    return pid, d

def pct(xs, q):
    if not xs: return float('nan')
    xs = sorted(xs); k = max(0, min(len(xs) - 1, int(round(q * (len(xs) - 1)))))
    return xs[k]

def bench(csvpath):
    walls, mods, allock, gc0 = [], [], [], 0
    if not os.path.exists(csvpath): return None
    with open(csvpath, newline='', encoding='utf-8', errors='replace') as f:
        r = csv.DictReader(f)
        prev_gc0 = None
        for row in r:
            try:
                walls.append(float(row['wall_ms'])); mods.append(float(row['mod_ms']))
                allock.append(float(row['gc_alloc_bytes']))
                g = int(row['gc0'])
                if prev_gc0 is not None and g > prev_gc0: gc0 += g - prev_gc0
                prev_gc0 = g
            except Exception:
                continue
    if not walls: return None
    return dict(wall_med=statistics.median(walls), wall_p99=pct(walls, .99),
                mod_ms=sum(mods)/len(mods), alloc=sum(allock)/len(allock), gc0=gc0)

def report(tag, dxgi, benchcsv=None):
    pid, d = series(dxgi)
    if not d:
        print(f'{tag:12s} no frames'); return None
    out = dict(frames=len(d), median=pct(d, .5), p95=pct(d, .95), p99=pct(d, .99), worst=max(d),
               over33=sum(1 for x in d if x > 33.3), over66=sum(1 for x in d if x > 66.7), pid=pid)
    b = bench(benchcsv) if benchcsv else None
    print(f"{tag:12s} pid={pid:6d} frames={out['frames']:6d} med={out['median']:6.2f} "
          f"p95={out['p95']:6.2f} p99={out['p99']:7.2f} worst={out['worst']:7.2f} "
          f">33.3={out['over33']:3d} >66.7={out['over66']:3d}"
          + (f" mod={b['mod_ms']:.3f} alloc={b['alloc']:8.0f} gc0={b['gc0']}" if b else ""))
    return out

if __name__ == '__main__':
    base = sys.argv[1] if len(sys.argv) > 1 else '.'
    for w in sorted(glob.glob(os.path.join(base, 'dxgi_*.csv*'))):
        tag = os.path.basename(w).split('.')[0][5:]
        report(tag, w)
