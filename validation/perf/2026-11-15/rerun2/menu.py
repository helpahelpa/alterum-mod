#!/usr/bin/env python3
"""Menu arm report: external DXGI present-to-present + in-process bench CSV for one window."""
import csv, os, statistics, sys

def read_rows(path):
    with open(path, 'rt', encoding='utf-8', errors='replace') as f:
        for line in f:
            parts = [p.strip() for p in line.split(',')]
            if len(parts) < 17: continue
            if parts[0] != 'Microsoft-Windows-DXGI': continue
            if parts[2] != '42': continue
            try:
                pid = int(parts[9], 16); ct = int(parts[16])
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
    return pid, [(b - a) / 10000.0 for a, b in zip(ts, ts[1:]) if b > a]

def pct(xs, q):
    if not xs: return float('nan')
    xs = sorted(xs); k = max(0, min(len(xs) - 1, int(round(q * (len(xs) - 1)))))
    return xs[k]

def bench(csvpath):
    walls, mods, allocs = [], [], []
    gc0 = 0; prev = None; flags = {}
    if not os.path.exists(csvpath):
        return None
    with open(csvpath, newline='', encoding='utf-8', errors='replace') as f:
        for row in csv.DictReader(f):
            try:
                walls.append(float(row['wall_ms'])); mods.append(float(row['mod_ms']))
                allocs.append(float(row['gc_alloc_bytes']))
                g = int(row['gc0'])
                if prev is not None and g > prev: gc0 += g - prev
                prev = g
            except Exception:
                pass
            for t in (row.get('perf_rows') or '').split('/'):
                t = t.strip().lstrip('.')
                if '=' not in t: continue
                k, v = t.split('=', 1)
                if k.startswith('scan:') or k.startswith('cache:'):
                    try: ms = float(v.rstrip('x30')) * (30 if v.endswith('x30') else 1)
                    except ValueError: continue
                    flags.setdefault(k, [0, 0.0])
                    flags[k][0] += 1; flags[k][1] += ms
    return dict(n=len(walls), wall_med=statistics.median(walls) if walls else None,
                mod=sum(mods)/len(mods), alloc=sum(allocs)/len(allocs), gc0=gc0, flags=flags)

def main(tag, base):
    dxg = os.path.join(base, 'raw', 'dxgi_%s.csv' % tag)
    pid, d = series(dxg)
    b = bench(os.path.join(base, 'bench_%s.csv' % tag))
    print('== %s' % tag)
    if d:
        print('  DXGI  pid=%s frames=%d median=%.2f p95=%.2f p99=%.2f worst=%.2f >33=%.0f >66=%.0f'
              % (pid, len(d), pct(d, .5), pct(d, .95), pct(d, .99), max(d),
                 sum(1 for x in d if x > 33.3), sum(1 for x in d if x > 66.7)))
    else:
        print('  DXGI  no frames (%s)' % dxg)
    if b:
        print('  BENCH n=%d wall_med=%.2f mod=%.3f ms/frame alloc=%.0f B/frame gc0=%d'
              % (b['n'], b['wall_med'], b['mod'], b['alloc'], b['gc0']))
        if b['flags']:
            for k, (n, ms) in sorted(b['flags'].items(), key=lambda kv: -kv[1][1]):
                print('    %-34s rows=%4d  total=%7.1f ms' % (k, n, ms))
        else:
            print('    no scan:/cache: rows')

if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else '.')
