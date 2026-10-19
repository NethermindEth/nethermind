#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Apply the blob-entry carve-out to the superseded FOCIL v0.3 fixtures."""

import argparse
import json
import os
import shutil
from pathlib import Path


LEGACY_RELEASE = "tests-focil-devnet@v0.3.0"
VALID_PAYLOAD_FIELDS = {"params", "newPayloadVersion", "forkchoiceUpdatedVersion", "inclusionListSatisfied"}


def is_blob_transaction(encoded):
    """Recognize a complete type-3 RLP transaction envelope, not an arbitrary 0x03 prefix."""
    try:
        raw = bytes.fromhex(encoded.removeprefix("0x"))

        def bounds(start):
            prefix = raw[start]
            if prefix < 0x80:
                return start, start + 1, False
            offset = 0xC0 if prefix >= 0xC0 else 0x80
            length = prefix - offset
            body = start + 1
            if length > 55:
                count = length - 55
                length = int.from_bytes(raw[body:body + count], "big")
                body += count
            end = body + length
            if end > len(raw):
                raise ValueError("Truncated RLP")
            return body, end, offset == 0xC0

        if raw[0] != 3:
            return False
        position, end, is_list = bounds(1)
        if not is_list or end != len(raw):
            return False
        fields = []
        while position < end:
            _, position, is_list = bounds(position)
            fields.append(is_list)
        return position == end and fields == [False] * 8 + [True, False, True, False, False, False]
    except (AttributeError, IndexError, ValueError):
        return False


def normalize(document):
    changed = 0
    for fixture in document.values():
        for payload in fixture.get("engineNewPayloads", []):
            # v0.3 predates the blob carve-out. Keep invalid payload and ordinary IL assertions intact.
            if payload.keys() - VALID_PAYLOAD_FIELDS or payload.get("inclusionListSatisfied") is not False:
                continue
            params = payload.get("params", [])
            if payload.get("newPayloadVersion") != "6" or len(params) != 5:
                continue
            included = set(params[0].get("transactions", []))
            omitted = [tx for tx in params[4] if tx not in included]
            if omitted and all(is_blob_transaction(tx) for tx in omitted):
                payload["inclusionListSatisfied"] = True
                changed += 1
    return changed


def prepare(release, source, destination):
    if release != LEGACY_RELEASE:
        raise ValueError(f"Compatibility adjustment only supports {LEGACY_RELEASE}")
    shutil.copytree(source, destination, copy_function=os.link)
    changed = 0
    for path in destination.rglob("*.json"):
        document = json.loads(path.read_text())
        count = normalize(document)
        if count:
            # Replace the hard link so the cached release stays byte-for-byte unchanged.
            replacement = path.with_suffix(".json.tmp")
            replacement.write_text(json.dumps(document))
            replacement.replace(path)
            changed += count
    return changed


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("release")
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    print(f"Updated {prepare(args.release, args.source, args.destination)} legacy blob-only IL expectations")
