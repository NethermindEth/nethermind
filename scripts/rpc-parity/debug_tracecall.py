#!/usr/bin/env python3
"""Run the retained 304-case traceCall inventory against a pinned Geth reference."""
import argparse
import copy
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from typing import Any
import urllib.request

REFERENCE = "23408c2b17c3f4e094921d2ff305bfef018b1ace"
STATE_ERROR = re.compile(r"missing trie node|historical state|pruned|no state available|state (?:is )?(?:not available|unavailable|not found)", re.I)
RESERVED = {"0x" + format(value, "040x") for value in range(0x1001, 0x1005)}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def request(identifier, method, params):
    return {"jsonrpc": "2.0", "id": identifier, "method": method, "params": params}


def index(rows, ids):
    require(isinstance(rows, list), "Expected a JSON-RPC batch response")
    require(len(rows) == len(ids), "Missing or duplicate responses")
    require(all(isinstance(row, dict) and row.get("jsonrpc") == "2.0" for row in rows), "Invalid response envelope")
    require({row.get("id") for row in rows} == set(ids), "Response IDs differ from request IDs")
    return {row["id"]: row for row in rows}


def result(row) -> Any:
    require(set(row) == {"jsonrpc", "id", "result"}, f"Expected result, received {row}")
    return row["result"]


def classify(case, candidate, reference):
    for row in (candidate, reference):
        require(set(row) == {"jsonrpc", "id", case["expectedKind"]}, "Unexpected response kind or fields")
        require(row["jsonrpc"] == "2.0" and row["id"] == case["id"], "Invalid case response envelope")
        if "error" in row:
            require(not STATE_ERROR.search(json.dumps(row["error"])) and row["error"].get("code") != 4444, "Execution state unavailable")
    allowance = case.get("diagnosticAllowance")
    if allowance is not None:
        require(all(set(row["error"]) == {"code", "message"} and row["error"]["code"] == -32000 for row in (candidate, reference)), "Diagnostic allowance requires exact -32000 error envelopes")
        require(candidate["error"]["message"] == allowance["candidateMessage"], "Candidate diagnostic cause/callback changed")
    if candidate == reference:
        return "exact"
    if allowance is None:
        raise ValueError("Unapproved response mismatch")
    if (case["id"], case.get("duration")) in ((22, "-1s"), (23, "0"), (24, "1ns")) and allowance["candidateMessage"] == "execution timeout":
        pattern = (r"execution timeout|execution timeout(?: at step \(<eval>:\d+:\d+\(\d+\)\))?"
                   r"    in server-side tracer function 'step'|execution timeout at Integer \(bigInt:\d+:\d+\(\d+\)\)")
        require(re.fullmatch(pattern, reference["error"]["message"]) is not None, "Unapproved expired-deadline annotation")
        return "approved_diagnostic"
    # Only source coordinates vary; cause, bounds and callback remain literal.
    pattern = re.escape(allowance["referenceMessage"])
    location = re.search(r" at (?:step|result)? ?\(?<eval>:1:\d+\(\d+\)\)?", allowance["referenceMessage"])
    if location is None:
        raise ValueError("Invalid diagnostic allowance")
    annotation = re.escape(location.group())
    annotation = re.sub(r"\d+", r"\\d+", annotation)
    pattern = pattern.replace(re.escape(location.group()), annotation)
    require(re.fullmatch(pattern, reference["error"]["message"]) is not None, "Reference diagnostic cause/callback changed")
    return "approved_diagnostic"


def materialize(corpus, block):
    require(len(block["transactions"]) >= 2, "Fixture requires two real prefix transactions")
    sender = block["transactions"][0]["from"]
    require(sender.lower() not in RESERVED, "Prefix sender collides with override fixture")
    datasets = copy.deepcopy(corpus["datasets"])
    for dataset in datasets:
        for position, (case, query) in enumerate(zip(dataset["cases"], dataset["requests"], strict=True)):
            require(query["method"] == "debug_traceCall" and query["id"] == case["id"], "Corrupt corpus")
            options = query["params"][2]
            if dataset["group"] == "14-state-selection":
                old_sender = corpus["sourceSender"]["address"]
                query = json.loads(json.dumps(query).replace(old_sender, sender))
                options = query["params"][2]
                if case.get("category") == "blockhash-next-number":
                    delta = int(options["blockOverrides"]["number"], 16) - int(corpus["sourcePins"]["core"], 16)
                    require(delta in (1, 2), "Invalid BLOCKHASH override template")
                    options["blockOverrides"]["number"] = hex(int(block["number"], 16) + delta)
                    options["stateOverrides"]["0x0000000000000000000000000000000000001001"]["code"] = "0x7f" + format(int(block["number"], 16), "064x") + "4060005260206000f3"
            selector = query["params"][1]
            if isinstance(selector, dict):
                selector["blockHash"] = block["hash"]
            else:
                query["params"][1] = block["hash"] if dataset["group"] == "16-native" else block["number"]
            dataset["requests"][position] = query
    require(sum(len(d["requests"]) for d in datasets) == 304, "Incomplete inventory")
    return datasets


