# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# void* __wrap_memset(void* dst, int c, size_t n)
#
# Takes every memset call (the guest links with --wrap=memset) from libc's, which stores 32-bit words. This
# aligns the destination and stores 64-bit words, 32 bytes per iteration; SP1 rejects misaligned stores.
    .option norvc
    .text
    .globl __wrap_memset
    .type __wrap_memset, @function
    .p2align 2
__wrap_memset:
    .cfi_startproc
    mv      t6, a0
    andi    a1, a1, 0xff
    li      t0, 16
    bltu    a2, t0, .Lbytes

    # Most fills are zero, which needs no broadcast.
    beqz    a1, .Lbroadcast_done
    slli    t1, a1, 8
    or      a1, a1, t1
    slli    t1, a1, 16
    or      a1, a1, t1
    slli    t1, a1, 32
    or      a1, a1, t1
.Lbroadcast_done:
    neg     t0, t6
    andi    t0, t0, 7
    beqz    t0, .Laligned
    sub     a2, a2, t0
.Lhead:
    sb      a1, 0(t6)
    addi    t6, t6, 1
    addi    t0, t0, -1
    bnez    t0, .Lhead

.Laligned:
    li      t1, 32
    bltu    a2, t1, .Lwords
.Lblocks:
    sd      a1, 0(t6)
    sd      a1, 8(t6)
    sd      a1, 16(t6)
    sd      a1, 24(t6)
    addi    t6, t6, 32
    addi    a2, a2, -32
    bgeu    a2, t1, .Lblocks
.Lwords:
    li      t1, 8
    bltu    a2, t1, .Lbytes
.Lword:
    sd      a1, 0(t6)
    addi    t6, t6, 8
    addi    a2, a2, -8
    bgeu    a2, t1, .Lword

.Lbytes:
    beqz    a2, .Ldone
.Lbyte:
    sb      a1, 0(t6)
    addi    t6, t6, 1
    addi    a2, a2, -1
    bnez    a2, .Lbyte
.Ldone:
    ret
    .cfi_endproc
    .size __wrap_memset, . - __wrap_memset
