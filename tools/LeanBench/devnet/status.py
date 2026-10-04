#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Read-only execution/proof status; never serves node configs or Engine credentials."""
import argparse
import concurrent.futures
import datetime
import json
import math
import re
import threading
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

MAX_READ = 2 * 1024 * 1024
BLOCK_FIELDS = (
    "name", "producer", "ingress", "transactionHash", "transactionCount", "blockHash", "blockNumber", "proofBytes",
    "dependencyHash", "receiptStatus", "gossipWaitSeconds", "buildSeconds", "importSeconds",
)
MIXED_FIELDS = ("nativeProofsVerified", "completedFinality", "peerPoolPropagationObserved", "freshMixedParentInOneBlock")
LOG_NAMES = ("node1", "node2", "driver", "driver-sphincs64", "mixed-reuse", "mixed-merge")
SENSITIVE = re.compile(r"jwt|secret|private.?key|sender.?key|password|authorization", re.I)
ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
JWT = re.compile(r"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")


def safe(value):
    if isinstance(value, str):
        return "[sensitive detail omitted]" if SENSITIVE.search(value) else JWT.sub("[redacted]", value[:512])
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if value is None or isinstance(value, (bool, int, float)):
        return value
    return None


def select(value, fields):
    return {key: safe(value[key]) for key in fields if isinstance(value, dict) and key in value}


def bounded_json(path):
    with path.open("rb") as source:
        content = source.read(MAX_READ + 1)
    if len(content) > MAX_READ:
        raise ValueError("Status file exceeds size limit")
    return json.loads(content)


def wrapper_check(check):
    item = select(check, ("name", "node", "rejected"))
    error = check.get("error", {}) if isinstance(check, dict) else {}
    if not isinstance(error, dict):
        error = {}
    item.update(expected="proof verification rejection", responseCode=safe(error.get("code")),
                rejectionReason=safe(error.get("message")),
                passed=item.get("rejected") is True and error.get("code") == -32000
                and error.get("message") == "a wrapper dependency proof failed verification")
    return item


def payload_check(check):
    item = select(check, ("mutation", "status"))
    reason = check.get("validationError") if isinstance(check, dict) else None
    expected = {"proof": "InvalidRecursiveStark", "commitment": "InvalidBlockDepsHash"}.get(item.get("mutation"))
    item.update(expected=expected, rejectionReason=safe(reason),
                passed=item.get("status") == "INVALID" and expected is not None
                and isinstance(reason, str) and reason.startswith(expected + ":"))
    return item


def timestamp_check(check):
    if not isinstance(check, dict) or not check:
        return {}
    item = select(check, ("rejected", "status"))
    error = check.get("error", {})
    if not isinstance(error, dict):
        error = {}
    reason = error.get("message", "")
    item.update(expected="invalid timestamp attributes", responseCode=safe(error.get("code")),
                rejectionReason=safe(reason), passed=item.get("rejected") is True
                and error.get("code") == -38003
                and isinstance(reason, str) and reason.startswith("Invalid payload timestamp "))
    return item


