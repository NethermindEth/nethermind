// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public static class GasUtils
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(long gasCost, ref long gasAvailable)
    {
        if (gasAvailable < gasCost) return false;
        gasAvailable -= gasCost;
        return true;
    }
}
