#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Export fixed load-test reports without transaction, proof or credential bytes."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path

RUNS = (
    "spam-stage1", "spam-stage1-recovery", "spam-stage1-finalized", "spam-baseline",
    "spam-baseline-retry", "spam-postfix-recovery", "spam-slow-baseline", "spam-postcache-01",
    "spam-stage2", "spam-stage3", "spam-shared", "spam-invalid",
    "spam-finality-postfix-recovery", "spam-finality-slow-baseline", "spam-finality-postcache-01", "spam-finality-stage2",
)
REPORT_FIELDS = (
    "startedUtc", "arrivalStartedUtc", "finishedUtc", "receiptDrainCompletedUtc",
    "sourceRevision", "driverSha256", "manifestSha256", "fixtureOffset", "preparation",
    "requestedRate", "invalidOnly", "workers", "uniqueTransactions",
    "negativeTemplateCount", "negativeAttemptCount", "negativeCacheNotice", "initialSenderNonces",
    "validTransactionsOffered", "plannedNegativeAttempts", "plannedValidOffers",
    "invalidIngressSenderNoncesUnchanged", "stopEvents",
    "uniqueSignatureDependencies", "sharedDependency", "measurementScope",
    "completed", "inclusionVerified", "completedFinality", "observerTransportRetries",
    "observerBeaconRetries", "acceptedTransactions", "includedTransactions",
    "invalidOutcomes", "arrivalWindowSeconds", "timedRunSeconds",
    "goodputIncludingDrainTxPerSecond", "observedInterarrivalOffersPerSecond",
    "offeredAttemptsOverTimedWindowPerSecond", "arrivalWindowGoodputTxPerSecond", "interarrivalMetricScope",
    "receiptDrainSenderNonces", "finalSenderNonces", "finalityCheckpoints",
    "totalIncludingFinalitySeconds", "nodeVersions", "recoveryFrom", "error", "stopReason",
)


def select(value, fields):
    return {field: value[field] for field in fields if field in value}


def read(path):
    with path.open('rb') as stream:
        raw = stream.read(24 * 1024 * 1024 + 1)
    if len(raw) > 24 * 1024 * 1024:
        raise ValueError("Evidence exceeds capture bound")
    return json.loads(raw), hashlib.sha256(raw).hexdigest()


