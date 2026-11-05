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
from urllib.error import HTTPError
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

    def test_dashboard_queries_use_the_metrics_contract(self):
        dashboard = json.loads(DASHBOARD.read_text(encoding="utf-8"))
        contract = {
            "ready", "initialized", "startup_stage", "head_block_number", "head_beacon_slot",
            "head_available", "head_fresh", "peers", "hosted_accounts", "hosted_storage_slots",
            "rpc_requests_total", "rpc_duration_seconds_bucket", "rpc_duration_seconds_sum",
            "rpc_duration_seconds_count",
        }
        expressions = [target["expr"] for panel in dashboard["panels"] for target in panel["targets"]]
        used = set()
        for expression in expressions:
            with self.subTest(query=expression):
                names = set(re.findall(r"\bnethermind_light_client_(\w+)", expression))
                self.assertLessEqual(names, contract)
                self.assertIn('job="nethermind-light-client"', expression)
                if names:
                    self.assertIn('network="$network"', expression)
                used.update(names)
        self.assertLessEqual({"ready", "head_fresh", "peers", "rpc_requests_total",
                              "rpc_duration_seconds_bucket"}, used)

    def test_smoke_waits_for_the_first_ready_scrape(self):
        ready_queries = 0

        def respond(url, body=None, authorization=None):
            nonlocal ready_queries
            if url.endswith("/status"):
                return {"startupStage": "running", "rpcInitialized": True, "latest": {"fresh": True}}
            if body is not None:
                return {"result": "0x1"}
            if url.endswith("/api/health"):
                return {"database": "ok"}
            if "/api/dashboards/" in url:
                raise HTTPError(url, 401, "Unauthorized", {}, None)
            query = parse_qs(urlparse(url).query).get("query", [""])[0]
            if query == 'nethermind_light_client_ready{network="mainnet"}':
                ready_queries += 1
                return {"data": {"result": [{"value": [0, "1" if ready_queries > 1 else "0"]}]}}
            return {"status": "success", "data": {"result": []}}

        with patch("sys.argv", ["smoke", "--prometheus-url", "http://localhost:9090"]), \
                patch.object(smoke, "request", side_effect=respond), \
                patch.object(smoke, "urlopen") as metrics, \
                patch.object(smoke.time, "sleep") as sleep, patch("builtins.print"):
            metrics.return_value.__enter__.return_value.read.return_value = (
                b"nethermind_light_client_ready 1\nprocess_working_set_bytes 1024\n")
            smoke.main()
        self.assertEqual(ready_queries, 2)
        sleep.assert_called_once_with(5)


if __name__ == "__main__":
    unittest.main(verbosity=2)
