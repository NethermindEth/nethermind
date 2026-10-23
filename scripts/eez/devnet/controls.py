#!/usr/bin/env python3
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import time

from verify import Rpc, equal


def hex_bytes(value, size=None):
    if not isinstance(value, str) or not re.fullmatch(r"0x(?:[0-9a-fA-F]{2})*", value):
        raise ValueError("invalid RPC byte string")
    decoded = bytes.fromhex(value[2:])
    if size is not None and len(decoded) != size:
        raise ValueError("invalid RPC byte length")
    return value


def recheck(l1, source, context):
    equal(l1.call("eth_getBlockByNumber", context["canonical_l1_number"], False)["hash"],
          context["canonical_l1_hash"], "control input canonical L1")
    equal(source.call("eth_getBlockByNumber", hex(context["height"]), False)["hash"],
          context["endpoint"], "control input settled L2 endpoint")
    equal(source.call("eth_getBlockByNumber", "0x0", False)["hash"],
          context["genesis_state"]["hash"], "control input genesis")
    equal(source.call("eth_chainId"), hex(context["genesis_state"]["chain_id"]), "control input chain")


def capture(source, context, rollup_id):
    current = context["endpoint"]
    pair = []
    for expected in range(context["height"], max(0, context["height"] - 64), -1):
        block = source.call("eth_getBlockByHash", current, False)
        equal(block["hash"], current, "control input ancestry")
        equal(block["number"], hex(expected), "control input height")
        hex_bytes(block["hash"], 32)
        hex_bytes(block["parentHash"], 32)
        hex_bytes(block["stateRoot"], 32)
        if block["transactions"] == []:
            pair.insert(0, block)
            if len(pair) == 2:
                break
        else:
            pair.clear()
        current = block["parentHash"]
    if len(pair) != 2:
        raise AssertionError("no consecutive transaction-empty settled blocks in the bounded control window")
    anchor = source.call("eth_getBlockByHash", pair[0]["parentHash"], False)
    equal(anchor["hash"], pair[0]["parentHash"], "control anchor identity")
    equal(int(anchor["number"], 16) + 1, int(pair[0]["number"], 16), "control anchor height")
    hex_bytes(anchor["hash"], 32)
    hex_bytes(anchor["stateRoot"], 32)
    blocks = []
    for block in pair:
        raw = hex_bytes(source.call("debug_getRawBlock", block["hash"]))
        if raw == "0x":
            raise ValueError("empty control input block")
        witness = source.call("debug_executionWitnessByBlockHash", block["hash"])
        for field in ("state", "codes", "keys", "headers"):
            if not isinstance(witness.get(field), list):
                raise ValueError("missing execution witness field: " + field)
            for value in witness[field]:
                hex_bytes(value)
        if not witness["state"]:
            raise ValueError("missing control execution state")
        blocks.append({"number": int(block["number"], 16), "hash": block["hash"],
                       "parent_hash": block["parentHash"], "state_root": block["stateRoot"],
                       "rlp": raw, "witness": witness})
    return {"rollup_id": rollup_id, "anchor_number": int(anchor["number"], 16),
            "anchor_hash": anchor["hash"], "anchor_state_root": anchor["stateRoot"],
            "blocks": blocks, "context": context}


def validate_report(report, data):
    equal(report["result"], "pass", "live control probe")
    equal(report["anchor_hash"], data["anchor_hash"], "control probe anchor")
    equal(report["blocks"], [{key: block[key] for key in ("number", "hash", "state_root")} for block in data["blocks"]], "control probe blocks")
    equal(report["checks"], ["disconnect-resume", "rewind-reexecute", "pipelined-cancel", "cancelled-resume-rejected", "independent-session"], "required live controls")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("install", "capture", "report"))
    args = parser.parse_args()
    upstream = Path(os.environ["EEZ_UPSTREAM"])
    directory = Path(os.environ["EEZ_CI_RESULT_DIR"])
    if args.mode == "install":
        target = upstream / "crates/eez-node-e2e/tests/nethermind_streaming_controls.rs"
        if target.exists():
            raise ValueError("control test target already exists")
        shutil.copyfile(Path(__file__).with_suffix(".rs"), target)
        return
    path = directory / "streaming-controls-input.json"
    deadline = time.monotonic() + 180
    l1, source = [Rpc(os.environ[name], deadline) for name in ("EEZ_DEVNET_L1_RPC", "EEZ_DEVNET_L2_RPC")]
    context = json.loads((directory / "fresh-follower.json").read_text())
    equal(context["result"], "pass", "control inputs require passed follower observation")
    equal(context["phase"], "fresh-follower", "control input phase")
    equal(context["profile"], "legacy-rollup0-0x76", "control input profile")
    recheck(l1, source, context)
    if args.mode == "capture":
        data = capture(source, context, int(os.environ["EEZ_ROLLUP_ID"]))
        recheck(l1, source, context)
        path.write_text(json.dumps(data, indent=2) + "\n")
        return
    data = json.loads(path.read_text())
    equal(data["context"], context, "control replay context")
    report = json.loads((directory / "streaming-controls-probe.json").read_text())
    validate_report(report, data)
    recheck(l1, source, context)
    report.update({"candidate_revision": os.environ["GITHUB_SHA"], "prover_image_id": os.environ["EEZ_CONTROL_IMAGE_ID"],
                   "upstream": json.loads((directory / "upstream.json").read_text()),
                   "input_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                   "context": context, "wire": "prove.v2", "profile": "rollup0-conditional",
                   "scope": "ordinary transaction-empty blocks; control cursor and fencing; no Finalize or settlement claim"})
    (directory / "streaming-controls.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
