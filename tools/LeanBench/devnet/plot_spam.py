#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Plot captured load outcomes and actual block sizes; never infer native timings."""
import argparse
import csv
import datetime
import gzip
import json
import math
from pathlib import Path



def receipt_goodput(run, admissions):
    if run.get("recoveryFrom") or run.get("invalidOnly") or not run.get("inclusionVerified"):
        return None
    intended = run.get("plannedValidOffers", run.get("uniqueTransactions"))
    if not isinstance(intended, int) or isinstance(intended, bool) or intended <= 0:
        return None
    if run.get("acceptedTransactions") != intended or run.get("includedTransactions") != intended or len(admissions) != intended:
        return None
    if any(not item.get("accepted") or not item.get("validSubmissionOffered") or item.get("error") for item in admissions):
        return None
    seconds, recorded = run.get("timedRunSeconds"), run.get("goodputIncludingDrainTxPerSecond")
    if not isinstance(seconds, (int, float)) or not math.isfinite(seconds) or seconds <= 0:
        return None
    if not isinstance(recorded, (int, float)) or not math.isfinite(recorded) or not math.isclose(recorded, intended / seconds, rel_tol=1e-9):
        return None
    try:
        drain = datetime.datetime.fromisoformat(run["receiptDrainCompletedUtc"].replace("Z", "+00:00"))
        if drain.tzinfo is None:
            return None
        stops = run.get("stopEvents", [])
        for stop in stops:
            occurred = datetime.datetime.fromisoformat(stop["utc"].replace("Z", "+00:00"))
            if occurred.tzinfo is None or occurred <= drain:
                return None
    except (KeyError, TypeError, ValueError):
        return None
    if (run.get("stopReason") or run.get("error")) and not stops and run.get("error") != "Finality observation deadline exceeded":
        return None
    return recorded


def summarize(evidence):
    stages, blocks = [], {}
    for name, run in sorted(evidence["runs"].items(), key=lambda item: item[1].get("startedUtc", "")):
        recovery = bool(run.get("recoveryFrom"))
        admissions = run.get("admissions", [])
        completed_load = run.get("completed", False) and not recovery and not run.get("invalidOnly", False)
        stages.append({
            "run": name, "recovery": recovery, "completed": run.get("completed", False),
            "state": "complete" if run.get("completed") else "failed" if run.get("error") or run.get("stopReason")
                     else "finality pending" if run.get("inclusionVerified") else "in progress",
            "inclusionVerified": run.get("inclusionVerified"), "completedFinality": run.get("completedFinality"),
            "requestedRate": run.get("requestedRate"), "sharedDependency": run.get("sharedDependency", False),
            "probeCycles": 0 if recovery else len(admissions),
            "newOffers": 0 if recovery or run.get("invalidOnly") else sum(
                item.get("validSubmissionOffered", bool(item.get("accepted")) or
                         item.get("negative", {}).get("outcome") == "proofRejected") for item in admissions),
            "offerCountMethod": "explicit" if all("validSubmissionOffered" in item for item in admissions)
                                else "historical accepted/proof-rejected probe evidence; Busy probes excluded",
            "newAccepted": 0 if recovery else sum(bool(item.get("accepted")) for item in admissions),
            "includedObserved": run.get("includedTransactions", 0),
            "goodputIncludingDrain": run.get("goodputIncludingDrainTxPerSecond") if completed_load else None,
            "receiptGoodput": receipt_goodput(run, admissions),
            "receiptIncludedCount": run.get("includedTransactions", 0), "receiptTimedSeconds": run.get("timedRunSeconds"),
            "receiptGoodputScope": "shared verified dependency; transport/pool/EVM" if run.get("sharedDependency") else "fresh signature claims",
            "error": run.get("error") or run.get("stopReason"),
        })
        for block in run.get("blocks", []):
            key = block["blockHash"]
            row = {
                "blockHash": key, "number": int(block["number"], 16), "slot": block["slot"],
                "transactionCount": block["transactionCount"],
                "signatureDependencies": block["aggregation"]["signatures"],
                "genericDependencies": block["aggregation"]["genericStarks"],
                "rawDeclarations": block["aggregation"]["rawDeclarations"],
                "proofBytes": block.get("proof", {}).get("proofBytes"),
                "proofSha256": block.get("proof", {}).get("proofSha256"),
                "finalizedBoth": block.get("finalizedBoth", False),
            }
            if key in blocks:
                prior = blocks[key]
                for field in ("number", "slot", "transactionCount", "signatureDependencies", "genericDependencies", "rawDeclarations", "proofBytes", "proofSha256"):
                    if prior[field] != row[field]:
                        raise ValueError(f"Conflicting captured canonical block field: {field}")
                row["finalizedBoth"] |= prior["finalizedBoth"]
            blocks[key] = row
    return stages, sorted(blocks.values(), key=lambda row: row["number"])


