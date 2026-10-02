# Per-process view of repair experiment runs: clean-iteration median ns/op per case for each run tag, grouped by build.
# Usage: ana.py <prefix> <build>... [--runs N]
import os, sys, statistics
sys.path.insert(0, '/root/fusion-bench/gate1')
from iters import estimate
args = [a for a in sys.argv[1:] if not a.startswith('--')]
pre, builds = args[0], args[1:]
runs = int(next((a.split('=')[1] for a in sys.argv if a.startswith('--runs=')), 9))
G = '/root/fusion-bench/gate1'
tags = {b: [f'{pre}-{b}-{r}' for r in range(1, runs + 1) if os.path.exists(f'{G}/logs/{pre}-{b}-{r}.log')] for b in builds}
E = {t: estimate(t, 'st') for b in builds for t in tags[b]}
keys = sorted(set.union(*[set(e) for e in E.values()]), key=lambda k: (k[0].lower(), k[1]))
print('chain\tc\t' + '\t'.join(f'{b}[{len(tags[b])}]' for b in builds) + '\t' + '\t'.join(f'd({b})%' for b in builds[1:]))
for k in keys:
    if k[0] == 'AddMod': continue
    cells = []; meds = []
    for b in builds:
        v = [E[t][k][0] for t in tags[b] if k in E[t]]
        flag = ''.join('*' for t in tags[b] if k in E[t] and E[t][k][1] != 'main')
        cells.append(' '.join(f'{x:.2f}' for x in v) + flag); meds.append(statistics.median(v) if v else float('nan'))
    print(f'{k[0]}\t{k[1][0]}\t' + '\t'.join(cells) + '\t' + '\t'.join(f'{(m / meds[0] - 1) * 100:+.1f}' for m in meds[1:]))
