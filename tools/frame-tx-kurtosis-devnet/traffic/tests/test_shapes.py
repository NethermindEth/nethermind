#!/usr/bin/env python3
"""Offline checks on the transaction shapes: envelope dialect, gas arithmetic, role coverage.

Runs without a devnet, so a shape regression is caught before an enclave is ever started.

    python3 traffic/tests/test_shapes.py
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from frame_traffic import shapes  # noqa: E402
from frame_traffic.accounts import Sender  # noqa: E402

CEILINGS = [100000, 236285, 300000, 322800, 500000]

# Signature-stuffed shape as the in-process harness measures it, ceiling -> (entries, declared
# gas). Taken from the campaign's own RESULT rows (case=signature_reject). Pinning them here
# keeps the devnet generator and the in-process harness measuring the same transaction.
HARNESS_SIGNATURE_STUFFED = {
    100000: (35, 98400),
    236285: (84, 235600),
    300000: (107, 300000),
    322800: (115, 322400),
    500000: (178, 498800),
}
CHAIN_ID = 3151908
# Deterministic throwaway key: this signs nothing that reaches a network.
TEST_KEY = "bcdf20249abf0ed6d944c0288fad489e33f66b3960d9e6229c1cd214ed3bbe31"
TEST_ADDRESS = "0x8943545177806ED17B9F23F0a21ee5948eCaa776"

failures: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    status = "ok  " if condition else "FAIL"
    print("  [{0}] {1}{2}".format(status, name, "" if condition else " -- " + detail))
    if not condition:
        failures.append(name)


def main() -> int:
    module = shapes.load_encoder()
    sender = Sender(TEST_ADDRESS, TEST_KEY)
    loop_address = bytes.fromhex("00" * 19 + "11")
    verifier_address = bytes.fromhex("00" * 19 + "22")

    print("encoder: {0}".format(module.__file__))

    print("\nbaseline")
    built = shapes.build_baseline(module, sender, CHAIN_ID, 0, 10**9, None)
    check("type byte is 0x06", built.raw[:1] == b"\x06", repr(built.raw[:1]))
    check("carries one signature", built.signatures == 1)
    check("declares verify gas", built.declared_verify_gas > 0, str(built.declared_verify_gas))

    print("\nbaseline with a K_retry expiry deadline")
    dated = shapes.build_baseline(module, sender, CHAIN_ID, 1, 10**9, 1_800_000_000)
    check("expiry frame added", dated.frames == built.frames + 1,
          "{0} vs {1}".format(dated.frames, built.frames))
    check("deadline recorded", dated.expiry_deadline == 1_800_000_000)

    for ceiling in CEILINGS:
        print("\nceiling {0}".format(ceiling))

        kw = shapes.build_keccak_wide(module, sender, CHAIN_ID, 0, 10**9, ceiling,
                                      loop_address, b"\x01" * 8, None)
        check("keccak-wide fits the ceiling", kw.declared_verify_gas <= ceiling,
              "{0} > {1}".format(kw.declared_verify_gas, ceiling))
        check("keccak-wide uses most of the ceiling",
              kw.declared_verify_gas >= ceiling * 0.98,
              "{0} of {1}".format(kw.declared_verify_gas, ceiling))

        ss = shapes.build_signature_stuffed(module, sender, CHAIN_ID, 0, 10**9, ceiling,
                                            b"\x02" * 8, None)
        check("signature-stuffed fits the ceiling", ss.declared_verify_gas <= ceiling,
              "{0} > {1}".format(ss.declared_verify_gas, ceiling))
        check("signature-stuffed uses most of the ceiling",
              ss.declared_verify_gas >= ceiling * 0.97,
              "{0} of {1}".format(ss.declared_verify_gas, ceiling))
        expected_entries, expected_gas = HARNESS_SIGNATURE_STUFFED[ceiling]
        check("signature count matches the in-process harness",
              ss.signatures == expected_entries,
              "{0} vs {1}".format(ss.signatures, expected_entries))
        check("declared gas matches the in-process harness",
              ss.declared_verify_gas == expected_gas,
              "{0} vs {1}".format(ss.declared_verify_gas, expected_gas))

        g16 = shapes.build_groth16(module, sender, CHAIN_ID, 0, 10**9, ceiling, verifier_address,
                                   b"\xab\xcd\xef\x01" + b"\x00" * 128, b"\x03" * 8, None,
                                   "groth16-soispoke")
        check("groth16 fits the ceiling", g16.declared_verify_gas <= ceiling,
              "{0} > {1}".format(g16.declared_verify_gas, ceiling))

        for label, tx in (("keccak-wide", kw), ("signature-stuffed", ss), ("groth16", g16)):
            check("{0} is type 0x06".format(label), tx.raw[:1] == b"\x06")

    print("\nsignature-stuffed detail at 322800")
    stuffed = shapes.build_signature_stuffed(module, sender, CHAIN_ID, 0, 10**9, 322800,
                                             b"\x04" * 8, None)
    per_signature = shapes.SECP256K1_VERIFICATION_GAS
    print("  {0} signature entries x {1} gas = {2}".format(
        stuffed.signatures, per_signature, stuffed.signatures * per_signature))
    check("declared verify gas is signature-dominated",
          stuffed.declared_verify_gas >= stuffed.signatures * per_signature)

    print("\ndeployment fixture")
    init = shapes.deployment_init_code(shapes.KECCAK_WIDE_RUNTIME)
    check("init code returns the runtime", init.endswith(shapes.KECCAK_WIDE_RUNTIME))
    check("init code is the 12-byte constructor plus runtime",
          len(init) == 12 + len(shapes.KECCAK_WIDE_RUNTIME), str(len(init)))
    check("runtime is the 4 KB keccak loop",
          shapes.KECCAK_WIDE_RUNTIME.hex() == "5b61100060002050600056",
          shapes.KECCAK_WIDE_RUNTIME.hex())

    print("\n{0} check(s) failed".format(len(failures)) if failures else "\nall checks passed")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
