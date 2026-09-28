#!/usr/bin/env python3
"""Offline checks on ChainWatcher, the component that decides whether a transaction was included.

A wrong answer here is invisible in the output and expensive: it took a whole batch of devnet
runs to notice that the per-transaction receipt poll it replaced was reporting slow inclusions
as no inclusion. These run without a devnet against a scripted chain.

    python3 traffic/tests/test_watcher.py
"""
from __future__ import annotations

import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from frame_traffic import runner  # noqa: E402

FAILURES = []


def check(label, condition):
    print("  [{0}] {1}".format("ok  " if condition else "FAIL", label))
    if not condition:
        FAILURES.append(label)


class FakeNode:
    """A chain that reveals one scripted block per call to block_number()."""

    name = "fake-el"
    client = "nethermind"

    def __init__(self, blocks):
        self._blocks = blocks           # list of (timestamp, [tx hashes])
        self._revealed = 0

    def block_number(self):
        if self._revealed < len(self._blocks):
            self._revealed += 1
        return self._revealed - 1

    def get_block(self, number, full=False):
        timestamp, hashes = self._blocks[number]
        return {"number": hex(number), "timestamp": hex(timestamp), "transactions": list(hashes)}


class Recorder:
    def __init__(self):
        self.events, self.emitted = [], []

    def event(self, **kwargs):
        self.events.append(kwargs)

    def emit(self, case, **kwargs):
        self.emitted.append((case, kwargs))


class Owner:
    pass


def drain(watcher, node, limit=50):
    """Waits until the watcher has walked every block the fake node will reveal."""
    for _ in range(limit):
        if watcher._next is not None and watcher._next >= len(node._blocks):
            return
        time.sleep(0.05)


def main():
    print("chain watcher")
    submitted_at = 1000.0
    node = FakeNode([
        (1000, []),
        (1006, ["0xAA", "0xbb"]),
        (1012, []),
        (1018, ["0xcc"]),
    ])
    recorder = Recorder()
    watcher = runner.ChainWatcher(node, recorder, poll=0.01)
    alice, bob = Owner(), Owner()
    watcher.register(alice, "0xaa", submitted_at)
    watcher.register(alice, "0xCC", submitted_at)
    watcher.register(bob, "0xbb", submitted_at)
    watcher.register(bob, "0xdd", submitted_at)     # never lands
    watcher.start()
    drain(watcher, node)
    watcher.stop()

    alice_latencies, alice_outstanding = watcher.stats(alice)
    bob_latencies, bob_outstanding = watcher.stats(bob)

    check("hash matching ignores case on both sides", len(alice_latencies) == 2)
    check("inclusion is attributed to the submitting owner", len(bob_latencies) == 1)
    check("a transaction that never lands is outstanding, not included", bob_outstanding == 1)
    check("an owner whose transactions all landed has none outstanding", alice_outstanding == 0)
    check("latency comes from the including block's timestamp",
          alice_latencies == [6000.0, 18000.0])
    check("one inclusion event per landed transaction",
          sum(1 for e in recorder.events if e.get("kind") == "inclusion") == 3)

    watcher.report_blocks()
    case, fill = next(pair for pair in recorder.emitted if pair[0] == "block_fill")
    check("every walked block is counted, empty ones included", fill["blocks"] == 4)
    check("empty blocks are reported separately", fill["empty_blocks"] == 2)
    check("block fill is the chain's transaction count, not the watched set",
          fill["txs_total"] == 3 and fill["txs_per_block_max"] == 2)

    print("\nsettle")
    late = FakeNode([(1000, []), (1006, ["0xee"])])
    recorder2 = Recorder()
    watcher2 = runner.ChainWatcher(late, recorder2, poll=0.01)
    owner = Owner()
    watcher2.register(owner, "0xee", 1000.0)
    watcher2.start()
    _, before = watcher2.stats(owner)
    check("a transaction is outstanding before the block carrying it is walked", before == 1)
    watcher2.settle(0.3)
    watcher2.stop()
    _, after = watcher2.stats(owner)
    check("settling resolves a transaction included after the load stopped", after == 0)

    print("\n" + ("all checks passed" if not FAILURES else "FAILED: " + ", ".join(FAILURES)))
    return 1 if FAILURES else 0


if __name__ == "__main__":
    raise SystemExit(main())
