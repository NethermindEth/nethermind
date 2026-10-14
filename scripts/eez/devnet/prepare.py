#!/usr/bin/env python3
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess


def revision(path):
    return subprocess.check_output(["git", "-C", str(path), "rev-parse", "HEAD"], text=True).strip()

def image(value, prefix):
    if not re.fullmatch(prefix + r":[a-z0-9._-]+", value):
        raise ValueError("invalid isolated candidate image")
    return value

def prepare(args):
    upstream = args.upstream.resolve()
    manifest = json.loads(args.manifest.read_text())
    if manifest["repository"] != "eez-association/eez-rollup0":
        raise ValueError("unexpected upstream repository")
    if revision(upstream) != manifest["revision"] or revision(upstream / "eez-core-protocol") != manifest["core_revision"]:
        raise ValueError("integration sources do not match their pinned revisions")
    source = Path(__file__).resolve().parent
    package = upstream / "testing/kurtosis"
    main = package / "main.star"
    original = main.read_text()
    if "nethermind.add_initial" in original:
        raise ValueError("the integration overlay was already applied")
    if 'l1_el = participants[0].el_context' not in original or 'name="eez-deployments"' not in original:
        raise ValueError("upstream deployment seam changed")
    main.write_text('nethermind = import_module("./nethermind.star")\n' + original +
        '\n    nethermind.add_initial(plan, eez, l1_el.rpc_http_url, chain_id, deploy.files_artifacts[0])\n')
    shutil.copyfile(source / "nethermind.star", package / "nethermind.star")
    values = {
        "enable_explorers": "false",
        "eez_node_image": image(args.node_image, "eez-composer"),
        "proof_signer_image": image(args.signer_image, "eez-proof-signer"),
        "deploy_image": image(args.deploy_image, "eez-deploy"),
    }
    text = (package / "ci-args.yaml").read_text()
    for name, value in values.items():
        text, count = re.subn(r"(?m)^  " + re.escape(name) + r":.*$", "  " + name + ": " + value, text)
        if count != 1:
            raise ValueError("upstream argument is missing or duplicated: " + name)
    text += "\n  nethermind_image: " + image(args.follower_image, "eez-nethermind") + "\n"
    args.output.write_text(text)
    driver = package / "run-ci.sh"
    commands = driver.read_text()
    names = []
    def hook(match):
        name = Path(match.group(1)).stem
        if not re.fullmatch(r"[a-z0-9-]+", name):
            raise ValueError("invalid upstream scenario name")
        names.append(name)
        return match.group(0) + '\nbash "$NETHERMIND_OBSERVE_SCRIPT" ' + name
    commands = re.sub(r'(?m)^(?:L2="\$l2" )?bash "\$HERE/scripts/([^"\n]+)"$', hook, commands)
    if not names or "verify-cross-chain-waves" not in names:
        raise ValueError("upstream scenario driver changed; no cross-chain observation boundary")
    marker = "\ncapture_service_log eez-proof-signer\n"
    if commands.count(marker) != 1:
        raise ValueError("upstream final verification boundary changed")
    commands = commands.replace(marker, '\nbash "$NETHERMIND_OBSERVE_SCRIPT" recovery\n' + marker)
    log_loop = 'for service in eez-node eez-proof-signer "$KURTOSIS_BUILDER_SERVICE" mev-relay-api; do'
    if commands.count(log_loop) != 1:
        raise ValueError("upstream diagnostic boundary changed")
    commands = commands.replace(log_loop, 'for service in nethermind-l2 nethermind-l2-late eez-node eez-proof-signer "$KURTOSIS_BUILDER_SERVICE" mev-relay-api; do')
    driver.write_text(commands)
    (args.output.parent / "scenario-hooks.json").write_text(json.dumps(names) + "\n")

def late(args):
    source = Path(__file__).resolve().parent
    args.output.mkdir(parents=True, exist_ok=False)
    shutil.copyfile(source / "nethermind.star", args.output / "nethermind.star")
    (args.output / "kurtosis.yml").write_text("name: github.com/NethermindEth/nethermind/eez-late-follower\n")
    (args.output / "main.star").write_text('follower = import_module("./nethermind.star")\n\ndef run(plan, args):\n'
        '    follower.add_follower(plan, args["image"], "nethermind-l2-late", args["l1_rpc"], args["l1_chain_id"], "eez-deployments")\n')
    (args.output / "args.json").write_text(json.dumps({"image": image(args.follower_image, "eez-nethermind"),
        "l1_rpc": (args.connection / "l1-rpc").read_text(), "l1_chain_id": int((args.connection / "l1-chain-id").read_text())}) + "\n")

def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="mode", required=True)
    start = commands.add_parser("prepare")
    start.add_argument("--manifest", type=Path, required=True)
    start.add_argument("--upstream", type=Path, required=True)
    start.add_argument("--output", type=Path, required=True)
    for name in ["node-image", "signer-image", "deploy-image", "follower-image"]:
        start.add_argument("--" + name, required=True)
    finish = commands.add_parser("late")
    finish.add_argument("--output", type=Path, required=True)
    finish.add_argument("--connection", type=Path, required=True)
    finish.add_argument("--follower-image", required=True)
    args = parser.parse_args()
    (prepare if args.mode == "prepare" else late)(args)

if __name__ == "__main__":
    main()
