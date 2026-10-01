# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import copy
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

from build_benchmark_images import PROFILE_PATH, compare_publications, extract_assembly, native_header, select_profile, verify_properties


class BenchmarkImageTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def test_profile_input_and_production_properties_are_required(self):
        properties = {"Properties": {"PublishReadyToRun": "true", "PublishReadyToRunComposite": "false",
                                     "PublishReadyToRunUseCrossgen2": "true", "TieredPGO": "true"},
                      "Items": {"PublishReadyToRunPgoFiles": []}}
        self.assertEqual(verify_properties(properties, False)["profile_inputs"], 0)
        for name in properties["Properties"]:
            with self.subTest(name=name):
                altered = copy.deepcopy(properties)
                altered["Properties"][name] = "unexpected"
                with self.assertRaisesRegex(ValueError, "matched R2R/dynamic PGO"):
                    verify_properties(altered, False)
        properties["Items"]["PublishReadyToRunPgoFiles"] = [{"Identity": PROFILE_PATH}]
        self.assertEqual(verify_properties(properties, True)["profile_inputs"], 1)
        with self.assertRaisesRegex(ValueError, "experimental arm"):
            verify_properties(properties, False)
        for paths in ([], ["historical.mibc"], [PROFILE_PATH, "extra.mibc"]):
            properties["Items"]["PublishReadyToRunPgoFiles"] = [{"Identity": path} for path in paths]
            with self.subTest(paths=paths), self.assertRaisesRegex(ValueError, "experimental arm"):
                verify_properties(properties, True)

    def test_assembly_evidence_excludes_surrounding_diagnostic_messages(self):
        log = self.root / "publish.log"
        log.write_text("private-build-property=sentinel\n"
                       "; Assembly listing for method Example:Hot(int):int (FullOpts) (TaskId:3)\n"
                       "; ReadyToRun compilation (TaskId:3)\n"
                       "G_M000_IG01: (TaskId:3)\n"
                       "  ret (TaskId:3)\n"
                       "; Total bytes of code 1 (TaskId:3)\n"
                       "private-build-property=sentinel\n", encoding="utf-8")
        output = self.root / "Hot.asm"
        self.assertEqual(extract_assembly(log, output, "Hot")["listings"], 1)
        self.assertNotIn("sentinel", output.read_text())
        self.assertNotIn("TaskId", output.read_text())
        for count, expected in (("0", 0), ("42", 1), ("1.5e+06", 1)):
            log.write_text("; Assembly listing for method Example:Hot():int (FullOpts)\n"
                           f"; with Synthesized PGO: fgCalledCount is {count}\n"
                           "; Total bytes of code 1\n")
            self.assertEqual(extract_assembly(log, output, "Hot")["positive_profile_entries"], expected)
        for text in ("no listing", "; Assembly listing for method Example:Other():int\n; Total bytes of code 1\n",
                     "; Assembly listing for method Example:Hot():int\nret\n"):
            with self.subTest(text=text):
                log.write_text(text)
                with self.assertRaisesRegex(ValueError, "missing or incomplete"):
                    extract_assembly(log, output, "Hot")

    def test_native_header_distinguishes_il_and_r2r(self):
        path = self.root / "test.dll"
        data = bytearray(0x400)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 0x3c, 0x80)
        data[0x80:0x84] = b"PE\0\0"
        struct.pack_into("<H", data, 0x86, 1)
        struct.pack_into("<H", data, 0x94, 240)
        struct.pack_into("<H", data, 0x98, 0x20b)
        struct.pack_into("<I", data, 0x98 + 112 + 14 * 8, 0x2000)
        struct.pack_into("<IIII", data, 0x98 + 240 + 8, 0x200, 0x2000, 0x200, 0x200)
        path.write_bytes(data)
        self.assertEqual(native_header(path), {"managed": True, "ready_to_run": False})
        struct.pack_into("<I", data, 0x200 + 64, 0x2100)
        data[0x300:0x304] = b"RTR\0"
        path.write_bytes(data)
        self.assertEqual(native_header(path), {"managed": True, "ready_to_run": True})
        path.write_bytes(b"native ELF")
        self.assertEqual(native_header(path), {"managed": False, "ready_to_run": False})

    def test_conversion_cache_and_host_invocations_preserve_original_bundle(self):
        raw = self.root / "bundle/nethermind-pgo-raw-data"
        raw.mkdir(parents=True)
        (raw / "nethermind-1.nettrace").write_bytes(b"original trace")
        (raw / "nethermind-1.etlx").write_bytes(b"authenticated cache")
        record = {"Method": "[Example]Example.Hot()", "InstrumentationData":
                  [{"InstrumentationKind": "BasicBlockIntCount", "Data": 42}]}
        commands = []
        def converter(command, log):
            commands.append(command)
            self.assertEqual(command[:2], ["pinned-dotnet", "pinned-pgo.dll"])
            output = Path(command[command.index("--output") + 1])
            if "create-mibc" in command:
                trace = Path(command[command.index("--trace") + 1])
                self.assertNotEqual(trace.parent, raw)
                self.assertEqual(trace.read_bytes(), b"original trace")
                trace.with_suffix(".etlx").write_bytes(b"regenerated cache")
                output.write_bytes(b"selected profile")
            else:
                output.write_text(json.dumps({"Methods": [record]}), encoding="utf-8")
        with patch("build_benchmark_images.run", side_effect=converter):
            report = select_profile(self.root / "bundle", self.root / "refs", {"files": [{"path": "app/Example.dll"}]},
                                    self.root / "selected", ["pinned-dotnet", "pinned-pgo.dll"], "Example.*Hot", record["Method"], self.root)
        self.assertEqual(report["instrumentation_records"], 1)
        self.assertEqual(len(commands), 3)
        self.assertEqual((raw / "nethermind-1.etlx").read_bytes(), b"authenticated cache")
        self.assertEqual((raw / "nethermind-1.nettrace").read_bytes(), b"original trace")

    def test_matched_publications_require_identical_non_dll_files_and_inventory(self):
        control = self.root / "control"
        profiled = self.root / "profiled"
        for root in (control, profiled):
            root.mkdir()
            (root / "Example.dll").write_bytes(b"original assembly")
            (root / "app.runtimeconfig.json").write_bytes(b"configuration-sentinel")
        (profiled / "Example.dll").write_bytes(b"trained assembly")
        report = compare_publications(control, profiled)
        self.assertEqual(report["changed_dlls"], ["Example.dll"])
        self.assertNotIn("configuration-sentinel", json.dumps(report))
        (profiled / "app.runtimeconfig.json").write_bytes(b"different config")
        with self.assertRaisesRegex(ValueError, "non-DLL application files"):
            compare_publications(control, profiled)
        (profiled / "extra.dll").write_bytes(b"unexpected")
        with self.assertRaisesRegex(ValueError, "different file inventories"):
            compare_publications(control, profiled)


if __name__ == "__main__":
    unittest.main()
