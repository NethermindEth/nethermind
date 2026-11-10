// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// Modular inversion for the 256-bit primes that contracts most often invert with MODEXP as base^(p - 2) mod p.
/// </summary>
/// <remarks>
/// By Fermat's little theorem base^(p - 2) mod p is the inverse of base, or 0 when p divides base. The inverse is
/// computed with the variable-time divstep algorithm of Bernstein and Yang ("Fast constant-time gcd computation and
/// modular inversion", 2019), 62 divsteps per batch on signed 62-bit limbs, which replaces about 300 modular
/// multiplications with a few hundred word operations. All inputs are public, so variable time is fine.
/// </remarks>
[SkipLocalsInit]
internal static class PrimeFieldInverse
{
    public const int Size = 32;

    private const int LimbBits = 62;
    private const ulong LimbMask = ulong.MaxValue >> (64 - LimbBits);
    private const long SignedLimbMask = (long)LimbMask;

    private static readonly Prime[] Primes =
    [
        // BN254 scalar field
        new("30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001"),
        // BN254 base field
        new("30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47"),
        // secp256k1 base field
        new("fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f"),
        // secp256k1 group order
        new("fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141"),
        // secp256r1 base field
        new("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff"),
        // secp256r1 group order
        new("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551"),
    ];

    /// <summary>
    /// Computes base^exponent mod modulus when the modulus is one of the known primes p and the exponent is p - 2.
    /// </summary>
    /// <param name="base">Big-endian base of at most 32 bytes.</param>
    /// <param name="exponent">Big-endian exponent of exactly 32 bytes.</param>
    /// <param name="modulus">Big-endian modulus of exactly 32 bytes.</param>
    /// <param name="result">The 32-byte big-endian result.</param>
    /// <returns><c>false</c> when the inputs do not have this shape and the general exponentiation must run.</returns>
    public static bool TryInvert(ReadOnlySpan<byte> @base, ReadOnlySpan<byte> exponent, ReadOnlySpan<byte> modulus, [NotNullWhen(true)] out byte[]? result)
    {
        // Every prime has a nonzero top byte, so its modulus and its p - 2 both take exactly 32 bytes
        if (@base.Length <= Size && exponent.Length == Size && modulus.Length == Size)
        {
            foreach (Prime prime in Primes)
            {
                if (modulus.SequenceEqual(prime.Modulus) && exponent.SequenceEqual(prime.Exponent))
                {
                    result = new byte[Size];
                    prime.Invert(@base, result);
                    return true;
                }
            }
        }

        result = null;
        return false;
    }

