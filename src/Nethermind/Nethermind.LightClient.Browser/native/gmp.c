// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#include "mini-gmp-aliases.h"
#include "mini-gmp.c"

_Static_assert(sizeof(__mpz_struct) == 12, "GMP binding requires the WASM32 mpz_t layout");
_Static_assert(sizeof(mp_limb_t) == 4, "GMP binding requires WASM32 limbs");
