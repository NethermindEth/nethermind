#!/usr/bin/python3

import os
import sys
import asyncio
import requests as r

from signal import SIGINT, SIGTERM, SIGQUIT


READ_BUFF_SIZE = 1024

NETHERMIND_CMD = "./nethermind"

COLIBRI_CMD = "colibri"

ROTKI_REST_API_PORT = "4242"
ROTKI_WEBSOCKETS_API_PORT = "4243"
ROTKI_API_CORS = "http://localhost:*/*,app://."
ROTKI_API_HOST = "0.0.0.0"
ROTKI_USERNAME = os.getenv("ROTKI_USERNAME")
ROTKI_PASSWORD = os.getenv("ROTKI_PASSWORD")
ROTKI_ARGS = [
    "rotki",
    "--rest-api-port",
    ROTKI_REST_API_PORT,
    "--websockets-api-port",
    ROTKI_WEBSOCKETS_API_PORT,
    "--api-cors",
    ROTKI_API_CORS,
    "--api-host",
    ROTKI_API_HOST,
]
ROTKI_CMD = " ".join(ROTKI_ARGS)

NGINX_CMD = "nginx -g 'daemon off;'"


async def run_nginx() -> asyncio.subprocess.Process:
    proc = await asyncio.create_subprocess_shell(
        NGINX_CMD,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    return proc


async def run_colibri() -> asyncio.subprocess.Process:
    proc = await asyncio.create_subprocess_shell(
        COLIBRI_CMD,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    return proc


async def run_rotki() -> asyncio.subprocess.Process:
    proc = await asyncio.create_subprocess_shell(
        ROTKI_CMD,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    return proc


async def run_nethermind() -> asyncio.subprocess.Process:
    args = sys.argv[1:]
    proc = await asyncio.create_subprocess_exec(
        NETHERMIND_CMD,
        *args,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    return proc


async def terminate_process(
    proc_name: str,
    proc: asyncio.subprocess.Process,
    signal,
):
    print(f"Terminating {proc_name}...")
    proc.send_signal(signal)
    await proc.wait()
    print(f"{proc_name} terminated with exit code {proc.returncode}.")
    return proc.returncode


async def print_stream(
    stream: asyncio.StreamReader,
    header: str | None = None,
    buff_size: int = READ_BUFF_SIZE,
):
    while not stream.at_eof():
        buf = await stream.read(buff_size)
        if not buf:
            break
        if header:
            print(f"[{header.upper()}] {buf.decode().strip()}")
        else:
            print(buf.decode().strip())


async def add_nethermind_to_rotki():
    # Create Rotki User if none exists
    resp = r.get(f"http://localhost:{ROTKI_REST_API_PORT}/api/1/users")
    data = resp.json()
    initial_config = False
    if resp.status_code != 200:
        print(
            f"Failed to get Rotki Users. Status: {resp.status_code}. Error: {data['message']}"
        )
        return
    if ROTKI_USERNAME not in data["result"]:
        # Create Rotki User
        body = {
            "name": ROTKI_USERNAME,
            "password": ROTKI_PASSWORD,
        }
        resp = r.put(
            f"http://localhost:{ROTKI_REST_API_PORT}/api/1/users",
            json=body,
        )
        data = resp.json()
        if resp.status_code != 200:
            print(
                f"Failed to create Rotki User. Status: {resp.status_code}. Error: {data['message']}"
            )
            return
        initial_config = True
        print("Rotki User created!")
    elif data["result"][ROTKI_USERNAME] == "loggedin":
        # User already logged in
        print(f"Rotki already configured!")
        return
    # Authenticate Rotki User
    body = {
        "password": ROTKI_PASSWORD,
        "sync_approval": "unknown",
        "resume_from_backup": True,
    }
    resp = r.post(
        f"http://localhost:{ROTKI_REST_API_PORT}/api/1/users/{ROTKI_USERNAME}",
        json=body,
    )
    data = resp.json()
    if resp.status_code != 200:
        print(
            f"Failed to login Rotki User. Status: {resp.status_code}. Error: {data['message']}"
        )
        return
    if initial_config:
        # Add Nethermind Node to Rotki
        body = {
            "name": "Nethermind Local Node",
            "endpoint": "http://localhost:8545",
            "owned": True,
            "weight": 50.0,  # TODO: modify if we want to only use this node
            "active": True,
        }
        resp = r.put(
            f"http://localhost:{ROTKI_REST_API_PORT}/api/1/blockchains/eth/nodes",
            json=body,
        )
        data = resp.json()
        if resp.status_code != 200:
            print(
                f"Failed to add Nethermind Node to Rotki. Status: {resp.status_code}. Error: {data['message']}"
            )
        else:
            print("Nethermind Local Node added to Rotki!")
            print("Rotki configuration complete!")


async def main():
    colibri_proc = await run_colibri()
    rotki_proc = await run_rotki()
    nginx_proc = await run_nginx()
    neth_proc = await run_nethermind()

    async def graceful_exit(signal, frame):
        print("Exiting...")
        results = await asyncio.gather(
            terminate_process("colibri", colibri_proc, signal),
            terminate_process("rotki", rotki_proc, signal),
            terminate_process("nginx", nginx_proc, signal),
            terminate_process("nethermind", neth_proc, signal),
        )
        if any(results):
            sys.exit(1)
        sys.exit(0)

    loop = asyncio.get_running_loop()
    for sig in (SIGINT, SIGTERM, SIGQUIT):
        loop.add_signal_handler(
            sig,
            lambda: asyncio.create_task(graceful_exit(sig, None)),
        )

    if ROTKI_USERNAME and ROTKI_PASSWORD:
        print("Checking Rotki configuration...")
        try:
            await add_nethermind_to_rotki()
        except Exception as e:
            print(f"Failed to configure Rotki. Error: {e}")
    else:
        print(
            "Missing ROTKI_USERNAME and ROTKI_PASSWORD from enviroment. Skipping Rotki configuration!"
        )

    printers = asyncio.gather(
        print_stream(colibri_proc.stderr, header="colibri"),
        print_stream(rotki_proc.stderr, header="rotki"),
        print_stream(nginx_proc.stderr, header="nginx"),
        print_stream(neth_proc.stdout),
        print_stream(neth_proc.stderr),
    )

    results = await asyncio.gather(
        colibri_proc.wait(),
        rotki_proc.wait(),
        nginx_proc.wait(),
        neth_proc.wait(),
    )
    if any(results):
        await printers
        sys.exit(1)
    await printers
    sys.exit(0)


if __name__ == "__main__":
    asyncio.run(main())
