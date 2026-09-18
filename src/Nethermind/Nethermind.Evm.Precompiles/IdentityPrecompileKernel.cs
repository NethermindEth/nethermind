// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Precompiles;

internal static class IdentityPrecompileKernel
{
    public static ulong BaseGasCost() => 15UL;

    public static ulong DataGasCost(uint inputLength) =>
        3UL * (((ulong)inputLength + 31UL) >> 5);
}
