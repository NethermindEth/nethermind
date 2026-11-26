# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# MUL on OpenVM's bigint extension: the RV32 form of ../Nethermind.Stateless.OpenVMGuest/mul.S. The RV32
# platform passes handler arguments in s1-s8 (JitZkHandlerConv), not a0-a7: s1 stack, s2 gas, s3 state, s4 ip,
# s5 head, s6 handlers, s7 code, s8 sp. The handler table holds 4-byte entries, and the pair at ip + 1 is read a
# byte at a time: OpenVM's RV32 memory computes a misaligned halfword load wrong rather than trapping.
    .option norvc
    .section .text, "ax", @progbits
    .balign 4
    .global nethermind_mul_dispatch
    .type nethermind_mul_dispatch, @function
nethermind_mul_dispatch:
    addi    t0, s8, -64
    addi    t1, s8, -32
    # zkvm_u256_mul: custom-0, funct3 0b101, funct7 10 (Int256Funct7::Mul), rd = rs2 = product
    .insn   r 0x0b, 0b101, 10, t0, t1, t0
    addi    s5, s5, -1
    addi    s8, s8, -32
    addi    s4, s4, 1
    lbu     t0, 0(s4)
    lbu     t1, 1(s4)
    slli    t1, t1, 8
    or      t0, t0, t1
    slli    t0, t0, 2
    add     t0, s6, t0
    lw      t0, 0(t0)
    jr      t0
    .size nethermind_mul_dispatch, .-nethermind_mul_dispatch
