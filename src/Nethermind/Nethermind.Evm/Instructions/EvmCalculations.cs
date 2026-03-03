// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Utility calculations for EVM operations.
/// </summary>
public static class EvmCalculations
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Div32Ceiling(in UInt256 length, out bool outOfGas)
    {
        if (length.IsLargerThanULong())
        {
            outOfGas = true;
            return 0;
        }

        return Div32Ceiling(length.u0, out outOfGas);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Div32Ceiling(ulong result, out bool outOfGas)
    {
        // Overflow-safe: (result >> 5) + ceil_bit avoids wrapping on large ulong values.
        // Equivalent to (result + 31) / 32 but without the addition overflow risk.
        ulong quotient = (result >> 5) + ((result & 31) != 0 ? 1ul : 0ul);
        if (quotient > uint.MaxValue)
        {
            outOfGas = true;
            return 0;
        }

        outOfGas = false;
        return (long)quotient;
    }

    public static long Div32Ceiling(in UInt256 length)
    {
        long result = Div32Ceiling(in length, out bool outOfGas);
        if (outOfGas)
        {
            ThrowOutOfGasException();
        }

        return result;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowOutOfGasException()
        {
            Metrics.EvmExceptions++;
            throw new OutOfGasException();
        }
    }
}
