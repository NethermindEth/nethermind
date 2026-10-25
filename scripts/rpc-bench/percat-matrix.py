#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Aggregate per-cell jsonbench-summary.md files from run-rpc-sweep.sh into MIXED-overall / ISOLATED / MIXED-per-scenario tables.
# Args: 'iso|<scenario>|<client>|<rps>=<summary.md>', 'mix|<client>|<rps>=<summary.md>', or '@manifest' (one arg per line).
import re
import sys

_OVR = {
    "avg": re.compile(r"\|\s*latency avg \(ms\)\s*\|\s*([0-9.]+)"),
    "median": re.compile(r"\|\s*latency p50 \(ms\)\s*\|\s*([0-9.]+)"),
    "p90": re.compile(r"\|\s*latency p90 \(ms\)\s*\|\s*([0-9.]+)"),
    "p99": re.compile(r"\|\s*latency p99 \(ms\)\s*\|\s*([0-9.]+)"),
    "tput": re.compile(r"\|\s*throughput \(req/s\)\s*\|\s*([0-9.]+)"),
    "checks": re.compile(r"\|\s*checks passed\s*\|\s*([0-9.]+)%"),
}
_METH = re.compile(r"\|\s*([\w/#-]+)\s*\|" + r"\s*([0-9.]+)\s*\|" * 6)


def norm(name):
    return re.sub(r"[^a-zA-Z0-9_-]", "-", name)


def parse(path):
    ovr, meth = {}, {}
    txt = open(path, encoding="utf-8", errors="replace").read()
    for k, rx in _OVR.items():
        m = rx.search(txt)
        if m:
            ovr[k] = float(m.group(1))
    in_meth = False
    for line in txt.splitlines():
        if line.strip().startswith("### Per method"):
            in_meth = True
            continue
        if in_meth:
            m = _METH.match(line.strip())
            if m and m.group(1) != "method":
                meth[norm(m.group(1))] = {"p90": float(m.group(4)), "p99": float(m.group(6))}
    return ovr, meth


def main():
    argv = sys.argv[1:]
    if len(argv) == 1 and argv[0].startswith("@"):
        argv = [ln.strip() for ln in open(argv[0][1:], encoding="utf-8") if ln.strip()]
    iso, mix = {}, {}
    clients, rpss, scen_iso, scen_mix = [], [], [], []
    for arg in argv:
        key, path = arg.split("=", 1)
        if key.startswith("iso|"):
            _, scen, client, rps = key.split("|")
            iso[(scen, client, rps)] = parse(path)[0]
            if scen not in scen_iso:
                scen_iso.append(scen)
        else:
            _, client, rps = key.split("|")
            ovr, meth = parse(path)
            mix[(client, rps)] = (ovr, meth)
            for s in meth:
                if s not in scen_mix:
                    scen_mix.append(s)
        if client not in clients:
            clients.append(client)
        if rps not in rpss:
            rpss.append(rps)
    rpss.sort(key=lambda r: (int(r.split("_")[0]) if r.split("_")[0].isdigit() else 0, r))
    cols = [(c, r) for c in clients for r in rpss]

    print("## MIXED - overall (client x rps): throughput r/s / checks% / avg / median / p90 / p99 ms\n")
    print("| client | " + " | ".join(f"rps {r}" for r in rpss) + " |")
    print("|" + "---|" * (len(rpss) + 1))
    for c in clients:
        row = [c]
        for r in rpss:
            o = mix.get((c, r), (None, None))[0]
            if not o:
                row.append("-")
            else:
                ck = f"{o['checks']:.0f}%" if "checks" in o else "na"
                row.append(f"{o.get('tput', 0):.0f} / {ck} / {o.get('avg', 0):.2f} / "
                           f"{o.get('median', 0):.2f} / {o.get('p90', 0):.0f} / {o.get('p99', 0):.0f}")
        print("| " + " | ".join(row) + " |")

    if scen_iso:
        print("\n## ISOLATED - each scenario ALONE at the sweep rps: achieved r/s / p99 ms (achieved << target = that scenario's ceiling)\n")
        print("| scenario | " + " | ".join(f"{c}@{r}" for c, r in cols) + " |")
        print("|" + "---|" * (len(cols) + 1))
        for s in scen_iso:
            row = [s]
            for c, r in cols:
                o = iso.get((s, c, r))
                row.append(f"{o['tput']:.0f}/{o['p99']:.0f}" if o and "p99" in o and "tput" in o else "-")
            print("| " + " | ".join(row) + " |")

    if scen_mix:
        print("\n## MIXED - per-scenario p99 ms (all scenarios at once)\n")
        print("| scenario | " + " | ".join(f"{c}@{r}" for c, r in cols) + " |")
        print("|" + "---|" * (len(cols) + 1))
        for s in scen_mix:
            row = [s] + [f"{mix[(c, r)][1][s]['p99']:.0f}" if (c, r) in mix and s in mix[(c, r)][1] else "-" for c, r in cols]
            print("| " + " | ".join(row) + " |")


if __name__ == "__main__":
    main()
