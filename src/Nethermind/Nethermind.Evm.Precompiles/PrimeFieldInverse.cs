// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Int256;

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
    private const long LimbMask = (1L << LimbBits) - 1;
    private const ulong UnsignedLimbMask = (1UL << LimbBits) - 1;

    /// <summary>The most batches of 62 divsteps any 256-bit input needs before g reaches 0.</summary>
    /// <remarks>
    /// Theorem 11.2 of the paper cited above proves that divsteps starting from delta = 1 bring g to 0 for every odd f
    /// and every g below 2^d within floor((49d + 57) / 17) steps when d >= 46. For d = 256 that is 741 divsteps, so
    /// ceil(741 / 62) = 12 batches. More batches mean a broken invariant, and the caller takes the general path.
    /// </remarks>
    internal const int MaxBatches = 12;

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
    /// <returns>
    /// <c>false</c> when the inputs do not have this shape, or the inversion does not finish within <see cref="MaxBatches"/>,
    /// and the general exponentiation must run.
    /// </returns>
    public static bool TryInvert(ReadOnlySpan<byte> @base, ReadOnlySpan<byte> exponent, ReadOnlySpan<byte> modulus, [NotNullWhen(true)] out byte[]? result) =>
        TryInvert(@base, exponent, modulus, MaxBatches, out result);

    // Tests lower maxBatches to reach the fallback that a broken invariant would take
    internal static bool TryInvert(ReadOnlySpan<byte> @base, ReadOnlySpan<byte> exponent, ReadOnlySpan<byte> modulus, int maxBatches, [NotNullWhen(true)] out byte[]? result)
    {
        // Every prime has a nonzero top byte, so its modulus and its p - 2 both take exactly 32 bytes
        if (@base.Length <= Size && exponent.Length == Size && modulus.Length == Size)
        {
            foreach (Prime prime in Primes)
            {
                if (modulus.SequenceEqual(prime.Modulus) && exponent.SequenceEqual(prime.Exponent))
                {
                    byte[] output = new byte[Size];
                    if (prime.TryInvert(@base, maxBatches, output))
                    {
                        result = output;
                        return true;
                    }

                    break;
                }
            }
        }

        result = null;
        return false;
    }

    /// <summary>The low 64 bits of an accumulator, which the limb arithmetic keeps exact or masks afterwards.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Low(Int128 value) => long.CreateTruncating(value);

    /// <summary>A limb of at most 62 bits, which fits a long unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Limb(ulong value) => long.CreateTruncating(value);

    /// <summary>A signed integer as v0 + v1·2^62 + v2·2^124 + v3·2^186 + v4·2^248.</summary>
    private struct Signed62(long v0, long v1, long v2, long v3, long v4)
    {
        public long V0 = v0, V1 = v1, V2 = v2, V3 = v3, V4 = v4;

        public readonly bool IsZero => (V0 | V1 | V2 | V3 | V4) == 0;

        public static Signed62 From(in UInt256 value) => new(
            Limb(value.u0 & UnsignedLimbMask),
            Limb(((value.u0 >> 62) | (value.u1 << 2)) & UnsignedLimbMask),
            Limb(((value.u1 >> 60) | (value.u2 << 4)) & UnsignedLimbMask),
            Limb(((value.u2 >> 58) | (value.u3 << 6)) & UnsignedLimbMask),
            Limb(value.u3 >> 56));

        /// <summary>Writes a value in [0, 2^256) as 32 big-endian bytes.</summary>
        public readonly void WriteBigEndian(Span<byte> output)
        {
            BinaryPrimitives.WriteInt64BigEndian(output, (V3 >> 6) | (V4 << 56));
            BinaryPrimitives.WriteInt64BigEndian(output[8..], (V2 >> 4) | (V3 << 58));
            BinaryPrimitives.WriteInt64BigEndian(output[16..], (V1 >> 2) | (V2 << 60));
            BinaryPrimitives.WriteInt64BigEndian(output[24..], V0 | (V1 << 62));
        }

        /// <summary>Moves the bits above 62 of the lower limbs up, leaving them in [0, 2^62) and the sign in the top limb.</summary>
        public void Carry()
        {
            V1 += V0 >> LimbBits;
            V0 &= LimbMask;
            V2 += V1 >> LimbBits;
            V1 &= LimbMask;
            V3 += V2 >> LimbBits;
            V2 &= LimbMask;
            V4 += V3 >> LimbBits;
            V3 &= LimbMask;
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
    }

    /// <summary>The transition matrix of 62 divsteps, scaled by 2^62.</summary>
    private readonly record struct Transition(long U, long V, long Q, long R);

    private sealed class Prime
    {
        public readonly byte[] Modulus;
        public readonly byte[] Exponent;
        private readonly UInt256 _modulus;
        private readonly Signed62 _value;
        // modulus^-1 mod 2^62
        private readonly long _inverse62;

        public Prime(string hex)
        {
            Modulus = Convert.FromHexString(hex);
            _modulus = new UInt256(Modulus, isBigEndian: true);
            _value = Signed62.From(_modulus);
            Exponent = (_modulus - new UInt256(2)).ToBigEndian();

            // Newton's iteration doubles the correct low bits each step; an odd m is its own inverse mod 8
            long inverse = _value.V0;
            for (int i = 0; i < 5; i++)
                inverse *= 2 - _value.V0 * inverse;
            _inverse62 = inverse & LimbMask;
        }

        public bool TryInvert(ReadOnlySpan<byte> @base, int maxBatches, Span<byte> result)
        {
            UInt256 x = new(@base, isBigEndian: true);
            // Every prime exceeds 2^253, so a 256-bit base is below 6p and at most five subtractions reduce it
            while (x >= _modulus)
                x -= _modulus;

            if (x.IsZero)
            {
                result.Clear();
                return true;
            }

            Signed62 g = Signed62.From(x);

            Signed62 d = default;
            Signed62 e = new(1, 0, 0, 0, 0);
            Signed62 f = _value;
            int eta = -1;

            for (int batches = 0; !g.IsZero; batches++)
            {
                if (batches == maxBatches)
                    return false;

                eta = DivSteps(eta, f.V0, g.V0, out Transition t);
                UpdateFg(ref f, ref g, t);
                UpdateDe(ref d, ref e, t);
            }

            // f is ±1 here and d·x ≡ f (mod p) with d in (-2p, p)
            if (d.V4 < 0)
                d.Add(_value);
            if (f.V4 < 0)
                d.Negate();
            if (d.V4 < 0)
                d.Add(_value);

            d.WriteBigEndian(result);
            return true;
        }

        /// <summary>Runs 62 divsteps on the low bits of f and g, skipping runs of zero bits of g at once.</summary>
        /// <remarks>Only the low bits of f and g matter, so the wrapping 64-bit arithmetic is exact where it is used.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DivSteps(int eta, long f, long g, out Transition t)
        {
            long u = 1, v = 0, q = 0, r = 1;
            int i = LimbBits;

            while (true)
            {
                // The sentinel bit at position i caps the count at the steps left
                int zeros = BitOperations.TrailingZeroCount(g | (-1L << i));
                g >>>= zeros;
                u <<= zeros;
                v <<= zeros;
                eta -= zeros;
                i -= zeros;
                if (i == 0)
                    break;

                if (eta < 0)
                {
                    eta = -eta;
                    long tmp = f;
                    f = g;
                    g = -tmp;
                    tmp = u;
                    u = q;
                    q = -tmp;
                    tmp = v;
                    v = r;
                    r = -tmp;
                }

                // Cancel up to six low bits of g at once: w = -g/f mod 2^limit, using f·(f·f - 2) ≡ -1/f (mod 64)
                int limit = Math.Min(Math.Min(eta + 1, i), 6);
                long w = f * g * (f * f - 2) & (-1L >>> (64 - limit));
                g += f * w;
                q += u * w;
                r += v * w;
            }

            t = new Transition(u, v, q, r);
            return eta;
        }

        /// <summary>Sets (f, g) to t·(f, g) / 2^62, which is exact.</summary>
        private static void UpdateFg(ref Signed62 f, ref Signed62 g, in Transition t)
        {
            Int128 cf = Math.BigMul(t.U, f.V0) + Math.BigMul(t.V, g.V0);
            Int128 cg = Math.BigMul(t.Q, f.V0) + Math.BigMul(t.R, g.V0);
            cf >>= LimbBits;
            cg >>= LimbBits;

            cf += Math.BigMul(t.U, f.V1) + Math.BigMul(t.V, g.V1);
            cg += Math.BigMul(t.Q, f.V1) + Math.BigMul(t.R, g.V1);
            f.V0 = Low(cf) & LimbMask;
            g.V0 = Low(cg) & LimbMask;
            cf >>= LimbBits;
            cg >>= LimbBits;

            cf += Math.BigMul(t.U, f.V2) + Math.BigMul(t.V, g.V2);
            cg += Math.BigMul(t.Q, f.V2) + Math.BigMul(t.R, g.V2);
            f.V1 = Low(cf) & LimbMask;
            g.V1 = Low(cg) & LimbMask;
            cf >>= LimbBits;
            cg >>= LimbBits;

            cf += Math.BigMul(t.U, f.V3) + Math.BigMul(t.V, g.V3);
            cg += Math.BigMul(t.Q, f.V3) + Math.BigMul(t.R, g.V3);
            f.V2 = Low(cf) & LimbMask;
            g.V2 = Low(cg) & LimbMask;
            cf >>= LimbBits;
            cg >>= LimbBits;

            cf += Math.BigMul(t.U, f.V4) + Math.BigMul(t.V, g.V4);
            cg += Math.BigMul(t.Q, f.V4) + Math.BigMul(t.R, g.V4);
            f.V3 = Low(cf) & LimbMask;
            g.V3 = Low(cg) & LimbMask;
            cf >>= LimbBits;
            cg >>= LimbBits;

            f.V4 = Low(cf);
            g.V4 = Low(cg);
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

            Int128 cd = Math.BigMul(t.U, d.V0) + Math.BigMul(t.V, e.V0);
            Int128 ce = Math.BigMul(t.Q, d.V0) + Math.BigMul(t.R, e.V0);

            md -= (_inverse62 * Low(cd) + md) & LimbMask;
            me -= (_inverse62 * Low(ce) + me) & LimbMask;

            Signed62 p = _value;
            cd += Math.BigMul(md, p.V0);
            ce += Math.BigMul(me, p.V0);
            cd >>= LimbBits;
            ce >>= LimbBits;

            cd += Math.BigMul(t.U, d.V1) + Math.BigMul(t.V, e.V1) + Math.BigMul(md, p.V1);
            ce += Math.BigMul(t.Q, d.V1) + Math.BigMul(t.R, e.V1) + Math.BigMul(me, p.V1);
            d.V0 = Low(cd) & LimbMask;
            e.V0 = Low(ce) & LimbMask;
            cd >>= LimbBits;
            ce >>= LimbBits;

            cd += Math.BigMul(t.U, d.V2) + Math.BigMul(t.V, e.V2) + Math.BigMul(md, p.V2);
            ce += Math.BigMul(t.Q, d.V2) + Math.BigMul(t.R, e.V2) + Math.BigMul(me, p.V2);
            d.V1 = Low(cd) & LimbMask;
            e.V1 = Low(ce) & LimbMask;
            cd >>= LimbBits;
            ce >>= LimbBits;

            cd += Math.BigMul(t.U, d.V3) + Math.BigMul(t.V, e.V3) + Math.BigMul(md, p.V3);
            ce += Math.BigMul(t.Q, d.V3) + Math.BigMul(t.R, e.V3) + Math.BigMul(me, p.V3);
            d.V2 = Low(cd) & LimbMask;
            e.V2 = Low(ce) & LimbMask;
            cd >>= LimbBits;
            ce >>= LimbBits;

            cd += Math.BigMul(t.U, d.V4) + Math.BigMul(t.V, e.V4) + Math.BigMul(md, p.V4);
            ce += Math.BigMul(t.Q, d.V4) + Math.BigMul(t.R, e.V4) + Math.BigMul(me, p.V4);
            d.V3 = Low(cd) & LimbMask;
            e.V3 = Low(ce) & LimbMask;
            cd >>= LimbBits;
            ce >>= LimbBits;

            d.V4 = Low(cd);
            e.V4 = Low(ce);
        }
    }
}
