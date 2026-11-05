import json
import os
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import prepare
import upstream


class NativePreparationTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.package = self.root / "testing/kurtosis"
        self.package.mkdir(parents=True)
        self.manifest = {"repository": upstream.PROFILES["rollupx"], "profile": "rollupx", "revision": "a" * 40, "core_revision": "b" * 40}
        self.args = SimpleNamespace(upstream=self.root, manifest=self.root / "upstream.json", output=self.root / "args.yaml",
            node_image="eez-composer:test", signer_image="eez-proof-signer:test", deploy_image="eez-deploy:test", follower_image="eez-nethermind:test")
        self.args.manifest.write_text(json.dumps(self.manifest))
        (self.package / "main.star").write_text('def run(plan, args):\n'
            '    eth = ethereum_package.run(resources.relay_startup_plan(plan), eth_args)\n'
            '    l1_el = participants[0].el_context\n    l1_cl = participants[0].cl_context\n'
            '    deploy = plan.run_sh(name="eez-deployments")\n'
            '    signer_cmd = "exec eez-proof-signer --l1-chain-config=/genesis/genesis.json"\n'
            '    env = {"EEZ_PROVER_URL": "http://eez-proof-signer:50061"}\n')
        (self.package / "start.sh").write_text("EEZ_SKIP_PROOF_SIGNER_BUILD")
        (self.package / "ci-args.yaml").write_text('  enable_explorers: true\n  eez_node_image: old\n  proof_signer_image: old\n  deploy_image: old\n')
        loop = 'for service in eez-node eez-proof-signer "$KURTOSIS_BUILDER_SERVICE" mev-relay-api; do true; done\n'
        self.driver = ('    timeout 30s kurtosis service logs --all "$KURTOSIS_ENCLAVE" "$service" \n'
            'L2="$l2" bash "$HERE/scripts/verify-eezl2-deployment.sh"\n'
            'timed_phase waves-and-state-chaining bash "$HERE/scripts/verify-cross-chain-waves.sh"\n'
            'timed_phase protocol-suite bash "$HERE/scripts/run-protocol-e2e.sh"\n'
            + loop + loop + 'capture_service_log eez-proof-signer\nverify_real_proof_path\nwrite_result pass 0\n')
        (self.package / "run-ci.sh").write_text(self.driver)
        (self.package / "scripts").mkdir()
        self.evidence_script = self.package / "scripts/verify-static-counter-l2.sh"
        self.evidence_script.write_text('set -euo pipefail\n'
            'causal_tmp="$1"\nblob_evidence_file="$2"\n'
            'causal_graph="$(jq -c . "$causal_tmp/output.json")"\n'
            'blob_da="$(jq -c . "$blob_evidence_file")"\n'
            'jq -n --argjson causal_graph "$causal_graph" --argjson blob_da "$blob_da" '
            "'{causal_graph: $causal_graph, blob_da: $blob_da, claims: {receipts_revalidated: true}}'\n")

    def prepare(self):
        with patch.object(prepare, "revision", side_effect=["a" * 40, "b" * 40]):
            prepare.prepare(self.args)

    def test_native_overlay_preserves_upstream_prover_command_and_assertions_and_adds_observations(self):
        self.prepare()
        main = (self.package / "main.star").read_text()
        self.assertIn('ethereum_package.run(nethermind.relay_plan(resources.relay_startup_plan(plan)), eth_args)', main)
        self.assertIn('exec eez-proof-signer --l1-chain-config=/genesis/genesis.json', main)
        self.assertIn('deploy.files_artifacts[0], l1_cl.beacon_http_url)', main)
        commands = (self.package / "run-ci.sh").read_text()
        for command in ('verify_real_proof_path', 'write_result pass 0',
            'timed_phase waves-and-state-chaining bash "$HERE/scripts/verify-cross-chain-waves.sh"',
            'timed_phase protocol-suite bash "$HERE/scripts/run-protocol-e2e.sh"'):
            self.assertIn(command, commands)
        recovery = commands.index('bash "$NETHERMIND_OBSERVE_SCRIPT" recovery')
        self.assertGreater(recovery, commands.index('timed_phase protocol-suite bash "$HERE/scripts/run-protocol-e2e.sh"'))
        self.assertLess(recovery, commands.index('capture_service_log eez-proof-signer'))
        names = json.loads((self.root / "scenario-hooks.json").read_text())
        self.assertEqual(names, ["verify-eezl2-deployment", "verify-cross-chain-waves", "run-protocol-e2e"])

    def test_native_prover_url_variable_preserves_the_upstream_route(self):
        path = self.package / "main.star"
        original = path.read_text().replace(
            '    env = {"EEZ_PROVER_URL": "http://eez-proof-signer:50061"}\n',
            '    prover_url = "http://eez-proof-signer:{}".format(PROOF_SIGNER_GRPC_PORT)\n'
            '    env = {"EEZ_PROVER_URL": prover_url,}\n')
        path.write_text(original)
        self.prepare()
        expected = original.replace(
            "ethereum_package.run(resources.relay_startup_plan(plan), eth_args)",
            "ethereum_package.run(nethermind.relay_plan(resources.relay_startup_plan(plan)), eth_args)")
        self.assertIn(expected, path.read_text(), "the original signer command and composer route are unchanged")
        self.assertNotIn("prove_capture_image", self.args.output.read_text(), "the adapter does not enable an upstream capture proxy")
        self.assertIn("verify_real_proof_path", (self.package / "run-ci.sh").read_text())

    def test_changed_native_prover_url_route_rejects_before_overlay(self):
        path = self.package / "main.star"
        original = path.read_text().replace(
            '    env = {"EEZ_PROVER_URL": "http://eez-proof-signer:50061"}\n',
            '    prover_url = "http://eez-proof-signer:{}".format(PROOF_SIGNER_GRPC_PORT)\n'
            '    env = {"EEZ_PROVER_URL": prover_url,}\n')
        for marker, replacement in (
            ("http://eez-proof-signer:{}", "http://other:{}"),
            ('"EEZ_PROVER_URL": prover_url,', '"EEZ_PROVER_URL": other_url,'),
            ('    prover_url = ', '    another_url = '),
            ('    prover_url = "http://eez-proof-signer:{}".format(PROOF_SIGNER_GRPC_PORT)\n',
                '    prover_url = "http://eez-proof-signer:{}".format(PROOF_SIGNER_GRPC_PORT)\n' * 2),
            ('"EEZ_PROVER_URL": prover_url,', '"EEZ_PROVER_URL": prover_url, "EEZ_PROVER_URL": prover_url,'),
            ("exec eez-proof-signer", "exec other-prover"),
        ):
            with self.subTest(marker=marker, replacement=replacement):
                changed = original.replace(marker, replacement)
                path.write_text(changed)
                with self.assertRaisesRegex(ValueError, "prover deployment seam"):
                    self.prepare()
                self.assertEqual(path.read_text(), changed, "unsupported routes fail before writing the overlay")

    def test_native_large_evidence_avoids_os_argument_limits_without_changing_json(self):
        self.prepare()
        directory = self.root / "evidence with spaces"
        directory.mkdir()
        blob = directory / "blobs.json"
        for size in (64, 256 * 1024):
            with self.subTest(size=size):
                causal = {"calls": ["quote\" dollar$ backtick` " + "a" * size], "success": True}
                blobs = {"payload": "0x" + "ab" * size, "proofs": [], "entries": [{"index": 7, "return_data": "0x"}]}
                (directory / "output.json").write_text(json.dumps(causal))
                blob.write_text(json.dumps(blobs))

                result = subprocess.run(["bash", str(self.evidence_script), str(directory), str(blob)],
                    capture_output=True, text=True, timeout=10, check=True)

                self.assertEqual(json.loads(result.stdout), {"causal_graph": causal, "blob_da": blobs, "claims": {"receipts_revalidated": True}})

    def test_native_evidence_invalid_json_still_fails(self):
        self.prepare()
        blob = self.root / "blob.json"
        causal = self.root / "output.json"
        for changed in (blob, causal):
            with self.subTest(file=changed.name):
                for path in (blob, causal):
                    path.write_text('{}' if path != changed else '{} {}')
                result = subprocess.run(["bash", str(self.evidence_script), str(self.root), str(blob)],
                    capture_output=True, text=True, timeout=10)
                self.assertNotEqual(result.returncode, 0, "multiple JSON documents cannot become valid evidence")

    def test_changed_native_evidence_schema_fails_instead_of_rewriting_unknown_output(self):
        self.evidence_script.write_text(self.evidence_script.read_text().replace('blob_da: $blob_da,', 'changed: $blob_da,'))
        with self.assertRaisesRegex(ValueError, "native evidence JSON boundary changed: blob_da"):
            self.prepare()

    def test_changed_native_scenario_boundary_fails_instead_of_skipping_coverage(self):
        original = (self.package / "main.star").read_text()
        for command in ('timed_phase waves-and-state-chaining', 'timed_phase protocol-suite'):
            with self.subTest(command=command):
                (self.package / "main.star").write_text(original)
                (self.package / "run-ci.sh").write_text(self.driver.replace(command, 'changed'))
                with self.assertRaisesRegex(ValueError, "native scenario boundary"):
                    self.prepare()

    def test_native_profile_cannot_silently_claim_streaming_composer_interoperability(self):
        self.manifest["streaming"] = {"wire": "prove.v2", "profile": "rollupx-native"}
        self.args.manifest.write_text(json.dumps(self.manifest))
        with self.assertRaisesRegex(ValueError, "separately reviewed"):
            self.prepare()

    def test_fresh_follower_uses_the_same_canonical_beacon_endpoint(self):
        self.prepare()
        connection = self.root / "connection"
        connection.mkdir()
        for name, value in (("l1-rpc", "http://l1:8545"), ("l1-chain-id", "7331"), ("l1-beacon", "http://beacon:5052")):
            (connection / name).write_text(value)
        output = self.root / "late"
        prepare.late(SimpleNamespace(output=output, connection=connection, follower_image="eez-nethermind:test"))
        values = json.loads((output / "args.json").read_text())
        self.assertEqual(values["l1_beacon"], "http://beacon:5052")
        self.assertIn('args["l1_beacon"]', (output / "main.star").read_text())
        overlay = (output / "nethermind.star").read_text()
        self.assertIn('environment["NETHERMIND_L1_BEACON"] = l1_beacon', overlay)
        self.assertIn('> /out/l1-beacon', overlay)


