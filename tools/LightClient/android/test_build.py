# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


DIRECTORY = Path(__file__).resolve().parent


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, DIRECTORY / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


binding = load("binding_build", "prepare-bls.py")
native = binding.native_build


class AndroidBuildTests(unittest.TestCase):
    def test_loader_adaptation_preserves_original_source(self):
        original = "untouched prefix\n" + binding.ANCHOR + "        string platform;\n    }\n"
        changed = binding.adapt_resolver(original)
        self.assertEqual(changed.count(binding.ANDROID_LOADER), 1)
        self.assertEqual(changed.replace(binding.ANDROID_LOADER, ""), original)

    def test_loader_adaptation_rejects_source_drift_and_repeat(self):
        for source in ("", binding.ANCHOR * 2, binding.ANCHOR + binding.ANDROID_LOADER):
            with self.subTest(source=source), self.assertRaises(RuntimeError):
                binding.adapt_resolver(source)

    def test_elf_validation_rejects_wrong_abi_dependencies_alignment_or_exports(self):
        elf = "ELF64 little endian AArch64\nLOAD 0 0x4000\nShared library: [libc.so]\n"
        invalid = (
            (elf.replace("AArch64", "X86-64"), "0 T blst_verify"),
            (elf.replace("libc.so", "libc.so.6"), "0 T blst_verify"),
            (elf.replace("0x4000", "0x1000"), "0 T blst_verify"),
            (elf, "0 T unrelated_symbol"),
        )
        with tempfile.TemporaryDirectory() as directory:
            library = Path(directory) / "libblst.so"
            library.write_bytes(b"test")
            for headers, symbols in invalid:
                with self.subTest(headers=headers, symbols=symbols):
                    with patch.object(native, "run", side_effect=[headers, symbols]):
                        with self.assertRaises(RuntimeError):
                            native.verify_library(Path(directory), library, ["blst_verify"])
            with patch.object(native, "run", side_effect=[elf, "0 T blst_verify"]):
                result = native.verify_library(Path(directory), library, ["blst_verify"])
                self.assertEqual(result["verified_imports"], 1)


if __name__ == "__main__":
    unittest.main()
