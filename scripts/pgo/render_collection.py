#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Render a bounded Fusaka collection with explicit, run-owned writable paths."""
import argparse
import json
from pathlib import Path
import re

import yaml

from replay_window import validate_window


def without_startup_warmup(flags):
    if not isinstance(flags, list) or any(not isinstance(flag, str) for flag in flags):
        raise ValueError("collection flags must be a list of strings")
    options = {"initpipelinewarmupenabled", "initevmwarmupenabled"}
    retained = []
    index = 0
    while index < len(flags):
        flag = flags[index]
        index += 1
        tokens = flag.split()
        matches = [token for token in tokens
                   if token.startswith("--")
                   and re.split("[=:]", token, maxsplit=1)[0].lstrip("-").replace(".", "").replace("-", "").lower() in options]
        if matches:
            if len(matches) != 1 or tokens[0] != matches[0] or len(tokens) > 2:
                raise ValueError("startup warmup flags must be separate options")
            option = re.split("[=:]", tokens[0], maxsplit=1)
            value = option[1] if len(option) == 2 else tokens[1] if len(tokens) == 2 else "true"
            if len(option) == len(tokens) == 1 and index < len(flags) and not flags[index].startswith("--"):
                value = flags[index]
                index += 1
            if value.lower() not in ("true", "false") or len(option) == 2 and len(tokens) != 1:
                raise ValueError("invalid startup warmup flag")
        else:
            retained.append(flag)
    return retained + ["--Init.PipelineWarmupEnabled=false", "--Init.EvmWarmupEnabled=false"]


def render(config_path, root, image, amount, delay=0):
    config_path = Path(config_path).resolve(strict=True)
    root = Path(root).resolve()
    if config_path.name != "github-action-mainnet-fusaka-flat.yaml":
        raise ValueError("collection requires the Fusaka Flat configuration")
    if isinstance(amount, bool) or not isinstance(amount, int) or not 1 <= amount <= 1000:
        raise ValueError("collection amount must be between 1 and 1000")
    if type(delay) is not int or delay not in (0, 1):
        raise ValueError("collection delay must be 0 or 1 seconds")
    if root.exists():
        raise ValueError(f"collection destination already exists: {root}")
    if not image.startswith("nethermindeth/nethermind@sha256:") or len(image.rsplit(":", 1)[1]) != 64:
        raise ValueError("collection image must be pinned by Docker digest")
    int(image.rsplit(":", 1)[1], 16)
    config = yaml.safe_load(config_path.read_text(encoding="utf-8"))
    if set(config["scenarios"]) != {"nethermind"}:
        raise ValueError("collection requires exactly one Nethermind scenario")
    scenario = config["scenarios"]["nethermind"]
    if scenario.get("client", "nethermind") != "nethermind":
        raise ValueError("collection requires client=nethermind")
    scenario["extra_flags"] = without_startup_warmup(scenario.get("extra_flags", []))
    for key in ("payloads", "fcus", "snapshot_source"):
        path = Path(scenario[key])
        if not path.is_absolute():
            path = config_path.parent / path
        path = path.resolve(strict=True)
        if key == "snapshot_source" and not path.is_dir():
            raise ValueError("snapshot_source must resolve to a directory")
        if key != "snapshot_source" and not path.is_file():
            raise ValueError(f"{key} must resolve to a file")
        if root == path or root.is_relative_to(path) or path.is_relative_to(root):
            raise ValueError("collection output must not overlap any replay input")
        scenario[key] = str(path)
    window = validate_window(scenario["payloads"], scenario["fcus"], scenario["snapshot_source"],
                             scenario.get("warmup", 0), amount)
    scenario.update({"client": "nethermind", "image": image, "amount": amount, "skip": 0,
                     "repeat": 1, "delay": delay, "warmup_delay": 0, "snapshot_backend": "overlay"})
    scenario.pop("snapshot_path", None)
    volumes = scenario.setdefault("extra_volumes", {})
    if any(volume.get("bind") == "/nethermind/pgo" for name, volume in volumes.items() if name != "pgo"):
        raise ValueError("ambiguous PGO volume binding")
    volumes["pgo"] = {"source": str(root / "pgo"), "bind": "/nethermind/pgo", "mode": "rw"}
    scenario.setdefault("extra_env", {}).update({"DOTNET_ReadPGOData": "0", "DOTNET_TieredPGO": "1"})
    config.pop("export", None)
    config["scenarios"] = {"nethermind-pgo-collect": scenario}
    config["paths"] = {"work": str(root / "work"), "outputs": str(root / "outputs")}
    text = yaml.safe_dump(config, sort_keys=False)
    if "<<" in text or ">>" in text:
        raise ValueError("unresolved placeholder in collection config")
    root.mkdir(parents=True)
    (root / "pgo").mkdir()
    (root / "config.yaml").write_text(text, encoding="utf-8")
    (root / "manifest.json").write_text(json.dumps({"config_source": str(config_path),
        "image": image, "amount": amount, "delay_seconds": delay, "warmup_delay_seconds": 0,
        "startup_warmup_disabled": True,
        "payloads": scenario["payloads"], "fcus": scenario["fcus"],
        "snapshot_source": scenario["snapshot_source"], "snapshot_backend": "overlay",
        "raw_profile_directory": str(root / "pgo"), "replay_window": window,
        "state_identity_verified": False}, indent=2) + "\n", encoding="utf-8")
    return config


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--amount", type=int, required=True)
    parser.add_argument("--delay", type=int, choices=(0, 1), default=0)
    args = parser.parse_args()
    render(args.config, args.root, args.image, args.amount, args.delay)


if __name__ == "__main__":
    main()
