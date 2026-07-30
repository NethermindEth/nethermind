// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

public static class UInt256Extensions
{
    // value?.IsZero == false <=> x > 0
    public static bool IsPositive(this UInt256? @this) => @this?.IsZero == false;

    [SkipLocalsInit]
    public static ValueHash256 ToValueHash(this in UInt256 value)
    {
        EvmWord leBytes = Unsafe.As<UInt256, EvmWord>(ref Unsafe.AsRef(in value));
        EvmWord beBytes = leBytes.ByteSwap();
        return Unsafe.As<EvmWord, ValueHash256>(ref beBytes);
    }

    /// <summary>
    /// Returns the value as 32 big-endian bytes in an <see cref="EvmWord"/> (Vector256&lt;byte&gt;).
    /// Same shape as the BAL wire format and EVM stack word.
    /// </summary>
    public static EvmWord ToBigEndianWord(this in UInt256 value)
        => Unsafe.As<UInt256, EvmWord>(ref Unsafe.AsRef(in value)).ByteSwap();

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