def report(path, real_consensus=False):
    try:
        data = bounded_json(path)
        result = select(data, ("chainId", "genesisHash", "completed", "error"))
        if real_consensus:
            result.update(select(data, MIXED_FIELDS))
        result["blocks"] = []
        for block in data.get("blocks", [])[-200:]:
            sanitized = select(block, BLOCK_FIELDS)
            sanitized["nativeInspection"] = select(block.get("nativeInspection"),
                ("signatures", "starks", "originalNativeProofValid"))
            if real_consensus:
                sanitized.update(select(block, ("beaconAnchoredBoth", "finalizedBoth")))
                sanitized["blockNumber"] = safe(block.get("number"))
                sanitized["name"] = "Mixed root reuse" if data.get("mode") == "reuse" else "Mixed parent merge"
                inspection = block.get("nativeInspection", {})
                if not isinstance(inspection, dict):
                    inspection = {}
                hashes = inspection.get("transactionHashes")
                if isinstance(hashes, list) and len(hashes) <= 4096:
                    sanitized["transactionCount"] = len(hashes)
                    admissions = data.get("admissions", [])
                    if isinstance(admissions, list):
                        receipts = [receipt for admission in admissions[:4096] if isinstance(admission, dict)
                                    and admission.get("transactionHash") in hashes
                                    for receipt in admission.get("receipts", []) if isinstance(receipt, dict)
                                    and receipt.get("blockHash") == block.get("blockHash")]
                        if hashes and len(receipts) == 2 * len(hashes) and all(r.get("status") == "0x1" for r in receipts):
                            sanitized["receiptStatus"] = "0x1 (both ELs)"
                sanitized["dependencyHash"] = safe(inspection.get("blockDepsHash"))
            checks = block.get("negativePayloadStatus", [])
            sanitized["negativePayloadStatus"] = []
            if isinstance(checks, list):
                for check in checks[:2]:
                    sanitized["negativePayloadStatus"].append(payload_check(check))
            result["blocks"].append(sanitized)
        checks = data.get("negativeIngress", [])
        if isinstance(checks, dict):
            checks = [{"name": name, **(value if isinstance(value, dict) else {"result": value})}
                      for name, value in checks.items()]
        result["negativeIngress"] = []
        for check in checks[:30]:
            result["negativeIngress"].append(wrapper_check(check))
        result["staleAttributes"] = timestamp_check(data.get("staleAttributes"))
        return result
    except FileNotFoundError:
        return {"completed": False, "blocks": [], "state": "Waiting for the driver report"}
    except (OSError, ValueError, TypeError, AttributeError):
        return {"completed": False, "blocks": [], "state": "Driver report temporarily unavailable"}



def combined_report(root):
    smoke_path = root / "runtime/driver/report.json"
    larger_path = root / "runtime/driver-sphincs64/report.json"
    larger_started = larger_path.parent.exists()
    mixed_paths = (("Mixed root reuse", root / "runtime/mixed-reuse/report.json"),
                   ("Mixed parent merge", root / "runtime/mixed-merge/report.json"))
    mixed_started = any(path.parent.exists() for _, path in mixed_paths)
    phases = []
    if smoke_path.parent.exists() or not mixed_started:
        phases.append(("Eight-case smoke", report(smoke_path), False))
    if larger_started:
        phases.append(("SPHINCS 64", report(larger_path), False))
    for name, path in mixed_paths:
        if path.parent.exists():
            phases.append((name, report(path, real_consensus=True), True))
    latest_name, latest, latest_real = phases[-1]
    result = dict(latest)
    result["phase"] = latest_name
    result["realConsensus"] = latest_real
    result["blocks"] = []
    result["negativeIngress"] = []
    result["phases"] = []
    for name, evidence, real in phases:
        phase = select(evidence, ("completed", "state", "error") + MIXED_FIELDS)
        phase["name"] = name
        phase["realConsensus"] = real
        phase["staleAttributes"] = evidence.get("staleAttributes", {})
        result["phases"].append(phase)
        result["blocks"].extend(dict(block, phase=name) for block in evidence.get("blocks", []))
        result["negativeIngress"].extend(dict(check, phase=name) for check in evidence.get("negativeIngress", []))
    result["blocks"] = result["blocks"][-200:]
    result["negativeIngress"] = result["negativeIngress"][-60:]
    return result


def tail(path):
    try:
        with path.open("rb") as source:
            source.seek(0, 2)
            length = source.tell()
            start = max(0, length - 16 * 1024)
            source.seek(start)
            text = source.read(16 * 1024).decode("utf-8", errors="replace")
        if start:
            text = text.partition("\n")[2]
        text = ANSI.sub("", text)
        return [safe(line) for line in text.splitlines()[-50:] if not SENSITIVE.search(line)]
    except OSError:
        return ["Log not available yet"]


