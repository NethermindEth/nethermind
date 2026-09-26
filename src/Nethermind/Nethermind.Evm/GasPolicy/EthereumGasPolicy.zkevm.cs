// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.GasPolicy;

public partial struct EthereumGasPolicy
{
    /// <inheritdoc/>
    /// <remarks>
    /// Subtracts first and tests the sign, which saves the compare RISC-V has no immediate form for. The sign is
    /// exact while <see cref="Value"/> is at most 2^63-1: header validation rejects a block gas limit above that, a
    /// transaction may not exceed its block's gas limit, and a system call runs on a fixed 30M.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas<TCost>(ref EthereumGasPolicy gas) where TCost : struct, IGasCost
    {
        Debug.Assert(gas.Value <= long.MaxValue, "The sign test is exact only for gas that fits a long.");
        long remaining = (long)gas.Value - (long)TCost.GasCost;
        if (remaining < 0)
        {
            gas.Value = 0;
            return false;
        }

        gas.Value = (ulong)remaining;
        return true;
    }
}
