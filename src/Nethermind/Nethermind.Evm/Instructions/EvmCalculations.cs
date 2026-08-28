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
    public static ulong Div32Ceiling(in UInt256 length, out bool outOfGas)
    {
        if (!length.IsUint64)
        {
            outOfGas = true;
            return 0;
        }

        return Div32Ceiling(length.u0, out outOfGas);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Div32Ceiling(ulong result, out bool outOfGas)
    {
        ulong rem = result & 31;
        result >>= 5;
        if (rem > 0)
        {
            result++;
        }

        if (result > uint.MaxValue)
        {
            outOfGas = true;
            return 0;
        }

        outOfGas = false;
        return result;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowOutOfGasException()
    {
        Metrics.EvmExceptions++;
        throw new OutOfGasException();
    }

    public static ulong Div32Ceiling(long length)
    {
        if (length < 0)
        {
            ThrowOutOfGasException();
        }

        return Div32Ceiling((ulong)length);
    }

    public static ulong Div32Ceiling(ulong length)
    {
        ulong result = Div32Ceiling(length, out bool outOfGas);
        if (outOfGas)
        {
            ThrowOutOfGasException();
        }

        return result;
    }

    public static ulong Div32Ceiling(in UInt256 length)
    {
        ulong result = Div32Ceiling(in length, out bool outOfGas);
        if (outOfGas)
        {
            ThrowOutOfGasException();
        }

        return result;
    }
}
