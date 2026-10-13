"""Compares two Benchmark_shapes_timing logs shape by shape: speedups and JIT time side by side.

usage: python -I compare_shapes.py <a.log> <b.log> [label-a] [label-b]
"""
import math
import re
import sys

ROW = re.compile(r"^(\S+)\s+(small|max)\s+(\d+)\s+([\d.]+)us\s+([\d.]+)us\s+([\d.]+)x\s+(\d+)\s+([\d.]+)\s+([\d.]+)")


def load(path):
    rows = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        m = ROW.match(line)
        if m:
            name, size = m.group(1), m.group(2)
            rows[(name, size)] = dict(interp=float(m.group(4)), comp=float(m.group(5)), speedup=float(m.group(6)),
                                      chunks=int(m.group(7)), il=float(m.group(8)), jit=float(m.group(9)))
    return rows


a, b = load(sys.argv[1]), load(sys.argv[2])
la = sys.argv[3] if len(sys.argv) > 3 else "a"
lb = sys.argv[4] if len(sys.argv) > 4 else "b"
print(f"{'shape':<22} {'size':<6} {la + ' x':>9} {lb + ' x':>9} {'comp ' + lb + '/' + la:>12} {la + ' jit':>9} {lb + ' jit':>9} {la + ' ch':>6} {lb + ' ch':>6}")
for size in ("small", "max"):
    logs = []
    jit_a = jit_b = 0.0
    for key in a:
        if key[1] != size or key not in b:
            continue
        ra, rb = a[key], b[key]
        # Compiled time normalized by each run's interpreter time, so machine drift between runs cancels.
        rel = (rb["comp"] / rb["interp"]) / (ra["comp"] / ra["interp"])
        logs.append(math.log(rb["speedup"] / ra["speedup"]))
        jit_a += ra["jit"]
        jit_b += rb["jit"]
        flag = "  <-- better" if rel < 0.9 else "  <-- worse" if rel > 1.1 else ""
        print(f"{key[0]:<22} {size:<6} {ra['speedup']:>8.2f}x {rb['speedup']:>8.2f}x {rel:>11.2f}x {ra['jit']:>9.1f} {rb['jit']:>9.1f} {ra['chunks']:>6} {rb['chunks']:>6}{flag}")
    if logs:
        ga = math.exp(sum(math.log(a[k]['speedup']) for k in a if k[1] == size and k in b) / len(logs))
        gb = math.exp(sum(math.log(b[k]['speedup']) for k in a if k[1] == size and k in b) / len(logs))
        print(f"== {size}: geomean speedup {la} x{ga:.2f}, {lb} x{gb:.2f}; JIT total {la} {jit_a:.0f} ms, {lb} {jit_b:.0f} ms")