def export(root):
    result = {"schemaVersion": 1, "capturedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "scope": "bounded real-CL SPHINCS load; recovery observations are not throughput runs",
              "runs": {}, "resourceEvidence": {}, "diagnostics": {}}
    for name in RUNS:
        path = root / name / "report.json"
        if not path.is_file():
            continue
        report, sha = read(path)
        summary = select(report, REPORT_FIELDS)
        summary["reportSha256"] = sha
        if "interarrivalMetricScope" not in summary:
            summary["interarrivalMetricScope"] = "historical probe-cycle start spacing; not actual valid-wrapper RPC start spacing"
        summary["admissions"] = [select(item, (
            "sequence", "name", "mode", "nonce", "transactionHash", "ingressNode",
            "offeredSeconds", "accepted", "acceptedSeconds", "finishedSeconds", "error",
            "negative", "gossipObservedSeconds", "gossipSeenBeforeReceipt", "includedSeconds",
            "recoveryReceiptObservedSeconds", "receipts", "preexistingTransaction",
            "validSubmissionOffered", "negativeSubmissionOffered", "deferredByBackpressure",
            "validOfferedSeconds", "negativeOfferedSeconds", "admissionUncertain",
        )) for item in report.get("admissions", [])]
        summary["blocks"] = []
        for block in report.get("blocks", []):
            value = select(block, (
                "blockHash", "number", "slot", "gasUsed", "gasLimit", "transactionCount",
                "aggregation", "bodyDependencyUnionMatchesManifest", "beaconAnchoredBoth",
                "finalizedBoth", "finalityBeaconRecheckedBoth", "managedInspection",
            ))
            if block.get("proof") is not None:
                value["proof"] = select(block["proof"], ("proofBytes", "proofSha256", "dependencyHash"))
            if "aggregation" in value:
                value["aggregation"] = select(value["aggregation"], (
                    "rawDeclarations", "canonicalDependencies", "signatures", "genericStarks", "canonicalTriplesSha256",
                ))
            if value.get("managedInspection") is not None:
                value["managedInspection"] = select(value["managedInspection"], (
                    "blockHash", "rawDeclarations", "canonicalDependencies", "signatures", "genericStarks",
                    "dependencyHash", "cachedProofCommitmentMatchesBody",
                ))
            summary["blocks"].append(value)
        # Fixed metric fields retain sampled values; no arbitrary sampler extras are exported.
        summary["health"] = []
        for sample in report.get("health", []):
            value = select(sample, ("utc", "nodes", "consensus"))
            if sample.get("resource") is not None:
                value["resource"] = select(sample["resource"], (
                    "capturedUtc", "hostAvailableBytes", "hostCpuPercent", "elMemoryPercent",
                    "elRestarts", "elOomKilled", "elRunnerRss", "error",
                ))
            summary["health"].append(value)
        summary["readDiagnostics"] = [select(item, (
            "kind", "node", "target", "attempt", "error", "latencySeconds",
        )) for item in report.get("readDiagnostics", [])]
        metadata = path.parent / "historical-proof-metadata.json"
        if metadata.is_file():
            saved, metadata_sha = read(metadata)
            summary["proofMetadataSource"] = {
                "kind": "historical sampler snapshot; not a fresh proof-generation measurement",
                "sha256": metadata_sha, "capturedUtc": saved.get("capturedUtc"), "source": saved.get("source"),
            }
        result["runs"][name] = summary
    for name in ("spam-oom-before-recovery.json", "spam-recovered-binary-caps-baseline.json"):
        path = root / name
        if path.is_file():
            value, sha = read(path)
            data = select(value, ("capturedUtc", "rssCapturedUtc", "peakMemoryEvidence"))
            if "containers" in value:
                data["containers"] = []
                for node in value["containers"]:
                    item = select(node, ("name", "memoryBytes", "memorySwapBytes"))
                    item["state"] = select(node.get("state", {}), ("Status", "OOMKilled", "ExitCode", "StartedAt", "FinishedAt"))
                    data["containers"].append(item)
            if "nodes" in value:
                data["nodes"] = []
                for node in value["nodes"]:
                    item = select(node, ("container", "image", "configuredMemoryBytes"))
                    item["state"] = select(node.get("state", {}), ("Status", "OOMKilled", "StartedAt"))
                    item["cgroup"] = select(node.get("cgroup", {}), ("memory.current", "memory.max", "memory.swap.max", "memory.events"))
                    item["binariesSha256"] = select(node.get("binariesSha256", {}), ("Nethermind.Consensus.dll", "Nethermind.Init.dll", "libnethermind_lean.so"))
                    data["nodes"].append(item)
            if "runnerProcesses" in value:
                data["runnerProcesses"] = [{"pid": node.get("pid"), "rss": select(node.get("rss", {}), ("VmHWM", "VmRSS", "VmSwap"))} for node in value["runnerProcesses"]]
            result["resourceEvidence"][name] = {"sha256": sha, "data": data}
    for name in ("spam-production-fix-deployment.json", "spam-production-cache-deployment.json"):
        path = root / name
        if path.is_file():
            value, sha = read(path)
            data = select(value, ("capturedUtc", "sourceRevision", "packageSha256", "nativeAbi", "nativeGuestKey", "nativeUnchanged", "recoveryReplayPerformed"))
            data["nodes"] = []
            for node in value.get("nodes", []):
                item = select(node, ("container", "memoryLimitBytes", "swapLimitBytes", "memoryPlusSwapLimitBytes", "additionalSwapBytes", "oomKilled", "version",
                                     "head", "peerCount", "nativeMapped", "runnerPid"))
                item["hashes"] = select(node.get("hashes", {}), (
                    "nethermind.dll", "Nethermind.Consensus.dll", "Nethermind.Merge.Plugin.dll",
                    "Nethermind.Init.dll", "libnethermind_lean.so",
                ))
                data["nodes"].append(item)
            data["consensus"] = [{"port": node.get("port"),
                                  "sync": select(node.get("sync", {}), ("is_syncing", "is_optimistic", "el_offline", "head_slot", "sync_distance")),
                                  "finality": select(node.get("finality", {}), ("epoch", "root"))}
                                 for node in value.get("consensus", [])]
            data["health"] = select(value.get("health", {}), (
                "capturedUtc", "hostAvailableBytes", "hostCpuPercent", "elMemoryPercent",
                "elRestarts", "elOomKilled", "elRunnerRss",
            ))
            result["resourceEvidence"][path.name] = {"sha256": sha, "data": data}
    path = root / "spam-baseline-engine-diagnostic.json"
    if path.is_file():
        value, sha = read(path)
        data = select(value, ("capturedUtc", "windowUtc", "deployedSource", "fixRevision"))
        data["logs"] = [select(log, ("container", "boundedWindowLogSha256", "sanitizedLines")) for log in value.get("logs", [])]
        result["diagnostics"][path.name] = {"sha256": sha, "data": data}
    path = root / "spam-cache-fix-inclusion-timing.json"
    if path.is_file():
        value, sha = read(path)
        data = select(value, (
            "capturedUtc", "sourceRevision", "deploymentStartedUtc", "el1StartedUtc", "el2StartedUtc",
            "replayPerformedByMonitor",
        ))
        data["transactions"] = [select(item, (
            "nonce", "hash", "receiptStatus", "blockHash", "blockNumber", "blockSlot",
            "blockUtc", "blockTimestamp", "transactionCount",
        )) for item in value.get("transactions", [])]
        data["logEvidence"] = {name: [line[:512] for line in lines[:64]]
                               for name, lines in value.get("logEvidence", {}).items()}
        data["timingScope"] = "EL log candidate wall time; no recorded native-prover isolation or proof-cache-hit attribution"
        result["diagnostics"][path.name] = {"sha256": sha, "data": data}
    path = root / "spam-finality-proof-metadata.json"
    if path.is_file():
        value, sha = read(path)
        data = select(value, ("capturedUtc", "scope"))
        data["recordCount"] = len(value.get("proofs", {}))
        data["sources"] = [{"run": Path(item.get("path", "")).parent.name,
                            **select(item, ("sha256", "sourceRevision"))}
                           for item in value.get("sources", [])[:16]]
        result["diagnostics"][path.name] = {"sha256": sha, "data": data}
    path = root / "spam-beacon-timeout-diagnostic.json"
    if path.is_file():
        value, sha = read(path)
        data = select(value, ("capturedUtc", "windowUtc"))
        data["observers"] = [{
            "report": item.get("report"),
            "stopEvents": [select(stop, ("utc", "phase", "reason")) for stop in item.get("stopEvents", [])[:16]],
            "beaconErrors": [select(error, ("kind", "node", "target", "attempt", "error", "latencySeconds"))
                             for error in item.get("beaconErrors", [])[:32]],
            "healthWindowSampleCount": len(item.get("healthDuringWindow", [])),
        } for item in value.get("observers", [])[:16]]
        data["beaconLogs"] = {name: [line[:512] for line in lines[:64]]
                              for name, lines in value.get("beaconLogs", {}).items()}
        if "cpuDiagnostic" in value:
            cpu = value["cpuDiagnostic"]
            data["cpuDiagnostic"] = select(cpu, ("capturedUtc", "method", "invalidEarlySample", "nativeSourceAudit", "limits"))
            data["cpuDiagnostic"]["intermittentPeak"] = select(cpu.get("intermittentPeak", {}), (
                "observedUtc", "el1PercentApprox", "el2PercentApprox", "threadType",
            ))
            data["cpuDiagnostic"]["simultaneousAllThreadProfile"] = select(cpu.get("simultaneousAllThreadProfile", {}), (
                "finishedUtc", "seconds", "frequencyHz", "el1CpuPercent", "el2CpuPercent", "sampleCount", "symbols",
            ))
        data["causeScope"] = "Recorded bounded REST timeouts; exact scheduler, lock, or native-prover cause not established"
        result["diagnostics"][path.name] = {"sha256": sha, "data": data}
    path = root / "spam-production-cache-recovery-replay.json"
    if path.is_file():
        value, sha = read(path)
        data = select(value, ("measurementScope", "capturedUtc"))
        data["attempts"] = [select(item, (
            "nonce", "transactionHash", "knownBeforeBoth", "receiptsBeforeBoth", "submitted",
        )) for item in value.get("attempts", [])]
        result["diagnostics"][path.name] = {"sha256": sha, "data": data}
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-root", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(export(args.runtime_root), sort_keys=True, indent=2, allow_nan=False))
