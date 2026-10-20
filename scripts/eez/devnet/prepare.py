#!/usr/bin/env python3
import argparse
import json
import os
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
    profile = manifest.get("profile", "rollup0")
    repositories = {"rollup0": "eez-association/eez-rollup0", "rollupx": "eez-association/eez-rollupX"}
    if profile not in repositories or manifest["repository"] != repositories[profile]:
        raise ValueError("unexpected upstream repository")
    native = profile == "rollupx"
    if native and "streaming" in manifest:
        raise ValueError("RollupX composer streaming requires a separately reviewed integration")
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
    beacon = ", l1_cl.beacon_http_url" if native else ""
    if native and "l1_cl = participants[0].cl_context" not in original:
        raise ValueError("upstream beacon endpoint boundary changed")
    original, launches = re.subn(
        r"ethereum_package\.run\((plan|resources\.relay_startup_plan\(plan\)), eth_args\)",
        r"ethereum_package.run(nethermind.relay_plan(\1), eth_args)", original)
    if launches != 1:
        raise ValueError("upstream relay startup boundary changed")
    if native:
        prepare_native_evidence(package)
    main.write_text('nethermind = import_module("./nethermind.star")\n' + original +
        '\n    nethermind.add_initial(plan, eez, l1_el.rpc_http_url, chain_id, deploy.files_artifacts[0]' + beacon + ')\n')
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
    if native:
        for phase, name in (("waves-and-state-chaining", "verify-cross-chain-waves"), ("protocol-suite", "run-protocol-e2e")):
            command = 'timed_phase ' + phase + ' bash "$HERE/scripts/' + name + '.sh"'
            if commands.count(command) != 1:
                raise ValueError("upstream native scenario boundary changed: " + phase)
            commands = commands.replace(command, command + '\nbash "$NETHERMIND_OBSERVE_SCRIPT" ' + name)
            names.append(name)
    if not names or "verify-cross-chain-waves" not in names:
        raise ValueError("upstream scenario driver changed; no cross-chain observation boundary")
    marker = "\ncapture_service_log eez-proof-signer\n"
    if commands.count(marker) != 1:
        raise ValueError("upstream final verification boundary changed")
    commands = commands.replace(marker, '\nbash "$NETHERMIND_OBSERVE_SCRIPT" recovery\n' + marker)
    log_loop = 'for service in eez-node eez-proof-signer '
    if commands.count(log_loop) != (2 if native else 1):
        raise ValueError("upstream diagnostic boundary changed")
    commands = commands.replace(log_loop, 'for service in nethermind-l2 nethermind-l2-late eez-node eez-proof-signer ')
    if "streaming" in manifest:
        readiness = 'timeout 10s kurtosis service logs "$KURTOSIS_ENCLAVE" eez-node'
        if commands.count(readiness) != 1:
            raise ValueError("upstream streaming readiness log boundary changed")
        commands = commands.replace(readiness, 'timeout 10s kurtosis service logs --all "$KURTOSIS_ENCLAVE" eez-node')
        capture = '    timeout 30s kurtosis service logs "$KURTOSIS_ENCLAVE" "$service" '
        if commands.count(capture) != 1:
            raise ValueError("upstream log capture boundary changed")
        commands = commands.replace(capture,
            '    [[ ! -s "$RESULT_DIR/$service.log" ]] || return 0\n'
            '    timeout 30s kurtosis service logs --all "$KURTOSIS_ENCLAVE" "$service" ')
    if os.environ.get("EEZ_SUITE"):
        from partitions import adapt_driver
        commands, names = adapt_driver(commands, package, profile, os.environ["EEZ_SUITE"])
        commands = partition_observations(commands, names)
    driver.write_text(commands)
    (args.output.parent / "scenario-hooks.json").write_text(json.dumps(names) + "\n")


def partition_observations(commands, names):
    initial = 'bash "$NETHERMIND_OBSERVE_SCRIPT" verify-eezl2-deployment'
    if len(names) != 2 or commands.count(initial) != 1:
        raise ValueError("upstream partition observation inventory changed")
    commands = commands.replace(initial, 'bash "$NETHERMIND_OBSERVE_SCRIPT" genesis')
    selected = 'bash "$NETHERMIND_OBSERVE_SCRIPT" ' + names[1]
    if commands.count(selected) != 1:
        raise ValueError("upstream selected observation boundary changed")
    commands = commands.replace(selected, initial + "\n" + selected)
    return commands

def prepare_native_evidence(package):
    path = package / "scripts/verify-static-counter-l2.sh"
    text = path.read_text()
    for name, source in (("causal_graph", "$causal_tmp/output.json"), ("blob_da", "$blob_evidence_file")):
        argument = '--argjson ' + name + ' "$' + name + '"'
        if argument not in text:
            continue
        field = name + ': $' + name + ','
        if text.count(argument) != 1 or text.count(field) != 1:
            raise ValueError("upstream native evidence JSON boundary changed: " + name)
        text = text.replace(argument, '--rawfile ' + name + ' "' + source + '"')
        text = text.replace(field, name + ': ($' + name + ' | fromjson),')
    path.write_text(text)

