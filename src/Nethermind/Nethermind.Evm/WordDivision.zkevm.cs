// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>256-bit division on 32-bit digits, for a 32-bit guest.</summary>
/// <remarks>
/// <see cref="UInt256.Divide(in UInt256, in UInt256, out UInt256)"/> divides on 64-bit limbs, whose 128-bit steps a
/// 32-bit guest emulates in software, its 64-bit division a long library call. Here every step is a 32-bit
/// instruction or a pair: Knuth's algorithm D on 32-bit digits (Hacker's Delight <c>divmnu</c>), each quotient digit
/// estimated by <see cref="DivideNormalized"/> from two 32-bit divisions.
/// </remarks>
internal static unsafe class WordDivision
{
    private const int Digits = 8;

    /// <summary>The quotient and remainder of <paramref name="dividend"/> by <paramref name="divisor"/>, which is not zero.</summary>
    [SkipLocalsInit]
    public static void DivRem(in UInt256 dividend, in UInt256 divisor, out UInt256 quotient, out UInt256 remainder)
    {
        Debug.Assert(!divisor.IsZero);
        quotient = default;
        remainder = default;
        fixed (UInt256* u = &dividend, v = &divisor, q = &quotient, r = &remainder)
            DivRem((uint*)u, SignificantDigits((uint*)u), (uint*)v, (uint*)q, (uint*)r);
    }

    /// <summary>The remainder of the 512-bit <paramref name="dividend"/>, sixteen digits, by <paramref name="divisor"/>, which is not zero.</summary>
    [SkipLocalsInit]
    public static void Remainder(uint* dividend, in UInt256 divisor, out UInt256 remainder)
    {
        Debug.Assert(!divisor.IsZero);
        uint* quotient = stackalloc uint[2 * Digits];
        int high = SignificantDigits(dividend + Digits);
        remainder = default;
        fixed (UInt256* v = &divisor, r = &remainder)
            DivRem(dividend, high > 0 ? Digits + high : SignificantDigits(dividend), (uint*)v, quotient, (uint*)r);
    }

    /// <summary>
    /// Divides the <paramref name="m"/> significant digits at <paramref name="u"/>, at most sixteen, by the eight at
    /// <paramref name="v"/>, which are not all zero, into the digits at <paramref name="q"/>, room for <paramref name="m"/>,
    /// and the eight at <paramref name="r"/>, which are all zero.
    /// </summary>
    [SkipLocalsInit]
    private static void DivRem(uint* u, int m, uint* v, uint* q, uint* r)
    {
        int n = SignificantDigits(v);
        Debug.Assert(n > 0);

        if (m < n || (m == n && IsBelow(u, v, n)))
        {
            for (int i = 0; i < m; i++) r[i] = u[i];
            return;
        }

        if (n == 1)
        {
            uint divisor = v[0];
            int shift = LeadingZeroCount(divisor);
            uint normalized = divisor << shift;
            uint carry = shift == 0 ? 0 : u[m - 1] >> (32 - shift);
            for (int j = m - 1; j >= 0; j--)
            {
                uint digit = (u[j] << shift) | (shift == 0 || j == 0 ? 0 : u[j - 1] >> (32 - shift));
                q[j] = DivideNormalized(carry, digit, normalized, out carry);
            }

            r[0] = carry >> shift;
            return;
        }

        // Normalized so the divisor's top digit has its top bit set; the dividend takes one more digit.
        uint* vn = stackalloc uint[Digits];
        uint* un = stackalloc uint[2 * Digits + 1];
        int s = LeadingZeroCount(v[n - 1]);
        if (s == 0)
        {
            for (int i = 0; i < n; i++) vn[i] = v[i];
            for (int i = 0; i < m; i++) un[i] = u[i];
            un[m] = 0;
        }
        else
        {
            for (int i = n - 1; i > 0; i--) vn[i] = (v[i] << s) | (v[i - 1] >> (32 - s));
            vn[0] = v[0] << s;
            un[m] = u[m - 1] >> (32 - s);
            for (int i = m - 1; i > 0; i--) un[i] = (u[i] << s) | (u[i - 1] >> (32 - s));
            un[0] = u[0] << s;
        }

        uint top = vn[n - 1], second = vn[n - 2];
        for (int j = m - n; j >= 0; j--)
        {
            // The estimate is at most two too large (Knuth D3); the test against the second digit catches nearly every excess.
            uint qhat, rhat;
            bool rhatOverflow = false;
            if (un[j + n] >= top)
            {
                qhat = uint.MaxValue;
                ulong sum = (ulong)un[j + n - 1] + top;
                rhat = (uint)sum;
                rhatOverflow = sum > uint.MaxValue;
            }
            else
            {
                qhat = DivideNormalized(un[j + n], un[j + n - 1], top, out rhat);
            }

            while (!rhatOverflow && (ulong)qhat * second > (((ulong)rhat << 32) | un[j + n - 2]))
            {
                qhat--;
                ulong sum = (ulong)rhat + top;
                rhat = (uint)sum;
                rhatOverflow = sum > uint.MaxValue;
            }

            // Multiply and subtract.
            uint borrow = 0, carry = 0;
            for (int i = 0; i < n; i++)
            {
                ulong product = (ulong)qhat * vn[i] + carry;
                carry = (uint)(product >> 32);
                uint low = (uint)product;
                uint digit = un[i + j];
                uint difference = digit - low;
                uint underflow = digit < low ? 1u : 0u;
                uint result = difference - borrow;
                underflow |= difference < borrow ? 1u : 0u;
                un[i + j] = result;
                borrow = underflow;
            }

            uint last = un[j + n];
            un[j + n] = last - carry - borrow;
            if (last < (ulong)carry + borrow)
            {
                // The estimate was one too large: add the divisor back.
                qhat--;
                uint addCarry = 0;
                for (int i = 0; i < n; i++)
                {
                    ulong sum = (ulong)un[i + j] + vn[i] + addCarry;
                    un[i + j] = (uint)sum;
                    addCarry = (uint)(sum >> 32);
                }

                un[j + n] += addCarry;
            }

            q[j] = qhat;
        }

        if (s == 0)
        {
            for (int i = 0; i < n; i++) r[i] = un[i];
        }
        else
        {
            for (int i = 0; i < n - 1; i++) r[i] = (un[i] >> s) | (un[i + 1] << (32 - s));
            r[n - 1] = un[n - 1] >> s;
        }
    }

