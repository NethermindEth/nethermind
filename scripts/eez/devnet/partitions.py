#!/usr/bin/env python3
import argparse
import json
import os
from pathlib import Path
import re

from embedded import CASES, NATIVE_CASES
from verify import equal


PROFILES = ("rollup0", "rollupx")
WAVE_GROUPS = {
    "wave-mixed": ("inbound", "mixed"),
    "wave-mixed-pure": ("inbound", "mixed-pure"),
    "wave-state-chaining": ("inbound", "state-chaining"),
    "recovery": ("inbound", "outbound"),
}


def suites(profile):
    if profile not in PROFILES:
        raise ValueError("unexpected profile")
    result = [*WAVE_GROUPS, "protocol"]
    if profile == "rollup0":
        result += ["ingress", "faults", "contracts"]
        result += ["embedded-" + suite for suite in CASES]
    else:
        result += ["embedded-" + suite for suite in NATIVE_CASES]
    return result


def protocol_cases(upstream, profile):
    if profile == "rollup0":
        driver = (upstream / "testing/kurtosis/scripts/run-protocol-e2e.sh").read_text()
        match = re.search(r"(?m)^SUPPORTED_TARGETS=\(([^)]+)\)", driver)
        if match is None:
            raise ValueError("upstream protocol target inventory changed")
        names = [line.split("#", 1)[0].strip().strip('"') for line in match[1].splitlines()]
        names = [name for name in names if name]
    else:
        root = upstream / "eez-core-protocol"
        paths = sorted((root / "script/e2e/scenarios").glob("**/E2E*.s.sol"))
        names = [path.parent.name for path in paths if len(path.relative_to(root / "script/e2e/scenarios").parts) >= 3
                 and "shared" not in path.relative_to(root).parts
                 and not re.search(r"(?m)^// E2E_EXCLUDE_FROM_ALL:", path.read_text())]
    if not names or len(set(names)) != len(names):
        raise ValueError("missing or duplicate upstream protocol cases")
    if any(not re.fullmatch(r"[a-zA-Z0-9_-]+", name) for name in names):
        raise ValueError("invalid upstream protocol scenario name")
    return names


def plan(upstream, manifest):
    profile = manifest["profile"]
    cases = protocol_cases(upstream, profile)
    result = {"upstream": manifest, "protocol_cases": cases, "suites": {suite: {} for suite in suites(profile)}}
    result["suites"]["protocol"]["targets"] = cases
    for suite, waves in WAVE_GROUPS.items():
        result["suites"][suite]["waves"] = list(waves)
    return result


def replace_once(source, old, new):
    if source.count(old) != 1:
        raise ValueError("upstream partition boundary changed: " + old[:100])
    return source.replace(old, new)


def check_plan(value, profile):
    equal(set(value["suites"]), set(suites(profile)), "prepared suite inventory")
    for suite, group in value["suites"].items():
        equal(group.get("waves", []), list(WAVE_GROUPS.get(suite, ())), "required wave prerequisites")
    targets = value["suites"]["protocol"]["targets"]
    if not targets or len(targets) != len(set(targets)):
        raise ValueError("missing or duplicate upstream protocol cases")
    equal(targets, value["protocol_cases"], "complete upstream protocol inventory")


def adapt_driver(commands, package, profile, suite):
    if suite not in suites(profile) or suite.startswith("embedded-") or suite == "contracts":
        raise ValueError("unexpected live partition")
    selected = "verify-cross-chain-waves" if suite.startswith("wave-") or suite == "recovery" else {
        "ingress": "verify-ingress-admission", "faults": "verify-fault-recovery",
    }.get(suite, "run-protocol-e2e")
    names = ["verify-eezl2-deployment", "verify-cross-chain-waves", "run-protocol-e2e"]
    if profile == "rollup0":
        names += ["verify-ingress-admission", "verify-fault-recovery"]
    observed = re.findall(r'(?m)^bash "\$NETHERMIND_OBSERVE_SCRIPT" ([a-z0-9-]+)$', commands)
    equal(sorted(observed), sorted(names + ["recovery"]), "upstream scenario invocation inventory")
    retained = []
    for name in names:
        pattern = (r'(?m)^(?:(?:L2="\$l2" |timed_phase [a-z-]+ )?bash "\$HERE/scripts/'
                   + re.escape(name) + r'\.sh")\n'
                   + r'bash "\$NETHERMIND_OBSERVE_SCRIPT" ' + re.escape(name) + r'$')
        matches = list(re.finditer(pattern, commands))
        if len(matches) != 1:
            raise ValueError("upstream scenario partition boundary changed: " + name)
        if name not in ("verify-eezl2-deployment", selected):
            commands = commands[:matches[0].start()] + commands[matches[0].end():]
        else:
            retained.append(name)
    if selected == "verify-cross-chain-waves":
        adapt_waves(package / "scripts/verify-cross-chain-waves.sh", WAVE_GROUPS[suite])
    if suite == "faults":
        adapt_fault_readiness(package)
    if profile == "rollupx":
        if suite == "protocol":
            readiness = '# Canonical replay proves readiness; an observer receipt alone does not.\n'
            inbound = ('timed_phase settlement-inbound env EEZ_WAVE_MODE=inbound EEZ_WAVE_COUNT=1 '
                       'EEZ_INCLUDE_REVERTS=1 bash "$HERE/scripts/cross-chain-wave.sh"\n\n')
            commands = replace_once(commands, readiness, inbound + readiness)
        elif suite != "recovery":
            commands = defer_native_settlement(commands, selected)
    if suite != "recovery":
        commands = replace_once(commands, '\nbash "$NETHERMIND_OBSERVE_SCRIPT" recovery\n', "\n")
    teardown = '    bash "$HERE/stop.sh" || true\n'
    if commands.count(teardown) != 1:
        raise ValueError("upstream teardown boundary changed")
    commands = replace_once(commands, teardown + '    exit "$status"\n}',
                            '    exit "$status"\n}')
    commands = replace_once(commands, "write_result() {", "write_upstream_result() {")
    wrapper = ('write_result() {\n'
               '    write_upstream_result "$@"\n'
               '    python3 "$EEZ_PARTITION_SCRIPT" scope --directory "$RESULT_DIR"\n'
               '}\n\n')
    commands = replace_once(commands, "trap cleanup EXIT", wrapper + "trap cleanup EXIT")
    summary = '\nif [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then\n'
    if commands.count(summary) != 1:
        raise ValueError("upstream full-suite summary boundary changed")
    commands = commands[:commands.index(summary)] + '\necho "upstream partition $EEZ_SUITE passed"\n'
    return commands, retained


