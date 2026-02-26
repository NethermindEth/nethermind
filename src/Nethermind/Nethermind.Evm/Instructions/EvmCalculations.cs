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
        // For EVM, the max valid memory size is 0x7FFFFFE0 bytes (MaxMemorySize).
        // Div32Ceiling(0x7FFFFFE0) = 0x3FFFFFF0, which is well under uint.MaxValue.
        // The overflow check is provably unreachable because:
        // 1. Values with upper 192 bits set are rejected by IsLargerThanULong() before this call
        // 2. EVM enforces memory limits (CheckMemoryAccessViolation) well before uint.MaxValue * 32 bytes
        Debug.Assert(((result + 31) >> 5) <= uint.MaxValue, "Div32Ceiling result exceeds uint.MaxValue - caller should validate input against EVM memory limits");
        outOfGas = false;
        return (long)((result + 31) >> 5);
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
