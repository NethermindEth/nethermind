# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# void* __wrap_memmove(void* dst, const void* src, size_t n)
#
# Takes every memmove call (the guest links with --wrap=memmove) from libc's, which copies a misaligned
# overlap byte by byte. A destination below the source, or past its end, is copied forward by
# __wrap_memcpy: it reads every source word before storing over it. Only a destination inside the source
# is copied backward, as __wrap_memcpy does forward: align the destination's end, then move 64-bit words,
# merging a misaligned source from the aligned words that hold it, since SP1 rejects misaligned accesses.
    .option norvc
    .text
    .globl __wrap_memmove
    .type __wrap_memmove, @function
    .p2align 2
__wrap_memmove:
    .cfi_startproc
    sub     t0, a0, a1
    bltu    t0, a2, .Lbackward
    tail    __wrap_memcpy

    # dst - src < n, so n > 0. t6 and a1 walk down from the ends of the destination and source.
.Lbackward:
    add     t6, a0, a2
    add     a1, a1, a2
    li      t0, 16
    bltu    a2, t0, .Lbytes

    andi    t0, t6, 7
    beqz    t0, .Ldst_aligned
    sub     a2, a2, t0
.Lhead:
    addi    a1, a1, -1
    addi    t6, t6, -1
    lbu     t1, 0(a1)
    sb      t1, 0(t6)
    addi    t0, t0, -1
    bnez    t0, .Lhead

.Ldst_aligned:
    andi    t0, a1, 7
    bnez    t0, .Lsrc_misaligned

    li      t1, 32
    bltu    a2, t1, .Lwords
.Lblocks:
    ld      t2, -8(a1)
    ld      t3, -16(a1)
    ld      t4, -24(a1)
    ld      t5, -32(a1)
    sd      t2, -8(t6)
    sd      t3, -16(t6)
    sd      t4, -24(t6)
    sd      t5, -32(t6)
    addi    a1, a1, -32
    addi    t6, t6, -32
    addi    a2, a2, -32
    bgeu    a2, t1, .Lblocks
.Lwords:
    li      t1, 8
    bltu    a2, t1, .Lbytes
.Lword:
    ld      t2, -8(a1)
    sd      t2, -8(t6)
    addi    a1, a1, -8
    addi    t6, t6, -8
    addi    a2, a2, -8
    bgeu    a2, t1, .Lword
    j       .Lbytes

    # t0 = src end & 7. Each destination word is the low bytes of one aligned source word and the high
    # bytes of the one below it: t3 and t4 are the two shifts, a3 walks the aligned source down, t2 holds
    # its word.
.Lsrc_misaligned:
    slli    t3, t0, 3
    li      t4, 64
    sub     t4, t4, t3
    andi    a3, a1, -8
    ld      t2, 0(a3)
    li      t1, 16
    bltu    a2, t1, .Lmerge_word
.Lmerge_pair:
    ld      t5, -8(a3)
    sll     a4, t2, t4
    srl     a5, t5, t3
    or      a4, a4, a5
    sd      a4, -8(t6)
    ld      t2, -16(a3)
    sll     a4, t5, t4
    srl     a5, t2, t3
    or      a4, a4, a5
    sd      a4, -16(t6)
    addi    a3, a3, -16
    addi    t6, t6, -16
    addi    a2, a2, -16
    bgeu    a2, t1, .Lmerge_pair
.Lmerge_word:
    li      t1, 8
    bltu    a2, t1, .Lmerge_done
    ld      t5, -8(a3)
    sll     a4, t2, t4
    srl     a5, t5, t3
    or      a4, a4, a5
    sd      a4, -8(t6)
    addi    a3, a3, -8
    addi    t6, t6, -8
    addi    a2, a2, -8
.Lmerge_done:
    add     a1, a3, t0

.Lbytes:
    beqz    a2, .Ldone
.Lbyte:
    addi    a1, a1, -1
    addi    t6, t6, -1
    lbu     t1, 0(a1)
    sb      t1, 0(t6)
    addi    a2, a2, -1
    bnez    a2, .Lbyte
.Ldone:
    ret
    .cfi_endproc
    .size __wrap_memmove, . - __wrap_memmove
