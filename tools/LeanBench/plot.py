#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Render measured LeanBench results; never synthesize missing samples."""

import argparse
import html
import gzip
import json
import os
from collections import defaultdict
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--svg", action="store_true", help="Also generate reproducible vector plots")
    args = parser.parse_args()
    text = gzip.decompress(args.results.read_bytes()).decode() if args.results.suffix == ".gz" else args.results.read_text()
    data = json.loads(text)
    rows = data["results"]
    if not rows:
        raise SystemExit("No measured results to plot")
    args.output.mkdir(parents=True, exist_ok=True)
    os.environ.setdefault("MPLCONFIGDIR", str(args.output / ".matplotlib"))
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    plt.rcParams.update({
        "figure.dpi": 150,
        "savefig.dpi": 180,
        "font.size": 10,
        "axes.spines.top": False,
        "axes.spines.right": False,
        "axes.grid": True,
        "grid.alpha": 0.18,
        "axes.axisbelow": True,
        "legend.frameon": False,
    })
    charts = []

    def case_label(value):
        if value.startswith("object-"):
            size = int(value[7:])
            return f"{size // (1024 * 1024)} MiB" if size >= 1024 * 1024 else f"{size // 1024} KiB"
        if value == "shared-sphincs1-tx16":
            return "1 SPHINCS claim / 16 tx"
        for prefix, name in (("sphincs", "SPHINCS"), ("stark", "STARK"), ("mixed", "Mixed")):
            if value.startswith(prefix):
                return f"{name} ×{value[len(prefix):]}"
        return value

    def save(fig, name, caption):
        fig.text(0.01, 0.012, "Measured on one host; see metadata and raw samples for scope.",
                 fontsize=8, color="#586174")
        fig.tight_layout(rect=(0, 0.04, 1, 0.96))
        for suffix in (("png", "svg") if args.svg else ("png",)):
            output = args.output / f"{name}.{suffix}"
            fig.savefig(output, bbox_inches="tight")
            if suffix == "svg":
                output.write_text("\n".join(line.rstrip() for line in output.read_text().splitlines()) + "\n")
        plt.close(fig)
        charts.append((name, caption))

    def series(kind):
        result = defaultdict(list)
        for row in rows:
            if row["kind"] == kind:
                result[row["case"]].append(row)
        return result

    protocol = "lean/2" if data.get("metadata", {}).get("protocolPreprovedTransport", "").startswith("lean/2") else "lean/1"
    for kind, prefix, scope in [("load", "transaction", "Proving + transport + admission"),
                                ("protocol-preproved", "protocol", f"Preproved {protocol} receive queue + admission")]:
        loads = series(kind)
        if not loads:
            continue
        fig, axes = plt.subplots(1, 2, figsize=(12.5, 5.0))
        fig.suptitle(scope)
        maximum = max(row["offeredTxPerSecond"] for group in loads.values() for row in group)
        axes[0].plot([0, maximum], [0, maximum], "--", color="#9ba3b1", label="Offered load")
        for label, group in sorted(loads.items()):
            label = case_label(label)
            group.sort(key=lambda row: row["offeredTxPerSecond"])
            x = [row["offeredTxPerSecond"] for row in group]
            axes[0].plot(x, [row["acceptedTxPerSecond"] for row in group], "o-", label=label)
            axes[1].plot(x, [row["goodputMbps"] for row in group], "o-", label=label)
        axes[0].set(xlabel="Offered transactions / second", ylabel="Unique accepted transactions / second",
                    title="Acceptance throughput")
        axes[1].set(xlabel="Offered transactions / second", ylabel="Useful transaction goodput (Mbit/s)",
                    title="Goodput excludes proof and framing overhead")
        axes[0].legend(fontsize=8)
        axes[1].legend(fontsize=8)
        save(fig, prefix + "-goodput", "Unique admission and transaction-byte goodput under offered load.")

        fig, ax = plt.subplots(figsize=(7, 5))
        for label, group in sorted(loads.items()):
            if not label.startswith("sphincs"):
                continue
            ax.plot([row["offeredTxPerSecond"] for row in group],
                    [row["acceptedTxPerSecond"] for row in group], "o-", label=case_label(label))
        ax.set(xlabel="Offered transactions / second", ylabel="Accepted transactions / second",
               title="Fresh SPHINCS proving → pool" if kind == "load" else f"Preproved SPHINCS {protocol} → pool")
        ax.set_ylim(bottom=0)
        ax.legend(fontsize=10)
        save(fig, prefix + "-sphincs-admission", "Short arrival windows; measured goodput includes drain.")

        fig, axes = plt.subplots(1, 3, figsize=(15, 5.0))
        fig.suptitle(scope)
        for label, group in sorted(loads.items()):
            label = case_label(label)
            x = [row["offeredTxPerSecond"] for row in group]
            axes[0].plot(x, [row["wireMbps"] for row in group], "o-", label=label)
            axes[1].plot(x, [row["p95LatencyMs"] for row in group], "o-", label=label)
            fractions = [(row["droppedTransactions"] + row["rejectedTransactions"])
                         / max(1, row["offeredTransactions"]) for row in group]
            axes[2].plot(x, fractions, "o-", label=label)
        axes[0].set(ylabel="Encoded RLPx throughput (Mbit/s)", title="Includes proofs and framing")
        axes[1].set(ylabel="95th percentile completed-batch latency (ms)", title="Includes bounded-queue wait")
        axes[2].set(ylabel="Dropped or rejected fraction", ylim=(0, 1), title="Admission and queue pressure")
        for ax in axes:
            ax.set_xlabel("Offered transactions / second")
            ax.legend(fontsize=8)
        save(fig, prefix + "-pressure", "RLPx bytes, end-to-end latency and loss; TCP/IP overhead is excluded.")

        fig, ax = plt.subplots(figsize=(8, 5))
        for label, group in sorted(loads.items()):
            label = case_label(label)
            ax.plot([row["offeredTxPerSecond"] for row in group],
                    [row["processCpuMs"] / max(1, row["durationSeconds"] * 1000) for row in group],
                    "o-", label=label)
        ax.set(xlabel="Offered transactions / second", ylabel="Average process CPU cores busy",
               title=scope)
        ax.legend(fontsize=8)
        save(fig, prefix + "-cpu", "Whole-process CPU includes concurrent stages; their CPU intervals are not summed.")

    crypto = [row for row in rows if row["kind"] == "crypto" and row["sphincsCount"] > 0
              and row["starkCount"] == 0 and not row["case"].startswith("shared-")]
    if crypto:
        crypto.sort(key=lambda row: row["sphincsCount"])
        fig, axes = plt.subplots(1, 3, figsize=(15, 5.0))
        counts = [row["sphincsCount"] for row in crypto]
        batches = [max(1, row.get("measuredBatches", row.get("sampleCount", 1))) for row in crypto]
        axes[0].plot(counts, [row["proveWallMs"] / n for row, n in zip(crypto, batches)], "o-", label="Prove")
        axes[0].plot(counts, [row["verifyWallMs"] / n for row, n in zip(crypto, batches)], "o-", label="Verify")
        axes[1].plot(counts, [row["proveWallMs"] / n / row["sphincsCount"]
                              for row, n in zip(crypto, batches)], "o-", label="Prove / signature")
        axes[1].plot(counts, [row["verifyWallMs"] / n / row["sphincsCount"]
                              for row, n in zip(crypto, batches)], "o-", label="Verify / signature")
        axes[2].plot(counts, [row["proofBytesMean"] / row["sphincsCount"] for row in crypto], "o-",
                     label="Aggregate proof / signature")
        metadata = data.get("metadata", {})
        witness_bytes = metadata.get("sphincsWitnessBytes")
        if witness_bytes is None:
            backend = metadata.get("backendCommit", "")
            # Legacy captures predate size metadata and used the BLAKE2s profile.
            if not backend or backend.startswith("b977f5f"):
                witness_bytes = 4956
            elif backend.startswith("f33f31bf"):
                witness_bytes = 6208
        if witness_bytes is not None:
            axes[2].axhline(witness_bytes, linestyle="--", color="#9ba3b1", label="Direct SPHINCS witness")
        axes[0].set(ylabel="Wall time / batch (ms)", title="Real native proving and verification")
        axes[1].set(ylabel="Wall time / signature (ms)", title="Amortized crypto cost")
        axes[2].set(ylabel="Serialized bytes / signature", title="Proof size versus direct witness")
        for ax in axes:
            ax.set_yscale("log")
            ax.set_xscale("log", base=2)
            ax.set_xticks(counts, labels=[str(n) for n in counts])
            ax.set_xlabel("Distinct signatures in aggregate")
            ax.legend(fontsize=8)
        save(fig, "aggregation-cost", "Fresh distinct signature claims; witness reference includes public key and signature.")

        fig, ax = plt.subplots(figsize=(7, 5))
        ax.plot(counts, [row["proveWallMs"] / n / 1000 for row, n in zip(crypto, batches)],
                "o-", label="Fresh prove")
        ax.plot(counts, [row["verifyWallMs"] / n / 1000 for row, n in zip(crypto, batches)],
                "o-", label="Verify")
        ax.set(xlabel="Distinct signature claims", ylabel="Mean wall time / batch (seconds)",
               title="Native SPHINCS proving and verification", yscale="log", xscale="log")
        ax.set_xticks(counts, labels=[str(n) for n in counts])
        ax.legend(fontsize=10)
        save(fig, "sphincs-batch-cost", "Fresh distinct claims; metadata identifies the captured backend and strategy.")

    generic = [row for row in rows if row["kind"] == "crypto" and row["starkCount"] > 0]
    if generic:
        fig, axes = plt.subplots(1, 2, figsize=(12.5, 5))
        names = [case_label(row["case"]) for row in generic]
        positions = list(range(len(generic)))
        prove = [row["proveWallMs"] / max(1, row["measuredBatches"]) for row in generic]
        verify = [row["verifyWallMs"] / max(1, row["measuredBatches"]) for row in generic]
        axes[0].bar([n - .2 for n in positions], prove, .4, label="Build aggregate")
        axes[0].bar([n + .2 for n in positions], verify, .4, label="Verify")
        axes[1].bar(positions, [row["proofBytesMean"] / 1024 for row in generic])
        axes[0].set(ylabel="Wall time / batch (ms)", title="Generic STARK envelopes and mixed aggregation")
        axes[1].set(ylabel="Serialized proof size (KiB)", title="Generic STARK witnesses remain uncompressed")
        axes[0].legend()
        for ax in axes:
            ax.set_xticks(positions, labels=names, rotation=20, ha="right")
        save(fig, "generic-stark-cost", "Generic witnesses are generated before timing; mixed batches add timed SPHINCS proving.")

    transport = series("transport")
    if transport:
        fig, axes = plt.subplots(1, 2, figsize=(12.5, 5.0))
        for label, group in sorted(transport.items()):
            label = case_label(label)
            group.sort(key=lambda row: row["offeredObjectsPerSecond"])
            x = [row["offeredObjectsPerSecond"] for row in group]
            line, = axes[0].plot(x, [row["goodputMbps"] for row in group], "o-", label=label + " goodput")
            axes[0].plot(x, [row["wireMbps"] for row in group], "--", color=line.get_color(),
                         label=label + " RLPx")
            axes[1].plot(x, [row["p95LatencyMs"] for row in group], "o-", label=label)
        axes[0].set(xlabel="Offered objects / second", ylabel="Transfer rate (Mbit/s)",
                    title="Large-object encrypted propagation")
        axes[1].set(xlabel="Offered objects / second", ylabel="95th percentile delivery latency (ms)",
                    title="Network-only test; no crypto proof claim")
        for ax in axes:
            ax.legend(fontsize=8)
        save(fig, "large-object-throughput", "Random incompressible payloads over real TCP and production RLPx codecs.")

    mixed = [row for row in rows if row["kind"] == "mixed-traffic"]
    if mixed:
        groups = defaultdict(list)
        for row in mixed:
            groups[row["case"]].append(row)
        fig, axes = plt.subplots(1, 3, figsize=(15, 5.0))
        fig.suptitle("Whole-wrapper versus application chunks: shared paced TCP connection")
        for label, group in sorted(groups.items()):
            group.sort(key=lambda row: row["chunkBytes"])
            positions = [0 if row["chunkBytes"] == 0 else {32768: 1, 65536: 2, 131072: 3}[row["chunkBytes"]] for row in group]
            line, = axes[0].plot(positions, [row["controlP95Ms"] for row in group], "o-", label=case_label(label))
            axes[0].plot(positions, [row["controlMaxMs"] for row in group], "--", color=line.get_color())
            axes[1].plot(positions, [row["goodputMbps"] for row in group], "o-", label=case_label(label))
            axes[2].plot(positions, [row["wireBytes"] / row["payloadBytes"] for row in group], "o-", label=case_label(label))
        axes[0].set(ylabel="Control delivery latency (ms)", title="p95 solid · maximum dashed")
        axes[0].set_yscale("log")
        axes[1].set(ylabel="Useful object goodput (Mbit/s)", title="Preproved transport; crypto outside timing")
        axes[2].set(ylabel="Encrypted RLPx / object bytes", title="Includes chunk metadata and controls")
        for ax in axes:
            ax.set_xticks(range(4), labels=["Whole", "32 KiB", "64 KiB", "128 KiB"])
            ax.legend(fontsize=8)
        save(fig, "chunked-mixed-traffic", "Real TCP and production PacketSender/codecs, application-paced wire rate. Control delivery is one-way; no WAN RTT or loss simulation.")

        synthetic = {label: group for label, group in groups.items() if label.startswith("object-")}
        if synthetic:
            fig, ax = plt.subplots(figsize=(6.5, 5.2))
            for label, group in sorted(synthetic.items()):
                x = [0 if row["chunkBytes"] == 0 else {32768: 1, 65536: 2, 131072: 3}[row["chunkBytes"]] for row in group]
                line, = ax.plot(x, [row["controlP95Ms"] for row in group], "o-", label=case_label(label) + " p95")
                ax.plot(x, [row["controlMaxMs"] for row in group], "s--", color=line.get_color(), label=case_label(label) + " max")
            ax.set_yscale("log")
            ax.set_xticks(range(4), labels=["Whole", "32 KiB", "64 KiB", "128 KiB"])
            ax.set(ylabel="Control delivery latency (ms)", xlabel="Application chunk size", title=f"Headers and pings with large proof traffic\n{mixed[0]['simulatedWireMbps']:g} Mbit/s application pacing · localhost TCP")
            ax.legend(fontsize=9)
            save(fig, "chunk-control-latency", "Control p95/max during incompressible object delivery; simulated application wire cap, no WAN RTT or loss.")

            fig, ax = plt.subplots(figsize=(6.5, 4.8))
            for label, group in sorted(synthetic.items()):
                x = [0 if row["chunkBytes"] == 0 else {32768: 1, 65536: 2, 131072: 3}[row["chunkBytes"]] for row in group]
                ax.plot(x, [row["goodputMbps"] for row in group], "o-", label=case_label(label))
            ax.axhline(mixed[0]["simulatedWireMbps"], color="#9ba3b1", linestyle="--", label="Encoded-wire cap")
            ax.set_xticks(range(4), labels=["Whole", "32 KiB", "64 KiB", "128 KiB"])
            ax.set(ylabel="Useful object goodput (Mbit/s)", xlabel="Application chunk size", title="Throughput tradeoff with concurrent controls", ylim=(0, mixed[0]["simulatedWireMbps"] * 1.1))
            ax.legend(fontsize=9)
            save(fig, "chunk-object-goodput", "Production PacketSender/codecs and reassembly over real localhost TCP; no crypto or admission inside timing.")

    metadata = data.get("metadata", {})
    caveats = []
    if any(row["kind"] == "protocol-preproved" for row in rows) and "protocolScheduler" not in metadata:
        caveats.append("Archived protocol rows used a Task.Run scheduler stand-in; production scheduler limits, deadlines and block-processing pauses were not measured.")
    if "percentileMethod" not in metadata and any(row["kind"] != "mixed-traffic" for row in rows):
        caveats.append("Archived full/load/protocol percentiles use sorted[ceil((count - 1) * p)]; graphs retain stored values. Current runs use nearest-rank.")
    notes = "".join(f"<p>{html.escape(note)}</p>" for note in caveats)
    meta = html.escape(json.dumps(metadata, indent=2))
    sections = "".join(f'<section><h2>{html.escape(caption)}</h2><img src="{name}.png" alt="{html.escape(caption)}">'
                       + (f'<p><a href="{name}.svg">SVG</a></p>' if args.svg else '') + '</section>'
                       for name, caption in charts)
    sample_link = '<a href="../samples.csv">Sample CSV</a>' if (args.results.parent / "samples.csv").exists() else 'Samples are included in the JSON'
    (args.output / "index.html").write_text(
        '<!doctype html><meta charset="utf-8"><title>Lean integration measurements</title>'
        '<style>body{max-width:1400px;margin:40px auto;padding:0 24px;font:16px system-ui;color:#17233b}'
        'img{max-width:100%}section{margin:40px 0}pre{white-space:pre-wrap;background:#f4f6fa;padding:20px}</style>'
        '<h1>Lean integration measurements</h1><p>Localhost results are not WAN or mainnet capacity estimates. '
        'Load durations include drain time; consult raw rejection reasons and samples.</p>'
        f'<p><a href="../{html.escape(args.results.name)}">Raw JSON and samples</a> · '
        '<a href="../results.csv">Row CSV</a> · ' + sample_link + '</p>'
        + notes + sections + '<h2>Measurement metadata</h2><pre>' + meta + '</pre>')
    print(json.dumps({"output": str(args.output), "charts": [name for name, _ in charts]}))


if __name__ == "__main__":
    main()
