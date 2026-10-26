ethereum_package = import_module("github.com/ethpandaops/ethereum-package/main.star")

scenario_lib = import_module("./src/scenario.star")
traffic_lib = import_module("./src/traffic.star")


def run(plan, args={}):
    """Runs one EIP-8141 MAX_VERIFY_GAS scenario on a multi-client devnet.

    Everything outside the `frame_tx` block is ethereum-package's own input, forwarded
    untouched apart from the per-scenario client image and ceiling flag this wrapper pins
    onto each participant."""
    scenario, ethereum_args = scenario_lib.split_args(plan, args)

    plan.print(
        ("frame-tx devnet scenario {0}: ceiling={1} role={2} k_retry={3} " +
         "attacker_rate={4}/s baseline_rate={5}/s duration={6}s").format(
            scenario.scenario_id,
            scenario.max_verify_gas,
            scenario.attacker_role,
            scenario.k_retry,
            scenario.attacker_rate,
            scenario.baseline_rate,
            scenario.duration_seconds,
        )
    )

    network = ethereum_package.run(plan, ethereum_args)

    endpoints = traffic_lib.endpoints_of(network.all_participants)
    for endpoint in endpoints:
        plan.print("execution client {0} ({1}) at {2}".format(endpoint.name, endpoint.client, endpoint.url))

    if scenario.traffic_enabled:
        traffic_lib.launch(plan, scenario, endpoints, network)
    else:
        plan.print("frame_tx.traffic_enabled=false: network only, no traffic generator")

    return struct(
        scenario_id=scenario.scenario_id,
        max_verify_gas=scenario.max_verify_gas,
        k_retry=scenario.k_retry,
        attacker_role=scenario.attacker_role,
        network_id=network.network_id,
        el_endpoints=endpoints,
        grafana_info=getattr(network, "grafana_info", None),
    )
