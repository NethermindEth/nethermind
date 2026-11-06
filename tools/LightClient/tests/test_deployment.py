# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Offline deployment checks; requires Python 3, Docker Compose and a POSIX shell."""

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlparse

import smoke


DEPLOYMENT = Path(__file__).resolve().parents[1]
DASHBOARD = DEPLOYMENT / "observability/grafana/dashboards/light-client.json"


def compose_config(password):
    environment = os.environ.copy()
    for key in ("GRAFANA_ADMIN_PASSWORD", "LC_IMAGE", "LC_NETWORK", "LC_CHECKPOINT_URL"):
        environment.pop(key, None)
    if password is not None:
        environment["GRAFANA_ADMIN_PASSWORD"] = password
    return subprocess.run(
        ["docker", "compose", "--env-file", str(DEPLOYMENT / ".env.example"),
         "-f", str(DEPLOYMENT / "compose.yml"), "config", "--format", "json"],
        env=environment, capture_output=True, text=True, check=False,
    )


class DeploymentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = compose_config("deployment-test-password")
        if result.returncode:
            raise RuntimeError(result.stderr)
        cls.services = json.loads(result.stdout)["services"]

    def test_grafana_requires_an_explicit_password(self):
        result = compose_config(None)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("GRAFANA_ADMIN_PASSWORD", result.stderr)
        grafana = self.services["grafana"]["environment"]
        self.assertEqual(grafana["GF_AUTH_ANONYMOUS_ENABLED"], "false")
        self.assertEqual(grafana["GF_USERS_ALLOW_SIGN_UP"], "false")

    def test_only_p2p_ports_are_public(self):
        public = set()
        private = set()
        for service, config in self.services.items():
            for port in config.get("ports", []):
                mapping = (service, int(port["published"]), int(port["target"]), port["protocol"])
                if port.get("host_ip") == "127.0.0.1":
                    private.add(mapping)
                else:
                    public.add(mapping)
        self.assertEqual(private, {
            ("light-client", 8545, 8545, "tcp"),
            ("grafana", 3000, 3000, "tcp"),
        })
        self.assertEqual(public, {
            ("light-client", 30303, 30303, "tcp"),
            ("light-client", 30303, 30303, "udp"),
            ("light-client", 9050, 9050, "tcp"),
            ("light-client", 9050, 9050, "udp"),
            ("light-client", 9051, 9051, "udp"),
        })

    def test_each_service_persists_its_owned_data(self):
        for service, target in (("light-client", "/data"), ("prometheus", "/prometheus"),
                                ("grafana", "/var/lib/grafana")):
            with self.subTest(service=service):
                config = self.services[service]
                mounts = [mount for mount in config["volumes"] if mount["target"] == target]
                self.assertEqual(len(mounts), 1)
                self.assertEqual(mounts[0]["type"], "volume")
                self.assertFalse(mounts[0].get("read_only", False))
                self.assertEqual(config["restart"], "unless-stopped")

    def test_entrypoint_preserves_arguments_and_replaces_the_shell(self):
        if os.name == "nt":
            candidate = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"
            shell = str(candidate) if candidate.exists() else None
        else:
            shell = shutil.which("sh") or shutil.which("bash")
        self.assertIsNotNone(shell, "Install a POSIX shell to test the image entrypoint")
        scratch = Path("C:/Temp") if os.name == "nt" else Path(tempfile.gettempdir())
        with tempfile.TemporaryDirectory(prefix="lc-deployment-", dir=scratch) as directory:
            work = Path(directory)
            shutil.copy(DEPLOYMENT / "docker-entrypoint.sh", work)
            app = work / "Nethermind.LightClient"
            app.write_text('#!/bin/sh\nprintf "%s\\n" "$@"\n', encoding="utf-8", newline="\n")
            app.chmod(0o755)
            forwarded = ["--network", "sepolia", "--checkpoint", "one argument with spaces"]
            result = subprocess.run(
                [shell, "-c", 'trap \'printf "EXIT_TRAP\\n"\' EXIT; '
                 '. ./docker-entrypoint.sh "$@"; printf "SHELL_RETURNED\\n"',
                 "entrypoint-test", *forwarded],
                cwd=work, capture_output=True, text=True, check=False,
            )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.splitlines(), [
            "--urls", "http://0.0.0.0:8545", "--data-dir", "/data",
            "--beacon-port", "9050", "--beacon-quic-port", "9051",
            "--execution-port", "30303", *forwarded,
        ])

    def test_publish_trimming_preserves_evm_dependencies_and_symbols(self):
        shell = (str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe")
                 if os.name == "nt" else shutil.which("sh"))
        scratch = Path("C:/Temp") if os.name == "nt" else Path(tempfile.gettempdir())
        with tempfile.TemporaryDirectory(prefix="lc-publish-", dir=scratch) as directory:
            publish = Path(directory)
            retained = ("Nethermind.LightClient.dll", "Nethermind.LightClient.pdb", "ClearScript.Core.dll",
                        "ClearScript.V8.dll", "runtimes/linux-x64/native/librocksdb.so", "kzg_trusted_setup.txt")
            removed = ("ClearScript.V8.ICUData.dll", "runtimes/linux-x64/native/ClearScriptV8.linux-x64.so",
                       "runtimes/linux-arm64/native/ClearScriptV8.linux-arm64.so.pem", "Data/JSTracers/4byte.js",
                       "Nethermind.JsonRpc.SourceGenerator.Build.exe", "Nethermind.JsonRpc.SourceGenerator.Build.dll",
                       "Nethermind.JsonRpc.SourceGenerator.Build.deps.json", "Nethermind.JsonRpc.SourceGenerator.Build.runtimeconfig.json")
            for name in (*retained, *removed):
                file = publish / name
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_text("preserve contents", encoding="utf-8")
            result = subprocess.run([shell, (DEPLOYMENT / "trim-publish.sh").as_posix(), publish.as_posix()],
                                    capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual({path.relative_to(publish).as_posix() for path in publish.rglob("*") if path.is_file()}, set(retained))
            for name in retained:
                self.assertEqual((publish / name).read_text(encoding="utf-8"), "preserve contents")

    def test_dashboard_queries_scope_job_and_network(self):
        dashboard = json.loads(DASHBOARD.read_text(encoding="utf-8"))
        expressions = [target["expr"] for panel in dashboard["panels"] for target in panel["targets"]]
        used = set()
        for expression in expressions:
            with self.subTest(query=expression):
                names = set(re.findall(r"\bnethermind_light_client_(\w+)", expression))
                self.assertIn('job="nethermind-light-client"', expression)
                if names:
                    self.assertIn('network="$network"', expression)
                used.update(names)
        self.assertLessEqual({"ready", "head_fresh", "peers", "rpc_requests_total",
                              "rpc_duration_seconds_bucket"}, used)


class SmokeTests(unittest.TestCase):
    def run_smoke(self, mutation=None, *, startup_errors=0, ready_scrapes=0, wait_seconds=0, rate_value=None,
                  cold_balance_seconds=0, balance_error=False):
        samples = []
        for name, dimensions in (
            ("ready", [{}]), ("initialized", [{}]), ("startup_stage", [{"stage": "running"}]),
            ("peers", [{"layer": layer} for layer in ("beacon", "execution", "snap")]),
            ("hosted_accounts", [{"state": state} for state in ("total", "current", "failed")]),
            ("hosted_storage_slots", [{}]),
            *[(name, [{"head": head} for head in ("latest", "finalized")]) for name in
              ("head_block_number", "head_beacon_slot", "head_fresh", "head_available")],
            ("rpc_requests_total", [{"method": "eth_chainId", "outcome": "ok"}]),
            ("http_requests_total", [{"outcome": "overloaded"}]),
            ("rpc_duration_seconds_bucket", [{"method": "eth_chainId", "outcome": "ok", "le": "+Inf"}]),
        ):
            for labels in dimensions:
                samples.append(("nethermind_light_client_" + name, dict(labels, network="mainnet")))
        for name in ("process_cpu_seconds_total", "process_working_set_bytes", "process_private_memory_bytes"):
            samples.append((name, {"network": "mainnet"}))
        metrics_text = "\n".join(name + "{" + ",".join(f'{key}="{value}"' for key, value in labels.items()) + "} 1"
                                 for name, labels in samples)
        if mutation == "scrape_labels":
            metrics_text = metrics_text.replace('layer="execution"', 'layer="wrong"')
        status_calls = 0
        ready_queries = 0
        dashboard = json.loads(DASHBOARD.read_text(encoding="utf-8"))

        def respond(url, body=None, authorization=None, *, timeout=15):
            nonlocal status_calls, ready_queries
            if url.endswith("/status"):
                status_calls += 1
                if status_calls <= startup_errors:
                    raise URLError(ConnectionRefusedError("client restarting"))
                return {"startupStage": "running", "rpcInitialized": True, "latest": {"fresh": True}}
            if body is not None:
                if body["method"] == "eth_getBalance":
                    if timeout < cold_balance_seconds:
                        raise TimeoutError("Cold account proof exceeded the HTTP client timeout")
                    if balance_error:
                        return {"error": {"code": -32001, "message": "Authenticated data unavailable"}}
                return {"result": "0x1"}
            if url.endswith("/api/health"):
                return {"database": "ok"}
            if "/api/dashboards/" in url:
                if authorization is None:
                    raise HTTPError(url, 401, "Unauthorized", {}, None)
                if mutation == "dashboard_missing":
                    raise HTTPError(url, 404, "Not found", {}, None)
                provisioned = json.loads(json.dumps(dashboard))
                if mutation == "dashboard_panels":
                    provisioned["panels"] = []
                if mutation == "dashboard_query":
                    provisioned["panels"][0]["targets"][0]["expr"] = "up"
                return {"dashboard": provisioned}
            query = parse_qs(urlparse(url).query).get("query", [""])[0]
            if "rate(" in query:
                values = [] if rate_value is None else [{"metric": {}, "value": [0, rate_value]}]
                return {"status": "success", "data": {"resultType": "vector", "result": values}}
            if query.startswith("up{"):
                values = [{"metric": {"job": "nethermind-light-client"}, "value": [0, "1"]}]
            else:
                name = query.split("{", 1)[0]
                labels = dict(re.findall(r'(\w+)="([^"]*)"', query))
                values = [{"metric": dict(sample_labels, job="nethermind-light-client", __name__=sample_name), "value": [0, "1"]}
                          for sample_name, sample_labels in samples if sample_name == name
                          and all(key == "job" or sample_labels.get(key) == value for key, value in labels.items())]
                if name.endswith("_ready"):
                    ready_queries += 1
                    if ready_queries <= ready_scrapes:
                        values = []
                if "head_block_number" in name:
                    if mutation == "query_empty":
                        values = []
                    if mutation == "query_labels":
                        for value in values:
                            value["metric"]["network"] = "wrong"
            return {"status": "success", "data": {"resultType": "vector", "result": values}}

        with patch("sys.argv", ["smoke", "--prometheus-url", "http://localhost:9090", "--wait-seconds", str(wait_seconds)]), \
                patch.dict(os.environ, {"GRAFANA_ADMIN_PASSWORD": "test-password"}), \
                patch.object(smoke, "request", side_effect=respond), \
                patch.object(smoke, "urlopen") as metrics, \
                patch.object(smoke.time, "sleep") as sleep, patch("builtins.print"):
            metrics.return_value.__enter__.return_value.read.return_value = metrics_text.encode()
            smoke.main()
        return status_calls, ready_queries, sleep.call_count

    def test_smoke_accepts_no_traffic_rate_panels(self):
        for rate_value in (None, "NaN", "0"):
            with self.subTest(rate_value=rate_value):
                self.run_smoke(rate_value=rate_value)

    def test_smoke_allows_the_server_budget_for_cold_account_proofs(self):
        for seconds in (20, 90):
            with self.subTest(seconds=seconds):
                self.run_smoke(cold_balance_seconds=seconds, wait_seconds=900)

    def test_smoke_does_not_hide_an_unavailable_balance_behind_retries(self):
        with self.assertRaisesRegex(RuntimeError, "eth_getBalance failed"):
            self.run_smoke(balance_error=True, wait_seconds=900)

    def test_smoke_rejects_missing_data_labels_or_dashboard(self):
        for mutation in ("query_empty", "query_labels", "scrape_labels", "dashboard_missing", "dashboard_panels", "dashboard_query"):
            with self.subTest(mutation=mutation), self.assertRaises((RuntimeError, HTTPError)):
                self.run_smoke(mutation)

    def test_smoke_retries_connection_errors_and_the_first_ready_scrape(self):
        status_calls, ready_queries, sleeps = self.run_smoke(startup_errors=2, ready_scrapes=1, wait_seconds=900)
        self.assertEqual(status_calls, 3)
        self.assertGreaterEqual(ready_queries, 2)
        self.assertEqual(sleeps, 3)

    def test_smoke_connection_timeout_retains_the_last_failure(self):
        with self.assertRaisesRegex(RuntimeError, "client restarting"):
            self.run_smoke(startup_errors=1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
