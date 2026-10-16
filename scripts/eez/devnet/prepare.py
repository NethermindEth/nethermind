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
    if 'EEZ_SKIP_PROOF_SIGNER_BUILD' not in (package / "start.sh").read_text():
        raise ValueError("upstream no longer supports the candidate prover image")
    main = package / "main.star"
    original = main.read_text()
    if 'exec eez-proof-signer' not in original or not any(endpoint in original for endpoint in (
        '"EEZ_PROVER_URL": "http://eez-proof-signer:',
        '"EEZ_PROVER_URL": "http://{}:{}".format(prover_service, PROOF_SIGNER_GRPC_PORT)')):
        raise ValueError("upstream prover deployment seam changed")
    if "nethermind.add_initial" in original:
        raise ValueError("the integration overlay was already applied")
    if 'l1_el = participants[0].el_context' not in original or 'name="eez-deployments"' not in original:
        raise ValueError("upstream deployment seam changed")
    if "streaming" in manifest:
        if manifest["streaming"]["wire"] != "prove.v2" or manifest["streaming"]["profile"] != "rollup0-conditional":
            raise ValueError("unexpected streaming profile")
        if not re.search(r"(?m)^  prover_backend: stateless$", (package / "ci-args.yaml").read_text()):
            raise ValueError("the candidate requires the stateless upstream backend")
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
    if "streaming" in manifest:
        capture = '    timeout 30s kurtosis service logs "$KURTOSIS_ENCLAVE" "$service" '
        if commands.count(capture) != 1:
            raise ValueError("upstream log capture boundary changed")
        commands = commands.replace(capture,
            '    [[ ! -s "$RESULT_DIR/$service.log" ]] || return 0\n'
            '    timeout 30s kurtosis service logs --all "$KURTOSIS_ENCLAVE" "$service" ')
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

def report(args):
    manifest = json.loads((args.directory / "upstream.json").read_text())
    result = json.loads((args.directory / "result.json").read_text())
    streaming = manifest["streaming"]
    counts = result["proof_flow"]
    if streaming["wire"] != "prove.v2" or streaming["profile"] != "rollup0-conditional" or result["result"] != "pass" or result["exit_code"] != 0:
        raise ValueError("live streaming scenarios did not pass")
    if not all(type(counts[name]) is int and counts[name] > 0 for name in ("signed_windows", "remote_attestations")):
        raise ValueError("missing actual signed windows or remote attestations")
    if result["commits"] != {"root": manifest["revision"], "protocol": manifest["core_revision"]}:
        raise ValueError("upstream results do not match selected source")
    if not re.fullmatch(r"[0-9a-f]{40}", args.revision) or not re.fullmatch(r"sha256:[0-9a-f]{64}", args.image_id):
        raise ValueError("invalid candidate provenance")
    log = args.directory / "eez-proof-signer.log"
    if log.stat().st_size > 64 * 1024 * 1024:
        raise ValueError("prover log exceeds its limit")
    signed = []
    with log.open() as source:
        for line in source:
            if '"event_name":"eez.proof_signer.window_signed"' not in line:
                continue
            match = re.search(r'(\{"event_name":"eez\.proof_signer\.window_signed".*\})', line)
            if match is None:
                raise ValueError("malformed signed-window evidence")
            event = json.loads(match[1])["fields"]
            start, end = event["validated_from_block"], event["validated_to_block"]
            if event.get("wire") != "prove.v2" or type(start) is not int or type(end) is not int or not 0 <= start <= end or not re.fullmatch(r"0x[0-9a-fA-F]{64}", event["recomputed_public_inputs_hash"]):
                raise ValueError("invalid or fallback signed-window evidence")
            signed.append(event)
    if len(signed) != counts["signed_windows"]:
        raise ValueError("signed-window evidence differs from upstream counts")
    report = {"result": "pass", "implementation": "Nethermind", "wire": "prove.v2",
        "profile": "rollup0-conditional", "transport": "isolated-h2-to-mtls",
        "candidate_revision": args.revision, "image_id": args.image_id,
        "proof_flow": counts, "signed_windows": signed}
    (args.directory / "candidate-prover.json").write_text(json.dumps(report, indent=2) + "\n")


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
    evidence = commands.add_parser("report")
    evidence.add_argument("--directory", type=Path, required=True)
    evidence.add_argument("--revision", required=True)
    evidence.add_argument("--image-id", required=True)
    args = parser.parse_args()
    {"prepare": prepare, "late": late, "report": report}[args.mode](args)

if __name__ == "__main__":
    main()
