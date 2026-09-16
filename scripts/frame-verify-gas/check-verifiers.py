"""Plausibility heuristic for Groth16 sweep artifacts. Usage: check-verifiers.py <root> <label>..."""

import re
import sys
from pathlib import Path

HEX = re.compile(r"0x(?:[0-9a-fA-F]{2})+")
GAS = re.compile(r"[1-9][0-9]{0,8}")
PRECOMPILES = {0x06: "ecAdd", 0x07: "ecMul", 0x08: "ecPairing"}
PUSH1, PUSH32, STATICCALL = 0x60, 0x7F, 0xFA
MIN_CODE_BYTES, MAX_CODE_BYTES = 1024, 24576


def walk(code):
    pushed, staticcall, pc = set(), False, 0
    while pc < len(code):
        op = code[pc]
        if op == PUSH1 and pc + 1 < len(code):
            pushed.add(code[pc + 1])
        staticcall |= op == STATICCALL
        pc += 1 + (op - PUSH1 + 1 if PUSH1 <= op <= PUSH32 else 0)
    return pushed, staticcall


def main(root, labels):
    failures = []

    def reject(path, check, detail):
        failures.append(f"check-verifiers.py heuristic rejected {path} ({check} check): {detail}")

    def read(path):
        return (root / path).read_bytes().decode("ascii", errors="replace").strip()

    def read_hex(path):
        text = read(path)
        if not HEX.fullmatch(text):
            reject(path, "hex", "not 0x-prefixed, byte-aligned hex")
            return None
        return bytes.fromhex(text[2:])

    for label in labels:
        sweep = f"sweep-{label}"
        verifier = f"{sweep}/verifier.hex"
        code = read_hex(verifier)
        if code is not None:
            if not MIN_CODE_BYTES <= len(code) <= MAX_CODE_BYTES:
                reject(verifier, "size", f"{len(code)} bytes, outside {MIN_CODE_BYTES}..{MAX_CODE_BYTES} (EIP-170)")
            pushed, staticcall = walk(code)
            missing = [f"0x{address:02x} {name}" for address, name in PRECOMPILES.items() if address not in pushed]
            if missing:
                reject(verifier, "precompile-push", f"no PUSH1 of {', '.join(missing)}")
            if not staticcall:
                reject(verifier, "staticcall", "no STATICCALL opcode")
        calldata_path = f"{sweep}/calldata-invalid.hex"
        calldata = read_hex(calldata_path)
        if calldata is not None and len(calldata) < 4:
            reject(calldata_path, "length", "shorter than a function selector")
        if not GAS.fullmatch(read(f"{sweep}/gas.txt")):
            reject(f"{sweep}/gas.txt", "integer", "not a positive integer")

    for failure in failures:
        print(f"::error::{failure}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1]), sys.argv[2:]))