def write_csv(path, rows):
    if not rows:
        return
    with path.open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def plot(evidence, out):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.ticker import MaxNLocator
    stages, blocks = summarize(evidence)
    out.mkdir(parents=True, exist_ok=True)
    write_csv(out / "stages.csv", stages)
    write_csv(out / "blocks.csv", blocks)
    plt.rcParams.update({"font.size": 11, "axes.spines.top": False, "axes.spines.right": False})
    fig, ax = plt.subplots(figsize=(8, max(4.8, 2 + .55 * len(stages))))
    y = list(range(len(stages)))
    for offset, field, label, color in ((-.24, "newOffers", "Valid offers", "#aab4c0"),
                                         (0, "newAccepted", "New accepted", "#2673bb"),
                                         (.24, "includedObserved", "Observed included", "#22936c")):
        ax.barh([i + offset for i in y], [row[field] for row in stages], height=.23, label=label, color=color)
    names = {"spam-stage1": "Initial trial", "spam-stage1-recovery": "Recovery · 300s",
             "spam-stage1-finalized": "Recovery · finality", "spam-baseline": "Partial baseline",
             "spam-baseline-retry": "Partial baseline retry", "spam-postfix-recovery": "Recovery after cache", "spam-slow-baseline": "Fresh after cache",
             "spam-postcache-01": "Fresh after cache", "spam-stage2": "Fresh",
             "spam-stage3": "Fresh", "spam-shared": "Shared dependency",
             "spam-invalid": "Invalid-only burst", "spam-finality-postfix-recovery": "Finality · recovery",
             "spam-finality-slow-baseline": "Finality · slow baseline",
             "spam-finality-postcache-01": "Finality · subset",
             "spam-finality-stage2": "Finality · partial run"}
    labels = []
    for row in stages:
        label = names.get(row["run"], row["run"])
        if row["run"] in ("spam-slow-baseline", "spam-postcache-01", "spam-stage2", "spam-stage3"):
            label += f" · {row['requestedRate']:g} tx/s" if row.get("requestedRate") is not None else ""
        labels.append(label + (" ✓" if row["completed"] else " · " + row["state"]))
    ax.set_yticks(y, labels)
    ax.invert_yaxis()
    ax.set_xlabel("Transaction count")
    ax.xaxis.set_major_locator(MaxNLocator(integer=True))
    fig.suptitle("Bounded SPHINCS trials\nAdmissions and inclusion observations", fontsize=14)
    fig.legend(*ax.get_legend_handles_labels(), loc="lower center", bbox_to_anchor=(.5, .14), ncol=3, frameon=False, fontsize=10)
    fig.text(.02, .015, "Recovery rows observe the same transactions; counts across rows must not be summed.\nPartial or recovered runs are not throughput measurements.", fontsize=9)
    fig.tight_layout(rect=(0, .23, 1, .9))
    fig.savefig(out / "load-outcomes.png", dpi=150)
    plt.close(fig)
    goodputs = [row for row in stages if row["receiptGoodput"] is not None]
    if goodputs:
        fig, ax = plt.subplots(figsize=(8, max(4.8, 2 + .7 * len(goodputs))))
        y = list(range(len(goodputs)))
        colors = ["#22936c" if row["sharedDependency"] else "#2673bb" for row in goodputs]
        values = [row["receiptGoodput"] for row in goodputs]
        ax.barh(y, values, color=colors)
        labels = []
        for row in goodputs:
            kind = "Shared claim" if row["sharedDependency"] else "Fresh claims"
            status = "finalized" if row["completedFinality"] else "later finality observer failed" if row["state"] == "failed" else "finality pending"
            labels.append(f"{kind} · offered {row['requestedRate']:g} tx/s\n{row['receiptIncludedCount']} / {row['receiptTimedSeconds']:.2f}s · {status}")
        ax.set_yticks(y, labels)
        ax.invert_yaxis()
        ax.set_xlim(0, max(values) * 1.25)
        for i, value in enumerate(values):
            ax.text(value + max(values) * .015, i, f"{value:.4f}", va="center", fontsize=10)
        ax.set_xlabel("Authenticated receipt goodput (tx/s; includes drain)")
        fig.suptitle("Complete intended receipt cohorts\nFresh claims and verified-dependency reuse", fontsize=14)
        fig.text(.02, .015, "Finite N / timed window; preparation and finality observation excluded.\nOnly all-offered, fully authenticated drains qualify. No maximum-capacity claim.\nShared claims measure transport/pool/EVM; global failures remain unchanged.", fontsize=9)
        fig.tight_layout(rect=(0, .19, 1, .9))
        fig.savefig(out / "receipt-goodput.png", dpi=150)
        plt.close(fig)
    if not blocks:
        return
    fig, ax = plt.subplots(figsize=(8, max(4.8, 2 + .3 * len(blocks))))
    y = list(range(len(blocks)))
    ax.barh(y, [(row["proofBytes"] or 0) / 1024 for row in blocks], color="#2673bb")
    ax.set_yticks(y, [f"{row['number']}: {row['transactionCount']} tx / {row['signatureDependencies']} sig" + (f" / {row['genericDependencies']} generic" if row['genericDependencies'] else "") for row in blocks])
    ax.invert_yaxis()
    ax.set_xlabel("Proof KiB (cached metadata)")
    fig.suptitle("Actual canonical blocks\nTransactions, signature claims and proof sizes", fontsize=14)
    fig.text(.02, .015, "Repeated recovery observations are deduplicated by block hash.\nProof sizes are not generation timings or a fresh-signature capacity benchmark.", fontsize=9)
    fig.tight_layout(rect=(0, .1, 1, .9))
    fig.savefig(out / "canonical-blocks.png", dpi=150)
    plt.close(fig)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    opener = gzip.open if args.input.suffix == ".gz" else open
    with opener(args.input, "rt") as source:
        plot(json.load(source), args.out)
