"""Summarizes the compiler's 'too large to optimize' diagnostics of a run with NETHERMIND_ILEVM_DIAG=1.

usage: python -I oversize_summary.py <log>
"""
import collections
import re
import sys

LINE = re.compile(r"il-evm (evm_[0-9a-f]+): chunk (\d+) too large to optimize: (\d+) units, (\d+)B/(\d+)i/(\d+)r/(\d+)l/(\d+)b/(\d+)s")
LAID = re.compile(r"il-evm laid out (evm_[0-9a-f]+): (\d+) B of code, (\d+) units, (\d+) copies, (\d+) chunks, (\d+) KiB IL")

attempts = collections.Counter()
last = {}
laid = set()
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    m = LINE.search(line)
    if m:
        name = m.group(1)
        attempts[name] += 1
        last[name] = tuple(int(g) for g in m.groups()[1:])
        continue
    m = LAID.search(line)
    if m:
        laid.add(m.group(1))

failed = {n: v for n, v in last.items() if n not in laid}
recovered = {n: v for n, v in last.items() if n in laid}
print(f"contracts cut again: {len(last)}; recovered by recutting: {len(recovered)}; still too large: {len(failed)}")

shape = collections.Counter()
for name, (chunk, units, ilb, ili, refs, locs, labels, sites) in failed.items():
    bound = "il" if ili > 18000 else "refs" if refs > 7000 else "labels" if labels > 1700 else "locals" if locs > 1500 else "bytes"
    shape[(units, bound, ili // 5000 * 5000)] += 1
print("still too large, by (units in the chunk, limit hit, IL instructions rounded down to 5k):")
for (units, bound, il), count in sorted(shape.items(), key=lambda kv: -kv[1]):
    print(f"  {count:5} x units={units:<5} limit={bound:<6} il>={il}")

print("attempts before giving up:", collections.Counter(attempts[n] for n in failed).most_common(5))