    /// <summary>The quotient of the two digits <paramref name="high"/>:<paramref name="low"/> by <paramref name="divisor"/>, whose top bit is set and which exceeds <paramref name="high"/>.</summary>
    /// <remarks>Hacker's Delight <c>divlu</c>: two 32-bit divisions by the divisor's top half, each corrected at most twice.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint DivideNormalized(uint high, uint low, uint divisor, out uint remainder)
    {
        Debug.Assert((divisor >> 31) != 0 && high < divisor);
        const uint Base = 1u << 16;
        uint vn1 = divisor >> 16, vn0 = divisor & 0xFFFF;
        uint un1 = low >> 16, un0 = low & 0xFFFF;

        uint q1 = high / vn1;
        uint rhat = high - q1 * vn1;
        while (q1 >= Base || q1 * vn0 > ((rhat << 16) | un1))
        {
            q1--;
            rhat += vn1;
            if (rhat >= Base) break;
        }

        uint un21 = (high << 16) + un1 - q1 * divisor;
        uint q0 = un21 / vn1;
        rhat = un21 - q0 * vn1;
        while (q0 >= Base || q0 * vn0 > ((rhat << 16) | un0))
        {
            q0--;
            rhat += vn1;
            if (rhat >= Base) break;
        }

        remainder = (un21 << 16) + un0 - q0 * divisor;
        return (q1 << 16) | q0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignificantDigits(uint* digits)
    {
        ulong* limbs = (ulong*)digits;
        int limb = limbs[3] != 0 ? 3 : limbs[2] != 0 ? 2 : limbs[1] != 0 ? 1 : 0;
        return 2 * limb + (digits[2 * limb + 1] != 0 ? 2 : digits[2 * limb] != 0 ? 1 : 0);
    }

    /// <remarks>Inline: without Zbb, <see cref="BitOperations.LeadingZeroCount(uint)"/> is a call to a table lookup.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LeadingZeroCount(uint value)
    {
        Debug.Assert(value != 0);
        int count = 0;
        if (value <= 0xFFFF) { count = 16; value <<= 16; }
        if (value <= 0xFF_FFFF) { count += 8; value <<= 8; }
        if (value <= 0xFFF_FFFF) { count += 4; value <<= 4; }
        if (value <= 0x3FFF_FFFF) { count += 2; value <<= 2; }
        if (value <= 0x7FFF_FFFF) count++;
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBelow(uint* a, uint* b, int digits)
    {
        for (int i = digits - 1; i >= 0; i--)
        {
            if (a[i] != b[i]) return a[i] < b[i];
        }

        return false;
    }
}
