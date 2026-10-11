#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Builds benchmark-only variants of Nethermind.Network.dll for the lean/1 network benchmark.

Each variant patches the source in place, builds Nethermind.Network, restores the source and places the patched DLL in
a copy of the Nethermind.Network.Test output (hard links for every other file). Build the test project first.
Combine variants with '+', e.g. seqfix+inflight16.

Usage: variants.py --out <dir> seqfix seqfix+inflight16 ...
       netbench.py run --test-dll <dir>/seqfix/Nethermind.Network.Test.dll ...
"""
import argparse
import os
import shutil
import subprocess
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "Nethermind"))
LEAN = os.path.join(ROOT, "Nethermind.Network", "P2P", "Subprotocols", "Lean")
TEST_OUTPUT = os.path.join(ROOT, "artifacts", "bin", "Nethermind.Network.Test", "release")
NETWORK_OUTPUT = os.path.join(ROOT, "artifacts", "bin", "Nethermind.Network", "release", "Nethermind.Network.dll")

PATCHES = {
    # Requests are numbered under the transport lock but sent after it, so two threads can emit N+1 before N and the
    # receiver disconnects for a non-consecutive ID. This sends each request under the lock that numbered it.
    "seqfix": [("LeanObjectTransport.cs",
                "public void Send(LeanPeer peer, LeanMessage message) => (_messages ??= []).Add((peer, message));",
                "public void Send(LeanPeer peer, LeanMessage message)\n        {\n"
                "            if (message is GetChunksMessage or GetObjectsMessage or GetTransactionsMessage)\n            {\n"
                "                if (!peer.Closed) peer.Link.Send(message);\n                return;\n            }\n"
                "            (_messages ??= []).Add((peer, message));\n        }")],
    "inflight16": [("Ethp2p/LeanBroadcastEngine.cs", "MaxShardsInFlightPerPeer = 4;", "MaxShardsInFlightPerPeer = 16;")],
    "window4m": [("Ethp2p/LeanEthp2pProtocol.cs", "StreamReceiveWindow = 256 * 1024;", "StreamReceiveWindow = 4 * 1024 * 1024;")],
}


def build(name, out, dotnet):
    originals = {}
    try:
        for part in name.split("+"):
            for relative, old, new in PATCHES[part]:
                path = os.path.join(LEAN, relative)
                text = open(path).read()
                originals.setdefault(path, text)
                if old not in text:
                    raise SystemExit(f"{name}: {relative} no longer contains {old!r}")
                with open(path, "w") as f:
                    f.write(text.replace(old, new))
        result = subprocess.run([dotnet, "build", os.path.join(ROOT, "Nethermind.Network", "Nethermind.Network.csproj"), "-c", "release"],
                                capture_output=True, text=True)
        if result.returncode != 0:
            raise SystemExit(result.stdout[-3000:])
        destination = os.path.join(out, name)
        shutil.rmtree(destination, ignore_errors=True)
        subprocess.run(["cp", "-al", TEST_OUTPUT, destination], check=True)
        os.remove(os.path.join(destination, "Nethermind.Network.dll"))
        shutil.copy(NETWORK_OUTPUT, os.path.join(destination, "Nethermind.Network.dll"))
        print(f"{name}: {destination}", flush=True)
    finally:
        for path, text in originals.items():
            with open(path, "w") as f:
                f.write(text)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("variants", nargs="+")
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)
    for name in args.variants:
        build(name, os.path.abspath(args.out), args.dotnet)
    # Leave Nethermind.Network's own output as the unpatched source builds it.
    subprocess.run([args.dotnet, "build", os.path.join(ROOT, "Nethermind.Network", "Nethermind.Network.csproj"), "-c", "release"],
                   capture_output=True, check=False)


if __name__ == "__main__":
    sys.exit(main())