def late(args):
    source = Path(__file__).resolve().parent
    args.output.mkdir(parents=True, exist_ok=False)
    shutil.copyfile(source / "nethermind.star", args.output / "nethermind.star")
    (args.output / "kurtosis.yml").write_text("name: github.com/NethermindEth/nethermind/eez-late-follower\n")
    (args.output / "main.star").write_text('follower = import_module("./nethermind.star")\n\ndef run(plan, args):\n'
        '    follower.add_follower(plan, args["image"], "nethermind-l2-late", args["l1_rpc"], args["l1_chain_id"], "eez-deployments", args["l1_beacon"])\n')
    (args.output / "args.json").write_text(json.dumps({"image": image(args.follower_image, "eez-nethermind"),
        "l1_rpc": (args.connection / "l1-rpc").read_text(), "l1_chain_id": int((args.connection / "l1-chain-id").read_text()),
        "l1_beacon": (args.connection / "l1-beacon").read_text() if (args.connection / "l1-beacon").exists() else ""}) + "\n")

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
    if os.environ.get("EEZ_SUITE"):
        for phase in json.loads((args.directory / "scenario-hooks.json").read_text()):
            observation = json.loads((args.directory / (phase + ".json")).read_text())
            if observation["result"] != "pass" or observation.get("verified_blocks", 0) <= 1:
                raise ValueError("missing complete candidate prefix observation")
    report = {"result": "pass", "implementation": "Nethermind", "wire": "prove.v2",
        "profile": "rollup0-conditional", "transport": "isolated-h2-to-mtls",
        "candidate_revision": args.revision, "image_id": args.image_id,
        "proof_flow": counts, "signed_windows": signed}
    (args.directory / "candidate-prover.json").write_text(json.dumps(report, indent=2) + "\n")


def follower_report(args):
    manifest = json.loads((args.directory / "upstream.json").read_text())
    result = json.loads((args.directory / "result.json").read_text())
    if manifest.get("profile") != "rollupx" or manifest["repository"] != "eez-association/eez-rollupX" or "streaming" in manifest:
        raise ValueError("unexpected native follower profile")
    if result["result"] != "pass" or result["exit_code"] != 0 or result["commits"] != {"root": manifest["revision"], "protocol": manifest["core_revision"]}:
        raise ValueError("native follower scenarios or source verification failed")
    counts = result["proof_flow"]
    if not all(type(counts[name]) is int and counts[name] > 0 for name in ("signed_windows", "remote_attestations")):
        raise ValueError("upstream proof path did not run")
    if not re.fullmatch(r"[0-9a-f]{40}", args.revision) or not re.fullmatch(r"sha256:[0-9a-f]{64}", args.image_id):
        raise ValueError("invalid candidate provenance")
    observations = {}
    phases = ("verify-eezl2-deployment", "before-restart", "after-restart", "fresh-follower")
    if os.environ.get("EEZ_SUITE") and os.environ["EEZ_SUITE"] != "recovery":
        phases = json.loads((args.directory / "scenario-hooks.json").read_text())
    for phase in phases:
        observation = json.loads((args.directory / (phase + ".json")).read_text())
        if observation["result"] != "pass" or observation["profile"] != "rollupx-native" or observation["phase"] != phase:
            raise ValueError("missing native follower observation")
        if os.environ.get("EEZ_SUITE") and observation.get("verified_blocks", 0) <= 1:
            raise ValueError("missing complete native follower prefix")
        if phase != "verify-eezl2-deployment" and not all(type(observation[name]) is int and observation[name] > 0 for name in ("verified_blocks", "verified_receipts", "verified_native_transactions")):
            raise ValueError("native follower scenario has no verified work")
        observations[phase] = observation
    report = {"result": "pass", "implementation": "Nethermind", "role": "follower", "profile": "rollupx-native",
        "candidate_revision": args.revision, "image_id": args.image_id, "prover": "upstream",
        "upstream_proof_flow": counts, "observations": observations}
    (args.directory / "candidate-follower.json").write_text(json.dumps(report, indent=2) + "\n")


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
    for mode in ("report", "follower-report"):
        evidence = commands.add_parser(mode)
        evidence.add_argument("--directory", type=Path, required=True)
        evidence.add_argument("--revision", required=True)
        evidence.add_argument("--image-id", required=True)
    args = parser.parse_args()
    {"prepare": prepare, "late": late, "report": report, "follower-report": follower_report}[args.mode](args)

if __name__ == "__main__":
    main()
