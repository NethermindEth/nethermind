import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import prepare


class ProverBootstrapTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.launcher = self.root / "eez-proof-signer"
        shutil.copyfile(Path(__file__).with_name("start-prover.sh"), self.launcher)
        self.trace = self.root / "trace.json"
        binary = self.root / "nethermind-eez-attester"
        binary.write_text(textwrap.dedent("""\
            #!/usr/bin/env python3
            import json, os, pathlib, sys
            path = pathlib.Path(os.environ["PROVER_TRACE"])
            trace = json.loads(path.read_text()) if path.exists() else []
            importing = sys.argv[1] == "keystore-import"
            trace.append({"import": importing, "args": sys.argv[1:],
                          "stdin": sys.stdin.read() if importing else None,
                          "key_in_environment": "EEZ_PROOF_SIGNER_KEY" in os.environ,
                          "skip_consensus_calls": os.environ.get("EEZ_SKIP_CONSENSUS_LAYER_CALLS"),
                          "block_time": os.environ.get("EEZ_L2_BLOCK_TIME_SECS")})
            path.write_text(json.dumps(trace))
            if importing:
                print("key import complete")
                print(os.environ["IMPORTED_ADDRESS"])
                sys.exit(int(os.environ.get("IMPORT_EXIT", "0")))
            """))
        binary.chmod(0o555)
        self.address = "0x" + "ab" * 20
        self.environment = dict(os.environ, EEZ_PROOF_SIGNER_KEY="fixture-private-key",
            EEZ_ATTESTER_ADDRESS=self.address, EEZ_PROOF_SYSTEM="0x" + "12" * 20,
            EEZ_VKEY="0x" + "34" * 32, EEZ_ROLLUP_ID="1",
            EEZ_KEYSTORE_DIRECTORY=str(self.root / "keystore"),
            EEZ_KEYSTORE_PASSWORD_FILE=str(self.root / "password"),
            PROVER_TRACE=str(self.trace), IMPORTED_ADDRESS=self.address)
        self.environment.pop("EEZ_CI_STREAMING", None)
        self.environment.pop("EEZ_L2_BLOCK_TIME_SECS", None)
        self.environment.pop("EEZ_SKIP_CONSENSUS_LAYER_CALLS", None)

    def run_launcher(self):
        return subprocess.run(["/bin/sh", str(self.launcher), "--listen-addr=0.0.0.0:50061",
            "--chain-config=/out/l2-genesis.json"], env=self.environment, text=True,
            capture_output=True, timeout=10)

    def test_imports_via_stdin_and_keeps_upstream_wire_arguments(self):
        self.environment["IMPORTED_ADDRESS"] = "0x" + "AB" * 20
        result = self.run_launcher()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = json.loads(self.trace.read_text())
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[0]["stdin"], "fixture-private-key")
        self.assertNotIn("fixture-private-key", calls[0]["args"])
        self.assertEqual(calls[1]["args"], ["--listen-addr=0.0.0.0:50061", "--chain-config=/out/l2-genesis.json"])
        self.assertFalse(calls[1]["key_in_environment"])
        self.assertEqual(calls[1]["block_time"], "2")
        self.assertEqual(calls[1]["skip_consensus_calls"], "true")
        self.assertEqual((self.root / "password").stat().st_mode & 0o777, 0o600)
        self.assertNotIn("fixture-private-key", result.stdout + result.stderr)

    def test_streaming_dispatch_keeps_arguments_and_removes_imported_private_key(self):
        launcher = self.root / "start-streaming.sh"
        launcher.write_text('exec "$(dirname "$0")/nethermind-eez-attester" "$@" --streaming-selected\n')
        self.environment["EEZ_CI_STREAMING"] = "true"
        result = self.run_launcher()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = json.loads(self.trace.read_text())
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[1]["args"], ["--listen-addr=0.0.0.0:50061", "--chain-config=/out/l2-genesis.json", "--streaming-selected"])
        self.assertFalse(calls[1]["key_in_environment"])

    def test_legacy_consensus_execution_can_be_selected_for_an_old_source_replay(self):
        self.environment["EEZ_SKIP_CONSENSUS_LAYER_CALLS"] = "false"
        result = self.run_launcher()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(self.trace.read_text())[1]["skip_consensus_calls"], "false")

    def test_missing_deployment_binding_never_starts_import(self):
        for name in ("EEZ_PROOF_SIGNER_KEY", "EEZ_ATTESTER_ADDRESS", "EEZ_PROOF_SYSTEM", "EEZ_VKEY", "EEZ_ROLLUP_ID"):
            with self.subTest(name=name):
                saved = self.environment.pop(name)
                try:
                    self.assertNotEqual(self.run_launcher().returncode, 0)
                    self.assertFalse(self.trace.exists())
                finally:
                    self.environment[name] = saved

    def test_wrong_imported_signer_never_starts_server(self):
        self.environment["IMPORTED_ADDRESS"] = "0x" + "cd" * 20
        result = self.run_launcher()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("does not match", result.stderr)
        self.assertEqual(len(json.loads(self.trace.read_text())), 1)

    def test_failed_import_cannot_start_server_even_after_printing_matching_address(self):
        self.environment["IMPORT_EXIT"] = "1"
        self.assertNotEqual(self.run_launcher().returncode, 0)
        self.assertEqual(len(json.loads(self.trace.read_text())), 1)


class ProverDeploymentSeamTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.package = self.root / "testing/kurtosis"
        self.package.mkdir(parents=True)
        self.original = ('def run(plan, args):\n'
            '    eth = ethereum_package.run(plan, eth_args)\n'
            '    l1_el = participants[0].el_context\n'
            '    deploy = plan.run_sh(name="eez-deployments")\n'
            '    signer_cmd = "exec eez-proof-signer"\n'
            '    env = {"EEZ_PROVER_URL": "http://eez-proof-signer:50061"}\n')
        (self.package / "main.star").write_text(self.original)
        (self.package / "start.sh").write_text("EEZ_SKIP_PROOF_SIGNER_BUILD")
        (self.package / "ci-args.yaml").write_text(
            "  enable_explorers: true\n  eez_node_image: old\n  proof_signer_image: old\n  deploy_image: old\n")
        self.driver = ('bash "$HERE/scripts/verify-cross-chain-waves.sh"\n'
            'test "$signed_window_count" -gt 0\n'
            'capture_service_log eez-proof-signer\n'
            'for service in eez-node eez-proof-signer "$KURTOSIS_BUILDER_SERVICE" mev-relay-api; do\n')
        (self.package / "run-ci.sh").write_text(self.driver)
        manifest = self.root / "manifest.json"
        manifest.write_text(json.dumps({"repository": "eez-association/eez-rollup0", "revision": "a" * 40, "core_revision": "b" * 40}))
        self.args = SimpleNamespace(upstream=self.root, manifest=manifest, output=self.root / "args.yaml",
            node_image="eez-composer:test", signer_image="eez-proof-signer:test",
            deploy_image="eez-deploy:test", follower_image="eez-nethermind:test")

    def prepare(self):
        with patch.object(prepare, "revision", side_effect=["a" * 40, "b" * 40]):
            prepare.prepare(self.args)

    def test_supported_signer_preserves_upstream_proof_assertion_and_command(self):
        self.prepare()
        expected = self.original.replace("ethereum_package.run(plan, eth_args)",
            "ethereum_package.run(nethermind.relay_plan(plan), eth_args)")
        self.assertIn(expected, (self.package / "main.star").read_text())
        self.assertIn('test "$signed_window_count" -gt 0', (self.package / "run-ci.sh").read_text())
        self.assertIn("proof_signer_image: eez-proof-signer:test", self.args.output.read_text())

    def test_streaming_capture_keeps_complete_frozen_proof_evidence_and_upstream_assertion(self):
        manifest = json.loads(self.args.manifest.read_text())
        manifest["streaming"] = {"wire": "prove.v2", "profile": "rollup0-conditional"}
        self.args.manifest.write_text(json.dumps(manifest))
        with (self.package / "ci-args.yaml").open("a") as target:
            target.write("  prover_backend: stateless\n")
        capture = '    timeout 30s kurtosis service logs "$KURTOSIS_ENCLAVE" "$service" '
        readiness = 'timeout 10s kurtosis service logs "$KURTOSIS_ENCLAVE" eez-node'
        (self.package / "run-ci.sh").write_text(readiness + "\n" + capture + "\n" + self.driver)
        self.prepare()
        commands = (self.package / "run-ci.sh").read_text()
        self.assertIn('[[ ! -s "$RESULT_DIR/$service.log" ]] || return 0', commands)
        self.assertIn('kurtosis service logs --all "$KURTOSIS_ENCLAVE" "$service"', commands)
        self.assertIn('kurtosis service logs --all "$KURTOSIS_ENCLAVE" eez-node', commands)
        self.assertIn('test "$signed_window_count" -gt 0', commands)

    def test_changed_relay_startup_boundary_rejects_before_overlay(self):
        changed = self.original.replace("ethereum_package.run(plan, eth_args)", "changed")
        (self.package / "main.star").write_text(changed)
        with self.assertRaisesRegex(ValueError, "relay startup boundary"):
            self.prepare()
        self.assertEqual((self.package / "main.star").read_text(), changed)

    def test_missing_skip_switch_rejects_an_otherwise_valid_deployment(self):
        (self.package / "start.sh").write_text("")
        with self.assertRaisesRegex(ValueError, "candidate prover image"):
            self.prepare()
        self.assertEqual((self.package / "main.star").read_text(), self.original)

    def test_each_changed_signer_boundary_rejects_before_overlay(self):
        for marker in ('exec eez-proof-signer', '"EEZ_PROVER_URL": "http://eez-proof-signer:'):
            with self.subTest(marker=marker):
                changed = self.original.replace(marker, "changed")
                (self.package / "main.star").write_text(changed)
                with self.assertRaisesRegex(ValueError, "prover deployment seam"):
                    self.prepare()
                self.assertEqual((self.package / "main.star").read_text(), changed)


if __name__ == "__main__":
    unittest.main()
