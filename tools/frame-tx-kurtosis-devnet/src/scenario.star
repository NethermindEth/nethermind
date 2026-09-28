CEILINGS = [100000, 235800, 250000, 300000, 400000, 500000]
K_RETRIES = [1, 2, 4, 8]
ATTACKER_ROLES = ["keccak-wide", "signature-stuffed", "soispoke-groth16", "none"]

DEFAULT_NETHERMIND_IMAGE_REPO = "frame-tx-devnet/nethermind"
DEFAULT_ETHREX_IMAGE_REPO = "frame-tx-devnet/ethrex"
DEFAULT_TRAFFIC_IMAGE = "frame-tx-devnet/traffic:local"

DEFAULTS = {
    "max_verify_gas": 235800,
    "k_retry": 1,
    "attacker_role": "keccak-wide",
    "attacker_rate": 25,
    "baseline_rate": 2,
    "duration_seconds": 240,
    "warmup_seconds": 30,
    "privacy_inclusion": True,
    "scenario_id": "",
    "traffic_image": DEFAULT_TRAFFIC_IMAGE,
    "nethermind_image": "",
    "ethrex_image": "",
    "groth16_artifacts_path": "",
    "traffic_enabled": True,
    "split_traffic": False,
    "extra_el_params": [],
}


def split_args(plan, args):
    """Separates our own scenario block from the args handed through to ethereum-package.

    ethereum-package's input parser rejects keys it does not know, so `frame_tx` has to be
    removed before it ever sees the dict."""
    ethereum_args = {}
    raw_scenario = {}
    for key in args:
        if key == "frame_tx":
            raw_scenario = args[key]
        else:
            ethereum_args[key] = args[key]

    scenario = _with_defaults(plan, raw_scenario)
    ethereum_args = _apply_client_overrides(plan, ethereum_args, scenario)
    return scenario, ethereum_args


def _with_defaults(plan, raw):
    merged = {}
    for key in DEFAULTS:
        merged[key] = DEFAULTS[key]
    for key in raw:
        if key not in DEFAULTS:
            fail(
                "unknown frame_tx parameter '{0}'. Known: {1}".format(
                    key, ", ".join(sorted(DEFAULTS.keys()))
                )
            )
        merged[key] = raw[key]

    ceiling = merged["max_verify_gas"]
    if type(ceiling) != type(1) or ceiling <= 0:
        fail("frame_tx.max_verify_gas must be a positive integer, got {0}".format(ceiling))
    if ceiling not in CEILINGS:
        plan.print(
            ("frame_tx.max_verify_gas={0} is a custom ceiling (the campaign's predefined set is {1}). " +
             "Client images for it must already be built: images/build.sh {0}").format(ceiling, CEILINGS)
        )

    if merged["k_retry"] not in K_RETRIES:
        plan.print(
            "frame_tx.k_retry={0} is outside the campaign's sweep {1}".format(
                merged["k_retry"], K_RETRIES
            )
        )
    if merged["k_retry"] < 1:
        fail("frame_tx.k_retry must be >= 1")

    if merged["attacker_role"] not in ATTACKER_ROLES:
        fail(
            "unknown frame_tx.attacker_role '{0}'. Known: {1}".format(
                merged["attacker_role"], ", ".join(ATTACKER_ROLES)
            )
        )

    if merged["scenario_id"] == "":
        merged["scenario_id"] = "c{0}-{1}-k{2}-a{3}".format(
            ceiling, merged["attacker_role"], merged["k_retry"], merged["attacker_rate"]
        )

    if merged["nethermind_image"] == "":
        merged["nethermind_image"] = "{0}:vg{1}".format(DEFAULT_NETHERMIND_IMAGE_REPO, ceiling)
    if merged["ethrex_image"] == "":
        merged["ethrex_image"] = "{0}:vg{1}".format(DEFAULT_ETHREX_IMAGE_REPO, ceiling)

    return struct(
        scenario_id=merged["scenario_id"],
        max_verify_gas=ceiling,
        k_retry=merged["k_retry"],
        attacker_role=merged["attacker_role"],
        attacker_rate=merged["attacker_rate"],
        baseline_rate=merged["baseline_rate"],
        duration_seconds=merged["duration_seconds"],
        warmup_seconds=merged["warmup_seconds"],
        privacy_inclusion=merged["privacy_inclusion"],
        traffic_image=merged["traffic_image"],
        traffic_enabled=merged["traffic_enabled"],
        nethermind_image=merged["nethermind_image"],
        ethrex_image=merged["ethrex_image"],
        groth16_artifacts_path=merged["groth16_artifacts_path"],
        split_traffic=merged["split_traffic"],
        extra_el_params=merged["extra_el_params"],
    )


def _apply_client_overrides(plan, ethereum_args, scenario):
    """Pins the ceiling-specific image and the runtime ceiling flag onto every participant.

    An `el_image` already written in the args file always wins, so a scenario can pin a
    stock upstream image to prove the ceiling is what moved the result."""
    participants = ethereum_args.get("participants", [])
    if len(participants) == 0:
        fail("no participants in the args file; this devnet needs at least one nethermind node")

    patched = []
    saw_nethermind = False
    saw_ethrex = False
    for participant in participants:
        updated = {}
        for key in participant:
            updated[key] = participant[key]

        el_type = updated.get("el_type", "")
        extra = list(updated.get("el_extra_params", []))

        if el_type == "nethermind":
            saw_nethermind = True
            if updated.get("el_image", "") == "":
                updated["el_image"] = scenario.nethermind_image
            # Runtime half of the ceiling: it bounds the declared validation gas at admission.
            # The simulated prefix stays capped at the compile-time Eip8141Constants.MaxVerifyGas
            # baked into the image, so both must carry the ceiling.
            extra.append("--TxPool.FrameTxMaxVerifyGas={0}".format(scenario.max_verify_gas))
            # Per-scenario client tuning, used to probe which admission limit drives the cliff.
            for param in scenario.extra_el_params:
                extra.append(param)
            updated["el_extra_params"] = extra
        elif el_type == "ethrex":
            saw_ethrex = True
            if updated.get("el_image", "") == "":
                updated["el_image"] = scenario.ethrex_image
            updated["el_extra_params"] = extra

        labels = {}
        for key in updated.get("el_extra_labels", {}):
            labels[key] = updated["el_extra_labels"][key]
        labels["frame_tx_scenario"] = scenario.scenario_id
        labels["frame_tx_ceiling"] = str(scenario.max_verify_gas)
        labels["frame_tx_attacker_role"] = scenario.attacker_role
        labels["frame_tx_k_retry"] = str(scenario.k_retry)
        updated["el_extra_labels"] = labels

        patched.append(updated)

    if not saw_nethermind:
        fail("no nethermind participant; this campaign measures Nethermind's admission path")
    if not saw_ethrex:
        plan.print("single-client topology: Nethermind only, no ethrex participant")

    ethereum_args["participants"] = patched
    return ethereum_args
