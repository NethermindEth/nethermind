SERVICE_NAME = "frame-tx-traffic"
RESULTS_DIR = "/results"
GROTH16_DIR = "/groth16"


def endpoints_of(participants):
    """Maps every execution client in the enclave to an in-enclave RPC URL.

    Keyed by service name rather than client type: a topology may run more than one node of
    the same client, and every measurement has to stay attributable to one node."""
    endpoints = []
    for participant in participants:
        el = participant.el_context
        endpoints.append(
            struct(
                name=el.service_name,
                client=el.client_name,
                url="http://{0}:{1}".format(el.dns_name, el.rpc_port_num),
            )
        )
    return endpoints


def launch(plan, scenario, endpoints, network):
    files = {}
    if scenario.groth16_artifacts_path != "":
        # A relative locator resolves against the module doing the call, which is this file in
        # src/, so a package-root path has to be made absolute or it looks for src/<path>.
        # Locators resolve against this module, which lives in src/, and an absolute locator
        # resolves from the repository root rather than this package. So a path given relative
        # to the package directory has to step up one level.
        src = scenario.groth16_artifacts_path
        if not src.startswith("/") and not src.startswith("github.com/"):
            src = "../" + src
        artifacts = plan.upload_files(src=src, name="frame-tx-groth16-artifacts")
        files[GROTH16_DIR] = artifacts

    cmd = [
        "run",
        "--scenario-id", scenario.scenario_id,
        "--chain-id", str(network.network_id),
        "--ceiling", str(scenario.max_verify_gas),
        "--k-retry", str(scenario.k_retry),
        "--seconds-per-slot", str(network.network_params.seconds_per_slot),
        "--attacker-role", scenario.attacker_role,
        "--attacker-rate", str(scenario.attacker_rate),
        "--baseline-rate", str(scenario.baseline_rate),
        "--duration", str(scenario.duration_seconds),
        "--warmup", str(scenario.warmup_seconds),
        "--results-dir", RESULTS_DIR,
    ]
    for endpoint in endpoints:
        cmd.extend(["--rpc", "{0}={1}={2}".format(endpoint.name, endpoint.client, endpoint.url)])
    for account in network.pre_funded_accounts:
        cmd.extend(["--account", "{0}:{1}".format(account.address, account.private_key)])
    if scenario.privacy_inclusion:
        cmd.append("--privacy-inclusion")
    if scenario.split_traffic:
        cmd.append("--split-traffic")
    if scenario.groth16_artifacts_path != "":
        cmd.extend(["--groth16-artifacts", GROTH16_DIR])
    # Kurtosis reports a service whose main process exited as stopped, which makes the run
    # look like a failure and races log collection. The generator parks instead, and the
    # runner tears the enclave down when it has the output.
    cmd.append("--keep-alive")

    service = plan.add_service(
        name=SERVICE_NAME,
        config=ServiceConfig(
            image=scenario.traffic_image,
            cmd=cmd,
            files=files,
            env_vars={
                "PYTHONUNBUFFERED": "1",
                "FRAME_TX_SCENARIO_ID": scenario.scenario_id,
            },
            labels={
                "frame_tx_scenario": scenario.scenario_id,
                "frame_tx_ceiling": str(scenario.max_verify_gas),
                "frame_tx_attacker_role": scenario.attacker_role,
                "frame_tx_k_retry": str(scenario.k_retry),
            },
        ),
    )
    return service
