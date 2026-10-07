# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# void openvm_divrem_hint(const uint64_t numerator[8], const uint64_t divisor[4], uint64_t quotient_remainder[8])
#
# Asks the runner for numerator / divisor and numerator mod divisor, the quotient truncated to 256 bits, and
# stores them little-endian, quotient first. The phantom (custom-0, funct3 0b011, imm 0x4d1) is not part of
# OpenVM's SDK configuration, so only a runner that registers it transpiles this; HINT_BUFFER (funct3 0b001,
# imm 1) then copies the 8 hinted doublewords. Nothing here is checked: the caller verifies the pair.
    .option norvc
    .text
    .globl openvm_divrem_hint
    .type openvm_divrem_hint, @function
    .p2align 2
openvm_divrem_hint:
    .cfi_startproc
    .insn i 0x0b, 0b011, a0, a1, 0x4d1
    li t0, 8
    .insn i 0x0b, 0b001, a2, t0, 1
    ret
    .cfi_endproc
    .size openvm_divrem_hint, . - openvm_divrem_hint

# The 512-bit product of the limbs at a0 and the four limbs preloaded in t0..t3, plus the four preloaded in
# a4..a7. Schoolbook on MULHU, which managed code cannot reach on this target (Math.BigMul there is four
# 32-bit products). Leaves the eight result limbs in a4 a5 a6 a7 t4 t5 t6 a1, least significant first;
# a2 and s0..s3 are scratch.
    .macro MAC_FIRST col, yj
    mul s0, a2, \yj
    mulhu s1, a2, \yj
    add \col, \col, s0
    sltu s3, \col, s0
    add s2, s1, s3
    .endm

    .macro MAC col, yj
    mul s0, a2, \yj
    mulhu s1, a2, \yj
    add s0, s0, s2
    sltu s3, s0, s2
    add s1, s1, s3
    add \col, \col, s0
    sltu s3, \col, s0
    add s2, s1, s3
    .endm

    .macro ROW off, c0, c1, c2, c3, c4
    ld a2, \off(a0)
    MAC_FIRST \c0, t0
    MAC \c1, t1
    MAC \c2, t2
    MAC \c3, t3
    mv \c4, s2
    .endm

    .macro MUL_ADD_512
    ROW 0, a4, a5, a6, a7, t4
    ROW 8, a5, a6, a7, t4, t5
    ROW 16, a6, a7, t4, t5, t6
    ROW 24, a7, t4, t5, t6, a1
    .endm

    .macro SAVE
    addi sp, sp, -48
    .cfi_def_cfa_offset 48
    sd s0, 0(sp)
    sd s1, 8(sp)
    sd s2, 16(sp)
    sd s3, 24(sp)
    sd s4, 32(sp)
    .endm

    .macro RESTORE
    ld s0, 0(sp)
    ld s1, 8(sp)
    ld s2, 16(sp)
    ld s3, 24(sp)
    ld s4, 32(sp)
    addi sp, sp, 48
    .cfi_def_cfa_offset 0
    .endm

# void openvm_mul_512(const uint64_t x[4], const uint64_t y[4], uint64_t product[8])
    .globl openvm_mul_512
    .type openvm_mul_512, @function
    .p2align 2
openvm_mul_512:
    .cfi_startproc
    SAVE
    mv s4, a2
    ld t0, 0(a1)
    ld t1, 8(a1)
    ld t2, 16(a1)
    ld t3, 24(a1)
    li a4, 0
    li a5, 0
    li a6, 0
    li a7, 0
    MUL_ADD_512
    sd a4, 0(s4)
    sd a5, 8(s4)
    sd a6, 16(s4)
    sd a7, 24(s4)
    sd t4, 32(s4)
    sd t5, 40(s4)
    sd t6, 48(s4)
    sd a1, 56(s4)
    RESTORE
    ret
    .cfi_endproc
    .size openvm_mul_512, . - openvm_mul_512

# int openvm_is_divrem(const uint64_t numerator[8], const uint64_t divisor[4], const uint64_t quotient[4], const uint64_t remainder[4])
#
# 1 when quotient * divisor + remainder is exactly the 512-bit numerator and remainder < divisor, which pins
# both to the true quotient and remainder whatever the hint said; 0 otherwise.
    .globl openvm_is_divrem
    .type openvm_is_divrem, @function
    .p2align 2
openvm_is_divrem:
    .cfi_startproc
    SAVE
    mv s4, a0
    ld t0, 0(a1)
    ld t1, 8(a1)
    ld t2, 16(a1)
    ld t3, 24(a1)
    ld a4, 0(a3)
    ld a5, 8(a3)
    ld a6, 16(a3)
    ld a7, 24(a3)
    # remainder < divisor, from the top limb down
    bltu a7, t3, 1f
    bne a7, t3, 2f
    bltu a6, t2, 1f
    bne a6, t2, 2f
    bltu a5, t1, 1f
    bne a5, t1, 2f
    bgeu a4, t0, 2f
1:
    mv a0, a2
    MUL_ADD_512
    ld s0, 0(s4)
    xor s0, s0, a4
    ld s1, 8(s4)
    xor s1, s1, a5
    or s0, s0, s1
    ld s1, 16(s4)
    xor s1, s1, a6
    or s0, s0, s1
    ld s1, 24(s4)
    xor s1, s1, a7
    or s0, s0, s1
    ld s1, 32(s4)
    xor s1, s1, t4
    or s0, s0, s1
    ld s1, 40(s4)
    xor s1, s1, t5
    or s0, s0, s1
    ld s1, 48(s4)
    xor s1, s1, t6
    or s0, s0, s1
    ld s1, 56(s4)
    xor s1, s1, a1
    or s0, s0, s1
    seqz a0, s0
    .cfi_remember_state
    RESTORE
    ret
2:
    .cfi_restore_state
    li a0, 0
    RESTORE
    ret
    .cfi_endproc
    .size openvm_is_divrem, . - openvm_is_divrem
