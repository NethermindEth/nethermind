# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# MUL on OpenVM's bigint extension, entered by a tail dispatch from the guest's MUL
# handler with the handler's arguments: a0 stack, a1 gas, a2 state, a3 ip, a4 head,
# a5 handlers, a6 code, a7 sp, the slot above the top word. The stack is checked and
# the gas charged. The product of the top two words (32 and 64 bytes below sp)
# replaces the second, sp and head step down a word, and the next opcode dispatches
# through the handler table by the two bytes at ip + 1, as the managed handlers do
# (RawCalliHelper.PairAt).
# Every argument stays in its register, so no frame is needed.
    .option norvc
    .section .text, "ax", @progbits
    .balign 4
    .global nethermind_mul_dispatch
    .type nethermind_mul_dispatch, @function
nethermind_mul_dispatch:
    addi    t0, a7, -64
    addi    t1, a7, -32
    # zkvm_u256_mul: custom-0, funct3 0b101, funct7 10 (Int256Funct7::Mul), rd = rs2 = product
    .insn   r 0x0b, 0b101, 10, t0, t1, t0
    addi    a4, a4, -1
    addi    a7, a7, -32
    addi    a3, a3, 1
    # OpenVM executes misaligned loads natively
    lhu     t0, 0(a3)
    slli    t0, t0, 3
    add     t0, a5, t0
    ld      t0, 0(t0)
    jr      t0
    .size nethermind_mul_dispatch, .-nethermind_mul_dispatch
