#!/usr/bin/env python3
"""Require native method entries and embedded PGO in a non-composite ReadyToRun PE."""
import argparse
import json
from pathlib import Path
import struct


def inspect(path):
    data = path.read_bytes()

    def u32(offset):
        return struct.unpack_from("<I", data, offset)[0]

    pe = u32(0x3C)
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Not a PE image")
    count, optional_size = struct.unpack_from("<H", data, pe + 6)[0], struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    directories = optional + {0x10B: 96, 0x20B: 112}[magic]

    def rva_offset(rva):
        for index in range(count):
            header = optional + optional_size + 40 * index
            size, address, raw_size, raw_offset = struct.unpack_from("<IIII", data, header + 8)
            if address <= rva < address + min(size, raw_size):
                return raw_offset + rva - address
        raise ValueError(f"RVA {rva:#x} has no file-backed section")

    clr = rva_offset(u32(directories + 14 * 8))
    native = rva_offset(u32(clr + 64))
    if u32(native) != 0x00525452:
        raise ValueError("No ReadyToRun header")
    sections = {}
    for index in range(u32(native + 12)):
        kind, rva, size = struct.unpack_from("<III", data, native + 16 + 12 * index)
        if size:
            rva_offset(rva)
            sections[kind] = size
    # ReadyToRunSectionType in dotnet/runtime v10.0.11 src/coreclr/inc/readytorun.h.
    if not sections.get(103) or not sections.get(117):
        raise ValueError("Missing native method entry points or embedded PGO section")
    return {"file": str(path), "method_entry_bytes": sections[103], "pgo_bytes": sections[117],
            "cross_module_inline_bytes": sections.get(119, 0), "hot_cold_map_bytes": sections.get(120, 0)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assemblies", nargs="+", type=Path)
    args = parser.parse_args()
    for path in args.assemblies:
        print(json.dumps(inspect(path)))
