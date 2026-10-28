import base64
import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import prepare
import upstream


class StreamingSourceTests(unittest.TestCase):
    def invoke(self, directory, factory="EthBlockExecutorFactory", main_streaming=False, changed=False, merged=False, revision=""):
        head, core = "a" * 40, "b" * 40
        selected = revision or (head if main_streaming else upstream.REVIEWED_STREAMING_REVISION)
        client = b"StreamingProverClient .prove_stream(request)"
        execution = ("type BlockExecutorFactory = " + factory + "<Receipt>;").encode()
        sources = {"crates/eez-prover-client/src/lib.rs": client, "crates/eez-evm/src/lib.rs": execution}
        blobs = {hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest(): data for data in sources.values()}
        entries = [{"path": path, "type": "blob", "sha": hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()} for path, data in sources.items()]
        calls = []
        def api(path):
            calls.append(path)
            if path == "commits/main": return {"sha": head}
            if path == "git/trees/" + head + "?recursive=1" and not main_streaming: return {"tree": []}
            if path == "git/trees/" + selected + "?recursive=1": return {"tree": entries}
            if path == "git/trees/" + selected: return {"tree": [{"path": "eez-core-protocol", "mode": "160000", "sha": core}]}
            if path == "pulls/206": return {"merged": merged, "head": {"sha": "c" * 40 if changed else upstream.REVIEWED_STREAMING_REVISION, "repo": {"full_name": upstream.REPOSITORY}}}
            if path.startswith("git/blobs/"):
                blob = path.split("/")[-1]
                data = blobs[blob]
                return {"sha": blob, "encoding": "base64", "size": len(data), "content": base64.b64encode(data).decode()}
            raise AssertionError(path)
        args = SimpleNamespace(revision=revision, streaming=True, output=directory / "manifest.json")
        with patch.object(upstream, "api", side_effect=api), patch.dict(upstream.os.environ, GITHUB_OUTPUT=str(directory / "outputs")):
            upstream.resolve(args)
        return json.loads(args.output.read_text()), calls

    def test_before_merge_freezes_reviewed_source_core_and_selected_execution(self):
        for factory, skip_calls in (("EthBlockExecutorFactory", False), ("EezBlockExecutorFactory", True)):
            with self.subTest(factory=factory), tempfile.TemporaryDirectory() as directory:
                manifest, calls = self.invoke(Path(directory), factory=factory)
                self.assertEqual(manifest["revision"], upstream.REVIEWED_STREAMING_REVISION)
                self.assertEqual(manifest["core_revision"], "b" * 40)
                self.assertEqual(manifest["main_at_resolution"], "a" * 40)
                self.assertEqual(manifest["streaming"]["source"], "reviewed-pr206-before-merge")
                self.assertEqual(manifest["streaming"]["skip_consensus_layer_calls"], skip_calls)
                self.assertEqual(calls.count("commits/main"), 1)

    def test_streaming_main_updates_automatically_and_selects_execution_from_immutable_source(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest, calls = self.invoke(Path(directory), factory="EezBlockExecutorFactory", main_streaming=True)
            self.assertEqual(manifest["revision"], "a" * 40)
            self.assertEqual(manifest["streaming"]["source"], "public-main")
            self.assertTrue(manifest["streaming"]["skip_consensus_layer_calls"])
            self.assertNotIn("pulls/206", calls)

    def test_changed_pr_merged_missing_composer_and_unknown_execution_cannot_fall_back(self):
        for options in ({"changed": True}, {"merged": True}, {"factory": "UnknownFactory"}, {"revision": "a" * 40}):
            with self.subTest(options=options), tempfile.TemporaryDirectory() as directory:
                with self.assertRaises(ValueError):
                    self.invoke(Path(directory), **options)
                self.assertFalse((Path(directory) / "manifest.json").exists())

    def test_explicit_reviewed_revision_is_replayable_before_merge(self):
        for revision in (upstream.REVIEWED_STREAMING_REVISION, "0b31df7edbe3e062fffdd18ab31d5911da3b51f3", "21e0805628f4bd80bcf41c9d2b8f58c179984072", "32549bff918f5d9ac14ed8a78eace22e5c114d4c", "30e6a1fc319d1bf02a4251dbbee240cefcf9310c"):
            with self.subTest(revision=revision), tempfile.TemporaryDirectory() as directory:
                manifest, calls = self.invoke(Path(directory), revision=revision)
                self.assertEqual(manifest["revision"], revision)
                self.assertEqual(manifest["streaming"]["source"], "reviewed-pr206-replay")
                self.assertFalse(any(path.startswith("compare/") for path in calls))

    def test_source_blob_digest_size_and_encoding_are_checked(self):
        data = b"source"
        blob = hashlib.sha1(b"blob 6\0" + data).hexdigest()
        original = {"sha": blob, "encoding": "base64", "size": 6, "content": base64.b64encode(data).decode()}
        for changes in ({"size": 7}, {"content": base64.b64encode(b"change").decode()}, {"encoding": "none"}, {"size": 2 * 1024 * 1024}):
            with self.subTest(changes=changes), patch.object(upstream, "api", return_value=dict(original, **changes)):
                with self.assertRaises(ValueError): upstream.source_blob({"sha": blob})


class StreamingEvidenceTests(unittest.TestCase):
    def test_requires_v2_signed_windows_matching_passed_upstream_source_and_counts(self):
        self.check_profile(False)
        self.check_profile(True)

    def check_profile(self, native):
        for mutation in (None, "v1", "absent", "count", "digest", "result", "source", "remote", "partition", "genesis-only", "missing-prefix"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                manifest = {"revision": "a" * 40, "core_revision": "b" * 40, "streaming": {"wire": "prove.v2", "profile": "rollup0-conditional"}}
                if native:
                    manifest = {"profile": "rollupx", "repository": "eez-association/eez-rollupX", "revision": "a" * 40, "core_revision": "b" * 40}
                result = {"result": "pass", "exit_code": 0, "commits": {"root": "a" * 40, "protocol": "b" * 40}, "proof_flow": {"signed_windows": 1, "remote_attestations": 2}}
                fields = {"wire": "prove.v2", "validated_from_block": 1, "validated_to_block": 6, "recomputed_public_inputs_hash": "0x" + "cd" * 32}
                if mutation == "v1": fields["wire"] = "prove.v1"
                if mutation == "count": result["proof_flow"]["signed_windows"] = 2
                if mutation == "digest": fields["recomputed_public_inputs_hash"] = "0x01"
                if mutation == "result": result["result"] = "fail"
                if mutation == "source": result["commits"]["root"] = "c" * 40
                if mutation == "remote": result["proof_flow"]["remote_attestations"] = 0
                (root / "upstream.json").write_text(json.dumps(manifest))
                (root / "result.json").write_text(json.dumps(result))
                event = json.dumps({"event_name": "eez.proof_signer.window_signed", "fields": fields}, separators=(",", ":"))
                (root / "eez-proof-signer.log").write_text("" if mutation == "absent" else "[log prefix] " + event + "\n")
                (root / "scenario-hooks.json").write_text(json.dumps(["verify-eezl2-deployment", "verify-cross-chain-waves"]))
                for phase in ("verify-eezl2-deployment", "verify-cross-chain-waves"):
                    observation = {"result": "pass", "verified_blocks": 20}
                    if mutation == "genesis-only": observation["verified_blocks"] = 0
                    if mutation == "missing-prefix": del observation["verified_blocks"]
                    (root / (phase + ".json")).write_text(json.dumps(observation))
                args = SimpleNamespace(directory=root, revision="e" * 40, image_id="sha256:" + "f" * 64)
                with patch.dict(os.environ, {"EEZ_SUITE": "wave-mixed" if mutation in ("partition", "genesis-only", "missing-prefix") else ""}):
                    if mutation and mutation != "partition":
                        with self.assertRaises(ValueError): prepare.report(args)
                        self.assertFalse((root / "candidate-prover.json").exists())
                    else:
                        prepare.report(args)
                        report = json.loads((root / "candidate-prover.json").read_text())
                        self.assertEqual(report["wire"], "prove.v2")
                        self.assertEqual(report["signed_windows"], [fields])
                        self.assertEqual(report["image_id"], args.image_id)


class StreamingProxyTests(unittest.TestCase):
    def test_generated_proxy_allows_only_v2_with_verified_backend_and_pinned_client(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            launcher = root / "start-streaming.sh"
            shutil.copyfile(Path(__file__).with_name("start-streaming.sh"), launcher)
            proxy = root / "haproxy"
            proxy.write_text('#!/bin/sh\nexit 7\n')
            proxy.chmod(0o555)
            environment = dict(os.environ, PATH=str(root) + os.pathsep + os.environ["PATH"], EEZ_KEYSTORE_DIRECTORY=str(root / "keystore"))
            result = subprocess.run(["bash", str(launcher), "--listen-addr=0.0.0.0:50061", "--chain-config=/out/l2-genesis.json"],
                env=environment, text=True, capture_output=True, timeout=30)
            self.assertEqual(result.returncode, 7, result.stderr)
            stream = root / "keystore/streaming"
            config = (stream / "haproxy.cfg").read_text()
            self.assertIn("bind :50061 proto h2", config)
            self.assertIn("http-request deny unless { path /prove.v2.Prover/ProveStream /prove.v2.Prover/Capabilities }", config)
            self.assertIn("http-request set-uri https://eez-ci-server%[pathq]", config)
            self.assertIn("log stdout format raw local0", config)
            self.assertIn("option httplog", config)
            self.assertIn("ssl alpn h2 verify required", config)
            self.assertIn("ca-file " + str(stream / "server.crt"), config)
            self.assertIn("verifyhost eez-ci-server sni str(eez-ci-server)", config)
            self.assertIn("crt " + str(stream / "client.pem"), config)
            settings = json.loads((stream / "config.json").read_text())
            self.assertEqual(settings["profile"], "rollup0-conditional")
            self.assertEqual(settings["listenAddress"], "127.0.0.1:50062")
            self.assertEqual(settings["composerCertificates"], ["client.cer"])
            self.assertEqual(settings["maxSessionsPerOwner"], 4)
            self.assertGreaterEqual(settings["maxReservedBytes"], 8 * 64 * 1024 * 1024 * settings["maxSessionsPerOwner"])
            self.assertEqual((stream / "server.pfx").stat().st_mode & 0o777, 0o600)
            self.assertNotIn((stream / "password").read_text().strip(), result.stdout + result.stderr)

    def test_native_proxy_uses_deployed_identity_and_forwards_only_the_known_limit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            launcher = root / "start-streaming.sh"
            shutil.copyfile(Path(__file__).with_name("start-streaming.sh"), launcher)
            proxy = root / "haproxy"
            proxy.write_text('#!/bin/bash\nif [[ "$1" == -c ]]; then exit 0; fi\n'
                'for ((n=0;n<1000;n++)); do [[ ! -f "$EEZ_TEST_ARGS" ]] || exit 7; sleep 0.01; done\nexit 9\n')
            proxy.chmod(0o555)
            prover = root / "nethermind-eez-attester"
            prover.write_text('#!/usr/bin/env python3\nimport json, os, pathlib, sys\n'
                'target = pathlib.Path(os.environ["EEZ_TEST_ARGS"])\n'
                'temporary = target.with_suffix(".tmp")\n'
                'temporary.write_text(json.dumps(sys.argv[1:]))\n'
                'temporary.replace(target)\n'
                'os.execvp("sleep", ["sleep", "30"])\n')
            prover.chmod(0o555)
            arguments = root / "attester-args.json"
            genesis = root / "l1.json"
            genesis.write_text('{"config":{"chainId":7331}}')
            jq = root / "jq"
            jq.write_text('#!/usr/bin/env python3\nimport os, sys\n'
                'args = [os.environ["EEZ_TEST_L1_GENESIS"] if a == "/genesis/genesis.json" else a for a in sys.argv[1:]]\n'
                'os.execv(os.environ["EEZ_TEST_JQ"], [os.environ["EEZ_TEST_JQ"], *args])\n')
            jq.chmod(0o555)
            environment = dict(os.environ, PATH=str(root) + os.pathsep + os.environ["PATH"],
                EEZ_KEYSTORE_DIRECTORY=str(root / "keystore"), EEZ_CI_PROFILE="rollupx",
                EEZ_REGISTRY_ADDRESS="0x" + "11" * 20, EEZ_ROLLUP_MANAGER_ADDRESS="0x" + "22" * 20,
                EEZ_INITIAL_BLOCK_HASH="0x" + "33" * 32, EEZ_TEST_JQ=shutil.which("jq"),
                EEZ_TEST_L1_GENESIS=str(genesis), EEZ_TEST_ARGS=str(arguments))
            result = subprocess.run(["bash", str(launcher), "--listen-addr=0.0.0.0:50061",
                "--chain-config=/out/l2-genesis.json", "--l1-chain-config=/genesis/genesis.json", "--max-request-blocks=64"],
                env=environment, text=True, capture_output=True, timeout=30)
            self.assertEqual(result.returncode, 7, result.stderr)
            self.assertEqual(json.loads(arguments.read_text()), ["--listen-addr=127.0.0.1:50063",
                "--chain-config=/out/l2-genesis.json", "--streaming-config=" + str(root / "keystore/streaming/config.json"),
                "--max-request-blocks=64"])
            settings = json.loads((root / "keystore/streaming/config.json").read_text())
            self.assertEqual(settings["profile"], "rollupx-native")
            self.assertEqual(settings["l1ChainId"], 7331)
            self.assertEqual(settings["registry"], environment["EEZ_REGISTRY_ADDRESS"])
            self.assertEqual(settings["manager"], environment["EEZ_ROLLUP_MANAGER_ADDRESS"])
            self.assertEqual(settings["l2GenesisHash"], environment["EEZ_INITIAL_BLOCK_HASH"])
            self.assertEqual(settings["l1ChainConfigPath"], "/genesis/genesis.json")
            self.assertNotIn("l1Rpc", settings)
            self.assertNotIn("l2Rpc", settings)

    def test_native_proxy_rejects_changed_or_duplicate_args_before_generating_certificates(self):
        for tail in ([], ["--l1-chain-config=/other"], ["--l1-chain-config=/genesis/genesis.json", "--max-request-blocks=0"],
                ["--l1-chain-config=/genesis/genesis.json", "--max-request-blocks=64", "--max-request-blocks=64"],
                ["--l1-chain-config=/genesis/genesis.json", "--streaming-config=/other"]):
            with self.subTest(tail=tail), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                environment = dict(os.environ, EEZ_KEYSTORE_DIRECTORY=str(root / "keystore"), EEZ_CI_PROFILE="rollupx")
                result = subprocess.run(["bash", str(Path(__file__).with_name("start-streaming.sh")),
                    "--listen-addr=0.0.0.0:50061", "--chain-config=/out/l2-genesis.json", *tail],
                    env=environment, text=True, capture_output=True, timeout=5)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("upstream prover arguments changed", result.stderr)
                self.assertFalse((root / "keystore").exists())

    def test_proxy_output_cannot_interleave_with_signed_window_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            launcher = root / "start-streaming.sh"
            shutil.copyfile(Path(__file__).with_name("start-streaming.sh"), launcher)
            proxy = root / "haproxy"
            proxy.write_text('#!/bin/bash\nif [[ "$1" == -c ]]; then exit 0; fi\n'
                'read -r ready < "$EEZ_TEST_READY_PIPE"\necho proxy-stdout\necho proxy-stderr >&2\nexit 7\n')
            proxy.chmod(0o555)
            prover = root / "nethermind-eez-attester"
            event = json.dumps({"event_name": "eez.proof_signer.window_signed", "fields": {"wire": "prove.v2"}}, separators=(",", ":"))
            prover.write_text("#!/bin/bash\nprintf '%s\\n' '" + event + "'\n"
                'echo ready > "$EEZ_TEST_READY_PIPE"\nexec sleep 30\n')
            prover.chmod(0o555)
            ready = root / "ready"
            os.mkfifo(ready, 0o600)
            environment = dict(os.environ, PATH=str(root) + os.pathsep + os.environ["PATH"],
                EEZ_KEYSTORE_DIRECTORY=str(root / "keystore"), EEZ_TEST_READY_PIPE=str(ready))
            result = subprocess.run(["bash", str(launcher), "--listen-addr=0.0.0.0:50061", "--chain-config=/out/l2-genesis.json"],
                env=environment, text=True, capture_output=True, timeout=30)
            self.assertEqual(result.returncode, 7, result.stderr)
            self.assertIn(event + "\n", result.stdout)
            self.assertNotIn("proxy-stdout", result.stdout + result.stderr)
            self.assertNotIn("proxy-stderr", result.stdout + result.stderr)
            log = root / "keystore/streaming/haproxy.log"
            self.assertEqual(log.read_text(), "proxy-stdout\nproxy-stderr\n")
            self.assertEqual(log.stat().st_mode & 0o777, 0o600)

    def test_changed_upstream_arguments_fail_before_certificate_generation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            environment = dict(os.environ, EEZ_KEYSTORE_DIRECTORY=str(root / "keystore"))
            launcher = Path(__file__).with_name("start-streaming.sh")
            result = subprocess.run(["bash", str(launcher), "--listen-addr=0.0.0.0:50062", "--chain-config=/out/l2-genesis.json"],
                env=environment, text=True, capture_output=True, timeout=5)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("upstream prover arguments changed", result.stderr)
            self.assertFalse((root / "keystore").exists())


if __name__ == "__main__":
    unittest.main()
