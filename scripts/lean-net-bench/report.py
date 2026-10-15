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


def offered_rate(row):
    if row.get("offered_objects_per_s") is not None:
        return row["offered_objects_per_s"]
    arguments = row.get("provenance", {}).get("arguments", {})
    burst = row.get("burst", arguments.get("burst"))
    interval = row.get("interval_ms", arguments.get("interval_ms"))
    return burst * 1000 / interval if burst is not None and interval else None


def incomplete(row):
    return bool(row.get("error")) or row.get("missing") != 0


def measured_values(rows, key):
    return [row[key] for row in rows if row.get(key) is not None]


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

    load = [r for r in rows if r.get("label", "").startswith("load-") and offered_rate(r) is not None]
    for count in sorted({r["transactions_per_object"] for r in load}):
        fig, (ax, delivery) = plt.subplots(2, 1, figsize=(6.5, 7), sharex=True,
                                         gridspec_kw={"height_ratios": [2, 1]})
        subset = [r for r in load if r["transactions_per_object"] == count]
        rates = sorted({offered_rate(r) for r in subset})
        warnings = []
        for binding, color in colors.items():
            x, y, low, high, actual_x, actual_y, delivery_x, delivery_y = [], [], [], [], [], [], [], []
            for rate in rates:
                matching = [r for r in subset if r["binding"] == binding and offered_rate(r) == rate]
                if not matching:
                    continue
                scheduled = rate * count
                values = measured_values(matching, "goodput_tx_per_s_per_receiver")
                if values:
                    center = statistics.median(values)
                    x.append(scheduled); y.append(center)
                    low.append(center - min(values)); high.append(max(values) - center)
                actual = measured_values(matching, "actual_offered_tx_per_s")
                if actual:
                    actual_x.append(scheduled); actual_y.append(statistics.median(actual))
                fractions = [r["validated_receiver_deliveries"] / r["expected_receiver_deliveries"] for r in matching
                             if r.get("validated_receiver_deliveries") is not None and r.get("expected_receiver_deliveries")]
                if fractions:
                    delivery_x.append(scheduled); delivery_y.append(statistics.median(fractions) * 100)
                failed = sum(incomplete(r) for r in matching)
                unknown = len(matching) - len(values)
                if failed or unknown:
                    warnings.append(f"{names[binding]} @ {scheduled:g} tx/s: {failed}/{len(matching)} incomplete/error, {unknown} unmeasured")
            if x:
                ax.errorbar(x, y, yerr=[low, high], label=f"Delivered: {names[binding]}", color=color, marker="o", capsize=3)
            if actual_x:
                ax.plot(actual_x, actual_y, color=color, marker="^", linestyle="--", linewidth=1,
                        label=f"Actual origin: {names[binding]}")
            if delivery_x:
                delivery.plot(delivery_x, delivery_y, marker="o", color=color, label=names[binding])
        if rates:
            ax.plot([0, rates[-1] * count], [0, rates[-1] * count], color="#999999", linestyle=":", label="Scheduled load")
        ax.set(title=f"{count} transaction{'s' if count != 1 else ''} per aggregate", ylabel="Transactions/s per receiver")
        delivery.set(xlabel="Scheduled transactions/s", ylabel="Receiver deliveries (%)", ylim=(-3, 103))
        ax.grid(alpha=.18); delivery.grid(alpha=.18)
        if ax.get_legend_handles_labels()[0]: ax.legend(fontsize=8)
        if delivery.get_legend_handles_labels()[0]: delivery.legend(fontsize=8)
        warning_text = "\n".join(warnings) if warnings else "All recorded attempts complete."
        fig.text(.02, .01, warning_text, fontsize=7, va="bottom", color="#8b3027" if warnings else "#44546a")
        fig.tight_layout(rect=(0, min(.35, .045 + len(warnings) * .014), 1, 1))
        save(fig, f"goodput-tx{count}", "Controlled verification: validated/recovered envelopes, not pool admission or native throughput. Median and min–max of measured attempts, including partial delivery. Actual origin rate uses first-to-last release span plus one configured interval. Failed/unmeasured attempts remain listed; unknown goodput is never imputed as zero.")

    subset = [r for r in load if r["transactions_per_object"] == 1]
    if subset:
        fig, ax = plt.subplots(figsize=(6, 4))
        for binding, color in colors.items():
            points = []
            for rate in sorted({offered_rate(r) for r in subset}):
                matching = [r for r in subset if r["binding"] == binding and offered_rate(r) == rate]
                value = median(matching, "goodput_mbit_per_s_per_receiver")
                if value is not None:
                    points.append((rate, value))
            if points:
                ax.plot(*zip(*points), marker="o", color=color, label=names[binding])
        ax.set(title="Useful proof-object goodput", xlabel="Scheduled proof objects/s", ylabel="Useful Mbit/s per receiver")
        ax.grid(alpha=.18)
        if ax.get_legend_handles_labels()[0]: ax.legend()
        save(fig, "goodput-bytes", "Canonical object body bytes, including proofs and transaction hashes, excluding separately recovered ETH envelopes and transport overhead. See completion/failure counts; unavailable results are not interpolated as successful capacity.")

    sizes = [r for r in rows if r.get("label", "").startswith("size-")]
    if sizes:
        fig, axes = plt.subplots(2, 1, figsize=(6.5, 6.5), sharex=True)
        for binding, color in colors.items():
            for axis, field in ((axes[0], "goodput_mbit_per_s_per_receiver"), (axes[1], None)):
                points = []
                for size in sorted({r["proof_bytes"] for r in sizes if r.get("proof_bytes") is not None}):
                    group = [r for r in sizes if r.get("binding") == binding and r.get("proof_bytes") == size]
                    values = measured_values(group, field) if field else [r["latency_ms"]["p95"] for r in group
                        if not incomplete(r) and r.get("latency_ms", {}).get("p95") is not None]
                    if values: points.append((size / 2**20, statistics.median(values)))
                if points: axis.plot(*zip(*points), color=color, marker="o", label=names[binding])
        axes[0].set(title="Large proof-object delivery", ylabel="Useful Mbit/s per receiver")
        axes[1].set(xlabel="Proof payload size", ylabel="Worst-receiver p95 (ms)", xscale="log", xticks=[.0625, 1, 8], xticklabels=["64 KiB", "1 MiB", "8 MiB"])
        for axis in axes:
            axis.grid(alpha=.18)
            if axis.get_legend_handles_labels()[0]: axis.legend(fontsize=8)
        failed = sum(incomplete(r) for r in sizes)
        fig.tight_layout()
        save(fig, "large-objects", f"{failed}/{len(sizes)} attempts incomplete or failed; all are retained in the table. Goodput includes measured partial delivery; latency is shown only for fully complete runs. Missing points are unavailable, not zero.")

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
            keys = {r.get("profile_key") for samples in native.values() for r in samples if r.get("profile_key")}
            if len(keys) > 1:
                raise ValueError("Native samples with different profile keys must be reported separately")
            native_provenance_path = Path(args.native_dir) / "provenance.json"
            native_provenance = json.loads(native_provenance_path.read_text()) if native_provenance_path.exists() else {}
            (out / "native-provenance.json").write_text(json.dumps(native_provenance, indent=2))
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
            native_host = html.escape(str(native_provenance.get("cpu", "unknown host")))
            workers = html.escape(str(native_provenance.get("workers", "unknown")))
            native_note = f'<p><a href="native-results.json">Native timing rows</a> and <a href="native-provenance.json">provenance</a>. Native host: {native_host}; workers: {workers}. Check capture contention and memory scope in the provenance. Transport runs use a different host. The STARK fixture is a trivial real program, not worst-case execution. Compare measurement scope, not raw CPU costs across machines.</p>'

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
        scheduled = median([dict(r, rate=offered_rate(r)) for r in samples], "rate")
        actual = median(samples, "actual_offered_objects_per_s")
        released = measured_values(samples, "released_objects")
        released_text = f"{sum(released)}/{sum(r.get('objects', 0) for r in samples)}"
        if len(released) != len(samples): released_text += f" ({len(samples) - len(released)} unknown)"
        error_text = html.escape("; ".join(sorted({str(r['error']) for r in samples if r.get('error')}))) or "—"
        rate_text = f"{scheduled:g}" if scheduled is not None else "—"
        actual_text = f"{actual:.2f}" if actual is not None else "—"
        failures = sum(incomplete(r) for r in samples)
        missing_known = [r["missing"] for r in samples if r.get("missing") is not None]
        missing = str(sum(missing_known)) + (f" + {len(samples) - len(missing_known)} unknown run(s)" if len(missing_known) != len(samples) else "")
        table.append(f"<tr><td>{html.escape(label)}</td><td>{len(samples)}</td><td>{rate_text}</td><td>{actual_text}</td><td>{released_text}</td><td>{goodput:.2f}</td><td>{p95:.1f}</td><td>{failures}</td><td>{missing}</td><td>{error_text}</td></tr>" if goodput is not None and p95 is not None else
                     f"<tr><td>{html.escape(label)}</td><td>{len(samples)}</td><td>{rate_text}</td><td>{actual_text}</td><td>{released_text}</td><td>{'—' if goodput is None else f'{goodput:.2f}'}</td><td>—</td><td>{failures}</td><td>{missing}</td><td>{error_text}</td></tr>")
    provenance = sorted({r.get("provenance", {}).get("source_revision", "unknown") for r in rows})
    figures = "\n".join(f'<figure><a href="{name}.svg"><img src="{name}.svg" alt="{name}" loading="lazy"></a><figcaption>{caption} <a href="{name}.png">PNG</a></figcaption></figure>' for name, caption in charts)
    document = f'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>EIP-8437 transport measurements</title><style>body{{font:16px system-ui;margin:24px auto;max-width:960px;padding:0 16px;color:#17263a;background:#f8fafc}}h1{{font-size:1.8rem}}figure{{margin:24px 0;background:white;padding:16px;border-radius:12px}}img{{width:100%;max-width:650px;display:block;margin:auto}}figcaption{{font-size:.9rem;color:#44546a}}table{{border-collapse:collapse;font-size:.85rem;width:100%}}td,th{{padding:8px;border-bottom:1px solid #ccd4df;text-align:right}}td:first-child,th:first-child{{text-align:left}}.scroll{{overflow-x:auto}}code{{overflow-wrap:anywhere}}a{{color:#145da0}}</style>
