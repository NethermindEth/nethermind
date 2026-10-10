# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# void* __wrap_memcpy(void* dst, const void* src, size_t n)
#
# Takes every memcpy call (the guest links with --wrap=memcpy) from libc's, which moves 32-bit words and
# merges a misaligned source with 32-bit shifts. OpenVM's rv64 loads and stores take any address at the cost
# of an aligned one (an access that straddles two 8-byte blocks is still one row), so this moves 64-bit words
# without aligning either side, and finishes with the copy's last word rather than a byte loop.
    .option norvc
    .text
    .globl __wrap_memcpy
    .type __wrap_memcpy, @function
    .p2align 2
__wrap_memcpy:
    .cfi_startproc
    mv      t6, a0
    li      t0, 8
    bltu    a2, t0, .Lbytes

    # The last word, read before the loop and stored after it: it may overlap the loop's last word, which
    # it rewrites with the same bytes.
    add     a3, a1, a2
    add     a4, a0, a2
    ld      t5, -8(a3)
    li      t1, 32
    bltu    a2, t1, .Lwords
.Lblocks:
    ld      t2, 0(a1)
    ld      t3, 8(a1)
    ld      t4, 16(a1)
    ld      a5, 24(a1)
    sd      t2, 0(t6)
    sd      t3, 8(t6)
    sd      t4, 16(t6)
    sd      a5, 24(t6)
    addi    a1, a1, 32
    addi    t6, t6, 32
    addi    a2, a2, -32
    bgeu    a2, t1, .Lblocks
.Lwords:
    bltu    a2, t0, .Ltail
.Lword:
    ld      t2, 0(a1)
    sd      t2, 0(t6)
    addi    a1, a1, 8
    addi    t6, t6, 8
    addi    a2, a2, -8
    bgeu    a2, t0, .Lword
.Ltail:
    sd      t5, -8(a4)
    ret

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
