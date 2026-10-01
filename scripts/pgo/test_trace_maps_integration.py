#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Check map retention against a saved Nettrace v6 fixture and its matching ETLX."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess


def read_var(data, cursor):
    value = 0
    for shift in range(0, 70, 7):
        byte = data[cursor]
        cursor += 1
        value |= (byte & 127) << shift
        if byte < 128:
            if value >= 1 << 64:
                raise ValueError("varint exceeds uint64")
            return value, cursor
    raise ValueError("invalid varint")


def read_string(data, cursor):
    size, cursor = read_var(data, cursor)
    end = cursor + size
    if end > len(data):
        raise ValueError("truncated string")
    return data[cursor:end].decode("utf-8"), end


def mutate_map(data, field, entry):
    if data[:8] != b"Nettrace" or struct.unpack_from("<I", data, 12)[0] != 6:
        raise ValueError("this regression fixture requires Nettrace v6")
    blocks = []
    map_ids = set()
    position = 20
    while position < len(data):
        header = struct.unpack_from("<I", data, position)[0]
        position += 4
        kind, size = header >> 24, header & 0xFFFFFF
        block = data[position:position + size]
        position += size
        if len(block) != size:
            raise ValueError("truncated block")
        blocks.append((kind, block))
        if kind != 3:
            continue
        if struct.unpack_from("<H", block)[0] != 0:
            raise ValueError("unsupported metadata header")
        cursor = 2
        while cursor < len(block):
            row_size = struct.unpack_from("<H", block, cursor)[0]
            cursor += 2
            end = cursor + row_size
            metadata_id, at = read_var(block, cursor)
            provider, at = read_string(block, at)
            event_id, at = read_var(block, at)
            if not at <= end <= len(block):
                raise ValueError("truncated metadata row")
            if provider == "Microsoft-Windows-DotNETRuntime" and event_id == 190:
                map_ids.add(metadata_id)
            cursor = end
    result = bytearray(data[:20])
    changed = False
    for kind, original in blocks:
        block = bytearray(original)
        if kind == 2:
            if struct.unpack_from("<HH", block) != (20, 1):
                raise ValueError("unsupported event block header")
            cursor = 20
            while cursor < len(block):
                if block[cursor] != 223:
                    raise ValueError("fixture requires explicit compressed header fields")
                cursor += 1
                values = []
                for _ in range(9):
                    value, cursor = read_var(block, cursor)
                    values.append(value)
                payload_start = cursor
                cursor += values[8]
                if cursor > len(block):
                    raise ValueError("truncated event payload")
                if values[0] not in map_ids or changed:
                    continue
                if values[8] < 21:
                    raise ValueError("truncated map header")
                count = struct.unpack_from("<H", block, payload_start + 17)[0]
                if count < 2 or values[8] < 21 + count * 8:
                    raise ValueError("fixture needs a complete map with multiple entries")
                index = 0 if entry == "first" else count - 1
                offset = payload_start + 19 + index * 4 + (count * 4 if field == "native" else 0)
                previous = struct.unpack_from("<i", block, offset)[0]
                struct.pack_into("<i", block, offset, previous + 1)
                changed = True
        result.extend(struct.pack("<I", kind << 24 | len(block)))
        result.extend(block)
    if not changed:
        raise ValueError("fixture contains no usable CLR IL/native map")
    if len(result) != len(data):
        raise ValueError("mutation must preserve all record sizes")
    return bytes(result)


def verify_offset_difference(report, field, entry):
    def snapshots(rows):
        result = {}
        for row in rows:
            snapshot = json.loads(row)
            mappings = snapshot.pop("mappings")
            key = json.dumps(snapshot, sort_keys=True)
            if key in result:
                raise ValueError("fixture needs unique map event identities")
            result[key] = mappings
        return result

    raw = snapshots(report["raw"])
    retained = snapshots(report["retained"])
    if raw.keys() != retained.keys():
        raise ValueError("offset mutation changed map metadata")
    changed = [key for key in raw if raw[key] != retained[key]]
    if len(changed) != 1:
        raise ValueError("offset mutation must change exactly one map")
    key = changed[0]
    actual, original = raw[key], retained[key]
    if len(actual) != len(original):
        raise ValueError("offset mutation changed entry count")
    index = 0 if entry == "first" else len(original) - 1
    expected = [dict(mapping) for mapping in original]
    expected[index][field + "Offset"] += 1
    if actual != expected:
        raise ValueError("map differs beyond the intended offset")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trace", type=Path, required=True)
    parser.add_argument("--etlx", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    source = args.trace.read_bytes()
    etlx_hash = hashlib.sha256(args.etlx.read_bytes()).hexdigest()
    args.output.mkdir(parents=True, exist_ok=False)
    validator = Path(__file__).with_name("validate-trace-maps.cs")
    reports = []
    for name in ("original", "native-first", "il-first", "native-last", "il-last"):
        trace = args.trace if name == "original" else args.output / f"{name}.nettrace"
        if name != "original":
            field, entry = name.split("-")
            trace.write_bytes(mutate_map(source, field, entry))
        report_path = args.output / f"{name}.json"
        command = [args.dotnet, "run", "--file", str(validator), "--",
                   str(trace.resolve()), str(args.etlx.resolve()), str(report_path.resolve())]
        with (args.output / f"{name}.log").open("wb") as log:
            result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
        expected = 0 if name == "original" else 1
        if result.returncode != expected:
            raise ValueError(f"{name}: validator exit {result.returncode}, expected {expected}")
        report = json.loads(report_path.read_text(encoding="utf-8"))
        if report["valid"] != (name == "original") or report["rawCount"] == 0:
            raise ValueError(f"{name}: incorrect validation result")
        if report["rawCount"] != report["retainedCount"]:
            raise ValueError(f"{name}: mutation must preserve map count")
        if name != "original":
            verify_offset_difference(report, field, entry)
        reports.append({"case": name, "exit": result.returncode,
                        "map_count": report["rawCount"], "valid": report["valid"]})
    if args.trace.read_bytes() != source or hashlib.sha256(args.etlx.read_bytes()).hexdigest() != etlx_hash:
        raise ValueError("original fixture was modified")
    summary = {"source_sha256": hashlib.sha256(source).hexdigest(), "etlx_sha256": etlx_hash,
               "cases": reports, "scope": "Regression fixture only; not training data."}
    (args.output / "verification.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
