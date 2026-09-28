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

CEILINGS = [100000, 235800, 250000, 300000, 400000, 500000]

# Signature-stuffed shape as the in-process harness builds it, ceiling -> (entries, declared
# gas): FrameTxMeasurementSupport.StuffedSignatureCount on the v2 harness, one 400-gas VERIFY
# frame plus floor((ceiling - 400) / 2800) secp256k1 entries. Pinning them here keeps the devnet
# generator and the in-process harness measuring the same transaction.
HARNESS_SIGNATURE_STUFFED = {
    100000: (35, 98400),
    235800: (84, 235600),
    250000: (89, 249600),
    300000: (107, 300000),
    400000: (142, 398000),
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

    print("\nsignature-stuffed detail at 235800")
    stuffed = shapes.build_signature_stuffed(module, sender, CHAIN_ID, 0, 10**9, 235800,
                                             b"\x04" * 8, None)
    per_signature = shapes.SECP256K1_VERIFICATION_GAS
    print("  {0} signature entries x {1} gas = {2}".format(
        stuffed.signatures, per_signature, stuffed.signatures * per_signature))
    check("declared verify gas is signature-dominated",
          stuffed.declared_verify_gas >= stuffed.signatures * per_signature)

    print("\ndeclared verify gas follows Nethermind's recognised prefix")
    frame, sig = module.Frame, module.FrameSig
    sender_int = int.from_bytes(sender.address_bytes, "big")

    def declared(frames, signatures=1):
        tx = shapes.frame_tx(
            module, chain_id=CHAIN_ID, nonce_keys=[], nonce_seq=0, sender=sender_int, frames=frames,
            signatures=[sig(sig.SECP256K1, sender.address_bytes, b"", b"\x00" * 65)] * signatures,
            max_priority_fee=0, max_fee=0)
        return shapes.declared_verify_gas(tx)

    self_verify = frame(shapes.MODE_VERIFY, shapes.APPROVE_EXECUTION_AND_PAYMENT, None, 1_000, 0, b"")
    payload = frame(shapes.MODE_DEFAULT, 0, sender_int, 50_000, 0, b"")
    expiry = frame(shapes.MODE_VERIFY, 0, shapes.EXPIRY_VERIFIER_ADDRESS, 400, 0, b"\x00" * 8)
    check("self-verify prefix stops before the payload frame",
          declared([self_verify, payload]) == 1_000 + 2_800, str(declared([self_verify, payload])))
    check("a leading expiry frame is part of the prefix",
          declared([expiry, self_verify, payload]) == 400 + 1_000 + 2_800)
    pair = [frame(shapes.MODE_VERIFY, shapes.APPROVE_EXECUTION, None, 400, 0, b""),
            frame(shapes.MODE_VERIFY, shapes.APPROVE_PAYMENT, 0x22, 90_000, 0, b"")]
    check("execution-then-payment pair is the prefix",
          declared(pair + [payload]) == 400 + 90_000 + 2_800)
    unrecognised = [frame(shapes.MODE_VERIFY, shapes.APPROVE_PAYMENT, 0x22, 7_000, 0, b""), payload]
    check("an unrecognised layout counts every frame",
          declared(unrecognised) == 7_000 + 50_000 + 2_800)
    check("each secp256k1 entry adds 2800", declared([self_verify], 3) == 1_000 + 3 * 2_800)

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
