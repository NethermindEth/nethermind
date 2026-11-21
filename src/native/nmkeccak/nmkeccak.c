// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#include <stddef.h>
#include <stdint.h>
#include "KeccakP-1600-SnP.h"

__attribute__((visibility("default"))) void nm_keccak_absorb(uint64_t *state, const uint8_t *data, size_t blocks)
{
    KeccakF1600_FastLoop_Absorb((KeccakP1600_state *)state, 17, data, blocks * 136);
}