def adapt_fault_readiness(package):
    script = package / "scripts/verify-fault-recovery.sh"
    library = package / "scripts/lib.sh"
    # L2 RPC readiness does not cover the separate transaction ingress front.
    ready = replace_once(script.read_text(), "wait_l2_rpc\nreplayed=",
                         'wait_l2_rpc\nretry timeout 5s cast chain-id --rpc-url "$L1F" >/dev/null\nreplayed=')
    diagnostics = replace_once(library.read_text(), '2>/dev/null); rc=$?',
                               '2>"$RESULT_DIR/front-rpc-error.log"); rc=$?')
    script.write_text(ready)
    library.write_text(diagnostics)


def defer_native_settlement(commands, selected):
    start = '# Canonical replay proves readiness; an observer receipt alone does not.\n'
    end = '\necho "==> running native cross-chain wave suite"'
    if commands.count(start) != 1 or commands.count(end) != 1:
        raise ValueError("upstream native settlement readiness boundary changed")
    begin, finish = commands.index(start), commands.index(end)
    if finish <= begin:
        raise ValueError("upstream native settlement readiness order changed")
    readiness = commands[begin:finish]
    commands = replace_once(commands, readiness, "")
    invocation = re.compile(r'(?m)^(?:timed_phase [a-z-]+ )?bash "\$HERE/scripts/' + re.escape(selected) + r'\.sh"$')
    matches = list(invocation.finditer(commands))
    if len(matches) != 1:
        raise ValueError("upstream native settlement scenario boundary changed")
    call = matches[0].group(0)
    readiness = ('readiness_started=$SECONDS\n'
                 'deadline=$((SECONDS + ${EEZ_CI_READY_TIMEOUT_SECS:-900}))\n' + readiness)
    return replace_once(commands, call, call + "\n" + readiness)


def adapt_waves(path, selected):
    source = path.read_text()
    start = source.index("for mode in inbound outbound mixed; do\n")
    blocks = source[start:]
    basic, rest = blocks.split('\nrun_check "cross-chain-wave-mixed-pure"', 1)
    pure, state = ('run_check "cross-chain-wave-mixed-pure"' + rest).split('\nrun_check "state-chaining"', 1)
    state = 'run_check "state-chaining"' + state
    if selected not in WAVE_GROUPS.values():
        raise ValueError("unexpected wave partition")
    modes = " ".join(mode for mode in selected if mode in ("inbound", "outbound", "mixed"))
    chosen = basic.replace("for mode in inbound outbound mixed; do", "for mode in " + modes + "; do")
    if "mixed-pure" in selected:
        chosen += "\n" + pure
    if "state-chaining" in selected:
        chosen += "\n" + state
    if blocks.count('bash "$HERE/cross-chain-wave.sh"') != 2 or blocks.count('bash "$HERE/verify-state-chaining.sh"') != 1:
        raise ValueError("upstream wave invocation inventory changed")
    path.write_text(source[:start] + chosen + "\n")


def scope(directory):
    path = directory / "result.json"
    original = json.loads(path.read_text())
    suite = os.environ["EEZ_SUITE"]
    profile = os.environ["EEZ_PROFILE"]
    if suite not in suites(profile):
        raise ValueError("unexpected result partition")
    fields = ("result", "exit_code", "commits", "images", "proof_flow")
    result = {key: original[key] for key in fields}
    result.update({"profile": profile, "suite": suite, "suite_complete": False,
                   "candidate_revision": os.environ["GITHUB_SHA"]})
    path.write_text(json.dumps(result, indent=2) + "\n")


