#!/usr/bin/env python3
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time

from verify import HEADER, Rpc, SYSTEM, equal, registry, verify_prefix


CASES = {
    "durability": {
        "restart_with_datadir_ahead_of_l1_retreats_before_production":
            ("unsettled-restart", ("before-restart", "after-restart")),
        "l1_only_restart_keeps_finalized":
            ("finalized-restart", ("before-restart", "after-restart")),
        "settled_safe_prefix_survives_restart_and_fresh_follower_replay":
            ("durability", ("before-restart", "after-restart", "after-new-traffic", "fresh-follower")),
    },
    "resumption": {
        "resumed_batch_settles_only_its_new_suffix":
            ("resumption", ("before-resumption", "after-resumption")),
    },
    "resumption_append": {
        "a_resumed_batch_appends_the_l2_content_of_the_entries_it_settled":
            ("resumption-append", ("before-resumption", "after-resumption")),
    },
    "contract_post": {
        "a_contract_post_derives_identically_and_an_untraced_node_stops":
            ("contract-post", ("after-contract-post", "after-subsequent-settlement")),
    },
    "partial_consumption": {
        "inclusion_revert_settles_a_mixed_prefix_and_follower_converges":
            ("partial-prefix", ("after-partial-settlement",)),
        "inclusion_revert_can_settle_only_the_anchor":
            ("anchor-only", ("after-anchor-settlement",)),
    },
    "native_system": {
        "two_composers_two_keyless_followers":
            ("native-system", ("after-deposit", "after-outbound", "fresh-follower")),
    },
}


def digest(text):
    return hashlib.sha256(text.encode()).hexdigest()


def inventory(upstream):
    cases = []
    for path in sorted(upstream.glob("crates/*/tests/**/*.rs")):
        source = path.read_text()
        pattern = r"(?m)((?:^[ \t]*#\[[^\n]+\][ \t]*\n)+)[ \t]*(?:async )?fn ([a-zA-Z0-9_]+)\s*\("
        for match in re.finditer(pattern, source):
            attributes, name = match.groups()
            if not re.search(r"#\[(?:tokio::)?test(?:\(|\])", attributes):
                continue
            ignored = "#[ignore" in attributes
            selected = path.parent == upstream / "crates/eez-node-e2e/tests" and name in CASES.get(path.stem, {})
            cases.append({"source": str(path.relative_to(upstream)), "test": name,
                          "source_sha256": digest(source), "upstream_ignored": ignored,
                          "asynchronous": "async fn " in match[0],
                          "candidate_status": "planned" if selected else "required_but_missing"})
    if not cases:
        raise ValueError("no upstream integration test inventory")
    return cases


def insert(source, marker, addition):
    if source.count(marker) != 1:
        raise ValueError("upstream candidate observation boundary changed: " + marker[:100])
    return source.replace(marker, marker + addition)


