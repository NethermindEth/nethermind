"""End-to-end check of the startup compile on a published node: the shipped-list path, DI, the code database and the step order.

usage: python -I startup_smoke.py <publish dir>

Starts the node on Hoodi from genesis (no peers, local ports) with the plugin on and Scope=All, reads the code hash of the
genesis deposit contract with eth_getProof, then restarts it with NETHERMIND_ILEVM_PRECOMPILE_LIST naming a file with that
hash (plus one hash that is in no database) and checks that the node logs the startup compile before it serves RPC, and
that eth_call answers the same as with the plugin off.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request

publish = sys.argv[1]
port = 18545
data = r"C:\tmp\vmext-data-startup"
DEPOSIT = "0x00000000219ab540356cbb839cbe05303d7705fa"
# get_deposit_count() and get_deposit_root()
CALLS = ["0x621fd130", "0xc5f2892f"]


def rpc(method, params, timeout=60):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    request = urllib.request.Request(f"http://127.0.0.1:{port}", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read())


def start(tag, extra_env):
    env = dict(os.environ)
    env.update({"NETHERMIND_ILEVMCONFIG_ENABLED": "true", "NETHERMIND_ILEVMCONFIG_SCOPE": "All"})
    env.update(extra_env)
    args = [os.path.join(publish, "nethermind.exe"), "--config", "hoodi", "--data-dir", data,
            "--JsonRpc.Enabled", "true", "--JsonRpc.Host", "127.0.0.1", "--JsonRpc.Port", str(port),
            "--JsonRpc.EngineHost", "127.0.0.1", "--JsonRpc.EnginePort", "18551",
            "--Network.P2PPort", "30399", "--Network.DiscoveryPort", "30399", "--Network.MaxActivePeers", "0",
            "--Init.WebSocketsEnabled", "false",
            "--HealthChecks.LowStorageSpaceShutdownThreshold", "0", "--HealthChecks.LowStorageSpaceWarningThreshold", "0"]
    log_path = rf"C:\tmp\vmext-node-startup-{tag}.log"
    log = open(log_path, "w", encoding="utf-8")
    node = subprocess.Popen(args, env=env, stdout=log, stderr=subprocess.STDOUT)
    started = time.time()
    while True:
        try:
            rpc("eth_blockNumber", [], timeout=5)
            break
        except Exception:
            if node.poll() is not None:
                raise SystemExit(f"node exited early, see {log_path}")
            if time.time() - started > 300:
                node.kill()
                raise SystemExit(f"node not up after 300 s, see {log_path}")
            time.sleep(1)
    return node, log, log_path, time.time() - started


def stop(node, log):
    node.terminate()
    try:
        node.wait(timeout=60)
    except subprocess.TimeoutExpired:
        node.kill()
    log.close()


def answers():
    return [rpc("eth_call", [{"to": DEPOSIT, "data": d}, "latest"])["result"] for d in CALLS]


shutil.rmtree(data, ignore_errors=True)
node, log, path, up = start("probe", {"NETHERMIND_ILEVMCONFIG_ENABLED": "false"})
try:
    proof = rpc("eth_getProof", [DEPOSIT, [], "latest"])["result"]
    code_hash = proof["codeHash"]
    reference = answers()
finally:
    stop(node, log)
print(f"deposit contract code hash {code_hash}; plugin-off answers {reference}")

list_path = r"C:\tmp\vmext-startup-precompile.txt"
with open(list_path, "w", encoding="utf-8") as f:
    f.write("# smoke list\n")
    f.write(f"{code_hash} ops=1 frames=1\n")
    f.write("0x" + "ab" * 32 + " not in any database\n")

node, log, path, up = start("list", {"NETHERMIND_ILEVM_PRECOMPILE_LIST": list_path})
try:
    got = answers()
finally:
    stop(node, log)
text = open(path, encoding="utf-8", errors="replace").read()
line = re.search(r"IL-EVM precompile: .*", text)
print(f"node up after {up:.1f} s; startup line: {line.group(0) if line else 'MISSING'}")
order = [m.start() for m in (re.search(r"IL-EVM precompile:", text), re.search(r"JSON RPC .*http://127\.0\.0\.1:%d" % port, text)) if m]
print(f"answers with the plugin on: {got} -> {'same' if got == reference else 'DIFFERENT'}")
ok = line is not None and "1 of 2 shipped contracts compiled" in line.group(0) and "1 not in the code database" in line.group(0) and got == reference
print("OK" if ok else "FAILED")
sys.exit(0 if ok else 1)
