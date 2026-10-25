# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# MUL on SP1's uint256_mul precompile, entered by a tail dispatch from the guest's MUL
# handler with the handler's arguments: a0 stack, a1 gas, a2 state, a3 ip, a4 head,
# a5 handlers, a6 code, a7 sp, the slot above the top word. The stack is checked and
# the gas charged. The product of the top two words (32 and 64 bytes below sp)
# replaces the second, sp and head step down a word, and the next opcode dispatches
# through the handler table by the two bytes at ip + 1, as the managed handlers do
# (RawCalliHelper.PairAt).
# Only temporaries are touched, so no frame is needed.
    .option norvc
    .section .text, "ax", @progbits
    .balign 4
    .global nethermind_mul_dispatch
    .type nethermind_mul_dispatch, @function
nethermind_mul_dispatch:
    addi    t1, a7, -64
    # The precompile reads the modulus after the multiplier; zero means 2^256. The slot
    # at sp is free, and the stack always holds one past the deepest head.
    sd      zero, 0(a7)
    sd      zero, 8(a7)
    sd      zero, 16(a7)
    sd      zero, 24(a7)
    mv      t2, a0
    mv      t3, a1
    mv      a0, t1
    addi    a1, t1, 32
    li      t0, 0x0001011d  # UINT256_MUL: a0 = x, overwritten by x * y mod m; a1 = y || m
    ecall
    mv      a0, t2
    mv      a1, t3
    addi    a4, a4, -1
    addi    a7, a7, -32
    addi    a3, a3, 1
    # SP1 rejects misaligned accesses, so the pair index is read bytewise
    lbu     t0, 0(a3)
    lbu     t1, 1(a3)
    slli    t1, t1, 8
    or      t0, t0, t1
    slli    t0, t0, 3
    add     t0, a5, t0
    ld      t0, 0(t0)
    jr      t0
    .size nethermind_mul_dispatch, .-nethermind_mul_dispatch
