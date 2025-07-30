// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

public static class UInt256Extensions
{
    // value?.IsZero == false <=> x > 0
    public static bool IsPositive(this UInt256? @this) => @this?.IsZero == false;

    public static int CountLeadingZeros(this in UInt256 uInt256)
    {
        // Scan from the highest limb down to the lowest
        for (int i = 3; i >= 0; i--)
        {
            ulong limb = uInt256[i];
            if (limb != 0)
            {
                return (3 - i) * 64 + BitOperations.LeadingZeroCount(limb);
            }
        }

        // All four limbs were zero
        return 256;
    }
}