def report(directory):
    profile, suite = os.environ["EEZ_PROFILE"], os.environ["EEZ_SUITE"]
    manifest = json.loads((directory / "upstream.json").read_text())
    partition_plan = json.loads((directory / "partitions.json").read_text())
    equal(partition_plan["upstream"], manifest, "prepared upstream source")
    check_plan(partition_plan, profile)
    protocol = None
    if suite.startswith("embedded-"):
        coverage = json.loads((directory / "embedded/candidate-coverage.json").read_text())
        selected = [case for case in coverage["cases"] if Path(case["source"]).stem == suite[9:] and case["candidate_status"] == "passed"]
        equal(len(selected), len(({**CASES, **NATIVE_CASES})[suite[9:]]), "embedded candidate case count")
    elif suite == "contracts":
        if not (directory / "contracts.json").stat().st_size:
            raise ValueError("missing contract test output")
    else:
        result = json.loads((directory / "result.json").read_text())
        equal((result["result"], result["exit_code"], result["profile"], result["suite"]),
              ("pass", 0, profile, suite), "upstream partition result")
        equal(result["commits"], {"root": manifest["revision"], "protocol": manifest["core_revision"]}, "upstream partition source")
        hooks = json.loads((directory / "scenario-hooks.json").read_text())
        phases = hooks + (["before-restart", "after-restart", "fresh-follower"] if suite == "recovery" else [])
        for phase in phases:
            observation = json.loads((directory / (phase + ".json")).read_text())
            equal(observation["result"], "pass", "candidate observation")
        candidate = "candidate-prover.json" if profile == "rollup0" else "candidate-follower.json"
        equal(json.loads((directory / candidate).read_text())["result"], "pass", "candidate proof/follower path")
        if suite == "recovery" and profile == "rollup0":
            equal(json.loads((directory / "streaming-controls.json").read_text())["result"], "pass", "live streaming controls")
        if suite == "protocol":
            protocol = json.loads((directory / "protocol-e2e/summary.json").read_text())
            equal(protocol["targets"], partition_plan["suites"][suite]["targets"], "executed protocol targets")
            equal(protocol["protocol_commit"], manifest["core_revision"], "protocol scenario source")
            if protocol["failed"] or not protocol["passed"]:
                raise ValueError("upstream protocol partition failed or executed no cases")
            if profile == "rollupx":
                equal(protocol["suite"]["complete"], True, "complete native protocol suite")
                equal(sorted(protocol["passed"]), sorted(protocol["targets"]), "native protocol evidence coverage")
                for field in ("evidence_complete",):
                    equal(protocol[field], True, "native protocol evidence")
                for field in ("accounting", "causal_coverage", "semantic_blob_coverage"):
                    key = "valid" if field == "accounting" else "complete"
                    equal(protocol[field][key], True, "native protocol evidence")
    result = {"result": "pass", "profile": profile, "suite": suite,
              "candidate_revision": os.environ["GITHUB_SHA"], "upstream": manifest,
              "plan": partition_plan, "protocol": protocol}
    (directory / "partition.json").write_text(json.dumps(result, indent=2) + "\n")


def aggregate(directory):
    reports = [json.loads(path.read_text()) for path in directory.glob("*/partition.json")]
    expected = {(profile, suite) for profile in PROFILES for suite in suites(profile)}
    actual = [(item["profile"], item["suite"]) for item in reports]
    if len(actual) != len(set(actual)) or set(actual) != expected:
        raise ValueError("missing or duplicate upstream test partitions")
    for item in reports:
        equal((item["result"], item["candidate_revision"]), ("pass", os.environ["GITHUB_SHA"]), "partition provenance")
    for profile in PROFILES:
        selected = [item for item in reports if item["profile"] == profile]
        sources = [item["upstream"] for item in selected]
        if any(source != sources[0] for source in sources):
            raise ValueError("partitions used different upstream revisions")
        plans = [item["plan"] for item in selected]
        if any(value != plans[0] for value in plans):
            raise ValueError("partitions used different coverage plans")
        check_plan(plans[0], profile)
    (directory / "summary.json").write_text(json.dumps({"result": "pass", "partitions": reports}, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="mode", required=True)
    for name in ("scope", "report", "aggregate"):
        command = commands.add_parser(name)
        command.add_argument("--directory", type=Path, required=True)
    inventory = commands.add_parser("suites")
    inventory.add_argument("--profile", choices=PROFILES, required=True)
    build = commands.add_parser("plan")
    build.add_argument("--upstream", type=Path, required=True)
    build.add_argument("--manifest", type=Path, required=True)
    build.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.mode == "suites":
        print(json.dumps(suites(args.profile)))
    elif args.mode == "plan":
        result = plan(args.upstream, json.loads(args.manifest.read_text()))
        args.output.write_text(json.dumps(result, indent=2) + "\n")
    else:
        {"scope": scope, "report": report, "aggregate": aggregate}[args.mode](args.directory)


if __name__ == "__main__":
    main()
