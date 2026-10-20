GENESIS_VALIDATORS = (
    '.data | sort_by(.index | tonumber) | ' +
    'if length > 0 and (map(.index | tonumber) == [range(0; length)]) ' +
    'and all(.[]; .validator.pubkey | test("^0x[0-9a-fA-F]{96}$")) ' +
    'and ((map(.validator.pubkey) | unique | length) == length) ' +
    'then map(.validator.pubkey) | join(",") ' +
    'else error("invalid genesis validator registry") end'
)


def relay_plan(plan):
    def add_service(name, config, **kwargs):
        if name == "mev-relay-api":
            uri = config.cmd[config.cmd.index("--beacon-uris") + 1].split(",")[0]
            if not uri.startswith("http://"):
                fail("unexpected devnet relay beacon URI")
            beacon = uri[len("http://"):].split(":")[0]
            validators = plan.request(
                service_name=beacon,
                recipe=GetHttpRequestRecipe(
                    port_id="http",
                    endpoint="/eth/v1/beacon/states/genesis/validators",
                    extract={"pubkeys": GENESIS_VALIDATORS},
                ),
                acceptable_codes=[200],
                description="load relay validators from the canonical beacon genesis registry",
            )
            config.cmd.extend(["--known-validators", validators["extract.pubkeys"]])
        return plan.add_service(name=name, config=config, **kwargs)

    methods = {name: getattr(plan, name) for name in dir(plan)}
    methods["add_service"] = add_service
    return struct(**methods)


def add_follower(plan, image, service_name, l1_rpc, l1_chain_id, deployment, l1_beacon=""):
    environment = {"NETHERMIND_L1_RPC": l1_rpc, "NETHERMIND_L1_CHAIN_ID": str(l1_chain_id)}
    if l1_beacon:
        environment["NETHERMIND_L1_BEACON"] = l1_beacon
    plan.add_service(
        name=service_name,
        config=ServiceConfig(
            image=image,
            ports={"rpc": PortSpec(number=8545, transport_protocol="TCP", application_protocol="http", wait="5m")},
            files={"/out": deployment},
            env_vars=environment,
        ),
    )

def add_initial(plan, args, l1_rpc, l1_chain_id, deployment, l1_beacon=""):
    image = args["nethermind_image"]
    add_follower(plan, image, "nethermind-l2", l1_rpc, l1_chain_id, deployment, l1_beacon)
    plan.run_sh(
        description="record Nethermind canonical L1 connection",
        image="alpine:3.20",
        env_vars={"L1_RPC": l1_rpc, "L1_CHAIN_ID": str(l1_chain_id), "L1_BEACON": l1_beacon},
        run="mkdir -p /out; printf '%s' \"$L1_RPC\" > /out/l1-rpc; printf '%s' \"$L1_CHAIN_ID\" > /out/l1-chain-id; printf '%s' \"$L1_BEACON\" > /out/l1-beacon",
        store=[StoreSpec(src="/out", name="nethermind-connection")],
    )