def adapt(suite, source):
    additions = []

    def after(marker, addition):
        nonlocal source
        source = insert(source, marker, addition)
        additions.append(addition)

    module_marker = {
        "native_system": "use serde_json::{Value, json};",
        "contract_post": "use std::time::{Duration, Instant};",
        "partial_consumption": "const TIMEOUT: Duration = Duration::from_mins(6);",
    }.get(suite, "use std::time::Duration;")
    after(module_marker, '\n\n#[path = "nethermind/mod.rs"]\nmod nethermind;')
    if suite == "native_system":
        after("    let withdrawal_before = l2_balance(&l1_rpc, world.withdrawal_recipient).await?;", '\n    let candidate = nethermind::Candidate::start_cross_chain("native-system", &world).await?;\n    let mut native_expected = json!({\n        "system_balance": "0x0", "system_nonce": "0x1",\n        "recipient": world.recipient, "recipient_balance": format!("{:#x}", recipient_before + deposit),\n    });')
        after("    // Drain any already accepted Composer 1 bundle before switching producers.",
              '\n    candidate.verify_native(&primary_rpc, 1, "after-deposit", &native_expected).await?;')
        after("    world.node.assert_no_divergence_failure_logs();", '\n    native_expected["system_balance"] = json!(format!("{withdrawal:#x}"));\n    native_expected["system_nonce"] = json!("0x2");\n    candidate.verify_native(&primary_rpc, final_height, "after-outbound", &native_expected).await?;\n    drop(candidate);\n    let fresh = nethermind::Candidate::start_cross_chain("native-system", &world).await?;\n    fresh.verify_native(&primary_rpc, final_height, "fresh-follower", &native_expected).await?;')
    elif suite == "contract_post":
        after("    let w = setup_cross_chain_with_env(&[external]).await.unwrap();",
              '\n    let candidate = nethermind::Candidate::start_cross_chain("contract-post", &w).await.unwrap();')
        after("    let first = commitment_at(&w, copied).await.unwrap();",
              '\n    candidate.verify_settled(&w.l2_rpc(), &first.to_string(), "after-contract-post").await.unwrap();')
        after("    assert_safe_holds_commitment(&w, &[&w.node, &traced], later).await;",
              '\n    let committed = commitment_at(&w, later).await.unwrap();\n    candidate.verify_settled(&w.l2_rpc(), &committed.to_string(), "after-subsequent-settlement").await.unwrap();')
    elif suite == "partial_consumption":
        for name, label in (("inclusion_revert_settles_a_mixed_prefix_and_follower_converges", "partial-prefix"),
                            ("inclusion_revert_can_settle_only_the_anchor", "anchor-only")):
            after("async fn " + name + "() {\n    let w = setup_cross_chain().await.unwrap();",
                  '\n    let candidate = nethermind::Candidate::start_cross_chain("' + label + '", &w).await.unwrap();')
        after("    assert_follower_reaches(&w, settled).await;",
              '\n    candidate.verify_settled(&l2_rpc, &settled.1.to_string(), "after-partial-settlement").await.unwrap();')
        after("    assert_follower_reaches(&w, (settled.0, settled.1)).await;",
              '\n    candidate.verify_settled(&l2_rpc, &settled.1.to_string(), "after-anchor-settlement").await.unwrap();')
    elif suite == "durability":
        after('    let env = harness.env_for(STAGING_USER_KEY, true).await.unwrap();',
              '\n    let mut candidate = nethermind::Candidate::start("unsettled-restart", harness.l2_genesis_path(), &harness.follower_env().await.unwrap()).await.unwrap();')
        after('            "the precondition requires every staged L2 block to be absent from L1"\n        );',
              '\n        candidate.verify_unsettled(&node.l2_rpc_url(), &format!("{staged_tx:#x}"), "before-restart").await.unwrap();\n        candidate.restart().await.unwrap();')
        after('        "the unfunded poster must not accidentally make the staged suffix L1-canonical"\n    );',
              '\n    candidate.verify_unsettled(&restarted.l2_rpc_url(), &format!("{staged_tx:#x}"), "after-restart").await.unwrap();')
        marker = "    let (finalized, safe) = {"
        addition = '    let mut candidate = nethermind::Candidate::start("finalized-restart", harness.l2_genesis_path(), &harness.follower_env().await.unwrap()).await.unwrap();\n'
        if source.count(marker) != 1:
            raise ValueError("upstream finality restart boundary changed")
        source = source.replace(marker, addition + marker)
        additions.append(addition)
        if source.count('        node.assert_no_process_death();\n        (finalized, safe)') != 1:
            raise ValueError("upstream finalized observation boundary changed")
        marker = '        node.assert_no_process_death();\n        (finalized, safe)'
        addition = '        candidate.verify_finalized(&rpc, finalized, "before-restart").await.unwrap();\n        candidate.restart().await.unwrap();\n'
        source = source.replace(marker, addition + marker)
        additions.append(addition)
        after('        "finalized fell from #{finalized} to #{after} after the restart"\n    );',
              '\n    candidate.verify_finalized(&rpc, finalized, "after-restart").await.unwrap();')
        after("    let states_before = chain.executed_states().await.unwrap();", "\n    let mut candidate = nethermind::Candidate::start(\"durability\", harness.l2_genesis_path(), &harness.follower_env().await.unwrap()).await.unwrap();")
        after('        .expect("composer did not retain its newly settled safe block");', '\n        candidate.verify(&node.l2_rpc_url(), settled_before_restart.0, "before-restart").await.unwrap();\n        candidate.restart().await.unwrap();')
        after('    .expect("restarted composer did not restore the exact pre-crash safe prefix");', '\n    candidate.verify(&restarted.l2_rpc_url(), settled_before_restart.0, "after-restart").await.unwrap();')
        after("    follower.assert_no_process_death();", '\n    candidate.verify(&restarted.l2_rpc_url(), settled_after_restart.0, "after-new-traffic").await.unwrap();\n    drop(candidate);\n    let fresh = nethermind::Candidate::start("durability", harness.l2_genesis_path(), &follower_env).await.unwrap();\n    fresh.verify(&restarted.l2_rpc_url(), settled_after_restart.0, "fresh-follower").await.unwrap();')
    elif suite == "resumption":
        after('    let node = NodeHandle::start("resume-composer", &NodeConfig::default(), &env)\n        .await\n        .unwrap();', '\n    let candidate = nethermind::Candidate::start("resumption", harness.l2_genesis_path(), &harness.follower_env().await.unwrap()).await.unwrap();')
        marker = "    chain.set_interval_mining(0).await.unwrap();"
        addition = '    candidate.verify(&node.l2_rpc_url(), 1, "before-resumption").await.unwrap();\n'
        if source.count(marker) != 1:
            raise ValueError("upstream resumption mining boundary changed")
        source = source.replace(marker, addition + marker)
        additions.append(addition)
        after("    node.assert_no_process_death();", '\n    candidate.verify(&node.l2_rpc_url(), 1, "after-resumption").await.unwrap();')
    elif suite == "resumption_append":
        after("    let w = setup_cross_chain().await.unwrap();", '\n    let candidate = nethermind::Candidate::start_cross_chain("resumption-append", &w).await.unwrap();')
        after("    let eez = w.cfg.eez_address;", '\n    candidate.verify(&l2, 1, "before-resumption").await.unwrap();')
        marker = '    .expect(\n        "no batch settled after the resumed append: the L1 commitment never \\\n         caught up to a new L2 safe block",\n    );'
        after(marker, '\n    candidate.verify(&l2, 1, "after-resumption").await.unwrap();')
    else:
        raise ValueError("unknown candidate integration suite")
    original = source
    for addition in reversed(additions):
        if addition:
            if original.count(addition) != 1:
                raise ValueError("candidate observation is not unique")
            original = original.replace(addition, "", 1)
    return source, original


