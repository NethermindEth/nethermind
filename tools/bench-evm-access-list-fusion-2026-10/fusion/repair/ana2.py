# Like ana.py, but each column is <prefix>:<build> (tags <prefix>-<build>-<r>); deltas against the first column.
import os, sys, statistics
sys.path.insert(0, '/root/fusion-bench/gate1')
from iters import estimate
G = '/root/fusion-bench/gate1'
cols = sys.argv[1:]
tags = {c: [f'{c.split(":")[0]}-{c.split(":")[1]}-{r}' for r in range(1, 10)
            if os.path.exists(f'{G}/logs/{c.split(":")[0]}-{c.split(":")[1]}-{r}.log')] for c in cols}
E = {t: estimate(t, 'st') for c in cols for t in tags[c]}
keys = sorted(set.union(*[set(e) for e in E.values()]), key=lambda k: (k[0].lower(), k[1]))
print('chain\tc\t' + '\t'.join(f'{c}[{len(tags[c])}]' for c in cols) + '\t' + '\t'.join(f'd({c})%' for c in cols[1:]))
for k in keys:
    if k[0] == 'AddMod': continue
    cells = []; meds = []
    for c in cols:
        v = [E[t][k][0] for t in tags[c] if k in E[t]]
        cells.append(' '.join(f'{x:.2f}' for x in v)); meds.append(statistics.median(v) if v else float('nan'))
    print(f'{k[0]}\t{k[1][0]}\t' + '\t'.join(cells) + '\t' + '\t'.join(f'{(m / meds[0] - 1) * 100:+.1f}' for m in meds[1:]))
