# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Check an already running deployment without creating or deleting containers."""

import argparse
import base64
import json
import os
from pathlib import Path
import time
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


def request(url, body=None, authorization=None):
    data = None if body is None else json.dumps(body).encode()
    req = Request(url, data=data, headers={"Content-Type": "application/json"})
    if authorization is not None:
        req.add_header("Authorization", authorization)
    with urlopen(req, timeout=15) as response:
        return json.load(response)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rpc-url", default="http://127.0.0.1:8545")
    parser.add_argument("--prometheus-url", help="Optional direct Prometheus URL for isolated deployment testing")
    parser.add_argument("--grafana-url", default="http://127.0.0.1:3000")
    parser.add_argument("--network", choices=("mainnet", "sepolia", "hoodi"), default="mainnet")
    parser.add_argument("--wait-seconds", type=int, default=900)
    args = parser.parse_args()
    authorization = None
    if args.prometheus_url:
        query_url = args.prometheus_url + "/api/v1/query?"
    else:
        password = os.environ.get("GRAFANA_ADMIN_PASSWORD")
        if not password:
            raise RuntimeError("Set GRAFANA_ADMIN_PASSWORD to query the provisioned datasource through Grafana")
        authorization = "Basic " + base64.b64encode(f"admin:{password}".encode()).decode()
        query_url = args.grafana_url + "/api/datasources/proxy/uid/Prometheus/api/v1/query?"
    deadline = time.monotonic() + args.wait_seconds
    while True:
        status = request(args.rpc_url + "/status")
        if (status.get("startupStage") == "running" and status.get("rpcInitialized")
                and (status.get("latest") or {}).get("fresh")):
            break
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Client not ready: {status.get('startupStage')}")
        print(f"Waiting for fresh authenticated head: {status.get('startupStage')}", flush=True)
        time.sleep(5)

    chain_id = {"mainnet": 1, "sepolia": 11155111, "hoodi": 560048}[args.network]
    for method, params, expected in (
        ("eth_chainId", [], hex(chain_id)),
        ("eth_getBalance", ["0xde198901C5ee4611142E0c61DfbbCEc7ab468F32", "latest"], None),
    ):
        started = time.monotonic()
        result = request(args.rpc_url + "/", {"jsonrpc": "2.0", "id": 1, "method": method, "params": params})
        if "error" in result or "result" not in result:
            raise RuntimeError(f"{method} failed: {result}")
        if expected is not None and result["result"] != expected:
            raise RuntimeError(f"Unexpected chain: {result['result']}")
        print(f"{method}: {result['result']} ({time.monotonic() - started:.3f}s)", flush=True)

    with urlopen(args.rpc_url + "/metrics", timeout=15) as response:
        metrics = response.read().decode()
    if "nethermind_light_client_ready" not in metrics or "process_working_set_bytes" not in metrics:
        raise RuntimeError("LC metrics are missing")

    dashboard_path = Path(__file__).resolve().parents[1] / "observability/grafana/dashboards/light-client.json"
    dashboard = json.loads(dashboard_path.read_text(encoding="utf-8"))
    queries = [target["expr"] for panel in dashboard["panels"] for target in panel["targets"]]
    queries.append('up{job="nethermind-light-client"}')
    for expression in queries:
        expression = expression.replace("$network", args.network).replace("$__rate_interval", "1m")
        result = request(query_url + urlencode({"query": expression}), authorization=authorization)
        if result.get("status") != "success":
            raise RuntimeError(f"Dashboard query failed: {expression}: {result}")
    query = urlencode({"query": f'nethermind_light_client_ready{{network="{args.network}"}}'})
    while True:
        ready = request(query_url + query, authorization=authorization)["data"]["result"]
        if ready and all(item["value"][1] == "1" for item in ready):
            break
        if time.monotonic() >= deadline:
            raise RuntimeError("Prometheus did not observe a ready client before the deadline")
        print("Waiting for Prometheus to scrape the ready client", flush=True)
        time.sleep(5)

    if request(args.grafana_url + "/api/health").get("database") != "ok":
        raise RuntimeError("Grafana database is not healthy")
    try:
        request(args.grafana_url + "/api/dashboards/uid/nethermind-light-client")
    except HTTPError as error:
        if error.code != 401:
            raise
    else:
        raise RuntimeError("Grafana dashboard is accessible without authentication")
    print(f"Ready {args.network} client; metrics, {len(queries)} PromQL queries, and Grafana access policy passed.")


if __name__ == "__main__":
    main()
