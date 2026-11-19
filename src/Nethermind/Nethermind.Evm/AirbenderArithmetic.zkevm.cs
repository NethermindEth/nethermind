// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm;

/// <summary>The EVM's 256-bit arithmetic on Airbender's u256 delegation, and in part on OpenVM's bigint extension.</summary>
/// <remarks>
/// bflat lists <c>airbender_u256_op</c> in the JIT's <c>JitZkDelegationPInvokes</c>, so each call below is the single
/// CSR access that runs the delegation on the operands in x10-x12, which changes only a0 and a2. libairbender
/// exports the same entry point for a JIT without that knob. Only code behind <see cref="IsAvailable"/> reaches this.
/// Every operand is a 256-bit integer as eight 32-bit words, least significant first, 32-byte aligned and in RAM;
/// the two operands must not alias.
/// <para>
/// On OpenVM's RV32 line the addition, the subtraction, the shifts and the comparisons are one instruction of the
/// bigint extension each, where software takes a carry chain over eight 32-bit words. On RV64 they stay in software,
/// which there works on four 64-bit limbs.
/// </para>
/// </remarks>
internal static unsafe class AirbenderArithmetic
{
    private const uint AddOp = 1u << 0;
    private const uint SubtractOp = 1u << 1;
    private const uint SubtractAndNegateOp = 1u << 2;
    private const uint MultiplyLowOp = 1u << 3;
    private const uint MultiplyHighOp = 1u << 4;
    private const uint EqualOp = 1u << 5;
    private const uint CopyOp = 1u << 7;

    // 2^i as the i-th of 256 words, then zero, in RAM where the delegation reads its operands.
    private static uint* s_powersOfTwo;
    // On OpenVM, the shift amount as a 256-bit word: the bigint extension takes it as its second operand.
    private static ulong* s_shift;

    /// <summary>Whether the guest's zkVM runs the arithmetic below: Airbender, or OpenVM's RV32 line.</summary>
    internal static bool IsAvailable => AirbenderFlag.IsActive || ZkEvmBitOperations.IsOpenVmRv32;

    // Where IsAvailable holds, not Airbender is OpenVM RV32. Tested by the flag, which ILC folds before it links,
    // so the OpenVM guests reference no Airbender symbol; it does not fold the pointer size.
    private static bool OnOpenVm => !AirbenderFlag.IsActive;

    /// <summary>Prepares the constants of the shifts and of <see cref="IsZero"/>; every handler table that reaches them calls this first.</summary>
    public static void Initialize()
    {
        if (OnOpenVm)
        {
            if (s_shift is null)
            {
                ulong* shift = (ulong*)NativeMemory.AlignedAlloc(32, 32);
                NativeMemory.Clear(shift, 32);
                s_shift = shift;
            }
            return;
        }

        if (s_powersOfTwo is not null)
            return;

        const int Words = 32 / sizeof(uint);
        uint* table = (uint*)NativeMemory.AlignedAlloc(257 * 32, 32);
        NativeMemory.Clear(table, 257 * 32);
        for (int exponent = 0; exponent < 256; exponent++)
            table[exponent * Words + (exponent >> 5)] = 1u << exponent;
        s_powersOfTwo = table;
    }

    /// <summary>Copies the word at <paramref name="source"/> to <paramref name="destination"/>.</summary>
    /// <remarks>One delegation in place of eight loads and eight stores.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy(void* destination, void* source)
    {
        Debug.Assert((((nuint)destination | (nuint)source) & 31) == 0 && destination != source);
        Accelerators.AirbenderUInt256Op(destination, source, CopyOp);
    }

    /// <summary>Exchanges the words at <paramref name="a"/> and <paramref name="b"/> through the free word <paramref name="scratch"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap(void* a, void* b, void* scratch)
    {
        Copy(scratch, a);
        Copy(a, b);
        Copy(b, scratch);
    }

    /// <summary>Adds <paramref name="right"/> to <paramref name="left"/>, modulo 2^256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(void* left, void* right)
    {
        Debug.Assert((((nuint)left | (nuint)right) & 31) == 0 && left != right);
        if (OnOpenVm)
        {
            Accelerators.UInt256Add((ulong*)left, (ulong*)left, (ulong*)right);
            return;
        }

        Accelerators.AirbenderUInt256Op(left, right, AddOp);
    }

    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/>, modulo 2^256; whether it borrowed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Subtract(void* left, void* right)
    {
        Debug.Assert((((nuint)left | (nuint)right) & 31) == 0 && left != right);
        return Accelerators.AirbenderUInt256Op(left, right, SubtractOp) != 0;
    }

    /// <summary>Replaces <paramref name="subtrahend"/> with <paramref name="minuend"/> minus it, modulo 2^256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubtractFrom(void* subtrahend, void* minuend)
    {
        Debug.Assert((((nuint)subtrahend | (nuint)minuend) & 31) == 0 && subtrahend != minuend);
        if (OnOpenVm)
        {
            Accelerators.UInt256Sub((ulong*)subtrahend, (ulong*)minuend, (ulong*)subtrahend);
            return;
        }

        Accelerators.AirbenderUInt256Op(subtrahend, minuend, SubtractAndNegateOp);
    }