class NativeFollowerEvidenceTests(unittest.TestCase):
    def test_report_requires_real_native_observations_and_never_claims_candidate_prover(self):
        for mutation in (None, "source", "zero-proofs", "wrong-profile", "failed-restart", "empty-fresh", "missing-fresh", "streaming", "partition", "genesis-only", "missing-prefix"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                manifest = {"repository": upstream.PROFILES["rollupx"], "profile": "rollupx", "revision": "a" * 40, "core_revision": "b" * 40}
                result = {"result": "pass", "exit_code": 0, "commits": {"root": "a" * 40, "protocol": "b" * 40}, "proof_flow": {"signed_windows": 2, "remote_attestations": 2}}
                if mutation == "source": result["commits"]["root"] = "c" * 40
                if mutation == "zero-proofs": result["proof_flow"]["signed_windows"] = 0
                if mutation == "streaming": manifest["streaming"] = {"wire": "prove.v2"}
                (root / "upstream.json").write_text(json.dumps(manifest))
                (root / "result.json").write_text(json.dumps(result))
                for phase in ("verify-eezl2-deployment", "before-restart", "after-restart", "fresh-follower"):
                    observation = {"result": "pass", "phase": phase, "profile": "rollupx-native", "verified_blocks": 20, "verified_receipts": 3, "verified_native_transactions": 2}
                    if mutation == "genesis-only" and phase == "verify-eezl2-deployment": observation["verified_blocks"] = 0
                    if mutation == "missing-prefix" and phase == "verify-eezl2-deployment": del observation["verified_blocks"]
                    if mutation == "wrong-profile": observation["profile"] = "legacy-rollup0-0x76"
                    if mutation == "failed-restart" and phase == "after-restart": observation["result"] = "fail"
                    if mutation == "empty-fresh" and phase == "fresh-follower": observation["verified_native_transactions"] = 0
                    if mutation != "missing-fresh" or phase != "fresh-follower":
                        (root / (phase + ".json")).write_text(json.dumps(observation))
                args = SimpleNamespace(directory=root, revision="e" * 40, image_id="sha256:" + "f" * 64)
                with patch.dict(os.environ, {"EEZ_SUITE": "recovery" if mutation in ("partition", "genesis-only", "missing-prefix") else ""}):
                    if mutation and mutation != "partition":
                        with self.assertRaises((ValueError, FileNotFoundError)):
                            prepare.follower_report(args)
                        self.assertFalse((root / "candidate-follower.json").exists())
                    else:
                        prepare.follower_report(args)
                        report = json.loads((root / "candidate-follower.json").read_text())
                        self.assertEqual(report["role"], "follower")
                        self.assertEqual(report["prover"], "upstream")
                        self.assertEqual(report["candidate_revision"], args.revision)
                        self.assertNotIn("signed_windows", report)
                self.assertFalse((root / "candidate-prover.json").exists())

    def test_candidate_prover_role_requires_matching_successful_native_v2_evidence(self):
        for mutation in (None, "result", "profile", "wire", "candidate_revision"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                manifest = {"repository": upstream.PROFILES["rollupx"], "profile": "rollupx", "revision": "a" * 40, "core_revision": "b" * 40}
                result = {"result": "pass", "exit_code": 0, "commits": {"root": "a" * 40, "protocol": "b" * 40},
                    "proof_flow": {"signed_windows": 2, "remote_attestations": 2}}
                (root / "upstream.json").write_text(json.dumps(manifest))
                (root / "result.json").write_text(json.dumps(result))
                for phase in ("verify-eezl2-deployment", "before-restart", "after-restart", "fresh-follower"):
                    observation = {"result": "pass", "phase": phase, "profile": "rollupx-native", "verified_blocks": 20,
                        "verified_receipts": 3, "verified_native_transactions": 2}
                    (root / (phase + ".json")).write_text(json.dumps(observation))
                args = SimpleNamespace(directory=root, revision="e" * 40, image_id="sha256:" + "f" * 64)
                prover = {"result": "pass", "profile": "rollupx-native", "wire": "prove.v2",
                    "candidate_revision": args.revision, "image_id": "sha256:" + "d" * 64}
                if mutation is not None: prover[mutation] = "wrong"
                (root / "candidate-prover.json").write_text(json.dumps(prover))
                with patch.dict(os.environ, {"EEZ_SUITE": ""}):
                    if mutation is not None:
                        with self.assertRaisesRegex(ValueError, "prover evidence"):
                            prepare.follower_report(args)
                        self.assertFalse((root / "candidate-follower.json").exists())
                    else:
                        prepare.follower_report(args)
                        report = json.loads((root / "candidate-follower.json").read_text())
                        self.assertEqual(report["prover"], "Nethermind")
                        self.assertEqual(report["candidate_prover_image_id"], prover["image_id"])

    def test_native_runtime_build_is_bound_to_selected_source_without_rollup0_artifact_fallback(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args = SimpleNamespace(manifest=root / "manifest", output=root / "runtime")
            manifest = {"repository": upstream.PROFILES["rollupx"], "profile": "rollupx", "revision": "a" * 40, "core_revision": "b" * 40}
            args.manifest.write_text(json.dumps(manifest))
            with patch.object(upstream, "api", return_value={"workflow_runs": []}) as api:
                upstream.runtime(args)
            api.assert_called_once()
            self.assertEqual(api.call_args.args[1], upstream.PROFILES["rollupx"])
            selected = json.loads(args.manifest.read_text())
            self.assertEqual(selected["revision"], manifest["revision"])
            self.assertEqual(selected["core_revision"], manifest["core_revision"])
            self.assertEqual(selected["runtime"], {"source": "build-required"})


    def test_native_build_records_all_seven_binaries_and_the_container_toolchain(self):
        for mutation in (None, "missing-binary", "tool-version", "core-source"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                args = SimpleNamespace(manifest=root / "manifest", checkout=root / "source", output=root)
                args.manifest.write_text(json.dumps({"repository": upstream.PROFILES["rollupx"], "profile": "rollupx", "revision": "a" * 40, "core_revision": "b" * 40}))
                names = upstream.BINARIES + ("e2e-causal-verify", "verify_blob_evidence")
                for name in names:
                    if mutation == "missing-binary" and name == "verify_blob_evidence":
                        continue
                    (root / name).write_bytes(b"binary")
                    (root / name).chmod(0o555)
                (root / "rustc-version.txt").write_text("rustc 1.95.0 (container)\n")
                (root / "cargo-version.txt").write_text("invalid" if mutation == "tool-version" else "cargo 1.95.0 (container)\n")
                revisions = ["a" * 40, "c" * 40 if mutation == "core-source" else "b" * 40]
                with patch.object(upstream.subprocess, "check_output", side_effect=revisions) as command:
                    if mutation:
                        with self.assertRaises(ValueError):
                            upstream.record_build(args)
                    else:
                        upstream.record_build(args)
                        runtime = json.loads(args.manifest.read_text())["runtime"]
                        self.assertEqual(set(runtime["binaries"]), set(names))
                        self.assertEqual(runtime["rustc"], "rustc 1.95.0 (container)")
                        self.assertEqual(runtime["cargo"], "cargo 1.95.0 (container)")
                    self.assertTrue(all(call.args[0][0] == "git" for call in command.call_args_list))


class NativeFollowerLauncherTests(unittest.TestCase):
    def test_native_requires_and_forwards_beacon_while_rollup0_keeps_its_arguments(self):
        for profile, beacon in (("rollup0", ""), ("rollupx", ""), ("rollupx", "http://beacon:5052")):
            with self.subTest(profile=profile, beacon=beacon), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                deployment = root / "deployments.env"
                deployment.write_text("EEZ_REGISTRY_ADDRESS=registry\nEEZ_REGISTRY_DEPLOY_BLOCK=1\nEEZ_ROLLUP_ID=1\n")
                entrypoint = root / "entrypoint"
                entrypoint.write_text('#!/usr/bin/env python3\nimport json,sys\nprint(json.dumps(sys.argv[1:]))\n')
                entrypoint.chmod(0o555)
                launcher = root / "start-follower.sh"
                source = Path(__file__).with_name("start-follower.sh").read_text()
                launcher.write_text(source.replace("/out/deployments.env", str(deployment)).replace("/nethermind/entrypoint.sh", str(entrypoint)))
                environment = dict(os.environ, EEZ_PROFILE=profile, NETHERMIND_L1_RPC="http://l1:8545", NETHERMIND_L1_CHAIN_ID="7331", NETHERMIND_L1_BEACON=beacon)
                result = subprocess.run(["/bin/sh", str(launcher)], env=environment, capture_output=True, text=True, timeout=10)
                if profile == "rollupx" and not beacon:
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("NETHERMIND_L1_BEACON", result.stderr)
                else:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    arguments = json.loads(result.stdout)
                    if profile == "rollupx":
                        self.assertEqual(arguments[arguments.index("--Eez.L1BeaconUrl") + 1], beacon)
                    else:
                        self.assertNotIn("--Eez.L1BeaconUrl", arguments)
