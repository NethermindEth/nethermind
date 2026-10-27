// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm;

/// <summary>The EVM's 256-bit arithmetic on Airbender's u256 delegation.</summary>
/// <remarks>
/// bflat lists <c>airbender_u256_op</c> in the JIT's <c>JitZkDelegationPInvokes</c>, so each call below is the single
/// CSR access that runs the delegation on the operands in x10-x12, which changes only a0 and a2. libairbender
/// exports the same entry point for a JIT without that knob. Only code behind <c>AirbenderFlag</c> reaches this.
/// Every operand is a 256-bit integer as eight 32-bit words, least significant first, 32-byte aligned and in RAM;
/// the two operands must not alias.
/// </remarks>
internal static unsafe class AirbenderArithmetic
{
    private const uint MultiplyLowOp = 1u << 3;
    private const uint MultiplyHighOp = 1u << 4;

    /// <summary>Replaces <paramref name="left"/> with the low 256 bits of <paramref name="left"/> times <paramref name="right"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MultiplyLow(void* left, void* right)
    {
        Debug.Assert((((nuint)left | (nuint)right) & 31) == 0);
        airbender_u256_op(left, right, MultiplyLowOp);
    }

    /// <summary>Shifts <paramref name="value"/> left by <paramref name="bits"/>, below 256, overwriting the free word <paramref name="scratch"/>.</summary>
    /// <remarks><c>value &lt;&lt; bits</c> is the low 256 bits of <c>value * 2^bits</c>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShiftLeft(void* value, void* scratch, int bits)
    {
        Debug.Assert((uint)bits < 256);
        SetPowerOfTwo((uint*)scratch, bits);
        MultiplyLow(value, scratch);
    }

    /// <summary>Shifts <paramref name="value"/> right by <paramref name="bits"/>, below 256, overwriting the free word <paramref name="scratch"/>.</summary>
    /// <remarks>For a nonzero shift, <c>value &gt;&gt; bits</c> is the high 256 bits of <c>value * 2^(256 - bits)</c>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShiftRight(void* value, void* scratch, int bits)
    {
        Debug.Assert((uint)bits < 256);
        Debug.Assert((((nuint)value | (nuint)scratch) & 31) == 0);
        if (bits == 0)
            return;

        SetPowerOfTwo((uint*)scratch, 256 - bits);
        airbender_u256_op(value, scratch, MultiplyHighOp);
    }

    /// <summary>Shifts <paramref name="value"/> right by <paramref name="bits"/>, below 256, filling with its sign, overwriting the free word <paramref name="scratch"/>.</summary>
    /// <remarks>A negative value shifts as <c>~(~value &gt;&gt; bits)</c>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShiftRightArithmetic(void* value, void* scratch, int bits)
    {
        ulong* limbs = (ulong*)value;
        bool negative = (long)limbs[3] < 0;
        if (negative)
            Complement(limbs);
        ShiftRight(value, scratch, bits);
        if (negative)
            Complement(limbs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Complement(ulong* limbs)
    {
        limbs[0] = ~limbs[0];
        limbs[1] = ~limbs[1];
        limbs[2] = ~limbs[2];
        limbs[3] = ~limbs[3];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetPowerOfTwo(uint* word, int exponent)
    {
        ulong* limbs = (ulong*)word;
        limbs[0] = 0;
        limbs[1] = 0;
        limbs[2] = 0;
        limbs[3] = 0;
        word[exponent >> 5] = 1u << exponent;
    }

    // Named as its entry point, which is the name the JIT matches.
    [DllImport("*")]
    private static extern uint airbender_u256_op(void* left, void* right, uint op);
}
