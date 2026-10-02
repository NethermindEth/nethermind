# The gate rule (gate.py) applied to any two pairs of single-thread runs: base tags A1 A2 vs candidate tags B1 B2.
# Usage: xgate.py A1 A2 B1 B2   (tags as in gate1/logs, re-runs picked up as compare.py does)
import sys, statistics
sys.path.insert(0, '/root/fusion-bench/gate1')
from iters import estimate
a1, a2, b1, b2 = sys.argv[1:5]
E = [estimate(t, 'st') for t in (a1, a2, b1, b2)]
keys = sorted(set.intersection(*[set(e) for e in E]), key=lambda k: (k[0].lower(), k[1]))
reg, bim, worse, better = [], 0, 0, 0
for k in keys:
    v = [e[k][0] for e in E]
    bm = statistics.mean(v[:2]); d = (statistics.mean(v[2:]) / bm - 1) * 100; sp = (max(v[:2]) / min(v[:2]) - 1) * 100
    if d > 2: worse += 1
    if d < -2: better += 1
    if sp >= 3: bim += 1
    elif d > 2: reg.append(f'{k[0]} {k[1][0]} {d:+.1f}% ({v[0]:.3f} {v[1]:.3f} -> {v[2]:.3f} {v[3]:.3f})')
cb = [(statistics.mean([E[2][('CompareBranch', c)][0], E[3][('CompareBranch', c)][0]]) /
       statistics.mean([E[0][('CompareBranch', c)][0], E[1][('CompareBranch', c)][0]]) - 1) * 100 for c in ('False', 'True')]
print(f'{a1},{a2} -> {b1},{b2}: CompareBranch {cb[0]:+.1f}/{cb[1]:+.1f}%; bimodal base {bim}; >2% worse {worse}, better {better}; '
      f'stable-base regressions {len(reg)}: ' + '; '.join(reg))
