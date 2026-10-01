# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Validate replay headers against snapshot metadata without opening the state DB."""
import json
from pathlib import Path


def requests(path, method):
    with Path(path).open("rb") as stream:
        for line in stream:
            if not line.rstrip(b"\r\n"):
                continue
            record = json.loads(line)
            if not isinstance(record, dict) or record.get("method") != method:
                raise ValueError(f"expected {method} request in {path}")
            params = record.get("params")
            if not isinstance(params, list) or not params or not isinstance(params[0], dict):
                raise ValueError(f"missing request body in {path}")
            yield params[0]


def validate_window(payloads, fcus, snapshot, warmup, amount):
    if isinstance(warmup, bool) or not isinstance(warmup, int) or not 0 <= warmup <= 1000:
        raise ValueError("warmup must be an integer between 0 and 1000")
    if isinstance(amount, bool) or not isinstance(amount, int) or not 1 <= amount <= 1000:
        raise ValueError("amount must be an integer between 1 and 1000")
    head = json.loads((Path(snapshot) / "_snapshot_eth_getBlockByNumber.json").read_text(encoding="utf-8"))["result"]
    head_number = int(head["number"], 16)
    payload_iter = requests(payloads, "engine_newPayloadV4")
    fcu_iter = requests(fcus, "engine_forkchoiceUpdatedV3")
    previous = None
    headers = []
    try:
        for index in range(warmup + amount):
            payload = next(payload_iter, None)
            fcu = next(fcu_iter, None)
            if payload is None or fcu is None:
                raise ValueError("not enough paired requests for warmup plus training amount")
            number = int(payload["blockNumber"], 16)
            if payload["blockHash"] != fcu["headBlockHash"]:
                raise ValueError(f"payload/FCU head mismatch at index {index}")
            if previous and (number != previous["number"] + 1 or payload["parentHash"] != previous["hash"]):
                raise ValueError(f"discontinuous payload chain at index {index}")
            if index == warmup - 1 and (number != head_number or payload["blockHash"] != head["hash"]
                                       or payload["stateRoot"] != head["stateRoot"]):
                raise ValueError("warmup prefix does not end at the snapshot header")
            if index == warmup and (number != head_number + 1 or payload["parentHash"] != head["hash"]):
                raise ValueError("training must start immediately after the snapshot header")
            previous = {"index": index, "number": number, "hash": payload["blockHash"],
                        "parent_hash": payload["parentHash"], "state_root": payload["stateRoot"]}
            headers.append(previous)
    finally:
        payload_iter.close()
        fcu_iter.close()
    return {"warmup": warmup, "amount": amount, "snapshot_number": head_number,
            "snapshot_hash": head["hash"], "snapshot_state_root": head["stateRoot"],
            "training_first_number": headers[warmup]["number"],
            "training_last_number": headers[-1]["number"], "headers": headers,
            "state_identity_verified": False}
