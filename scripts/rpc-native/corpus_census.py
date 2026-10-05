# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Read every corpus record once and emit content-free provenance/input-size counts."""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import sys


def census(corpus, output):
    if corpus.name != "eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz":
        raise ValueError("CORPUS_NAME")
    before = corpus.stat()
    digest = hashlib.sha256()
    with corpus.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    count, override_records, code_records, code_bytes = 0, 0, 0, 0
    request_bytes = []
    with gzip.open(corpus, "rt", encoding="utf-8") as source:
        for line in source:
            if not line.strip():
                continue
            value = json.loads(line)
            if not isinstance(value, dict) or value.get("method") != "eth_call" or not isinstance(value.get("params"), list):
                raise ValueError("CORPUS_RECORD")
            count += 1
            request_bytes.append(len(line.encode("utf-8")))
            params = value["params"]
            overrides = params[2] if len(params) > 2 else None
            if overrides:
                if not isinstance(overrides, dict):
                    raise ValueError("CORPUS_OVERRIDES")
                override_records += 1
                any_code = False
                for account in overrides.values():
                    if not isinstance(account, dict):
                        raise ValueError("CORPUS_ACCOUNT")
                    code = account.get("code")
                    if code is not None:
                        if not isinstance(code, str) or not re.fullmatch(r"0x(?:[0-9a-fA-F]{2})*", code):
                            raise ValueError("CORPUS_CODE")
                        any_code = True
                        code_bytes += (len(code) - 2) // 2
                code_records += int(any_code)
    after = corpus.stat()
    if (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns) or count != 497:
        raise ValueError("CORPUS_CHANGED_OR_COUNT")
    request_bytes.sort()
    result = {"schema": 1, "sha256": digest.hexdigest(), "compressed_bytes": before.st_size, "records": count,
              "state_override_records": override_records, "code_override_records": code_records, "total_code_override_bytes": code_bytes,
              "request_bytes": {"minimum": request_bytes[0], "median": request_bytes[len(request_bytes) // 2], "maximum": request_bytes[-1]},
              "nested_return_pooling_coverage": "UNKNOWN_REQUIRES_UNTRACED_RUNTIME_CENSUS", "subset_selected": False}
    with output.open("x", encoding="utf-8") as target:
        json.dump(result, target, indent=2)
        target.write("\n")
    print(json.dumps(result))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    os.umask(0o077)
    try:
        census(args.corpus, args.out)
    except Exception:
        print("CORPUS_CENSUS_FAILED", file=sys.stderr)
        raise SystemExit(1)
