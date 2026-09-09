import argparse
import copy
import json
from pathlib import Path
import tempfile
import unittest

from convert import check_profile, convert
from collect import validate_expb_log
from check_guest import compare


class ProfileValidationTests(unittest.TestCase):
    def test_guest_requires_correct_output_and_rejects_cost_regressions(self):
        expected = bytes.fromhex("01020301")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = root / "baseline"
            baseline.mkdir()
            (baseline / "output.bin").write_bytes(expected.ljust(256, b"\0"))
            (baseline / "stats.csv").write_text("STEPS,100\nCOST,TOTAL,1000,100%\n")
            for name, cost in (("faster", 999), ("equal", 1000), ("slower", 1001)):
                candidate = root / name
                candidate.mkdir()
                (candidate / "output.bin").write_bytes(expected.ljust(256, b"\0"))
                # Fewer instructions are insufficient if weighted cost increases.
                (candidate / "stats.csv").write_text(f"STEPS,90\nCOST,TOTAL,{cost},100%\n")
                result = compare(baseline, [candidate], expected)["candidates"][0]
                self.assertEqual(result["accepted"], name != "slower")
            for name in ("wrong_output", "truncated_output", "missing_cost"):
                with self.subTest(name=name):
                    candidate = root / name
                    candidate.mkdir()
                    output = b"\0" * 256 if name == "wrong_output" else expected.ljust(256, b"\0")
                    (candidate / "output.bin").write_bytes(output[:4] if name == "truncated_output" else output)
                    (candidate / "stats.csv").write_text("STEPS,100\n" if name == "missing_cost" else "STEPS,100\nCOST,TOTAL,999,100%\n")
                    with self.assertRaises(ValueError):
                        compare(baseline, [candidate], expected)

    def test_expb_rejects_invalid_blocks_even_after_graceful_shutdown(self):
        clean = "Nethermind is shut down\nCleanup completed\n"
        validate_expb_log(clean)
        for text in ("", "Nethermind is shut down", "Cleanup completed", *(
                clean + error for error in ("Invalid block", "invalid_block", "INVALID-BLOCK", "InvalidBlock",
                                           "System.InvalidOperationException", "Unhandled", "FATAL"))):
            with self.subTest(text=text), self.assertRaises(ValueError):
                validate_expb_log(text)

    def test_preserves_low_count_type_profiles_and_rejects_empty_training(self):
        valid = {"Methods": [{"Method": "[Nethermind.Evm]Example.Run()", "InstrumentationData": [
            {"InstrumentationKind": "EdgeIntCount", "Data": 1},
            {"InstrumentationKind": "HandleHistogramTypes", "Data": ["[Nethermind.State]WorldState"]},
        ]}]}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "profile.json"
            for case in ("valid", "no_methods", "framework_only", "zero_counts", "null_types"):
                with self.subTest(case=case):
                    profile = copy.deepcopy(valid)
                    if case == "no_methods":
                        profile["Methods"] = []
                    elif case == "framework_only":
                        profile["Methods"][0]["Method"] = "[System.Private.CoreLib]Example.Run()"
                    elif case == "zero_counts":
                        profile["Methods"][0]["InstrumentationData"][0]["Data"] = 0
                    elif case == "null_types":
                        profile["Methods"][0]["InstrumentationData"][1]["Data"] = ["null", "UnknownType"]
                    path.write_text(json.dumps(profile))
                    if case == "valid":
                        self.assertEqual(check_profile(path, "[Nethermind.")["type_sites"], 1)
                    else:
                        with self.assertRaises(ValueError):
                            check_profile(path, "[Nethermind.")

    def test_sampling_requires_positive_call_weights_and_spgo_blocks(self):
        valid = {"Methods": [{"Method": "[Nethermind.Evm]Example.Run()",
                             "CallWeights": [{"Method": "Example.Other()", "Weight": 1}],
                             "InstrumentationData": [{"InstrumentationKind": "BasicBlockIntCount", "Data": 1}]}]}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "profile.json"
            for case in ("valid", "zero_weights", "zero_blocks", "edge_counts_only"):
                with self.subTest(case=case):
                    profile = copy.deepcopy(valid)
                    method = profile["Methods"][0]
                    if case == "zero_weights":
                        method["CallWeights"][0]["Weight"] = 0
                    elif case == "zero_blocks":
                        method["InstrumentationData"][0]["Data"] = 0
                    elif case == "edge_counts_only":
                        method["InstrumentationData"][0]["InstrumentationKind"] = "EdgeIntCount"
                    path.write_text(json.dumps(profile))
                    if case == "valid":
                        self.assertEqual(check_profile(path, "[Nethermind.", "sampling")["block_counts"], 1)
                    else:
                        with self.assertRaises(ValueError):
                            check_profile(path, "[Nethermind.", "sampling")

    def test_rejects_mixed_collections_before_extracting_or_converting(self):
        for key in ("image_id", "source_sha", "source_diff_sha256", "runtime", "workload",
                    "config_sha256", "amount", "payloads_sha256", "fcus_sha256", "corpus_sha256", "passes", "db_source"):
            with self.subTest(key=key), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                paths = [root / (workload + "-" + mode) for workload in ("fusaka", "ethcall")
                         for mode in ("instrumentation", "sampling")]
                for index, path in enumerate(paths):
                    path.mkdir()
                    metadata = dict(image_id="image", source_sha="commit", source_diff_sha256="diff", runtime="runtime")
                    metadata["workload"], metadata["mode"] = path.name.split("-")
                    metadata.update(config_sha256="config", amount=1000, payloads_sha256="payloads", fcus_sha256="fcus",
                                    corpus_sha256="corpus", passes=25, db_source="snapshot")
                    if index == (3 if key in ("corpus_sha256", "passes", "db_source") else 1):
                        metadata[key] = "different"
                    (path / "collection.json").write_text(json.dumps(metadata))
                with self.assertRaises(ValueError):
                    convert(argparse.Namespace(output=root / "output", collections=paths, tool=root / "tool.dll"))


if __name__ == "__main__":
    unittest.main()
