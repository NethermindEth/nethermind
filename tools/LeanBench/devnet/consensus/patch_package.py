#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Apply the devnet-only proof relay hooks to an isolated ethereum-package checkout."""
import argparse
from pathlib import Path


def replace(path, old, new):
    text = path.read_text()
    if text.count(old) != 1:
        raise ValueError(f"Unexpected upstream hook at {path}")
    path.write_text(text.replace(old, new))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    args = parser.parse_args()
    package = args.package
    replace(package / 'src/participant_network.star', '    # Launch all consensus layer clients\n', '''    # Devnet-only relay: shared cache preserves genuine EL proofs across both CLs.
    proxy_script = plan.upload_files(src="../lean-engine-proxy.py", name="lean-engine-proxy-code")
    plan.add_service(name="lean-proof-engine", config=ServiceConfig(
        image="python:3.12-slim",
        ports={"engine-1": PortSpec(number=8551), "engine-2": PortSpec(number=8552)},
        cmd=["python3", "/code/lean-engine-proxy.py", "--bind", "0.0.0.0",
            "--jwt", "/jwt/jwtsecret", "--cache", "/cache", "--listen1", "8551", "--listen2", "8552",
            "--upstream-url1", "http://{}:{}".format(all_el_contexts[0].dns_name, all_el_contexts[0].engine_rpc_port_num),
            "--upstream-url2", "http://{}:{}".format(all_el_contexts[1].dns_name, all_el_contexts[1].engine_rpc_port_num)],
        files={"/code": proxy_script, "/jwt": jwt_file}, max_cpu=1000, max_memory=384))

    # Launch all consensus layer clients
''')
    replace(package / 'src/cl/lighthouse/lighthouse_launcher.star', '''    EXECUTION_ENGINE_ENDPOINT = cl_shared.get_execution_engine_endpoint(
        participant, el_context, snooper_el_engine_context
    )''', '''    EXECUTION_ENGINE_ENDPOINT = "http://lean-proof-engine:{}".format(8551 + participant_index)''')
    replace(package / 'src/shared_utils/shared_utils.star', '''    public_port_range = ()
    if component == "cl":''', '''    public_port_range = ()
    if component == "el":
        return [19403 + 100 * participant_index, 19551 + 100 * participant_index,
            19701 + 100 * participant_index, 19445 + 100 * participant_index,
            19703 + 100 * participant_index, 19704 + 100 * participant_index, 19705 + 100 * participant_index]
    if component == "cl":
        return [19711 + 100 * participant_index, 19552 + 100 * participant_index,
            19712 + 100 * participant_index, 19713 + 100 * participant_index,
            19714 + 100 * participant_index, 19715 + 100 * participant_index, 19716 + 100 * participant_index]
    if component == "cl":''')


if __name__ == '__main__':
    main()