    /// <summary>Whether the words at <paramref name="left"/> and <paramref name="right"/> are equal.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Equal(void* left, void* right)
    {
        Debug.Assert((((nuint)left | (nuint)right) & 31) == 0 && left != right);
        return Accelerators.AirbenderUInt256Op(left, right, EqualOp) != 0;
    }

    /// <summary>Whether the word at <paramref name="value"/> is zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsZero(void* value) => Equal(value, s_powersOfTwo + 256 * (32 / sizeof(uint)));

    /// <summary>Replaces <paramref name="left"/> with the low 256 bits of <paramref name="left"/> times <paramref name="right"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MultiplyLow(void* left, void* right)
    {
        Debug.Assert((((nuint)left | (nuint)right) & 31) == 0);
        Accelerators.AirbenderUInt256Op(left, right, MultiplyLowOp);
    }

    /// <summary>Replaces <paramref name="left"/> with the high 256 bits of <paramref name="left"/> times <paramref name="right"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MultiplyHigh(void* left, void* right)
    {
        Debug.Assert((((nuint)left | (nuint)right) & 31) == 0);
        Accelerators.AirbenderUInt256Op(left, right, MultiplyHighOp);
    }

    /// <summary>Shifts <paramref name="value"/> left by <paramref name="bits"/>, below 256.</summary>
    /// <remarks><c>value &lt;&lt; bits</c> is the low 256 bits of <c>value * 2^bits</c>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShiftLeft(void* value, int bits)
    {
        Debug.Assert((uint)bits < 256);
        if (OnOpenVm)
        {
            s_shift[0] = (ulong)bits;
            Accelerators.UInt256ShiftLeft((ulong*)value, (ulong*)value, s_shift);
            return;
        }

        MultiplyLow(value, PowerOfTwo(bits));
    }

    /// <summary>Shifts <paramref name="value"/> right by <paramref name="bits"/>, below 256.</summary>
    /// <remarks>For a nonzero shift, <c>value &gt;&gt; bits</c> is the high 256 bits of <c>value * 2^(256 - bits)</c>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShiftRight(void* value, int bits)
    {
        Debug.Assert((uint)bits < 256);
        Debug.Assert(((nuint)value & 31) == 0);
        if (OnOpenVm)
        {
            s_shift[0] = (ulong)bits;
            Accelerators.UInt256ShiftRight((ulong*)value, (ulong*)value, s_shift);
            return;
        }

        if (bits == 0)
            return;

        Accelerators.AirbenderUInt256Op(value, PowerOfTwo(256 - bits), MultiplyHighOp);
    }

    /// <summary>Shifts <paramref name="value"/> right by <paramref name="bits"/>, below 256, filling with its sign.</summary>
    /// <remarks>A negative value shifts as <c>~(~value &gt;&gt; bits)</c>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShiftRightArithmetic(void* value, int bits)
    {
        if (OnOpenVm)
        {
            s_shift[0] = (ulong)bits;
            Accelerators.UInt256ShiftRightArithmetic((ulong*)value, (ulong*)value, s_shift);
            return;
        }

        ulong* limbs = (ulong*)value;
        bool negative = (long)limbs[3] < 0;
        if (negative)
            Complement(limbs);
        ShiftRight(value, bits);
        if (negative)
            Complement(limbs);
    }

    /// <summary>Whether <paramref name="left"/> is below <paramref name="right"/>, overwriting <paramref name="left"/>.</summary>
    /// <remarks>The borrow out of <c>left - right</c>; signed, both sign bits are flipped first, which orders two's
    /// complement words as unsigned ones.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBelowConsuming(void* left, void* right, bool signed)
    {
        Debug.Assert((((nuint)left | (nuint)right) & 31) == 0);
        if (OnOpenVm)
        {
            if (signed)
                Accelerators.UInt256LessThanSigned((ulong*)left, (ulong*)left, (ulong*)right);
            else
                Accelerators.UInt256LessThan((ulong*)left, (ulong*)left, (ulong*)right);
            return ((ulong*)left)[0] != 0;
        }

        if (signed)
        {
            ((uint*)left)[7] ^= 0x80000000u;
            ((uint*)right)[7] ^= 0x80000000u;
        }
        return Accelerators.AirbenderUInt256Op(left, right, SubtractOp) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Complement(ulong* limbs)
    {
        limbs[0] = ~limbs[0];
        limbs[1] = ~limbs[1];
        limbs[2] = ~limbs[2];
        limbs[3] = ~limbs[3];
    }

    /// <summary>The word 2^<paramref name="exponent"/>, for an exponent below 256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint* PowerOfTwo(int exponent)
    {
        Debug.Assert(s_powersOfTwo is not null, "Initialize runs before any handler that shifts");
        return s_powersOfTwo + exponent * (32 / sizeof(uint));
    }
}