def prepare(args):
    manifest = json.loads(args.manifest.read_text())
    upstream = args.upstream.resolve()
    if manifest.get("profile") != "rollup0":
        raise ValueError("embedded candidate adapter requires Rollup0")
    for root, expected in ((upstream, manifest["revision"]), (upstream / "eez-core-protocol", manifest["core_revision"])):
        actual = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
        equal(actual, expected, "embedded source revision")
    cases = inventory(upstream)
    directory = upstream / "crates/eez-node-e2e/tests"
    changes = []
    for suite, selected in CASES.items():
        path = directory / (suite + ".rs")
        source = path.read_text()
        found = [case for case in cases if case["source"] == str(path.relative_to(upstream)) and not case["upstream_ignored"] and case["asynchronous"]]
        if {case["test"] for case in found} != set(selected):
            raise ValueError("upstream suite inventory changed; candidate observations required: " + suite)
        changed, restored = adapt(suite, source)
        equal(restored, source, "all original upstream bytes and assertions must remain")
        changes.append((path, changed))
    helper = directory / "nethermind/mod.rs"
    if helper.exists():
        raise ValueError("candidate module already exists")
    helper.parent.mkdir()
    shutil.copyfile(Path(__file__).with_name("embedded.rs"), helper)
    for path, changed in changes:
        path.write_text(changed)
    args.output.write_text(json.dumps({"upstream": manifest, "cases": cases}, indent=2) + "\n")


