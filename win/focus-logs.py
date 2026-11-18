#!/usr/bin/env python3
"""Read-only focused Alterum log extraction; original files are never changed.

Example: python3 win/focus-logs.py --log client=/mnt/c/AlterumB/BepInEx/logs/b.log --tail-mb 8
"""
import argparse
from collections import Counter
from pathlib import Path
import re

STAMP = re.compile(r'^\d\d:\d\d:\d\d\.\d+\s+')
TAGS = ('[reel:act]', '[reel:net]', '[reel:debug]', '[music:req]', '[music:host]', '[music:sync]')
NOISE = ('the finished track\'s channel stopped', 'reconcile targetMs=', 'rx state rev=',
         'rx probe-reply', 'tx probe id=', '[music:host] play:')


def extract(path, nbytes):
    with path.open('rb') as f:
        size = f.seek(0, 2)
        f.seek(max(0, size - nbytes))
        if size > nbytes:
            f.readline()
        return f.read().decode('utf-8', errors='replace').splitlines()


def summarize(lines):
    groups = Counter()
    focused = []
    for line in lines:
        normalized = STAMP.sub('', line).strip()
        if any(x in line for x in NOISE):
            if "the finished track's channel stopped" in line:
                groups['music: finished channel stopped warning'] += 1
            continue
        if '[Error' in line or '[Fatal' in line or 'Exception:' in line:
            groups[normalized] += 1
        if any(t in line for t in TAGS) or ('[reel]' in line and any(
                w in line for w in ('consumed', 'pull start', 'settle', 'result', 'refus'))):
            focused.append(line)
    return groups, focused


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--log', action='append', required=True, metavar='NAME=PATH')
    p.add_argument('--tail-mb', type=int, default=8, choices=range(1, 129))
    p.add_argument('--limit', type=int, default=200)
    args = p.parse_args()
    if not 1 <= args.limit <= 5000:
        p.error('--limit must be 1..5000')
    for spec in args.log:
        if '=' not in spec:
            p.error('--log requires NAME=PATH')
        name, path = spec.split('=', 1)
        groups, focused = summarize(extract(Path(path), args.tail_mb * 1024 * 1024))
        print(f'\n=== {name}: grouped errors/warnings ===')
        for message, count in groups.most_common(40):
            print(f'{count:7} {message}')
        print(f'=== {name}: focused events (last {args.limit}, of {len(focused)}) ===')
        print('\n'.join(focused[-args.limit:]))


if __name__ == '__main__':
    main()
