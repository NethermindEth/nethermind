import argparse
import copy
import json
from pathlib import Path
import tempfile
import unittest

from convert import check_profile, convert
from collect import validate_expb_log


class ProfileValidationTests(unittest.TestCase):
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