def rpc(port, method, params=()):
    request = urllib.request.Request(f"http://127.0.0.1:{port}",
        json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": list(params)}).encode(),
        {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=2) as response:
        data = response.read(MAX_READ + 1)
    if len(data) > MAX_READ:
        raise ValueError("RPC status response exceeds size limit")
    result = json.loads(data)
    if "error" in result:
        raise ValueError("RPC status method unavailable")
    return result["result"]


def node(port, name):
    try:
        head = rpc(port, "eth_getBlockByNumber", ("latest", False))
        peers = rpc(port, "admin_peers")
        pool = rpc(port, "txpool_status")
        return {"name": name, "online": True, "version": safe(rpc(port, "web3_clientVersion")),
                "head": select(head, ("number", "hash", "timestamp", "gasUsed", "gasLimit")),
                "peers": len(peers), "capabilities": sorted({cap for peer in peers for cap in peer.get("caps", [])
                    if isinstance(cap, str) and re.fullmatch(r"[a-z]+/\d+", cap)}),
                "pool": select(pool, ("pending", "queued"))}
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        return {"name": name, "online": False, "state": "RPC status unavailable"}


def collect_status(root, workers, ports):
    updated = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
    try:
        futures = [workers.submit(node, port, "node" + str(index + 1)) for index, port in enumerate(ports)]
        return {"updated": updated, "nodes": [future.result() for future in futures],
                "report": combined_report(root),
                "logs": {name: tail(root / "logs" / f"{name}.log")
                         for name in LOG_NAMES}}
    except Exception:
        # Keep polling after an unexpected RPC/report shape without exposing exception details.
        return {"updated": updated, "nodes": [], "logs": {},
                "report": {"blocks": [], "state": "Status temporarily unavailable"}}


HTML = """<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>Lean devnet status</title><style>body{font:15px system-ui;background:#111827;color:#e5e7eb;margin:2rem;max-width:1500px}h1{margin-bottom:.4rem}a{color:#93c5fd}.cards{display:flex;gap:1rem;flex-wrap:wrap}.card,pre{background:#1f2937;padding:1rem;border-radius:8px;overflow:auto}table{border-collapse:collapse;width:100%;font-size:13px}td,th{padding:.6rem;text-align:left;border-bottom:1px solid #374151}td{max-width:20rem;overflow-wrap:anywhere}.ok{color:#86efac}.bad{color:#fca5a5}pre{font-size:12px;white-space:pre-wrap}small{color:#9ca3af}</style>
<h1>Lean execution devnet</h1><p id=consensus>Two real Nethermind Runners. Engine test driver supplies fork choice; this page does not represent beacon consensus or Dora.</p>
<h2>Run outcomes</h2><div id=outcomes></div>
<small id=updated>Loading status…</small><div class=cards id=nodes></div><h2>Proofs and block imports</h2><p id=progress></p>
<table><thead><tr><th>Case</th><th>Block</th><th>Producer</th><th>Transactions</th><th>Proof size</th><th>Native claims / proof</th><th>Negative imports</th><th>Receipt</th><th>Gossip/build/import seconds</th><th>Block hash / dependency hash</th></tr></thead><tbody id=blocks></tbody></table>
<h2>Expected rejection checks</h2><p>PASS means an intentionally invalid input was rejected by the expected validation rule. Actual driver failures are shown separately above.</p><p id=negative-summary></p>
<table><thead><tr><th>Case / check</th><th>Node</th><th>Outcome</th><th>Expected rejection</th><th>Observed response</th></tr></thead><tbody id=negative-checks></tbody></table>
<details><summary>Rejection response details</summary><pre id=checks></pre></details><h2>Live log tails</h2><div id=logs></div><p><small>Read-only snapshots refresh every five seconds. Only selected test evidence and filtered log tails are public.</small></p>
<script>const set=(id,text)=>document.getElementById(id).textContent=text;
function cell(row,text){let c=document.createElement('td');c.textContent=text??'—';row.append(c)}
async function refresh(){try{let s=await(await fetch('/api/status',{cache:'no-store'})).json();set('updated','Updated '+new Date(s.updated).toLocaleString(undefined,{timeZoneName:'short'}));
set('consensus',s.report.realConsensus===true?'Two real Nethermind Runners with Lighthouse beacon and validator processes. Beacon consensus produces the blocks; the test driver submits wrappers and verifies results. This page is not Dora.':'Two real Nethermind Runners. Engine test driver supplies fork choice; this page does not represent beacon consensus or Dora.');
let nodes=document.getElementById('nodes');nodes.replaceChildren();for(let n of s.nodes){let d=document.createElement('pre');d.className='card '+(n.online?'ok':'bad');d.textContent=n.name+' '+(n.online?'online':'offline')+'\\n'+JSON.stringify(n,null,2);nodes.append(d)}
set('progress',(s.report.phase??'Driver')+': '+(s.report.error?'error: '+s.report.error:s.report.completed?'completed':s.report.state??'running'));
let outcomes=document.getElementById('outcomes');outcomes.replaceChildren();for(let p of s.report.phases??[]){let d=document.createElement('p');d.className=p.error?'bad':p.completed?'ok':'';d.textContent=p.name+': '+(p.error?'RUN FAILED — '+p.error:p.completed?'COMPLETED':p.state??'INCOMPLETE');if(p.realConsensus===true){d.textContent+=' | native proof '+(p.nativeProofsVerified===true?'verified':'pending')+' | peer pool '+(p.peerPoolPropagationObserved===true?'observed':'pending')+' | finality '+(p.completedFinality===true?'confirmed':'pending');if(p.name==='Mixed parent merge'){d.textContent+=' | one-block mixed root '+(p.freshMixedParentInOneBlock===true?'confirmed':'pending')}}outcomes.append(d)}
let blocks=document.getElementById('blocks');blocks.replaceChildren();for(let b of [...s.report.blocks].reverse()){let r=document.createElement('tr');cell(r,b.name);cell(r,b.blockNumber);cell(r,b.producer);cell(r,b.transactionCount??1);cell(r,typeof b.proofBytes==='number'?(b.proofBytes/1024).toFixed(1)+' KiB':'—');let n=b.nativeInspection??{};cell(r,n.originalNativeProofValid===true?(n.signatures??0)+' SPH / '+(n.starks??0)+' STARK; verified':'—');let checks=b.negativePayloadStatus??[];cell(r,checks.length?checks.filter(x=>x.passed).length+'/'+checks.length+' passed':'—');cell(r,b.receiptStatus);cell(r,[b.gossipWaitSeconds,b.buildSeconds,b.importSeconds].map(x=>typeof x==='number'?x.toFixed(2):'—').join(' / '));cell(r,(b.blockHash??'')+' / '+(b.dependencyHash??''));blocks.append(r)}
let checks=(s.report.negativeIngress??[]).map(c=>({...c,label:c.name,node:c.node}));for(let b of s.report.blocks){for(let c of b.negativePayloadStatus??[]){checks.push({...c,label:b.name+' / '+c.mutation,node:b.ingress??'import peer'})}}for(let p of s.report.phases??[]){if(p.staleAttributes?.expected){checks.push({...p.staleAttributes,label:p.name+' / stale timestamp',node:1})}}
let passed=checks.filter(c=>c.passed===true).length;set('negative-summary',passed+' / '+checks.length+' expected rejection checks passed');let negative=document.getElementById('negative-checks');negative.replaceChildren();for(let c of checks){let r=document.createElement('tr');cell(r,c.label);cell(r,c.node);cell(r,c.passed===true?'PASS — expected rejection':'FAIL — unexpected response');r.cells[2].className=c.passed===true?'ok':'bad';cell(r,c.expected);cell(r,c.status??c.responseCode);negative.append(r)}
set('checks',JSON.stringify(checks,null,2));let logs=document.getElementById('logs');logs.replaceChildren();for(let [name,lines] of Object.entries(s.logs)){let h=document.createElement('h3');h.textContent=name;let p=document.createElement('pre');p.textContent=lines.join('\\n');logs.append(h,p)}}catch(e){set('updated','Status temporarily unavailable')}setTimeout(refresh,5000)}refresh();</script></html>"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--port", type=int, default=19480)
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--node1-port", type=int, default=19145)
    parser.add_argument("--node2-port", type=int, default=19245)
    args = parser.parse_args()
    if any(not 1 <= port <= 65535 for port in (args.port, args.node1_port, args.node2_port)):
        parser.error("Ports must be in 1..65535")
    root = args.root.resolve()
    stop = threading.Event()
    lock = threading.Lock()
    snapshot = b'{"updated":"starting","nodes":[],"report":{"blocks":[]},"logs":{}}'

    def poll():
        nonlocal snapshot
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as workers:
            while not stop.is_set():
                data = collect_status(root, workers, (args.node1_port, args.node2_port))
                with lock:
                    snapshot = json.dumps(data, allow_nan=False).encode()
                stop.wait(5)

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path == "/":
                body, content_type = HTML.encode(), "text/html; charset=utf-8"
            elif self.path == "/api/status":
                with lock:
                    body = snapshot
                content_type = "application/json"
            else:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", content_type)
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Content-Security-Policy", "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *_):
            pass

    server = ThreadingHTTPServer((args.bind, args.port), Handler)
    thread = threading.Thread(target=poll, daemon=True)
    thread.start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        server.server_close()
        thread.join(timeout=10)


if __name__ == "__main__":
    main()
