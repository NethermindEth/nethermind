# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# void zisk_jump_dest_bitmap(uint64_t* bitmap, const uint8_t* code, uint64_t size)
#
# ZisK's EVM JUMPDEST bitmap precompile (ziskos_jump_dest!). The transpiler folds the csrs/add pair
# into one operation only while they are adjacent, so they live here rather than behind a compiler.
# The CFI directives put the function in .eh_frame, which the ISA gates scan in the stripped image.
    .option norvc
    .text
    .globl zisk_jump_dest_bitmap
    .type zisk_jump_dest_bitmap, @function
    .p2align 2
zisk_jump_dest_bitmap:
    .cfi_startproc
    csrs 0x81c, a1
    add x0, a0, a2
    ret
    .cfi_endproc
    .size zisk_jump_dest_bitmap, . - zisk_jump_dest_bitmap
