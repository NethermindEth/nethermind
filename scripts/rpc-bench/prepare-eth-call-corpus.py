#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Convert JSON Lines RPC captures into an ``eth_call`` JSON array corpus.

RPC_BENCH_CORPUS_METHOD rewrites each record to ``debug_traceCall`` or ``trace_call`` as it is
converted. The fixture k6 replays therefore already carries the rewritten bodies — the conversion
is one pass over the corpus before the node is loaded, so it costs the measured cell nothing.
"""

import argparse
import gzip
import json
import os
from pathlib import Path
import sys
import tempfile
from typing import Sequence, TextIO

sys.path.insert(0, str(Path(__file__).parent))
# corpus_parity owns what a legal corpus is and how a record is rewritten; importing keeps the
# k6 fixture and the parity/timings replay from drifting into two different transforms.
from corpus_parity import (  # noqa: E402
    CorpusParityError,
    corpus_rewrite,
    rewrite_record,
)


class CorpusError(ValueError):
    def __init__(self, source: Path, line_number: int, message: str) -> None:
        super().__init__(f"{source}: line {line_number}: {message}")


def _reject_non_json_constant(value: str) -> None:
    raise ValueError(f"invalid JSON constant {value!r}")


def _open_source(source: Path) -> TextIO:
    if source.name.endswith(".jsonl.gz"):
        return gzip.open(source, "rt", encoding="utf-8", newline="")
    if source.name.endswith(".jsonl"):
        return source.open("r", encoding="utf-8", newline="")
    raise CorpusError(source, 0, "source must have a .jsonl or .jsonl.gz extension")


def _parse_record(source: Path, line_number: int, line: str, method: str, options: dict) -> dict:
    try:
        record = json.loads(line, parse_constant=_reject_non_json_constant)
    except json.JSONDecodeError as error:
        raise CorpusError(source, line_number, f"invalid JSON: {error.msg}") from error
    except (RecursionError, ValueError) as error:
        raise CorpusError(source, line_number, f"invalid JSON: {error}") from error

    if not isinstance(record, dict):
        raise CorpusError(source, line_number, "record must be a JSON object")

    if record.get("method") != "eth_call":
        raise CorpusError(source, line_number, "method must be exactly 'eth_call'")

    if not isinstance(record.get("params"), list):
        raise CorpusError(source, line_number, "params must be a JSON array")

    if method == "eth_call":
        return {"method": record["method"], "params": record["params"]}
    try:
        return {"method": method, "params": rewrite_record(record["params"], method, options)}
    except CorpusParityError as error:
        raise CorpusError(source, line_number, str(error)) from None


def convert(source: str | os.PathLike[str], destination: str | os.PathLike[str]) -> None:
    """Stream an ``eth_call`` JSONL corpus to a JSON array at ``destination``."""

    source_path = Path(source)
    destination_path = Path(destination)
    if source_path.resolve() == destination_path.resolve():
        raise CorpusError(source_path, 0, "source and destination must be different files")
    method, options = corpus_rewrite()

    temporary_path: Path | None = None
    line_number = 0
    try:
        try:
            source_file = _open_source(source_path)
        except (OSError, UnicodeError) as error:
            raise CorpusError(source_path, 0, f"unable to read source: {error}") from error

        with source_file:
            descriptor, temporary_name = tempfile.mkstemp(
                dir=destination_path.parent,
                prefix=f".{destination_path.name}.",
                suffix=".tmp",
            )
            temporary_path = Path(temporary_name)
            with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
                output.write("[")
                has_records = False
                try:
                    for line_number, line in enumerate(source_file, start=1):
                        if not line.strip():
                            continue

                        record = _parse_record(source_path, line_number, line, method, options)
                        if has_records:
                            output.write(",")
                        json.dump(record, output, ensure_ascii=False, separators=(",", ":"), allow_nan=False)
                        has_records = True
                except (OSError, UnicodeError) as error:
                    raise CorpusError(source_path, line_number, f"unable to read source: {error}") from error

                if not has_records:
                    raise CorpusError(source_path, 0, "input contains no nonblank JSON records")

                output.write("]\n")
                output.flush()
                os.fsync(output.fileno())

        os.chmod(temporary_path, 0o644)
        os.replace(temporary_path, destination_path)
        temporary_path = None
    finally:
        if temporary_path is not None:
            try:
                temporary_path.unlink()
            except FileNotFoundError:
                pass


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="source .jsonl or .jsonl.gz file")
    parser.add_argument("destination", type=Path, help="destination JSON array file")
    arguments = parser.parse_args(argv)

    try:
        convert(arguments.source, arguments.destination)
    except (CorpusError, CorpusParityError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
