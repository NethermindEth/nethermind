// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable SYSLIB5004 // X86Base.X64.DivRem is experimental in .NET 10.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.IlEvm;

public static unsafe partial class IlEvm
{
    // DIV and MOD of two words (IlEvmSettings.FastDivide): a dividend of three 64-bit digits (2^128 <= dividend < 2^192)
    // over a divisor of two (2^64 <= divisor < 2^128, not a power of two) takes a schoolbook division that skips the
    // quotient digit that must be zero, two hardware divisions where UInt256's 128-bit-divisor routine always makes three;
    // anything else is the opcode's own helper. A dividend below the divisor UInt256 answers without dividing; a fourth
    // digit gains nothing.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlDivideCalled(ref byte top) => IlDivideOrMod(ref top, mod: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlModCalled(ref byte top) => IlDivideOrMod(ref top, mod: true);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlDivideCalledAhead(ref byte top) => IlDivideOrMod(ref top, mod: false);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlModCalledAhead(ref byte top) => IlDivideOrMod(ref top, mod: true);

    /// <summary>DIV or MOD of the top slot (the dividend) by the one below (the divisor), the result over the divisor, as one 32-byte store.</summary>
    /// <remarks>Anything but the two-digit case runs exactly the opcode's own helper; the two-digit division is a call of its own, so it does not shape that code.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlDivideOrMod(ref byte top, bool mod)
    {
        ref UInt256 x = ref Unsafe.As<byte, UInt256>(ref top);
        // A dividend of three digits: above any two-digit divisor, so a division is due, and one UInt256 makes with three.
        if (X86Base.X64.IsSupported && x.u3 == 0 && x.u2 != 0 && IsTwoDigitDivisor(in Unsafe.Subtract(ref x, 1)))
        {
            IlDivideTwoDigitsInto(ref top, mod);
            return;
        }

        if (mod)
            IlMath2<EvmInstructions.OpMod>(ref top);
        else
            IlMath2<EvmInstructions.OpDiv>(ref top);
    }

    /// <summary>The two-digit case of <see cref="IlDivideOrMod"/>: the quotient or the remainder over the divisor's slot.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlDivideTwoDigitsInto(ref byte top, bool mod)
    {
        ref UInt256 x = ref Unsafe.As<byte, UInt256>(ref top);
        ref UInt256 y = ref Unsafe.Subtract(ref x, 1);
        IlDivideByTwoDigits(in x, y.u0, y.u1, out UInt256 quotient, out ulong r0, out ulong r1);
        Vector256<ulong> result = mod ? Vector256.Create(r0, r1, 0, 0) : Vector256.Create(quotient.u0, quotient.u1, quotient.u2, 0);
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt256, byte>(ref y), result);
    }

    /// <summary>Whether <paramref name="y"/> is at least 2^64, below 2^128 and not a power of two, which UInt256 shifts by.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsTwoDigitDivisor(in UInt256 y) =>
        y.u1 != 0 && (y.u2 | y.u3) == 0 && (y.u0 != 0 || (y.u1 & (y.u1 - 1)) != 0);

    /// <summary>
    /// <paramref name="x"/> divided by the two-digit divisor y1:y0 (y1 != 0): the quotient, below 2^192, and the remainder,
    /// below the divisor. Knuth's algorithm D on the divisor normalized to its top bit; with two divisor digits each estimate
    /// corrected against the low digit is exact. A quotient digit is computed only where the dividend digits above it reach the
    /// divisor; the others are zero.
    /// </summary>
    [SkipLocalsInit]
    internal static void IlDivideByTwoDigits(in UInt256 x, ulong y0, ulong y1, out UInt256 quotient, out ulong r0, out ulong r1)
    {
        int s = BitOperations.LeadingZeroCount(y1);
        ulong v1, v0, u4, u3, u2, u1, u0;
        if (s != 0)
        {
            int back = 64 - s;
            v1 = (y1 << s) | (y0 >> back);
            v0 = y0 << s;
            u4 = x.u3 >> back;
            u3 = (x.u3 << s) | (x.u2 >> back);
            u2 = (x.u2 << s) | (x.u1 >> back);
            u1 = (x.u1 << s) | (x.u0 >> back);
            u0 = x.u0 << s;
        }
        else
        {
            v1 = y1;
            v0 = y0;
            u4 = 0;
            u3 = x.u3;
            u2 = x.u2;
            u1 = x.u1;
            u0 = x.u0;
        }

        // Each step divides the three digits from (high) down by the divisor, and leaves the two-digit remainder in (high - 1, high - 2);
        // a step whose top digit is zero and whose next is below v1 has a zero quotient digit and leaves its digits as they are.
        ulong q2 = u4 == 0 && u3 < v1 ? 0 : Step(ref u4, ref u3, ref u2, v1, v0);
        ulong q1 = u3 == 0 && u2 < v1 ? 0 : Step(ref u3, ref u2, ref u1, v1, v0);
        ulong q0 = u2 == 0 && u1 < v1 ? 0 : Step(ref u2, ref u1, ref u0, v1, v0);
        quotient = new UInt256(q0, q1, q2, 0);
        if (s != 0)
        {
            r0 = (u0 >> s) | (u1 << (64 - s));
            r1 = u1 >> s;
        }
        else
        {
            r0 = u0;
            r1 = u1;
        }
    }

    /// <summary>
    /// One digit of the quotient of (high, middle, low) by the normalized divisor v1:v0, where high:middle is below v1:v0: the
    /// estimate from high:middle over v1, corrected at most twice against v0, which is then exact; the remainder goes to middle:low,
    /// and high to zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Step(ref ulong high, ref ulong middle, ref ulong low, ulong v1, ulong v0)
    {
        ulong qhat, rhat;
        bool rhatOverflow;
        if (high == v1)
        {
            // The estimate would not fit a digit: the largest digit, with what is left of high:middle.
            qhat = ulong.MaxValue;
            rhat = middle + v1;
            rhatOverflow = rhat < middle;
        }
        else
        {
            (qhat, rhat) = X86Base.X64.DivRem(middle, high, v1);
            rhatOverflow = false;
        }

        ulong productHigh = Math.BigMul(qhat, v0, out ulong productLow);
        if (!rhatOverflow && (productHigh > rhat || (productHigh == rhat && productLow > low)))
        {
            qhat--;
            productHigh -= productLow < v0 ? 1UL : 0UL;
            productLow -= v0;
            ulong grown = rhat + v1;
            if (grown >= rhat && (productHigh > grown || (productHigh == grown && productLow > low)))
            {
                qhat--;
                productHigh -= productLow < v0 ? 1UL : 0UL;
                productLow -= v0;
                grown += v1;
            }

            rhat = grown;
        }

        // The remainder: (rhat, low) - (productHigh, productLow), which fits two digits.
        ulong borrow = low < productLow ? 1UL : 0UL;
        low -= productLow;
        middle = rhat - productHigh - borrow;
        high = 0;
        return qhat;
    }
}