def verify_oracles(datasets, responses, block):
    state = next(d for d in datasets if d["group"] == "14-state-selection")
    rows = responses[state["name"]]
    require(all(result(rows[i])["returnValue"] == block["hash"] for i in (1, 2)), "BLOCKHASH n+1 oracle failed")
    require(all(result(rows[i])["returnValue"] == "0x" + "0" * 64 for i in (3, 4)), "BLOCKHASH n+2 oracle failed")
    nonce = int(block["transactions"][0]["nonce"], 16)
    require(result(rows[5]) == {"nonce": nonce} and result(rows[7]) == {"nonce": nonce + 1}, "Prefix nonce oracle failed")
    require(result(rows[6]) == result(rows[8]) == {"nonce": 66}, "Prefix state-override oracle failed")
    caught = next(d for d in datasets if d["group"] == "2-caught-helpers")
    rows = responses[caught["name"]]
    require("error" in rows[1] and result(rows[2]) == {"value": "swallowed"}, "Helper catchability oracle failed")
    rows = responses["native-configurations"]
    for left, right, child in ((12, 15, "callTracer"), (13, 15, "prestateTracer"), (14, 15, "flatCallTracer"), (15, 16, None), (6, 7, "callTracer")):
        other = result(rows[right])
        require(result(rows[left]) == (other[child] if child else other), "Native direct/mux equivalence failed")


