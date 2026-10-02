// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Barrett reduction of a 512-bit product modulo one fixed modulus of four 64-bit limbs.
/// </summary>
/// <remarks>
/// Contracts that do field arithmetic (MiMC, Poseidon and pairing-friendly field code) run long MULMOD chains against a
/// single prime. Long division of each product costs a hardware divide per quotient limb; with the reciprocal
/// <c>floor(2^512 / m)</c> computed once, the quotient comes from multiplications instead, and at most a few
/// subtractions of the modulus finish the remainder.
/// </remarks>
internal sealed partial class MulModReducer
{
    private readonly ulong _m0, _m1, _m2, _m3;
    private readonly ulong _mu0, _mu1, _mu2, _mu3, _mu4;

    /// <param name="modulus">A modulus whose top limb is non-zero.</param>
    public MulModReducer(in UInt256 modulus)
    {
        if (modulus.u3 == 0) throw new ArgumentException("The modulus must use all four limbs.", nameof(modulus));
        _m0 = modulus.u0; _m1 = modulus.u1; _m2 = modulus.u2; _m3 = modulus.u3;

        // floor((2^512 - 1) / m) fits five limbs even for m = 2^192, where floor(2^512 / m) is 2^320; for a power of two
        // it is one below the true reciprocal, which only lowers the quotient estimate by one more step.
        BigInteger mu = ((BigInteger.One << 512) - 1) / (BigInteger)modulus;
        Span<byte> bytes = stackalloc byte[48];
        bytes.Clear();
        mu.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        _mu0 = BitConverter.ToUInt64(bytes[..8]);
        _mu1 = BitConverter.ToUInt64(bytes[8..16]);
        _mu2 = BitConverter.ToUInt64(bytes[16..24]);
        _mu3 = BitConverter.ToUInt64(bytes[24..32]);
        _mu4 = BitConverter.ToUInt64(bytes[32..40]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Reduces(in UInt256 modulus) =>
        modulus.u0 == _m0 && modulus.u1 == _m1 && modulus.u2 == _m2 && modulus.u3 == _m3;

    /// <summary>acc = low(acc + a * b + carry); returns the high word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mac(ulong a, ulong b, ref ulong acc, ulong carry)
    {
        ulong hi = Math.BigMul(a, b, out ulong lo);
        lo += acc;
        hi += lo < acc ? 1UL : 0UL;
        lo += carry;
        hi += lo < carry ? 1UL : 0UL;
        acc = lo;
        return hi;
    }

    /// <summary>Sets <paramref name="result"/> to <c>(x * y) mod m</c> for any 256-bit <paramref name="x"/> and <paramref name="y"/>.</summary>
    [SkipLocalsInit]
    public void MultiplyMod(in UInt256 x, in UInt256 y, out UInt256 result)
    {
        ulong x0 = x.u0, x1 = x.u1, x2 = x.u2, x3 = x.u3;
        ulong y0 = y.u0, y1 = y.u1, y2 = y.u2, y3 = y.u3;

        // t = x * y, eight limbs.
        ulong t0 = 0, t1 = 0, t2 = 0, t3 = 0, t4, t5, t6, t7, c;
        c = Mac(x0, y0, ref t0, 0); c = Mac(x0, y1, ref t1, c); c = Mac(x0, y2, ref t2, c); c = Mac(x0, y3, ref t3, c); t4 = c;
        c = Mac(x1, y0, ref t1, 0); c = Mac(x1, y1, ref t2, c); c = Mac(x1, y2, ref t3, c); c = Mac(x1, y3, ref t4, c); t5 = c;
        c = Mac(x2, y0, ref t2, 0); c = Mac(x2, y1, ref t3, c); c = Mac(x2, y2, ref t4, c); c = Mac(x2, y3, ref t5, c); t6 = c;
        c = Mac(x3, y0, ref t3, 0); c = Mac(x3, y1, ref t4, c); c = Mac(x3, y2, ref t5, c); c = Mac(x3, y3, ref t6, c); t7 = c;

        // Quotient estimate: ((t >> 192) * mu) >> 320. Partial products below limb 3 can only carry a few units into
        // limb 5 and are dropped; the estimate then undershoots by a little more, which the subtractions below absorb.
        ulong mu0 = _mu0, mu1 = _mu1, mu2 = _mu2, mu3 = _mu3, mu4 = _mu4;
        ulong p3, p4 = 0, p5, p6, p7, p8, p9;
        p3 = Math.BigMul(t3, mu2, out _);
        c = Mac(t3, mu3, ref p3, 0); c = Mac(t3, mu4, ref p4, c); p5 = c;
        ulong h = Math.BigMul(t4, mu1, out _);
        p3 += h; c = p3 < h ? 1UL : 0UL;
        c = Mac(t4, mu2, ref p3, c); c = Mac(t4, mu3, ref p4, c); c = Mac(t4, mu4, ref p5, c); p6 = c;
        h = Math.BigMul(t5, mu0, out _);
        p3 += h; c = p3 < h ? 1UL : 0UL;
        c = Mac(t5, mu1, ref p3, c); c = Mac(t5, mu2, ref p4, c); c = Mac(t5, mu3, ref p5, c); c = Mac(t5, mu4, ref p6, c); p7 = c;
        c = Mac(t6, mu0, ref p3, 0); c = Mac(t6, mu1, ref p4, c); c = Mac(t6, mu2, ref p5, c); c = Mac(t6, mu3, ref p6, c); c = Mac(t6, mu4, ref p7, c); p8 = c;
        c = Mac(t7, mu0, ref p4, 0); c = Mac(t7, mu1, ref p5, c); c = Mac(t7, mu2, ref p6, c); c = Mac(t7, mu3, ref p7, c); c = Mac(t7, mu4, ref p8, c); p9 = c;
        ulong q0 = p5, q1 = p6, q2 = p7, q3 = p8, q4 = p9;

        // r2 = (q * m) mod 2^320.
        ulong m0 = _m0, m1 = _m1, m2 = _m2, m3 = _m3;
        ulong r0 = 0, r1 = 0, r2 = 0, r3 = 0, r4 = 0;
        c = Mac(q0, m0, ref r0, 0); c = Mac(q0, m1, ref r1, c); c = Mac(q0, m2, ref r2, c); c = Mac(q0, m3, ref r3, c); r4 += c;
        c = Mac(q1, m0, ref r1, 0); c = Mac(q1, m1, ref r2, c); c = Mac(q1, m2, ref r3, c); r4 += q1 * m3 + c;
        c = Mac(q2, m0, ref r2, 0); c = Mac(q2, m1, ref r3, c); r4 += q2 * m2 + c;
        c = Mac(q3, m0, ref r3, 0); r4 += q3 * m1 + c;
        r4 += q4 * m0;

        // s = (t - q * m) mod 2^320, the remainder plus a small multiple of m.
        ulong s0 = t0 - r0; ulong b = t0 < r0 ? 1UL : 0UL;
        ulong s1 = t1 - r1 - b; b = t1 < r1 || (t1 == r1 && b != 0) ? 1UL : 0UL;
        ulong s2 = t2 - r2 - b; b = t2 < r2 || (t2 == r2 && b != 0) ? 1UL : 0UL;
        ulong s3 = t3 - r3 - b; b = t3 < r3 || (t3 == r3 && b != 0) ? 1UL : 0UL;
        ulong s4 = t4 - r4 - b;

        while (s4 != 0 || !LessThanModulus(s3, s2, s1, s0))
        {
            ulong d0 = s0 - m0; b = s0 < m0 ? 1UL : 0UL;
            ulong d1 = s1 - m1 - b; b = s1 < m1 || (s1 == m1 && b != 0) ? 1UL : 0UL;
            ulong d2 = s2 - m2 - b; b = s2 < m2 || (s2 == m2 && b != 0) ? 1UL : 0UL;
            ulong d3 = s3 - m3 - b; b = s3 < m3 || (s3 == m3 && b != 0) ? 1UL : 0UL;
            s0 = d0; s1 = d1; s2 = d2; s3 = d3; s4 -= b;
        }

        result = new UInt256(s0, s1, s2, s3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool LessThanModulus(ulong a3, ulong a2, ulong a1, ulong a0)
    {
        if (a3 != _m3) return a3 < _m3;
        if (a2 != _m2) return a2 < _m2;
        if (a1 != _m1) return a1 < _m1;
        return a0 < _m0;
    }
}
