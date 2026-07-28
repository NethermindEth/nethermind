#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Aggregate per-cell jsonbench-summary.md files from run-rpc-sweep.sh into three
# views that keep per-call cost and system-under-load behaviour distinct:
#   MIXED overall      - client x rps saturation view (throughput/checks/p90/p99)
#   ISOLATED p99       - scenario x client@rps, each scenario run alone (per-call truth)
#   MIXED p99          - scenario x client@rps, from the all-at-once run (contended)
#   DELTA (mixed/iso)  - contention amplification per scenario (>1x = queued under load)
# Args: 'iso|<scenario>|<client>|<rps>=<summary.md>' and 'mix|<client>|<rps>=<summary.md>'
# (legacy '<client>:<rps>=<summary.md>' is treated as a MIXED cell).
import re
import sys

_OVR = {
    "p50": re.compile(r"\|\s*latency p50 \(ms\)\s*\|\s*([0-9.]+)"),
    "p90": re.compile(r"\|\s*latency p90 \(ms\)\s*\|\s*([0-9.]+)"),
    "p99": re.compile(r"\|\s*latency p99 \(ms\)\s*\|\s*([0-9.]+)"),
    "tput": re.compile(r"\|\s*throughput \(req/s\)\s*\|\s*([0-9.]+)"),
    "checks": re.compile(r"\|\s*checks passed\s*\|\s*([0-9.]+)%"),
}
_METH = re.compile(
    r"\|\s*([\w/#-]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|"
)


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
    iso, mix = {}, {}                 # iso[(scen,client,rps)]=ovr ; mix[(client,rps)]=(ovr,meth)
    clients, rpss, scen_iso, scen_mix = [], [], [], []
    for arg in sys.argv[1:]:
        key, path = arg.split("=", 1)
        if key.startswith("iso|"):
            _, scen, client, rps = key.split("|")
            ovr, _ = parse(path)
            iso[(scen, client, rps)] = ovr
            if scen not in scen_iso:
                scen_iso.append(scen)
        else:
            if key.startswith("mix|"):
                _, client, rps = key.split("|")
            else:
                client, rps = key.split(":")
            ovr, meth = parse(path)
            mix[(client, rps)] = (ovr, meth)
            for s in meth:
                if s not in scen_mix:
                    scen_mix.append(s)
        if client not in clients:
            clients.append(client)
        if rps not in rpss:
            rpss.append(rps)
    rpss.sort(key=int)
    cols = [(c, r) for c in clients for r in rpss]

    def col_hdr():
        return "| " + " | ".join(f"{c[:4]}@{r}" for c, r in cols) + " |\n|" + "---|" * (len(cols) + 1)

    # 1) MIXED overall (saturation)
    print("## MIXED - overall (client x rps): throughput r/s / checks% / p90 / p99 ms\n")
    print("| client | " + " | ".join(f"rps {r}" for r in rpss) + " |")
    print("|" + "---|" * (len(rpss) + 1))
    for c in clients:
        row = [c]
        for r in rpss:
            o = mix.get((c, r), (None, None))[0]
            row.append(f"{o['tput']:.0f} / {o['checks']:.0f}% / {o['p90']:.0f} / {o['p99']:.0f}" if o else "-")
        print("| " + " | ".join(row) + " |")

    # 2) ISOLATED per-scenario p99 (per-call truth)
    if scen_iso:
        print("\n## ISOLATED - per-scenario p99 ms (each scenario run ALONE)\n")
        print("| scenario | " + " | ".join(f"{c[:4]}@{r}" for c, r in cols) + " |")
        print("|" + "---|" * (len(cols) + 1))
        for s in scen_iso:
            row = [s] + [f"{iso[(s,c,r)]['p99']:.0f}" if (s, c, r) in iso and 'p99' in iso[(s, c, r)] else "-" for c, r in cols]
            print("| " + " | ".join(row) + " |")

    # 3) MIXED per-scenario p99 (contended)
    if scen_mix:
        print("\n## MIXED - per-scenario p99 ms (all scenarios at once)\n")
        print("| scenario | " + " | ".join(f"{c[:4]}@{r}" for c, r in cols) + " |")
        print("|" + "---|" * (len(cols) + 1))
        for s in scen_mix:
            row = [s] + [f"{mix[(c,r)][1][s]['p99']:.0f}" if (c, r) in mix and s in mix[(c, r)][1] else "-" for c, r in cols]
            print("| " + " | ".join(row) + " |")

    # 4) DELTA mixed/isolated (contention amplification)
    if scen_iso and scen_mix:
        print("\n## DELTA - mixed p99 / isolated p99 (contention amplification; >1x = queued under load)\n")
        print("| scenario | " + " | ".join(f"{c[:4]}@{r}" for c, r in cols) + " |")
        print("|" + "---|" * (len(cols) + 1))
        for s in scen_iso:
            row = [s]
            for c, r in cols:
                i = iso.get((s, c, r), {}).get("p99")
                m = mix.get((c, r), (None, {}))[1].get(s, {}).get("p99")
                row.append(f"{m/i:.1f}x" if i and m and i > 0 else "-")
            print("| " + " | ".join(row) + " |")


if __name__ == "__main__":
    main()