def verify(settings_path):
    settings = json.loads(settings_path.read_text())
    directory = Path(os.environ["EEZ_EMBEDDED_RESULTS"]).resolve()
    equal(str(directory), str(Path(settings["result_directory"]).resolve()), "candidate result directory")
    equal(settings["candidate_revision"], os.environ["GITHUB_SHA"], "candidate source")
    if not re.fullmatch(r"sha256:[a-f0-9]{64}", settings["image_id"]):
        raise ValueError("candidate must use immutable image identity")
    image = json.loads(subprocess.check_output(["docker", "image", "inspect", settings["image_id"]], text=True))[0]
    equal(image["Id"], settings["image_id"], "candidate image")
    container = json.loads(subprocess.check_output(["docker", "container", "inspect", settings["container"]], text=True))[0]
    equal(container["Image"], settings["image_id"], "running candidate image")
    if settings["reference"] == settings["candidate"]:
        raise ValueError("candidate must be an independent node")
    equal(image["Config"]["Labels"]["org.opencontainers.image.revision"], settings["candidate_revision"], "candidate image source")
    case, phase = settings["case"], settings["phase"]
    allowed = [phases for selected in CASES.values() for label, phases in selected.values() if label == case]
    if len(allowed) != 1 or phase not in allowed[0]:
        raise ValueError("untracked candidate observation")
    call = subprocess.check_output(["cast", "calldata", "rollups(uint64)", settings["rollup_id"]], text=True).strip()
    deadline = time.monotonic() + 180
    l1, reference, candidate = [Rpc(settings[name], deadline) for name in ("l1", "reference", "candidate")]
    equal(l1.call("eth_chainId"), hex(int(settings["l1_chain_id"])), "L1 chain identity")
    equal(reference.call("eth_chainId"), candidate.call("eth_chainId"), "L2 chain identity")
    genesis = reference.call("eth_getBlockByNumber", "0x0", False)["hash"]
    expected_hash = settings.get("settled_hash")
    expected_height = 0
    if expected_hash is not None:
        if not re.fullmatch(r"0x[a-fA-F0-9]{64}", expected_hash):
            raise ValueError("invalid scenario settlement hash")
        expected = reference.call("eth_getBlockByHash", expected_hash, False)
        if expected is None or expected["hash"] != expected_hash or int(expected["number"], 16) == 0:
            raise ValueError("reference is missing the scenario settlement")
        expected_height = int(expected["number"], 16)
    previous = None
    if case == "finalized-restart" and phase == "after-restart":
        previous = json.loads((directory / "finalized-restart-before-restart.json").read_text())
        equal((previous["result"], previous["case"], previous["phase"]),
              ("pass", case, "before-restart"), "finality restart baseline")
        for field in ("candidate_revision", "image_id", "database_id"):
            equal(previous[field], settings[field], "finality restart " + field)
        if previous["container"] == container["Id"]:
            raise ValueError("finality check requires an actual candidate restart")
    while time.monotonic() < deadline:
        snapshot = l1.call("eth_getBlockByNumber", "latest", False)
        commitment = registry(l1, settings["registry"], call, snapshot)
        endpoint = reference.call("eth_getBlockByHash", commitment, False)
        own = candidate.call("eth_getBlockByHash", commitment, False)
        if endpoint is None or own is None or int(endpoint["number"], 16) < max(settings["minimum_height"], expected_height):
            time.sleep(1)
            continue
        finality = None
        if case == "finalized-restart":
            finality = check_finalized(reference, candidate, settings["finalized_minimum"], int(endpoint["number"], 16), previous)
            if finality is None:
                time.sleep(1)
                continue
        if case == "unsettled-restart":
            equal(commitment, genesis, "unsettled scenario must remain at the registered genesis")
            result = check_unsettled(reference, candidate, genesis, settings["staged_transaction"], phase)
        else:
            result = verify_prefix(reference, candidate, commitment, genesis, 8192)
        if finality is not None:
            result.update(finality)
        if case == "native-system":
            state = settings["native_state"]
            check_native_state(reference, candidate, commitment, state)
            minimum = 1 if phase == "after-deposit" else 2
            if result["verified_native_transactions"] < minimum:
                raise ValueError("native scenario did not execute the required system transactions")
            equal(state["system_nonce"], hex(minimum), "native scenario nonce")
            result["native_state"] = state
        equal(l1.call("eth_getBlockByNumber", snapshot["number"], False)["hash"], snapshot["hash"], "canonical L1 recheck")
        for node in (reference, candidate):
            equal(node.call("eth_getBlockByNumber", hex(result["height"]), False)["hash"], commitment, "canonical settled endpoint")
            if expected_hash is not None:
                equal(node.call("eth_getBlockByNumber", hex(expected_height), False)["hash"], expected_hash,
                      "canonical scenario settlement")
        if expected_hash is not None:
            result.update(settled_hash=expected_hash, settled_height=expected_height)
        if case == "resumption-append" and phase == "after-resumption" and result["verified_native_transactions"] == 0:
            raise AssertionError("append scenario has no verified native transactions")
        result.update({"result": "pass", "case": case, "phase": phase, "image_id": settings["image_id"],
                       "candidate_revision": settings["candidate_revision"], "canonical_l1_hash": snapshot["hash"],
                       "database_id": settings["database_id"], "container": container["Id"]})
        (directory / (case + "-" + phase + ".json")).write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result))
        return
    raise TimeoutError("candidate did not derive the required settled prefix")



