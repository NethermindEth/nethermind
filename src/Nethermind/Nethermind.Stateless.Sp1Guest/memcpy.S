# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# void* __wrap_memcpy(void* dst, const void* src, size_t n)
#
# Takes every memcpy call (the guest links with --wrap=memcpy) from libc's, which moves 32-bit words and
# merges a misaligned source with 32-bit shifts. SP1 has no DMA copy, so every byte costs instructions,
# and the guest's copies (witness nodes and codes decoded out of the input, padded code) mostly come from
# an unaligned source. This aligns the destination, then moves 64-bit words, merging a misaligned source
# from the aligned words that hold it. SP1 rejects misaligned loads and stores, so every access is either a
# byte or an aligned doubleword; reading a whole aligned word that holds a needed byte never reaches past
# the source's last word.
    .option norvc
    .text
    .globl __wrap_memcpy
    .type __wrap_memcpy, @function
    .p2align 2
__wrap_memcpy:
    .cfi_startproc
    mv      t6, a0
    li      t0, 16
    bltu    a2, t0, .Lbytes

    andi    t0, t6, 7
    beqz    t0, .Ldst_aligned
    li      t1, 8
    sub     t0, t1, t0
    sub     a2, a2, t0
.Lhead:
    lbu     t1, 0(a1)
    sb      t1, 0(t6)
    addi    a1, a1, 1
    addi    t6, t6, 1
    addi    t0, t0, -1
    bnez    t0, .Lhead

.Ldst_aligned:
    andi    t0, a1, 7
    bnez    t0, .Lsrc_misaligned

    li      t1, 32
    bltu    a2, t1, .Lwords
.Lblocks:
    ld      t2, 0(a1)
    ld      t3, 8(a1)
    ld      t4, 16(a1)
    ld      t5, 24(a1)
    sd      t2, 0(t6)
    sd      t3, 8(t6)
    sd      t4, 16(t6)
    sd      t5, 24(t6)
    addi    a1, a1, 32
    addi    t6, t6, 32
    addi    a2, a2, -32
    bgeu    a2, t1, .Lblocks
.Lwords:
    li      t1, 8
    bltu    a2, t1, .Lbytes
.Lword:
    ld      t2, 0(a1)
    sd      t2, 0(t6)
    addi    a1, a1, 8
    addi    t6, t6, 8
    addi    a2, a2, -8
    bgeu    a2, t1, .Lword
    j       .Lbytes

    # t0 = src & 7. Each destination word is the high bytes of one aligned source word and the low
    # bytes of the next: t3 and t4 are the two shifts, a3 walks the aligned source, t2 holds its word.
.Lsrc_misaligned:
    slli    t3, t0, 3
    li      t4, 64
    sub     t4, t4, t3
    andi    a3, a1, -8
    ld      t2, 0(a3)
    li      t1, 16
    bltu    a2, t1, .Lmerge_word
.Lmerge_pair:
    ld      t5, 8(a3)
    srl     a4, t2, t3
    sll     a5, t5, t4
    or      a4, a4, a5
    sd      a4, 0(t6)
    ld      t2, 16(a3)
    srl     a4, t5, t3
    sll     a5, t2, t4
    or      a4, a4, a5
    sd      a4, 8(t6)
    addi    a3, a3, 16
    addi    t6, t6, 16
    addi    a2, a2, -16
    bgeu    a2, t1, .Lmerge_pair
.Lmerge_word:
    li      t1, 8
    bltu    a2, t1, .Lmerge_done
    ld      t5, 8(a3)
    srl     a4, t2, t3
    sll     a5, t5, t4
    or      a4, a4, a5
    sd      a4, 0(t6)
    addi    a3, a3, 8
    addi    t6, t6, 8
    addi    a2, a2, -8
.Lmerge_done:
    add     a1, a3, t0

.Lbytes:
    beqz    a2, .Ldone
.Lbyte:
    lbu     t1, 0(a1)
    sb      t1, 0(t6)
    addi    a1, a1, 1
    addi    t6, t6, 1
    addi    a2, a2, -1
    bnez    a2, .Lbyte
.Ldone:
    ret
    .cfi_endproc
    .size __wrap_memcpy, . - __wrap_memcpy
