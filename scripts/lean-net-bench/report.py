#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Render measured transport goodput, latency and failures as PNG/SVG and mobile-friendly HTML.

Usage: report.py results.jsonl --out report
Requires matplotlib. Native proving results must be reported separately.
"""
import argparse
import csv
import html
import json
from pathlib import Path
import statistics


def median(rows, key):
    values = [row.get(key) for row in rows if row.get(key) is not None]
    return statistics.median(values) if values else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", nargs="+")
    parser.add_argument("--out", required=True)
    parser.add_argument("--native-dir", help="Directory of native cost CSV files and matching metadata JSON")
    args = parser.parse_args()
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    plt.rcParams.update({"font.size": 10, "axes.spines.top": False, "axes.spines.right": False,
                         "figure.dpi": 150, "savefig.bbox": "tight"})
    rows = []
    for path in args.results:
        with open(path) as stream:
            rows.extend(json.loads(line) for line in stream if line.strip())
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "results.json").write_text(json.dumps(rows, indent=2))
    colors = {"rlpx": "#145DA0", "ethp2p": "#C1492F"}
    names = {"rlpx": "RLPx + ETH", "ethp2p": "QUIC + ETH"}
    charts = []

    def save(fig, name, caption):
        fig.savefig(out / f"{name}.png")
        fig.savefig(out / f"{name}.svg")
        plt.close(fig)
        charts.append((name, caption))

    load = [r for r in rows if r.get("label", "").startswith("load-") and not r.get("error")]
    for count in sorted({r["transactions_per_object"] for r in load}):
        fig, ax = plt.subplots(figsize=(6, 4))
        subset = [r for r in load if r["transactions_per_object"] == count]
        rates = sorted({r["offered_objects_per_s"] for r in subset})
        for binding, color in colors.items():
            x, y, low, high = [], [], [], []
            for rate in rates:
                matching = [r for r in subset if r["binding"] == binding and r["offered_objects_per_s"] == rate]
                values = [r["goodput_tx_per_s_per_receiver"] for r in matching]
                if not values:
                    continue
                center = statistics.median(values)
                x.append(rate * count); y.append(center)
                low.append(center - min(values)); high.append(max(values) - center)
            ax.errorbar(x, y, yerr=[low, high], label=names[binding], color=color, marker="o", capsize=3)
        if rates:
            ax.plot([0, rates[-1] * count], [0, rates[-1] * count], color="#999999", linestyle=":", label="Offered load")
        ax.set(title=f"{count} transaction{'s' if count != 1 else ''} per aggregate", xlabel="Offered transactions/s", ylabel="Delivered transactions/s per receiver")
        ax.grid(alpha=.18); ax.legend(fontsize=9)
        save(fig, f"goodput-tx{count}", "Transport only: synthetic proof bytes and controlled verification. Median and min–max across repetitions; drain time and missing deliveries reduce goodput.")

    subset = [r for r in load if r["transactions_per_object"] == 1]
    if subset:
        fig, ax = plt.subplots(figsize=(6, 4))
        for binding, color in colors.items():
            points = []
            for rate in sorted({r["offered_objects_per_s"] for r in subset}):
                matching = [r for r in subset if r["binding"] == binding and r["offered_objects_per_s"] == rate]
                value = median(matching, "goodput_mbit_per_s_per_receiver")
                if value is not None:
                    points.append((rate, value))
            if points:
                ax.plot(*zip(*points), marker="o", color=color, label=names[binding])
        ax.set(title="Useful proof-object goodput", xlabel="Offered proof objects/s", ylabel="Useful Mbit/s per receiver")
        ax.grid(alpha=.18); ax.legend()
        save(fig, "goodput-bytes", "Useful canonical object bytes only, excluding ETH envelopes, headers, retransmissions and coding overhead.")

    native_note = ""
    if args.native_dir:
        native = {}
        for path in sorted(Path(args.native_dir).glob("**/*.csv")):
            with path.open() as stream:
                for row in csv.DictReader(stream):
                    if "proving_ms" not in row or "claims" not in row:
                        continue
                    label = f"{row['mode']} / {row['claims']}"
                    native.setdefault(label, []).append(row)
        if native:
            preferred = ["raw-binary-tree / 1", "raw-binary-tree / 16", "raw-binary-tree / 64", "generic-single / 1", "mixed-raw / 2", "mixed-merge / 2"]
            labels = sorted(native, key=lambda label: preferred.index(label) if label in preferred else len(preferred))
            display = {"raw-binary-tree / 1": "SPHINCS 1", "raw-binary-tree / 16": "SPHINCS 16",
                       "raw-binary-tree / 64": "SPHINCS 64", "generic-single / 1": "STARK 1",
                       "mixed-raw / 2": "SPHINCS + STARK", "mixed-merge / 2": "Merge two parents"}
            fig, axes = plt.subplots(2, 1, figsize=(7, 7))
            for axis, field, scale, title, ylabel in (
                (axes[0], "proving_ms", .001, "Native proof aggregation", "Seconds"),
                (axes[1], "proof_bytes", 1 / 1024, "Final recursive proof size", "KiB")):
                means = [statistics.median(float(r[field]) * scale for r in native[label]) for label in labels]
                errors = [[means[i] - min(float(r[field]) * scale for r in native[label]) for i, label in enumerate(labels)],
                          [max(float(r[field]) * scale for r in native[label]) - means[i] for i, label in enumerate(labels)]]
                axis.bar(range(len(labels)), means, yerr=errors, capsize=3, color="#145DA0")
                axis.set_xticks(range(len(labels)), [f"{display.get(label, label)}\n(n={len(native[label])})" for label in labels], rotation=25, ha="right", fontsize=8)
                axis.set(title=title, ylabel=ylabel)
                axis.grid(axis="y", alpha=.18)
                for x, value in enumerate(means):
                    axis.text(x, value, f"{value:.1f}", ha="center", va="bottom", fontsize=8)
                axis.margins(y=.18)
            fig.tight_layout()
            save(fig, "native-aggregation", "Real native proofs, one worker; cold folding includes all leaves and merges. Parent-merge cases assume child proofs already exist. Fixture generation is excluded. Medians of recorded samples; these are not slot-capacity guarantees.")
            (out / "native-results.json").write_text(json.dumps(native, indent=2))
            native_note = '<p><a href="native-results.json">Native timing rows</a>. Native results use an Apple M2 Max with one prover worker; managed builds overlapped part of capture. Transport runs use a different host. The STARK fixture is a trivial real program, not worst-case execution. Compare measurement scope, not raw CPU costs across machines.</p>'

    cells = {}
    for row in rows:
        label = row.get("label", "unlabelled").rsplit("-rep", 1)[0]
        cells.setdefault(label, []).append(row)
    table = []
    for label, samples in sorted(cells.items()):
        completed = [r for r in samples if not r.get("error") and r.get("missing") == 0]
        latencies = [r.get("latency_ms", {}).get("p95") for r in completed]
        latencies = [x for x in latencies if x is not None]
        p95 = statistics.median(latencies) if latencies else None
        goodput = median(samples, "goodput_mbit_per_s_per_receiver")
        failures = sum(bool(r.get("error") or r.get("missing")) for r in samples)
        missing = sum(r.get("missing", 0) for r in samples)
        table.append(f"<tr><td>{html.escape(label)}</td><td>{len(samples)}</td><td>{goodput:.2f}</td><td>{p95:.1f}</td><td>{failures}</td><td>{missing}</td></tr>" if goodput is not None and p95 is not None else
                     f"<tr><td>{html.escape(label)}</td><td>{len(samples)}</td><td>{'—' if goodput is None else f'{goodput:.2f}'}</td><td>—</td><td>{failures}</td><td>{missing}</td></tr>")
    provenance = sorted({r.get("provenance", {}).get("source_revision", "unknown") for r in rows})
    figures = "\n".join(f'<figure><a href="{name}.svg"><img src="{name}.svg" alt="{name}" loading="lazy"></a><figcaption>{caption} <a href="{name}.png">PNG</a></figcaption></figure>' for name, caption in charts)
    document = f'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>EIP-8437 transport measurements</title><style>body{{font:16px system-ui;margin:24px auto;max-width:960px;padding:0 16px;color:#17263a;background:#f8fafc}}h1{{font-size:1.8rem}}figure{{margin:24px 0;background:white;padding:16px;border-radius:12px}}img{{width:100%;max-width:650px;display:block;margin:auto}}figcaption{{font-size:.9rem;color:#44546a}}table{{border-collapse:collapse;font-size:.85rem;width:100%}}td,th{{padding:8px;border-bottom:1px solid #ccd4df;text-align:right}}td:first-child,th:first-child{{text-align:left}}.scroll{{overflow-x:auto}}code{{overflow-wrap:anywhere}}a{{color:#145da0}}</style>
<h1>EIP-8437 transport measurements</h1><p>Production RLPx and QUIC bindings with authenticated ETH envelope recovery. Proof contents and the verifier are controlled fixtures: these charts measure networking, not native proving or end-to-end block throughput.</p>
<p>Load scenarios: two processes, 100 Mbit/s per node, 50 ms RTT, no injected loss. Each aggregate contains distinct transactions and dependencies. Other link conditions appear in the table. Every attempted run is retained; failed runs are counted, not replaced.</p>
{figures}{native_note}<h2>All runs</h2><p>p95 is shown only for complete runs. Goodput includes partial delivery and queue drain. Error bars are observed ranges, not confidence intervals.</p>
<div class="scroll"><table><thead><tr><th>Scenario</th><th>Runs</th><th>Useful Mbit/s</th><th>p95 ms</th><th>Failed runs</th><th>Missing objects</th></tr></thead><tbody>{''.join(table)}</tbody></table></div>
<p><a href="results.json">Raw results and provenance</a></p><p>Source revisions: <code>{html.escape(', '.join(provenance))}</code></p></html>'''
    (out / "index.html").write_text(document)


if __name__ == "__main__":
    main()