def check_unsettled(reference, candidate, genesis, transaction, phase):
    if not re.fullmatch(r"0x[a-fA-F0-9]{64}", transaction):
        raise ValueError("invalid staged transaction hash")
    left = reference.call("eth_getBlockByNumber", "0x0", True)
    right = candidate.call("eth_getBlockByNumber", "0x0", True)
    for field in (*HEADER, "transactions"):
        equal(left[field], right[field], "unsettled genesis " + field)
    for node in (reference, candidate):
        for tag in ("safe", "finalized"):
            equal(node.call("eth_getBlockByNumber", tag, False)["hash"], genesis, "unsettled " + tag)
    equal(candidate.call("eth_getBlockByNumber", "latest", False)["hash"], genesis, "candidate imported unsettled tail")
    equal(candidate.call("eth_getTransactionReceipt", transaction), None, "candidate imported unsettled receipt")
    receipt = reference.call("eth_getTransactionReceipt", transaction)
    equal(receipt is not None, phase == "before-restart", "upstream staged receipt lifecycle")
    if receipt is not None:
        equal(receipt["transactionHash"], transaction, "staged receipt identity")
    return {"height": 0, "endpoint": genesis, "verified_blocks": 1, "verified_receipts": 0,
            "verified_native_transactions": 0, "staged_transaction": transaction}


def check_finalized(reference, candidate, minimum, height, previous=None):
    if not isinstance(minimum, int) or minimum <= 0:
        raise ValueError("finalized scenario needs a non-genesis anchor")
    safe = candidate.call("eth_getBlockByNumber", "safe", False)
    if safe is None or int(safe["number"], 16) > height:
        return None
    safe_height = int(safe["number"], 16)
    if previous is not None and safe_height <= previous["safe_height"]:
        return None
    blocks = [node.call("eth_getBlockByNumber", "finalized", False) for node in (reference, candidate)]
    if previous is not None:
        if blocks[1] is None or int(blocks[1]["number"], 16) < previous["finalized_height"]:
            raise AssertionError("candidate finalized head regressed after safe advanced")
        for node in (reference, candidate):
            equal(node.call("eth_getBlockByNumber", hex(previous["finalized_height"]), False)["hash"],
                  previous["finalized_hash"], "pre-restart finalized block")
    if any(block is None or not minimum <= int(block["number"], 16) <= height for block in blocks):
        return None
    if int(blocks[1]["number"], 16) > safe_height:
        return None
    anchor = reference.call("eth_getBlockByNumber", hex(minimum), False)
    equal(candidate.call("eth_getBlockByNumber", hex(minimum), False)["hash"], anchor["hash"], "finalized anchor")
    for node, block in zip((reference, candidate), blocks):
        equal(node.call("eth_getBlockByNumber", block["number"], False)["hash"], block["hash"], "canonical finalized block")
    equal(candidate.call("eth_getBlockByNumber", safe["number"], False)["hash"], safe["hash"], "canonical safe block")
    return {"safe_height": safe_height, "safe_hash": safe["hash"],
            "finalized_height": int(blocks[1]["number"], 16), "finalized_hash": blocks[1]["hash"],
            "finalized_anchor_height": minimum, "finalized_anchor_hash": anchor["hash"]}


def check_native_state(reference, candidate, commitment, expected):
    context = {"blockHash": commitment, "requireCanonical": True}
    for node in (reference, candidate):
        equal(node.call("eth_getBalance", SYSTEM, "0x0"), "0x0", "native genesis balance")
        equal(node.call("eth_getTransactionCount", SYSTEM, "0x0"), "0x0", "native genesis nonce")
        equal(node.call("eth_getBalance", SYSTEM, context), expected["system_balance"], "native system balance")
        equal(node.call("eth_getTransactionCount", SYSTEM, context), expected["system_nonce"], "native system nonce")
        equal(node.call("eth_getBalance", expected["recipient"], context), expected["recipient_balance"], "native recipient balance")