<h1>EIP-8437 transport measurements</h1><p>Production RLPx and QUIC bindings with authenticated ETH envelope recovery. Proof contents, pool admission and the verifier are controlled fixtures: completion means validated/recovered wrappers, not successful pool admission, native proving or block throughput. Broadcast uses a test signer and accepts the reconstructed consensus context; it does not exercise live CL anchor validation.</p>
<p>Load scenarios: two processes, 100 Mbit/s per node, 50 ms RTT, no injected loss. Each aggregate contains distinct transactions and dependencies. Other link conditions appear in the table. Every attempted run is retained; failed runs are counted, not replaced.</p>
{figures}{native_note}<h2>All runs</h2><p>p95 is shown only for complete runs. Goodput includes partial delivery and queue drain. Error bars are observed ranges, not confidence intervals. Actual origin rate is released objects divided by first-to-last release span plus one configured interval; latency begins actual origination and excludes earlier scheduler lateness. Unknown measurements remain unknown, not zero. Native and transport measurements use different hosts and must not be compared as a single throughput pipeline.</p>
<div class="scroll"><table><thead><tr><th>Scenario</th><th>Runs</th><th>Scheduled objects/s</th><th>Actual origin objects/s</th><th>Released / scheduled objects</th><th>Useful Mbit/s</th><th>Worst-receiver p95 ms</th><th>Failed runs</th><th>Missing objects</th><th>Errors</th></tr></thead><tbody>{''.join(table)}</tbody></table></div>
<p><a href="results.json">Raw results and provenance</a></p><p>Source revisions: <code>{html.escape(', '.join(provenance))}</code></p></html>'''
    (out / "index.html").write_text(document)


if __name__ == "__main__":
    main()