class Client:
    def __init__(self, url, host, args):
        self.url, self.host, self.args = url, host, args
        self.control = tempfile.TemporaryDirectory(prefix="tracecall-ssh-") if host else None

    def close(self):
        if self.control:
            try:
                subprocess.run(["ssh", "-S", self.control.name + "/socket", "-O", "exit", self.host], capture_output=True, timeout=5, check=False)
            except (OSError, subprocess.TimeoutExpired) as error:
                print("SSH cleanup warning: " + str(error), file=sys.stderr)
            finally:
                self.control.cleanup()

    def call(self, queries):
        body = json.dumps(queries).encode()
        if self.control:
            command = ["ssh", "-o", "ControlMaster=auto", "-o", "ControlPersist=30", "-o", "ControlPath=" + self.control.name + "/socket", "-i", str(self.args.ssh_key), "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=3", "-o", "UserKnownHostsFile=" + str(self.args.known_hosts), self.host,
                       "curl --fail --max-time 120 -sS -H 'Content-Type: application/json' --data-binary @- http://127.0.0.1:8545"]
            try:
                raw = subprocess.run(command, input=body, capture_output=True, check=True, timeout=135).stdout
            except subprocess.CalledProcessError as error:
                raise ValueError("SSH transport failed: " + error.stderr.decode(errors="replace")) from error
        else:
            req = urllib.request.Request(self.url, body, {"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=135) as response:
                raw = response.read()
        rows = json.loads(raw)
        index(rows, [q["id"] for q in queries])
        return rows


def metadata(client, version):
    queries = [request(1, "web3_clientVersion", []), request(2, "eth_syncing", []), request(3, "eth_getBlockByNumber", ["latest", False]), request(4, "eth_chainId", []), request(5, "net_version", [])]
    rows = client.call(queries)
    by_id = index(rows, range(1, 6))
    require(version in result(by_id[1]), "Wrong client build/version")
    require(result(by_id[2]) is False, "Client still syncing")
    require(result(by_id[4]) == "0x1" and result(by_id[5]) == "1", "Inventory requires mainnet")
    require(0 <= datetime.now(timezone.utc).timestamp() - int(result(by_id[3])["timestamp"], 16) < 300, "Stale chain head")
    return rows


def verify_versions(before, after):
    require(all(index(before[name], range(1, 6))[1] == index(after[name], range(1, 6))[1] for name in before), "Client version changed during capture")


def common_block(clients, metadata_before, explicit):
    height = int(explicit, 0) if explicit else min(int(result(index(m, range(1, 6))[3])["number"], 16) for m in metadata_before.values()) - 4
    for offset in range(1 if explicit else 32):
        headers = {name: client.call([request(1, "eth_getBlockByNumber", [hex(height - offset), True])])[0] for name, client in clients.items()}
        block = result(headers["geth"])
        candidate = result(headers["nethermind"])
        if block is None or candidate is None:
            raise ValueError("Selected block unavailable")
        fields = ("number", "hash", "stateRoot")
        require(all(block[field] == candidate[field] for field in fields), "Selected block/state differs between clients")
        require([(t["hash"], t["from"], t["nonce"]) for t in block["transactions"]] == [(t["hash"], t["from"], t["nonce"]) for t in candidate["transactions"]], "Selected transactions differ between clients")
        txs = block["transactions"]
        if len(txs) < 2 or txs[0]["from"].lower() in RESERVED or txs[0].get("authorizationList"):
            continue
        receipts = {name: client.call([request(i + 1, "eth_getTransactionReceipt", [tx["hash"]]) for i, tx in enumerate(txs[:2])]) for name, client in clients.items()}
        require(index(receipts["nethermind"], (1, 2)) == index(receipts["geth"], (1, 2)), "Prefix receipts differ between clients")
        require(all(result(row) is not None and result(row)["blockHash"] == block["hash"] for rows in receipts.values() for row in rows), "Prefix receipt fixture unavailable")
        if sum(len(result(row)["logs"]) for row in receipts["geth"]) > 0:
            return block, headers, receipts
    raise ValueError("No common retained block with two transactions and prefix logs; no cases counted as passed")


def run(args, evidence):
    corpus_path = Path(__file__).with_name("debug-tracecall-corpus.json")
    corpus = json.loads(corpus_path.read_text())
    require(corpus["schema"] == 1 and corpus["referenceCommit"] == REFERENCE, "Wrong corpus/reference")
    require(sum(len(d["cases"]) for d in corpus["datasets"]) == corpus["expectedRequests"] == 304, "Wrong corpus cardinality")
    require(sum("diagnosticAllowance" in c for d in corpus["datasets"] for c in d["cases"]) == corpus["expectedDiagnosticAllowances"] == 11, "Wrong allowance cardinality")
    evidence["corpusSha256"] = hashlib.sha256(corpus_path.read_bytes()).hexdigest()
    clients = {"nethermind": Client(args.candidate_url, args.candidate_host, args), "geth": Client(args.reference_url, args.reference_host, args)}
    try:
        versions = {"nethermind": args.candidate_version, "geth": REFERENCE[:8]}
        evidence["metadataBefore"] = {name: metadata(client, versions[name]) for name, client in clients.items()}
        block, evidence["selectedHeaders"], evidence["prefixReceipts"] = common_block(clients, evidence["metadataBefore"], args.block)
        evidence["selectedBlock"] = block
        datasets = materialize(corpus, block)
        responses = {name: {} for name in clients}
        evidence["datasets"] = []
        for dataset in datasets:
            item = copy.deepcopy(dataset)
            evidence["datasets"].append(item)
            header_query = [request(1, "eth_getBlockByNumber", [block["number"], False])]
            for phase in ("Before", "After"):
                if phase == "After":
                    item["responses"] = {name: client.call(item["requests"]) for name, client in clients.items()}
                item["headers" + phase] = {name: client.call(header_query)[0] for name, client in clients.items()}
                require(all((result(row)["hash"], result(row)["stateRoot"]) == (block["hash"], block["stateRoot"]) for row in item["headers" + phase].values()), "Block/state-root changed during capture")
            for name in clients:
                responses[name][item["name"]] = index(item["responses"][name], [c["id"] for c in item["cases"]])
            item["classifications"] = []
            for case in item["cases"]:
                item["classifications"].append(classify(case, responses["nethermind"][item["name"]][case["id"]], responses["geth"][item["name"]][case["id"]]))
            args.output.write_text(json.dumps(evidence, indent=2) + "\n")
            print(item["name"], len(item["classifications"]), "accepted", flush=True)
        for rows in responses.values():
            verify_oracles(datasets, rows, block)
        evidence["metadataAfter"] = {name: metadata(client, versions[name]) for name, client in clients.items()}
        verify_versions(evidence["metadataBefore"], evidence["metadataAfter"])
        classifications = [v for d in evidence["datasets"] for v in d["classifications"]]
        require(len(classifications) == 304, "Incomplete acceptance capture")
        evidence.update(status="completed", requests=304, exactMatches=classifications.count("exact"), approvedDiagnostics=classifications.count("approved_diagnostic"), skipped=0)

    finally:
        for client in clients.values():
            client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for client in ("candidate", "reference"):
        group = parser.add_mutually_exclusive_group(required=True)
        group.add_argument("--" + client + "-url")
        group.add_argument("--" + client + "-host", help="SSH destination, e.g. root@host")
    parser.add_argument("--ssh-key", type=Path)
    parser.add_argument("--known-hosts", type=Path)
    parser.add_argument("--candidate-version", required=True, help="Required deployed revision substring")
    parser.add_argument("--block", help="Explicit common block number; otherwise select a recent retained fixture")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    require(args.candidate_version.strip(), "Candidate revision must not be empty")
    if args.candidate_host or args.reference_host:
        require(args.ssh_key and args.ssh_key.is_file() and args.known_hosts and args.known_hosts.is_file(), "SSH requires an existing key and verified known_hosts")
    require(not args.output.exists(), "Output already exists; preserve earlier evidence")
    evidence = {"startedAt": datetime.now(timezone.utc).isoformat(), "status": "running", "referenceCommit": REFERENCE, "candidateVersionRequired": args.candidate_version}
    with args.output.open("x") as stream:
        stream.write(json.dumps(evidence) + "\n")
    try:
        run(args, evidence)
    except Exception as error:
        evidence.update(status="failed", failure=str(error))
        raise
    finally:
        evidence["finishedAt"] = datetime.now(timezone.utc).isoformat()
        args.output.write_text(json.dumps(evidence, indent=2) + "\n")


if __name__ == "__main__":
    main()
