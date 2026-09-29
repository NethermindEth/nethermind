#!/usr/bin/env python3
"""Offline checks on how senders and nonces are handed out.

    python3 traffic/tests/test_accounts.py
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from frame_traffic.accounts import FixedNonce, Sender, SenderRotation  # noqa: E402

failures: list[str] = []

# ethereum-package's public default prefunded accounts 0 and 1; this test sends nothing.
KEYS = [
    ("0x8943545177806ED17B9F23F0a21ee5948eCaa776",
     "bcdf20249abf0ed6d944c0288fad489e33f66b3960d9e6229c1cd214ed3bbe31"),
    ("0xE25583099BA105D9ec0A67f5Ae86D90e50036425",
     "39725efee3fb28614de3bacaffe4cc4bd8c436257e2c8bb887c4b5c4be45e76d"),
]


class FakeRpc:
    def __init__(self, nonces):
        self.nonces = nonces

    def nonce(self, address, tag):
        return self.nonces[address.lower()]


def check(name: str, condition: bool, detail: str = "") -> None:
    print("  [{0}] {1}{2}".format("ok  " if condition else "FAIL", name, "" if condition else " -- " + detail))
    if not condition:
        failures.append(name)


def main() -> int:
    senders = [Sender(address, key) for address, key in KEYS]
    rpc = FakeRpc({senders[0].address.lower(): 7, senders[1].address.lower(): 3})

    print("fixed nonce (attack)")
    fixed = FixedNonce(senders[0])
    fixed.sync(rpc)
    picks = [fixed.acquire()[1] for _ in range(3)]
    fixed.submitted(senders[0], "", False)
    check("every attack transaction carries the state nonce", picks == [7, 7, 7], str(picks))

    print("\nsender rotation (honest traffic)")
    rotation = SenderRotation(senders)
    rotation.sync(rpc)
    first = rotation.acquire()
    second = rotation.acquire()
    check("each sender starts at its state nonce",
          first[1] == 7 and second[1] == 3, "{0} {1}".format(first[1], second[1]))
    check("no free sender while both have a transaction in flight", rotation.acquire() is None)

    rotation.submitted(first[0], "0xaa", True)
    rotation.submitted(second[0], "0xbb", False)
    again = rotation.acquire()
    check("a refused transaction frees its sender at the same nonce",
          again is not None and again[0] is second[0] and again[1] == 3, str(again))

    rotation.included("0xAA")
    after = rotation.acquire()
    check("an included transaction frees its sender at the next nonce",
          after is not None and after[0] is first[0] and after[1] == 8, str(after))

    rotation.sync(rpc)
    check("a second sync does not reset senders in flight", rotation.acquire() is None)

    print("\n{0} check(s) failed".format(len(failures)) if failures else "\nall checks passed")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
