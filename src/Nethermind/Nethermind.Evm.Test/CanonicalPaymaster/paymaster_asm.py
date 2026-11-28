# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Canonical paymaster runtime assembler - EIP-8141 (Stage 2, finalized).

Opcode stack orders verified against Nethermind.Evm/Instructions/EvmInstructions.FrameTx.cs:

  APPROVE (0xaa): pops (offset, length, scope) top-to-bottom -> push scope, length, offset.
                  ApprovePayment scope bit = 0x1 (TxFrame.ApprovePayment), NOT 0x2.
  SIGPARAM (0xb4): pops (signatureIndex, param) top-to-bottom -> push param, then index.
                   param 0x00 resolved_signer, 0x01 scheme, 0x02 msg. Scheme ARBITRARY = 0x0.

Spec (ethereum/EIPs #12012, 2026-07-24 update mirrored):
  - validation scheme gate is `scheme != ARBITRARY` (no named curve).
  - admin authorized() = CALLER == slot0  OR  a signer-signed entry at index 1
    (scheme != ARBITRARY, resolved_signer == slot0, empty msg).
"""

DELAY = 86400  # seconds (1 day) - per-version constant, spec Treasury rules.

OPS = {
    'STOP': 0x00, 'ADD': 0x01, 'LT': 0x10, 'EQ': 0x14, 'ISZERO': 0x15, 'SHR': 0x1c,
    'CALLVALUE': 0x34, 'CALLDATALOAD': 0x35, 'CALLDATASIZE': 0x36, 'CALLER': 0x33,
    'TIMESTAMP': 0x42, 'POP': 0x50, 'SLOAD': 0x54, 'SSTORE': 0x55,
    'JUMPI': 0x57, 'JUMPDEST': 0x5b, 'GAS': 0x5a, 'DUP1': 0x80, 'DUP5': 0x84,
    'CALL': 0xf1, 'REVERT': 0xfd, 'APPROVE': 0xaa, 'SIGPARAM': 0xb4,
}


def push_bytes(n: int) -> bytes:
    if n == 0:
        return bytes([0x5f])  # PUSH0
    b = n.to_bytes((n.bit_length() + 7) // 8, 'big')
    return bytes([0x5f + len(b)]) + b


def authorized(uid: int):
    """authorized() = CALLER == slot0 OR signer-signed entry@1; falls through on success."""
    ok = f'AUTH_OK_{uid}'
    return [
        ('op', 'CALLER'), ('push', 0), ('op', 'SLOAD'), ('op', 'EQ'), ('jumpi', ok),
        # signature route: scheme(1) != ARBITRARY
        ('push', 1), ('push', 1), ('op', 'SIGPARAM'), ('op', 'ISZERO'), ('jumpi', 'FAIL'),
        # resolved_signer(1) == slot0
        ('push', 0), ('push', 1), ('op', 'SIGPARAM'), ('push', 0), ('op', 'SLOAD'),
        ('op', 'EQ'), ('op', 'ISZERO'), ('jumpi', 'FAIL'),
        # msg(1) == 0 (signature over the canonical sig hash, not a bespoke digest)
        ('push', 2), ('push', 1), ('op', 'SIGPARAM'), ('jumpi', 'FAIL'),
        ('dest', ok),
    ]


def set_unlock():
    """slot2 = TIMESTAMP + DELAY."""
    return [('op', 'TIMESTAMP'), ('push', DELAY), ('op', 'ADD'), ('push', 2), ('op', 'SSTORE')]


PROGRAM = []

# --- dispatch ---
PROGRAM += [
    ('op', 'CALLVALUE'), ('jumpi', 'HASVALUE'),
    ('op', 'CALLDATASIZE'), ('jumpi', 'ADMIN'),
    # --- validation path (empty calldata, zero value): the pay frame ---
    ('push', 1), ('push', 1), ('op', 'SIGPARAM'), ('op', 'ISZERO'), ('jumpi', 'FAIL'),      # scheme==ARBITRARY -> revert
    ('push', 0), ('push', 1), ('op', 'SIGPARAM'), ('push', 0), ('op', 'SLOAD'),
    ('op', 'EQ'), ('op', 'ISZERO'), ('jumpi', 'FAIL'),                                       # resolved_signer != slot0 -> revert
    ('push', 2), ('push', 1), ('op', 'SIGPARAM'), ('jumpi', 'FAIL'),                         # msg != 0 -> revert
    ('push', 1), ('push', 0), ('push', 0), ('op', 'APPROVE'),                                # APPROVE(scope=PAYMENT, len=0, off=0)
]

# --- HASVALUE: deposit iff no calldata, else non-payable admin -> revert ---
PROGRAM += [
    ('dest', 'HASVALUE'),
    ('op', 'CALLDATASIZE'), ('jumpi', 'FAIL'),
    ('op', 'STOP'),
]

# --- ADMIN dispatch on the first calldata byte ---
PROGRAM += [
    ('dest', 'ADMIN'),
    ('push', 0), ('op', 'CALLDATALOAD'), ('push', 248), ('op', 'SHR'),
    ('op', 'DUP1'), ('push', 1), ('op', 'EQ'), ('jumpi', 'W_INIT'),
    ('op', 'DUP1'), ('push', 2), ('op', 'EQ'), ('jumpi', 'R_INIT'),
    ('op', 'DUP1'), ('push', 3), ('op', 'EQ'), ('jumpi', 'CANCEL'),
    ('push', 4), ('op', 'EQ'), ('jumpi', 'FINALIZE'),
    ('dest', 'FAIL'), ('push', 0), ('push', 0), ('op', 'REVERT'),
]

# --- W_INIT: initiate withdrawal(amount @ calldata[1:33]) ---
PROGRAM += [('dest', 'W_INIT'), ('op', 'POP')]
PROGRAM += authorized(0)
PROGRAM += [
    ('push', 2), ('op', 'SLOAD'), ('jumpi', 'FAIL'),               # action already pending -> revert
    ('push', 1), ('op', 'CALLDATALOAD'),
    ('op', 'DUP1'), ('op', 'ISZERO'), ('jumpi', 'FAIL'),           # amount == 0 -> revert
    ('push', 1), ('op', 'SSTORE'),                                 # slot1 = amount
]
PROGRAM += set_unlock()
PROGRAM += [('op', 'STOP')]

# --- R_INIT: initiate rotation(new_signer @ calldata[1:33]) ---
PROGRAM += [('dest', 'R_INIT'), ('op', 'POP')]
PROGRAM += authorized(1)
PROGRAM += [
    ('push', 2), ('op', 'SLOAD'), ('jumpi', 'FAIL'),
    ('push', 1), ('op', 'CALLDATALOAD'),
    ('op', 'DUP1'), ('op', 'ISZERO'), ('jumpi', 'FAIL'),           # new_signer == 0 -> revert
    ('push', 3), ('op', 'SSTORE'),                                 # slot3 = new_signer
]
PROGRAM += set_unlock()
PROGRAM += [('op', 'STOP')]

# --- CANCEL: clear pending state ---
PROGRAM += [('dest', 'CANCEL'), ('op', 'POP')]
PROGRAM += authorized(2)
PROGRAM += [
    ('push', 0), ('push', 1), ('op', 'SSTORE'),
    ('push', 0), ('push', 2), ('op', 'SSTORE'),
    ('push', 0), ('push', 3), ('op', 'SSTORE'),
    ('op', 'STOP'),
]

# --- FINALIZE: anyone, once matured ---
PROGRAM += [
    ('dest', 'FINALIZE'),
    ('push', 2), ('op', 'SLOAD'), ('op', 'DUP1'), ('op', 'ISZERO'), ('jumpi', 'FAIL'),   # no pending -> revert
    ('op', 'TIMESTAMP'), ('op', 'LT'), ('jumpi', 'FAIL'),                                 # block.timestamp < unlock -> revert
    ('push', 1), ('op', 'SLOAD'), ('op', 'DUP1'), ('op', 'ISZERO'), ('jumpi', 'F_ROT'),  # amount == 0 -> rotation
    # withdrawal: checks-effects: clear slots 1 and 2 before the value-bearing call.
    ('push', 0), ('push', 1), ('op', 'SSTORE'),
    ('push', 0), ('push', 2), ('op', 'SSTORE'),
    # CALL(gas, to=slot0, value=amount, 0, 0, 0, 0); stack holds [amount].
    ('push', 0), ('push', 0), ('push', 0), ('push', 0),   # retLength, retOffset, argsLength, argsOffset
    ('op', 'DUP5'),                                        # value = amount
    ('push', 0), ('op', 'SLOAD'),                          # to = signer
    ('op', 'GAS'),                                         # gas
    ('op', 'CALL'),
    ('op', 'ISZERO'), ('jumpi', 'FAIL'),                  # insufficient balance / failed send -> revert
    ('op', 'STOP'),
    # rotation: slot0 = slot3; clear slots 3 and 2.
    ('dest', 'F_ROT'),
    ('op', 'POP'),
    ('push', 3), ('op', 'SLOAD'), ('push', 0), ('op', 'SSTORE'),
    ('push', 0), ('push', 3), ('op', 'SSTORE'),
    ('push', 0), ('push', 2), ('op', 'SSTORE'),
    ('op', 'STOP'),
]


def encode(item):
    kind = item[0]
    if kind == 'op':
        return bytes([OPS[item[1]]])
    if kind == 'push':
        return push_bytes(item[1])
    if kind == 'jumpi':
        return bytes([0x61, 0, 0, OPS['JUMPI']])  # PUSH2 <hi><lo> + JUMPI
    if kind == 'dest':
        return bytes([OPS['JUMPDEST']])
    raise ValueError(item)


def assemble():
    # Pass 1: assign offsets, collect label addresses (every label ref is a fixed-width PUSH2 + JUMPI).
    labels = {}
    offset = 0
    for item in PROGRAM:
        if item[0] == 'dest':
            labels[item[1]] = offset
        offset += len(encode(item))

    # Pass 2: emit, patching PUSH2 operands with resolved addresses.
    out = bytearray()
    for item in PROGRAM:
        if item[0] == 'jumpi':
            addr = labels[item[1]]
            out += bytes([0x61, (addr >> 8) & 0xff, addr & 0xff, OPS['JUMPI']])
        else:
            out += encode(item)
    return bytes(out)


# --- Keccak-256 (Ethereum), self-contained for reproducible code hash ---
def keccak256(data: bytes) -> bytes:
    RC = [
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000,
        0x000000000000808B, 0x0000000080000001, 0x8000000080008081, 0x8000000000008009,
        0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
        0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003,
        0x8000000000008002, 0x8000000000000080, 0x000000000000800A, 0x800000008000000A,
        0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    ]
    ROT = [
        [0, 36, 3, 41, 18], [1, 44, 10, 45, 2], [62, 6, 43, 15, 61],
        [28, 55, 25, 21, 56], [27, 20, 39, 8, 14],
    ]
    MASK = (1 << 64) - 1

    def rol(x, n):
        return ((x << n) | (x >> (64 - n))) & MASK

    rate = 136  # 1088 bits for keccak-256
    state = [[0] * 5 for _ in range(5)]
    padded = bytearray(data)
    padded.append(0x01)
    while len(padded) % rate != 0:
        padded.append(0x00)
    padded[-1] ^= 0x80

    for block in range(0, len(padded), rate):
        chunk = padded[block:block + rate]
        for i in range(rate // 8):
            lane = int.from_bytes(chunk[i * 8:i * 8 + 8], 'little')
            state[i % 5][i // 5] ^= lane
        for rnd in range(24):
            C = [state[x][0] ^ state[x][1] ^ state[x][2] ^ state[x][3] ^ state[x][4] for x in range(5)]
            D = [C[(x - 1) % 5] ^ rol(C[(x + 1) % 5], 1) for x in range(5)]
            for x in range(5):
                for y in range(5):
                    state[x][y] ^= D[x]
            B = [[0] * 5 for _ in range(5)]
            for x in range(5):
                for y in range(5):
                    B[y][(2 * x + 3 * y) % 5] = rol(state[x][y], ROT[x][y])
            for x in range(5):
                for y in range(5):
                    state[x][y] = B[x][y] ^ ((~B[(x + 1) % 5][y]) & B[(x + 2) % 5][y] & MASK)
            state[0][0] ^= RC[rnd]

    out = bytearray()
    for i in range(rate // 8):
        if len(out) >= 32:
            break
        out += (state[i % 5][i // 5] & MASK).to_bytes(8, 'little')
    return bytes(out[:32])


# Pinned output. Must equal PaymasterRuntimeHex/CanonicalCodeHash in Eip8141CanonicalPaymasterTests.cs;
# asserted below so editing PROGRAM without re-pasting the hex fails loudly instead of drifting silently.
EXPECTED_LENGTH = 355
EXPECTED_CODEHASH = '0xda42f0d11838c4c0c3129b8b8e93e9718127ad6b315e517e1088125707c4d45c'

if __name__ == '__main__':
    code = assemble()
    codehash = '0x' + keccak256(code).hex()
    assert len(code) == EXPECTED_LENGTH, f'length drifted: {len(code)} != {EXPECTED_LENGTH}'
    assert codehash == EXPECTED_CODEHASH, f'code hash drifted: {codehash} != {EXPECTED_CODEHASH}'
    print("length:", len(code), "bytes")
    print("bytecode:", "0x" + code.hex())
    print("codehash:", codehash)