def report(args):
    result = json.loads(args.inventory.read_text())
    for case in result["cases"]:
        if case["candidate_status"] != "planned":
            continue
        suite = Path(case["source"]).stem
        partition = os.environ.get("EEZ_SUITE")
        if partition and partition != "embedded-" + suite:
            continue
        label, phases = CASES[suite][case["test"]]
        observations = []
        for phase in phases:
            observation = json.loads((args.directory / (label + "-" + phase + ".json")).read_text())
            if observation["result"] != "pass" or observation["case"] != label or observation["phase"] != phase or observation["verified_blocks"] < (1 if label == "unsettled-restart" else 2):
                raise ValueError("missing actual candidate execution")
            equal(observation["candidate_revision"], os.environ["GITHUB_SHA"], "candidate test source")
            equal(observation["image_id"], os.environ["EEZ_CANDIDATE_IMAGE"], "candidate test image")
            observations.append(observation)
        if suite in ("contract_post", "partial_consumption"):
            for observation in observations:
                if not re.fullmatch(r"0x[a-fA-F0-9]{64}", observation.get("settled_hash", "")) or not 0 < observation.get("settled_height", 0) <= observation["height"]:
                    raise ValueError("missing exact scenario settlement evidence")
        if label in ("unsettled-restart", "finalized-restart"):
            before, restarted = observations
            if before["database_id"] != restarted["database_id"] or before["container"] == restarted["container"]:
                raise ValueError("candidate restart must retain its database and replace its container")
            if label == "unsettled-restart":
                equal(before["staged_transaction"], restarted["staged_transaction"], "same unsettled transaction")
                for observation in observations:
                    equal((observation["height"], observation["verified_blocks"], observation["verified_receipts"]),
                          (0, 1, 0), "candidate must retain only genesis")
            else:
                if not 0 < before["finalized_anchor_height"] <= before["finalized_height"] <= restarted["finalized_height"]:
                    raise ValueError("candidate finalized head regressed across restart")
                if not before["finalized_height"] <= before["safe_height"] < restarted["safe_height"] <= restarted["height"]:
                    raise ValueError("candidate safe head did not advance after restart")
                equal(before["finalized_anchor_height"], restarted["finalized_anchor_height"], "same finalized anchor height")
                equal(before["finalized_anchor_hash"], restarted["finalized_anchor_hash"], "same finalized anchor hash")
        if label == "durability":
            before, restarted, advanced, fresh = observations
            if before["database_id"] != restarted["database_id"] or restarted["database_id"] != advanced["database_id"] or fresh["database_id"] == before["database_id"]:
                raise ValueError("restart must retain its database; fresh recovery must use a different database")
            if before["container"] == restarted["container"]:
                raise ValueError("candidate did not restart")
        if suite == "native_system":
            before, advanced, fresh = observations
            if before["database_id"] != advanced["database_id"] or fresh["database_id"] == before["database_id"]:
                raise ValueError("native recovery must use a new database after normal follower execution")
            equal(advanced["native_state"], fresh["native_state"], "native fresh recovery state")
            for index, observation in enumerate(observations):
                minimum = 1 if index == 0 else 2
                if observation["verified_native_transactions"] < minimum:
                    raise ValueError("missing native scenario execution")
                equal(observation["native_state"]["system_nonce"], hex(minimum), "native scenario nonce")
        case["candidate_status"] = "passed"
        case["observations"] = observations
    if not os.environ.get("EEZ_SUITE"):
        if not (args.directory / "contracts.json").stat().st_size:
            raise ValueError("upstream contract test results are missing")
        result["contracts"] = {"result": "pass", "command": "forge test --root contracts --json",
                               "candidate_role": None, "note": "upstream contract tests; separate from candidate execution evidence"}
    result["scope"] = "candidate follower; upstream composer and prover"
    result["coverage_complete"] = all(case["candidate_status"] == "passed" for case in result["cases"])
    (args.directory / "candidate-coverage.json").write_text(json.dumps(result, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="mode", required=True)
    start = commands.add_parser("prepare")
    for field in ("upstream", "manifest", "output"):
        start.add_argument("--" + field, type=Path, required=True)
    check = commands.add_parser("verify")
    check.add_argument("settings", type=Path)
    finish = commands.add_parser("report")
    for field in ("inventory", "directory"):
        finish.add_argument("--" + field, type=Path, required=True)
    args = parser.parse_args()
    if args.mode == "prepare": prepare(args)
    elif args.mode == "verify": verify(args.settings)
    else: report(args)


if __name__ == "__main__":
    main()
