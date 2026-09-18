// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

internal readonly struct ExtendedStackSingleDecode(bool isValid, int depth)
{
    public readonly bool IsValid = isValid;
    public readonly int Depth = depth;
}

internal readonly struct ExtendedStackPairDecode(bool isValid, int firstPosition, int secondPosition)
{
    public readonly bool IsValid = isValid;
    public readonly int FirstPosition = firstPosition;
    public readonly int SecondPosition = secondPosition;
}

/// <summary>Pure EIP-8024 immediate decoder used by the extended-stack opcode adapter.</summary>
internal static class ExtendedStackDecoderKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ExtendedStackSingleDecode DecodeSingle(byte immediate)
    {
        int depth = (immediate + 145) & 0xFF;
        bool isValid = (uint)(immediate - 0x5B) > 0x24;
        return new ExtendedStackSingleDecode(isValid, depth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ExtendedStackPairDecode DecodePair(byte immediate)
    {
        int shifted = immediate ^ 0x8F;
        int quotient = shifted >> 4;
        int remainder = shifted & 0x0F;
        int mask = (quotient - remainder) >> 31;
        int firstPosition = ((quotient & mask) | (remainder & ~mask)) + 2;
        int secondPosition = (((remainder + 1) & mask) | ((29 - quotient) & ~mask)) + 1;
        bool isValid = (uint)(immediate - 0x52) > 0x2D;
        return new ExtendedStackPairDecode(isValid, firstPosition, secondPosition);
    }
}
