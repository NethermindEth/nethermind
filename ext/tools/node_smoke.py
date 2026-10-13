"""Starts a published Nethermind node with the ext plugin on or off and times eth_calls of a looping contract.

usage: python -I node_smoke.py <publish dir> <on|off|diag> [calls] [loop iterations, at most 2^24 - 1]

The node runs Sepolia from genesis with no peers and local ports only, on a throwaway data directory. The contract
is sent as a state override: a 100,000-iteration countdown loop that returns 42. "diag" turns the plugin's
diagnostics on, which prints what it compiles to the node's console (and times every frame, so not for timing).
"""
import json
import os
import shutil
import statistics
import subprocess
import sys
import time
import urllib.request

publish, mode = sys.argv[1], sys.argv[2]
calls = int(sys.argv[3]) if len(sys.argv) > 3 else 400
port = 18545
data = rf"C:\tmp\vmext-data-{mode}"
log_path = rf"C:\tmp\vmext-node-{mode}.log"
shutil.rmtree(data, ignore_errors=True)

env = dict(os.environ)
env["NETHERMIND_ILEVMCONFIG_ENABLED"] = "false" if mode == "off" else "true"
env["NETHERMIND_ILEVM_DIAG"] = "1" if mode == "diag" else "0"

args = [os.path.join(publish, "nethermind.exe"), "--config", "sepolia", "--data-dir", data,
        "--JsonRpc.Enabled", "true", "--JsonRpc.Host", "127.0.0.1", "--JsonRpc.Port", str(port),
        "--JsonRpc.EngineHost", "127.0.0.1", "--JsonRpc.EnginePort", "18551",
        "--Network.P2PPort", "30399", "--Network.DiscoveryPort", "30399", "--Network.MaxActivePeers", "0",
        "--Init.WebSocketsEnabled", "false",
        # A local machine short of disk: the low-space shutdown is off for this throwaway node.
        "--HealthChecks.LowStorageSpaceShutdownThreshold", "0", "--HealthChecks.LowStorageSpaceWarningThreshold", "0"]

CONTRACT = "0x00000000000000000000000000000000000c0de0"
# PUSH3 <iterations>; loop: JUMPDEST PUSH1 1 SWAP1 SUB DUP1 PUSH1 4 JUMPI; POP; MSTORE 42 at 0; RETURN 32 bytes
ITERATIONS = int(sys.argv[4]) if len(sys.argv) > 4 else 100_000
CODE = "0x62" + f"{ITERATIONS:06x}" + "5b600190038060045750602a60005260206000f3"
EXPECTED = "0x" + "00" * 31 + "2a"


def rpc(method, params, timeout=60):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    request = urllib.request.Request(f"http://127.0.0.1:{port}", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read())


with open(log_path, "w", encoding="utf-8") as log:
    node = subprocess.Popen(args, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        start = time.time()
        while True:
            try:
                rpc("eth_blockNumber", [], timeout=5)
                break
            except Exception:
                if node.poll() is not None:
                    print(f"node exited with {node.returncode}; see {log_path}")
                    sys.exit(1)
                if time.time() - start > 180:
                    print("node did not answer within 180 s")
                    sys.exit(1)
                time.sleep(1)
        print(f"[{mode}] node up in {time.time() - start:.1f} s")

        latencies, bad = [], 0
        call = [{"to": CONTRACT, "data": "0x", "gas": "0x5f5e100"}, "latest", {CONTRACT: {"code": CODE}}]
        for i in range(calls):
            t0 = time.perf_counter()
            reply = rpc("eth_call", call)
            latencies.append((time.perf_counter() - t0) * 1000)
            if reply.get("result") != EXPECTED:
                bad += 1
                if bad <= 3:
                    print(f"[{mode}] call {i}: unexpected reply {reply}")
        windows = [(0, 10), (10, 50), (50, 150), (calls // 2, calls // 2 + 150), (max(0, calls - 150), calls)]
        print(f"[{mode}] {calls} calls, {bad} wrong; median ms " + ", ".join(
            f"calls {a}-{b}: {statistics.median(latencies[a:b]):.2f}" for a, b in windows if b > a))

        # The same call shape around a contract that returns at once: what the request costs outside the loop.
        trivial = [{"to": CONTRACT, "data": "0x", "gas": "0x5f5e100"}, "latest", {CONTRACT: {"code": "0x602a60005260206000f3"}}]
        overhead = []
        for _ in range(200):
            t0 = time.perf_counter()
            rpc("eth_call", trivial)
            overhead.append((time.perf_counter() - t0) * 1000)
        print(f"[{mode}] trivial call median {statistics.median(overhead[100:]):.2f} ms")
    finally:
        node.terminate()
        try:
            node.wait(30)
        except subprocess.TimeoutExpired:
            node.kill()

text = open(log_path, encoding="utf-8", errors="replace").read()
for needle in ["Nethermind.IlEvm", "IL-EVM", "il-evm laid out", "Exception", "Failed to load plugin"]:
    hits = [line.strip() for line in text.splitlines() if needle in line]
    print(f"[{mode}] log '{needle}': {len(hits)}" + (f" e.g. {hits[0][:200]}" if hits else ""))
