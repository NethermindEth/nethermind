# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Check an already running deployment without creating or deleting containers."""

import argparse
import base64
import json
import math
import os
from pathlib import Path
import re
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


def request(url, body=None, authorization=None, *, timeout=15):
    data = None if body is None else json.dumps(body).encode()
    req = Request(url, data=data, headers={"Content-Type": "application/json"})
    if authorization is not None:
        req.add_header("Authorization", authorization)
    with urlopen(req, timeout=timeout) as response:
        return json.load(response)


def selectors(expression):
    for name, text in re.findall(r'\b([a-zA-Z_:][a-zA-Z0-9_:]*)\{([^{}]*)\}', expression):
        labels = dict((key, json.loads(value)) for key, value in
                      re.findall(r'(\w+)=("(?:[^"\\]|\\.)*")', text))
        yield name, labels


def scrape_series(metrics):
    series = []
    for line in metrics.splitlines():
        if not line or line.startswith("#"):
            continue
        match = re.fullmatch(r'([a-zA-Z_:][a-zA-Z0-9_:]*)(\{[^{}]*\})?\s+(\S+)(?:\s+\S+)?', line)
        if match:
            name, labels, value = match.groups()
            series.append((name, next(selectors(name + (labels or "{}")))[1], float(value)))
    return series


def dashboard_contract(dashboard):
    return [(panel["id"], panel["title"], panel["type"], panel.get("datasource"),
             [(target["refId"], target["expr"], target.get("datasource")) for target in panel["targets"]])
            for panel in dashboard.get("panels", [])]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rpc-url", default="http://127.0.0.1:8545")
    parser.add_argument("--prometheus-url", help="Optional direct Prometheus URL for isolated deployment testing")
    parser.add_argument("--grafana-url", default="http://127.0.0.1:3000")
    parser.add_argument("--network", choices=("mainnet", "sepolia", "hoodi"), default="mainnet")
    parser.add_argument("--wait-seconds", type=int, default=900)
    args = parser.parse_args()
    password = os.environ.get("GRAFANA_ADMIN_PASSWORD")
    if not password:
        raise RuntimeError("Set GRAFANA_ADMIN_PASSWORD to verify the provisioned Grafana dashboard")
    grafana_authorization = "Basic " + base64.b64encode(f"admin:{password}".encode()).decode()
    authorization = None
    if args.prometheus_url:
        query_url = args.prometheus_url + "/api/v1/query?"
    else:
        authorization = grafana_authorization
        query_url = args.grafana_url + "/api/datasources/proxy/uid/Prometheus/api/v1/query?"
    deadline = time.monotonic() + args.wait_seconds
    while True:
        try:
            status = request(args.rpc_url + "/status")
            if (status.get("startupStage") == "running" and status.get("rpcInitialized")
                    and (status.get("latest") or {}).get("fresh")):
                break
            diagnostic = f"startupStage={status.get('startupStage')}, rpcInitialized={status.get('rpcInitialized')}, latest={status.get('latest')}"
        except (URLError, ConnectionError, TimeoutError) as error:
            diagnostic = str(error)
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Client not ready: {diagnostic}")
        print(f"Waiting for fresh authenticated head: {diagnostic}", flush=True)
        time.sleep(min(5, max(0, deadline - time.monotonic())))

    chain_id = {"mainnet": 1, "sepolia": 11155111, "hoodi": 560048}[args.network]
    for method, params, expected in (
        ("eth_chainId", [], hex(chain_id)),
        ("eth_getBalance", ["0xde198901C5ee4611142E0c61DfbbCEc7ab468F32", "latest"], None),
    ):
        started = time.monotonic()
        result = request(args.rpc_url + "/", {"jsonrpc": "2.0", "id": 1, "method": method, "params": params}, timeout=95)
        if "error" in result or "result" not in result:
            raise RuntimeError(f"{method} failed: {result}")
        if expected is not None and result["result"] != expected:
            raise RuntimeError(f"Unexpected chain: {result['result']}")
        print(f"{method}: {result['result']} ({time.monotonic() - started:.3f}s)", flush=True)

    with urlopen(args.rpc_url + "/metrics", timeout=15) as response:
        metrics = response.read().decode()
    samples = scrape_series(metrics)

    dashboard_path = Path(__file__).resolve().parents[1] / "observability/grafana/dashboards/light-client.json"
    dashboard = json.loads(dashboard_path.read_text(encoding="utf-8"))
    provisioned = request(args.grafana_url + "/api/dashboards/uid/" + dashboard["uid"],
                          authorization=grafana_authorization).get("dashboard", {})
    if provisioned.get("uid") != dashboard["uid"] or dashboard_contract(provisioned) != dashboard_contract(dashboard):
        raise RuntimeError("Provisioned Grafana dashboard does not match the expected panels and queries")
    queries = [target["expr"] for panel in dashboard["panels"] for target in panel["targets"]]
    for expression in queries:
        expression = expression.replace("$network", args.network).replace("$__rate_interval", "1m")
        expected = list(selectors(expression))
        if not expected:
            raise RuntimeError(f"Dashboard query has no metric selectors: {expression}")
        for name, labels in expected:
            if labels.get("job") != "nethermind-light-client":
                raise RuntimeError(f"Dashboard query selects the wrong scrape job: {expression}")
            if name != "up" and not any(sample_name == name and math.isfinite(value) and
                    all(key == "job" or sample_labels.get(key) == label for key, label in labels.items())
                    for sample_name, sample_labels, value in samples):
                raise RuntimeError(f"Scrape has no matching metric and labels for: {expression}")
        while True:
            result = request(query_url + urlencode({"query": expression}), authorization=authorization)
            if result.get("status") != "success" or result.get("data", {}).get("resultType") != "vector":
                raise RuntimeError(f"Dashboard query failed: {expression}: {result}")
            values = result["data"]["result"]
            if "rate(" in expression:
                break
            valid = bool(values) and all(math.isfinite(float(item["value"][1])) and
                any(all(item.get("metric", {}).get(key) == label for key, label in labels.items())
                    for _, labels in expected) for item in values)
            if expected[0][0] in ("up", "nethermind_light_client_ready"):
                valid = valid and all(item["value"][1] == "1" for item in values)
            if valid:
                break
            if time.monotonic() >= deadline:
                raise RuntimeError(f"Prometheus has no matching live series before the deadline: {expression}: {values}")
            print(f"Waiting for Prometheus series: {expression}", flush=True)
            time.sleep(min(5, max(0, deadline - time.monotonic())))

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
