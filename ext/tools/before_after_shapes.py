"""Before/after table of Benchmark_shapes_timing runs: per shape, the median speedup over the repeats of each side.

usage: python -I before_after_shapes.py <before logs, comma-separated> <after logs, comma-separated> [--markdown]
A shape the before side could not compile counts at its measured speedup (the interpreter ran it, about 1x); the
after side's effective speedup counts 1x where the self-check gave the contract back to the interpreter.
"""
import math
import re
import statistics
import sys

ROW = re.compile(r"^(\S+)\s+(small|max)\s+(\d+)\s+([\d.]+)us\s+([\d.]+)us\s+([\d.]+)x\s+(\d+)\s+([\d.]+)\s+([\d.]+)(.*)$")


def load(paths):
    rows = {}
    for path in paths:
        for line in open(path, encoding="utf-8", errors="replace"):
            m = ROW.match(line)
            if not m:
                continue
            key = (m.group(1), m.group(2))
            rest = m.group(10)
            verdict = re.search(r"self-check (\w+)", rest)
            entry = rows.setdefault(key, {"speedup": [], "jit": [], "compiled": True, "verdict": []})
            entry["speedup"].append(float(m.group(6)))
            entry["jit"].append(float(m.group(9)))
            if "NOT COMPILED" in rest:
                entry["compiled"] = False
            if verdict:
                entry["verdict"].append(verdict.group(1))
    return rows


before = load(sys.argv[1].split(","))
after = load(sys.argv[2].split(","))
markdown = "--markdown" in sys.argv


def geo(values):
    return math.exp(sum(math.log(v) for v in values) / len(values))


for size in ("small", "max"):
    keys = [k for k in after if k[1] == size and k in before]
    b_all, a_raw, a_eff = [], [], []
    not_compiled = slower_before = slower_after = reverted = 0
    lines = []
    for key in keys:
        b, a = before[key], after[key]
        bs = statistics.median(b["speedup"])
        ar = statistics.median(a["speedup"])
        given_back = a["verdict"] and statistics.mode(a["verdict"]) == "interpreted"
        ae = 1.0 if given_back else ar
        b_all.append(bs)
        a_raw.append(ar)
        a_eff.append(ae)
        not_compiled += not b["compiled"]
        slower_before += bs < 0.95
        slower_after += ae < 0.95
        reverted += bool(given_back)
        lines.append((key[0], bs, b["compiled"], ar, ae, statistics.median(b["jit"]), statistics.median(a["jit"]), given_back))
    print(f"\n== {size}: {len(keys)} shapes; before geomean x{geo(b_all):.2f} ({not_compiled} not compiled, {slower_before} below 0.95x); "
          f"after raw x{geo(a_raw):.2f}, with self-check x{geo(a_eff):.2f} ({reverted} given back, {slower_after} below 0.95x); "
          f"JIT total {sum(l[5] for l in lines):.0f} -> {sum(l[6] for l in lines):.0f} ms")
    if markdown:
        print("| shape | before | after (raw) | after (self-check) |")
        print("|---|---|---|---|")
    for name, bs, compiled, ar, ae, bj, aj, back in lines:
        note = "" if compiled else " (not compiled)"
        if markdown:
            print(f"| {name} | {bs:.2f}x{note} | {ar:.2f}x | {ae:.2f}x{' (interpreter)' if back else ''} |")
        else:
            print(f"  {name:<20} before {bs:6.2f}x{note:<15} after raw {ar:6.2f}x  effective {ae:6.2f}x{'  given back' if back else ''}   JIT {bj:7.1f} -> {aj:7.1f} ms")
