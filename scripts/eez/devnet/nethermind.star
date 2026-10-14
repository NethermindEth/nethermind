def add_follower(plan, image, service_name, l1_rpc, l1_chain_id, deployment):
    plan.add_service(
        name=service_name,
        config=ServiceConfig(
            image=image,
            ports={"rpc": PortSpec(number=8545, transport_protocol="TCP", application_protocol="http", wait="5m")},
            files={"/out": deployment},
            env_vars={"NETHERMIND_L1_RPC": l1_rpc, "NETHERMIND_L1_CHAIN_ID": str(l1_chain_id)},
        ),
    )

def add_initial(plan, args, l1_rpc, l1_chain_id, deployment):
    image = args["nethermind_image"]
    add_follower(plan, image, "nethermind-l2", l1_rpc, l1_chain_id, deployment)
    plan.run_sh(
        description="record Nethermind canonical L1 connection",
        image="alpine:3.20",
        env_vars={"L1_RPC": l1_rpc, "L1_CHAIN_ID": str(l1_chain_id)},
        run="mkdir -p /out; printf '%s' \"$L1_RPC\" > /out/l1-rpc; printf '%s' \"$L1_CHAIN_ID\" > /out/l1-chain-id",
        store=[StoreSpec(src="/out", name="nethermind-connection")],
    )
