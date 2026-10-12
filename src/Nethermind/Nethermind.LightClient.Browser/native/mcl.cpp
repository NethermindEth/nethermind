// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#define MCL_FP_BIT 384
#define MCL_FR_BIT 256
#define MCL_DONT_USE_XBYAK
#define MCL_BINT_ASM 0
#define MCL_MSM 0
#define CYBOZU_MINIMUM_EXCEPTION
#include "fp.cpp"

static_assert(sizeof(mclBnFp) == 48 && sizeof(mclBnFr) == 32, "MCL field layout differs from the managed binding");
static_assert(MCLBN_COMPILED_TIME_VAR == 46, "MCL compiled-time ABI differs from the managed binding");