    private static UInt256Limbs ReadBigEndian(ReadOnlySpan<byte> value)
    {
        Span<byte> padded = stackalloc byte[Size];
        padded[..(Size - value.Length)].Clear();
        value.CopyTo(padded[(Size - value.Length)..]);
        return new UInt256Limbs(
            BinaryPrimitives.ReadUInt64BigEndian(padded[24..]),
            BinaryPrimitives.ReadUInt64BigEndian(padded[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(padded[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(padded));
    }

    private readonly record struct UInt256Limbs(ulong U0, ulong U1, ulong U2, ulong U3)
    {
        public bool IsZero => (U0 | U1 | U2 | U3) == 0;

        public static bool operator >=(in UInt256Limbs a, in UInt256Limbs b) =>
            a.U3 != b.U3 ? a.U3 > b.U3 :
            a.U2 != b.U2 ? a.U2 > b.U2 :
            a.U1 != b.U1 ? a.U1 > b.U1 :
            a.U0 >= b.U0;

        public static bool operator <=(in UInt256Limbs a, in UInt256Limbs b) => b >= a;

        public static UInt256Limbs operator -(in UInt256Limbs a, in UInt256Limbs b)
        {
            ulong r0 = a.U0 - b.U0;
            ulong borrow = a.U0 < b.U0 ? 1UL : 0UL;
            ulong r1 = a.U1 - b.U1 - borrow;
            borrow = a.U1 < b.U1 || (a.U1 == b.U1 && borrow != 0) ? 1UL : 0UL;
            ulong r2 = a.U2 - b.U2 - borrow;
            borrow = a.U2 < b.U2 || (a.U2 == b.U2 && borrow != 0) ? 1UL : 0UL;
            ulong r3 = a.U3 - b.U3 - borrow;
            return new UInt256Limbs(r0, r1, r2, r3);
        }

        public Signed62 ToSigned62() => new(
            (long)(U0 & LimbMask),
            (long)(((U0 >> 62) | (U1 << 2)) & LimbMask),
            (long)(((U1 >> 60) | (U2 << 4)) & LimbMask),
            (long)(((U2 >> 58) | (U3 << 6)) & LimbMask),
            (long)(U3 >> 56));
    }

    /// <summary>A signed integer as v0 + v1·2^62 + v2·2^124 + v3·2^186 + v4·2^248.</summary>
    private struct Signed62(long v0, long v1, long v2, long v3, long v4)
    {
        public long V0 = v0, V1 = v1, V2 = v2, V3 = v3, V4 = v4;

        public readonly bool IsZero => (V0 | V1 | V2 | V3 | V4) == 0;

        /// <summary>Moves the bits above 62 of the lower limbs up, leaving them in [0, 2^62) and the sign in the top limb.</summary>
        public void Carry()
        {
            V1 += V0 >> LimbBits;
            V0 &= SignedLimbMask;
            V2 += V1 >> LimbBits;
            V1 &= SignedLimbMask;
            V3 += V2 >> LimbBits;
            V2 &= SignedLimbMask;
            V4 += V3 >> LimbBits;
            V3 &= SignedLimbMask;
        }

        public void Add(in Signed62 other)
        {
            V0 += other.V0;
            V1 += other.V1;
            V2 += other.V2;
            V3 += other.V3;
            V4 += other.V4;
            Carry();
        }

        public void Negate()
        {
            V0 = -V0;
            V1 = -V1;
            V2 = -V2;
            V3 = -V3;
            V4 = -V4;
            Carry();
        }

        /// <summary>Writes a value in [0, 2^256) as 32 big-endian bytes.</summary>
        public readonly void WriteBigEndian(Span<byte> output)
        {
            ulong u0 = (ulong)V0 | ((ulong)V1 << 62);
            ulong u1 = ((ulong)V1 >> 2) | ((ulong)V2 << 60);
            ulong u2 = ((ulong)V2 >> 4) | ((ulong)V3 << 58);
            ulong u3 = ((ulong)V3 >> 6) | ((ulong)V4 << 56);
            BinaryPrimitives.WriteUInt64BigEndian(output, u3);
            BinaryPrimitives.WriteUInt64BigEndian(output[8..], u2);
            BinaryPrimitives.WriteUInt64BigEndian(output[16..], u1);
            BinaryPrimitives.WriteUInt64BigEndian(output[24..], u0);
        }
    }

    /// <summary>The transition matrix of 62 divsteps, scaled by 2^62.</summary>
    private readonly record struct Transition(long U, long V, long Q, long R);

    private sealed class Prime
    {
        public readonly byte[] Modulus;
        public readonly byte[] Exponent;
        private readonly UInt256Limbs _value;
        private readonly Signed62 _signed;
        // modulus^-1 mod 2^62
        private readonly ulong _inverse62;

        public Prime(string hex)
        {
            Modulus = Convert.FromHexString(hex);
            _value = ReadBigEndian(Modulus);
            _signed = _value.ToSigned62();

            Exponent = new byte[Size];
            UInt256Limbs exponent = _value - new UInt256Limbs(2, 0, 0, 0);
            BinaryPrimitives.WriteUInt64BigEndian(Exponent, exponent.U3);
            BinaryPrimitives.WriteUInt64BigEndian(Exponent.AsSpan(8), exponent.U2);
            BinaryPrimitives.WriteUInt64BigEndian(Exponent.AsSpan(16), exponent.U1);
            BinaryPrimitives.WriteUInt64BigEndian(Exponent.AsSpan(24), exponent.U0);

            // Newton's iteration doubles the correct low bits each step; an odd m is its own inverse mod 8
            ulong inverse = _value.U0;
            for (int i = 0; i < 5; i++)
                inverse *= 2 - _value.U0 * inverse;
            _inverse62 = inverse & LimbMask;
        }

        public void Invert(ReadOnlySpan<byte> @base, Span<byte> result)
        {
            UInt256Limbs x = ReadBigEndian(@base);
            // Every prime exceeds 2^253, so a 256-bit base is below 6p and at most five subtractions reduce it
            while (x >= _value)
                x -= _value;

            if (x.IsZero)
            {
                result.Clear();
                return;
            }

            Signed62 d = default;
            Signed62 e = new(1, 0, 0, 0, 0);
            Signed62 f = _signed;
            Signed62 g = x.ToSigned62();
            long eta = -1;

            while (!g.IsZero)
            {
                eta = DivSteps(eta, (ulong)f.V0, (ulong)g.V0, out Transition t);
                UpdateFg(ref f, ref g, t);
                UpdateDe(ref d, ref e, t);
            }

            // f is ±1 here and d·x ≡ f (mod p) with d in (-2p, p)
            if (d.V4 < 0)
                d.Add(_signed);
            if (f.V4 < 0)
                d.Negate();
            if (d.V4 < 0)
                d.Add(_signed);

            d.WriteBigEndian(result);
        }

        /// <summary>Runs 62 divsteps on the low bits of f and g, skipping runs of zero bits of g at once.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long DivSteps(long eta, ulong f, ulong g, out Transition t)
        {
            ulong u = 1, v = 0, q = 0, r = 1;
            int i = LimbBits;

            while (true)
            {
                // The sentinel bit at position i caps the count at the steps left
                int zeros = BitOperations.TrailingZeroCount(g | (ulong.MaxValue << i));
                g >>= zeros;
                u <<= zeros;
                v <<= zeros;
                eta -= zeros;
                i -= zeros;
                if (i == 0)
                    break;

                if (eta < 0)
                {
                    eta = -eta;
                    ulong tmp = f;
                    f = g;
                    g = 0 - tmp;
                    tmp = u;
                    u = q;
                    q = 0 - tmp;
                    tmp = v;
                    v = r;
                    r = 0 - tmp;
                }

                // Cancel up to six low bits of g at once: w = -g/f mod 2^limit, using f·(f·f - 2) ≡ -1/f (mod 64)
                int limit = (int)Math.Min(Math.Min(eta + 1, i), 6);
                ulong w = f * g * (f * f - 2) & (ulong.MaxValue >> (64 - limit));
                g += f * w;
                q += u * w;
                r += v * w;
            }

            t = new Transition((long)u, (long)v, (long)q, (long)r);
            return eta;
        }

        /// <summary>Sets (f, g) to t·(f, g) / 2^62, which is exact.</summary>
        private static void UpdateFg(ref Signed62 f, ref Signed62 g, in Transition t)
        {
            Int128 cf = (Int128)t.U * f.V0 + (Int128)t.V * g.V0;
            Int128 cg = (Int128)t.Q * f.V0 + (Int128)t.R * g.V0;
            cf >>= LimbBits;
            cg >>= LimbBits;

            cf += (Int128)t.U * f.V1 + (Int128)t.V * g.V1;
            cg += (Int128)t.Q * f.V1 + (Int128)t.R * g.V1;
            f.V0 = (long)cf & SignedLimbMask;
            g.V0 = (long)cg & SignedLimbMask;
            cf >>= LimbBits;
            cg >>= LimbBits;

            cf += (Int128)t.U * f.V2 + (Int128)t.V * g.V2;
            cg += (Int128)t.Q * f.V2 + (Int128)t.R * g.V2;
            f.V1 = (long)cf & SignedLimbMask;
            g.V1 = (long)cg & SignedLimbMask;
            cf >>= LimbBits;
            cg >>= LimbBits;

            cf += (Int128)t.U * f.V3 + (Int128)t.V * g.V3;
            cg += (Int128)t.Q * f.V3 + (Int128)t.R * g.V3;
            f.V2 = (long)cf & SignedLimbMask;
            g.V2 = (long)cg & SignedLimbMask;
            cf >>= LimbBits;
            cg >>= LimbBits;

            cf += (Int128)t.U * f.V4 + (Int128)t.V * g.V4;
            cg += (Int128)t.Q * f.V4 + (Int128)t.R * g.V4;
            f.V3 = (long)cf & SignedLimbMask;
            g.V3 = (long)cg & SignedLimbMask;
            cf >>= LimbBits;
            cg >>= LimbBits;

            f.V4 = (long)cf;
            g.V4 = (long)cg;
        }

        /// <summary>
        /// Sets (d, e) to t·(d, e) / 2^62 modulo p, adding the multiple of p that makes the division exact and keeps
        /// both in (-2p, p).
        /// </summary>
        private void UpdateDe(ref Signed62 d, ref Signed62 e, in Transition t)
        {
            long sd = d.V4 >> 63;
            long se = e.V4 >> 63;
            long md = (t.U & sd) + (t.V & se);
            long me = (t.Q & sd) + (t.R & se);

            Int128 cd = (Int128)t.U * d.V0 + (Int128)t.V * e.V0;
            Int128 ce = (Int128)t.Q * d.V0 + (Int128)t.R * e.V0;

            md -= (long)((_inverse62 * (ulong)cd + (ulong)md) & LimbMask);
            me -= (long)((_inverse62 * (ulong)ce + (ulong)me) & LimbMask);

            Signed62 p = _signed;
            cd += (Int128)p.V0 * md;
            ce += (Int128)p.V0 * me;
            cd >>= LimbBits;
            ce >>= LimbBits;

            cd += (Int128)t.U * d.V1 + (Int128)t.V * e.V1 + (Int128)p.V1 * md;
            ce += (Int128)t.Q * d.V1 + (Int128)t.R * e.V1 + (Int128)p.V1 * me;
            d.V0 = (long)cd & SignedLimbMask;
            e.V0 = (long)ce & SignedLimbMask;
            cd >>= LimbBits;
            ce >>= LimbBits;

            cd += (Int128)t.U * d.V2 + (Int128)t.V * e.V2 + (Int128)p.V2 * md;
            ce += (Int128)t.Q * d.V2 + (Int128)t.R * e.V2 + (Int128)p.V2 * me;
            d.V1 = (long)cd & SignedLimbMask;
            e.V1 = (long)ce & SignedLimbMask;
            cd >>= LimbBits;
            ce >>= LimbBits;

            cd += (Int128)t.U * d.V3 + (Int128)t.V * e.V3 + (Int128)p.V3 * md;
            ce += (Int128)t.Q * d.V3 + (Int128)t.R * e.V3 + (Int128)p.V3 * me;
            d.V2 = (long)cd & SignedLimbMask;
            e.V2 = (long)ce & SignedLimbMask;
            cd >>= LimbBits;
            ce >>= LimbBits;

            cd += (Int128)t.U * d.V4 + (Int128)t.V * e.V4 + (Int128)p.V4 * md;
            ce += (Int128)t.Q * d.V4 + (Int128)t.R * e.V4 + (Int128)p.V4 * me;
            d.V3 = (long)cd & SignedLimbMask;
            e.V3 = (long)ce & SignedLimbMask;
            cd >>= LimbBits;
            ce >>= LimbBits;

            d.V4 = (long)cd;
            e.V4 = (long)ce;
        }
    }
}
