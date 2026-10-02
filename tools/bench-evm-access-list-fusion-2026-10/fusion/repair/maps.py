# Handler addresses per process from the perf maps: for each run tag, the start address (mod 4096 and mod 64) of the
# handlers whose names match the given substrings, for one cancelable flavour. Usage: maps.py <tag>... -- <substr>...
import sys, re
i = sys.argv.index('--'); tags = sys.argv[1:i]; pats = sys.argv[i + 1:]
D = '/root/fusion-bench/repair/maps'
for t in tags:
    out = []
    for line in open(f'{D}/{t}.map', errors='replace'):
        a, s, name = line.rstrip('\n').split(' ', 2)
        if 'RawCalliHelper' not in name: continue
        for p in pats:
            if re.search(p, name):
                out.append((p, int(a, 16), int(s, 16)))
    print(t, ' '.join(f'{p}@{a % 4096:03x}/{a % 64:02d}+{s}' for p, a, s in out))
