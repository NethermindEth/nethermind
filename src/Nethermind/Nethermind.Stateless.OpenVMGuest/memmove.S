# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# void* __wrap_memmove(void* dst, const void* src, size_t n)
#
# Takes every memmove call (the guest links with --wrap=memmove) from libc's, which copies a misaligned
# overlap byte by byte. A destination below the source, or past its end, is copied forward by
# __wrap_memcpy: it reads every source word before storing over it, and its last word before the loop.
# Only a destination inside the source is copied backward, as __wrap_memcpy does forward: 64-bit words
# without aligning either side, finishing with the copy's first word, read before the loop.
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
    li      t0, 8
    bltu    a2, t0, .Lbyte

    # The first word, read before the loop and stored after it: it may overlap the loop's last word, which
    # it rewrites with the same bytes.
    sub     a3, a1, a2
    ld      t5, 0(a3)
    li      t1, 32
    bltu    a2, t1, .Lwords
.Lblocks:
    ld      t2, -8(a1)
    ld      t3, -16(a1)
    ld      t4, -24(a1)
    ld      a5, -32(a1)
    sd      t2, -8(t6)
    sd      t3, -16(t6)
    sd      t4, -24(t6)
    sd      a5, -32(t6)
    addi    a1, a1, -32
    addi    t6, t6, -32
    addi    a2, a2, -32
    bgeu    a2, t1, .Lblocks
.Lwords:
    bltu    a2, t0, .Lhead
.Lword:
    ld      t2, -8(a1)
    sd      t2, -8(t6)
    addi    a1, a1, -8
    addi    t6, t6, -8
    addi    a2, a2, -8
    bgeu    a2, t0, .Lword
.Lhead:
    sd      t5, 0(a0)
    ret

.Lbyte:
    addi    a1, a1, -1
    addi    t6, t6, -1
    lbu     t1, 0(a1)
    sb      t1, 0(t6)
    addi    a2, a2, -1
    bnez    a2, .Lbyte
    ret
    .cfi_endproc
    .size __wrap_memmove, . - __wrap_memmove
