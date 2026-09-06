#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Split a JSON Lines eth_call corpus into one JSON-array fixture per 4-byte selector class.

Writes ``<dest>/class_<k>.json`` (k ranked by record count) plus ``<dest>/classes.json``
mapping class name to record count. Selectors themselves are never written: the mapping
stays implicit in the fixture files, which never leave the runner.
"""

import argparse
import gzip
import json
import os
import sys
import tempfile
from pathlib import Path
from typing import Sequence, TextIO

SELECTOR_LENGTH = 10  # "0x" + 4 bytes


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


def _parse_record(source: Path, line_number: int, line: str) -> dict:
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
    return {"method": record["method"], "params": record["params"]}


def selector(params: list) -> str:
    call = params[0] if params and isinstance(params[0], dict) else {}
    data = call.get("data") or call.get("input") or ""
    if isinstance(data, str) and data.startswith("0x") and len(data) >= SELECTOR_LENGTH:
        return data[:SELECTOR_LENGTH].lower()
    return "none"


class _ClassWriter:
    def __init__(self, directory: Path) -> None:
        descriptor, name = tempfile.mkstemp(dir=directory, prefix=".class.", suffix=".tmp")
        self.path = Path(name)
        self.handle = os.fdopen(descriptor, "w", encoding="utf-8", newline="\n")
        self.handle.write("[")
        self.count = 0

    def write(self, record: dict) -> None:
        if self.count:
            self.handle.write(",")
        json.dump(record, self.handle, ensure_ascii=False, separators=(",", ":"), allow_nan=False)
        self.count += 1

    def close(self) -> None:
        self.handle.write("]\n")
        self.handle.flush()
        os.fsync(self.handle.fileno())
        self.handle.close()


def convert(source: str | os.PathLike[str], destination: str | os.PathLike[str]) -> dict[str, int]:
    """Stream the corpus into per-class fixtures under ``destination``; return {class: count}."""
    source_path = Path(source)
    destination_dir = Path(destination)
    destination_dir.mkdir(parents=True, exist_ok=True)
    writers: dict[str, _ClassWriter] = {}
    line_number = 0
    try:
        try:
            source_file = _open_source(source_path)
        except (OSError, UnicodeError) as error:
            raise CorpusError(source_path, 0, f"unable to read source: {error}") from error
        with source_file:
            try:
                for line_number, line in enumerate(source_file, start=1):
                    if not line.strip():
                        continue
                    record = _parse_record(source_path, line_number, line)
                    key = selector(record["params"])
                    if key not in writers:
                        writers[key] = _ClassWriter(destination_dir)
                    writers[key].write(record)
            except (OSError, UnicodeError) as error:
                raise CorpusError(source_path, line_number, f"unable to read source: {error}") from error
        if not writers:
            raise CorpusError(source_path, 0, "input contains no nonblank JSON records")
        for writer in writers.values():
            writer.close()
        ranked = sorted(writers.values(), key=lambda w: -w.count)
        classes: dict[str, int] = {}
        for rank, writer in enumerate(ranked, start=1):
            name = f"class_{rank}"
            os.chmod(writer.path, 0o644)
            os.replace(writer.path, destination_dir / f"{name}.json")
            classes[name] = writer.count
        manifest = destination_dir / "classes.json"
        manifest.write_text(json.dumps(classes) + "\n", encoding="utf-8")
        os.chmod(manifest, 0o644)
        writers.clear()
        return classes
    finally:
        for writer in writers.values():
            try:
                writer.handle.close()
            except OSError:
                pass
            writer.path.unlink(missing_ok=True)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="source .jsonl or .jsonl.gz file")
    parser.add_argument("destination", type=Path, help="destination directory for class_<k>.json + classes.json")
    arguments = parser.parse_args(argv)
    try:
        classes = convert(arguments.source, arguments.destination)
    except (CorpusError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    print(f"corpus classes: {len(classes)} ({', '.join(f'{k}={v}' for k, v in classes.items())})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
