// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0.For full terms, see LICENSE in the project root.

using System.Runtime.CompilerServices;
using Nethermind.Int256;
using FE = Nethermind.Verkle.Fields.FpEElement.FpE;

namespace Nethermind.Verkle.Fields.FpEElement;

public readonly partial struct FpE
{
    public static void Inverse(in FE x, out FE z)
    {
        // Implements "Optimized Binary GCD for Modular Inversion"
        // https://github.com/pornin/bingcd/blob/main/doc/bingcd.pdf

        FE a = x;
        FE b = qElement;

        FE u = new(1);

        // Update factors: we get [u; v] ← [f₀ g₀; f₁ g₁] [u; v]
        // cᵢ = fᵢ + 2³¹ - 1 + 2³² * (gᵢ + 2³¹ - 1)

        // Saved update factors to reduce the number of field multiplications
        long pf0 = 0, pf1 = 0, pg0 = 0, pg1 = 0;

        FE v = Zero;

        // Since u,v are updated every other iteration, we must make sure we terminate after evenly many iterations
        // This also lets us get away with half as many updates to u,v
        // To make this constant-time-ish, replace the condition with i < invIterationsN
        uint i = 0;

        while ((i & 1) == 1 || !a.IsZero)
        {
            int n = Math.Max(a.BitLen(), b.BitLen());

            ulong aApprox = Approximate(a, n);
            ulong bApprox = Approximate(b, n);

            // f₀, g₀, f₁, g₁ = 1, 0, 0, 1
            long c0 = updateFactorIdentityMatrixRow0;
            long c1 = updateFactorIdentityMatrixRow1;

            for (int j = 0; j < approxLowBitsN; j++)
            {
                // -2ʲ < f₀, f₁ ≤ 2ʲ
                // |f₀| + |f₁| < 2ʲ⁺¹

                if ((aApprox & 1) == 0)
                    aApprox = aApprox / 2;
                else
                {
                    ulong borrow = 0;
                    SubtractWithBorrow(aApprox, bApprox, ref borrow, out ulong sx);
                    if (borrow == 1)
                    {
                        sx = bApprox - aApprox;
                        bApprox = aApprox;
                        (c0, c1) = (c1, c0);
                    }

                    aApprox = sx / 2;
                    c0 = c0 - c1;

                    // Now |f₀| < 2ʲ⁺¹ ≤ 2ʲ⁺¹ (only the weaker inequality is needed, strictly speaking)
                    // Started with f₀ > -2ʲ and f₁ ≤ 2ʲ, so f₀ - f₁ > -2ʲ⁺¹
                    // Invariants unchanged for f₁
                }

                c1 *= 2;
                // -2ʲ⁺¹ < f₁ ≤ 2ʲ⁺¹
                // So now |f₀| + |f₁| < 2ʲ⁺²
            }

            FE s = a;

            // from this point on c0 aliases for f0
            (c0, long g0) = UpdateFactorsDecompose(c0);
            ulong aHi = LinearCombNonModular(s, c0, b, g0, out a);

            if ((aHi & signBitSelector) != 0)
            {
                // if aHi < 0
                (c0, g0) = (-c0, -g0);
                aHi = NegL(a, aHi, out a);
            }


            // right-shift a by k-1 bits

            ulong t0 = (a.u0 >> approxLowBitsN) | (a.u1 << approxHighBitsN);
            ulong t1 = (a.u1 >> approxLowBitsN) | (a.u2 << approxHighBitsN);
            ulong t2 = (a.u2 >> approxLowBitsN) | (a.u3 << approxHighBitsN);
            ulong t3 = (a.u3 >> approxLowBitsN) | (aHi << approxHighBitsN);
            a = new FE(t0, t1, t2, t3);

            // from this point on c1 aliases for g0
            (long f1, c1) = UpdateFactorsDecompose(c1);
            ulong bHi = LinearCombNonModular(s, f1, b, c1, out b);
            if ((bHi & signBitSelector) != 0)
            {
                // if aHi < 0
                (f1, c1) = (-f1, -c1);
                bHi = NegL(b, bHi, out b);
            }

            // right-shift b by k-1 bits
            t0 = (b.u0 >> approxLowBitsN) | (b.u1 << approxHighBitsN);
            t1 = (b.u1 >> approxLowBitsN) | (b.u2 << approxHighBitsN);
            t2 = (b.u2 >> approxLowBitsN) | (b.u3 << approxHighBitsN);
            t3 = (b.u3 >> approxLowBitsN) | (bHi << approxHighBitsN);
            b = new FE(t0, t1, t2, t3);


            if ((i & 1) == 1)
            {
                // Combine current update factors with previously stored ones
                // [F₀, G₀; F₁, G₁] ← [f₀, g₀; f₁, g₁] [pf₀, pg₀; pf₁, pg₁], with capital letters denoting new combined values
                // We get |F₀| = | f₀pf₀ + g₀pf₁ | ≤ |f₀pf₀| + |g₀pf₁| = |f₀| |pf₀| + |g₀| |pf₁| ≤ 2ᵏ⁻¹|pf₀| + 2ᵏ⁻¹|pf₁|
                // = 2ᵏ⁻¹ (|pf₀| + |pf₁|) < 2ᵏ⁻¹ 2ᵏ = 2²ᵏ⁻¹
                // So |F₀| < 2²ᵏ⁻¹ meaning it fits in a 2k-bit signed register

                // c₀ aliases f₀, c₁ aliases g₁
                (c0, g0, f1, c1) = ((c0 * pf0) + (g0 * pf1),
                    (c0 * pg0) + (g0 * pg1),
                    (f1 * pf0) + (c1 * pf1),
                    (f1 * pg0) + (c1 * pg1));

                s = u;

                // 0 ≤ u, v < 2²⁵⁵
                // |F₀|, |G₀| < 2⁶³
                LinearComb(u, c0, v, g0, out u);
                // |F₁|, |G₁| < 2⁶³
                LinearComb(s, f1, v, c1, out v);
            }
            else
            {
                // Save update factors
                (pf0, pg0, pf1, pg1) = (c0, g0, f1, c1);
            }

            i++;
        }

        // For every iteration that we miss, v is not being multiplied by 2ᵏ⁻²
        const ulong pSq = (ulong)1 << (2 * (K - 1));
        a = new FE(pSq);
        // If the function is constant-time ish, this loop will not run (no need to take it out explicitly)
        while (i < invIterationsN)
        {
            // could optimize further with mul by word routine or by pre-computing a table since with k=26,
            // we would multiply by pSq up to 13times;
            // on x86, the assembly routine outperforms generic code for mul by word
            // on arm64, we may loose up to ~5% for 6 limbs
            MultiplyMod(v, a, out v);
            i += 2;
        }

        u = x; // for correctness check

        MultiplyMod(
            v,
            new FE(
                inversionCorrectionFactorWord0,
                inversionCorrectionFactorWord1,
                inversionCorrectionFactorWord2,
                inversionCorrectionFactorWord3
            ),
            out z
        );

        // correctness check
        MultiplyMod(u, z, out v);
        if (!v.IsOne && !u.IsZero) InverseExp(u, out z);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MulWNonModular(in FE x, long y, out FE res)
    {
        long m = y >> 63;
        ulong w = (ulong)((y ^ m) - m);

        U4 z = new();

        ulong c = Math.BigMul(x.u0, w, out z.u0);
        c = MAdd1(x.u1, w, c, out z.u1);
        c = MAdd1(x.u2, w, c, out z.u2);
        c = MAdd1(x.u3, w, c, out z.u3);
        Unsafe.SkipInit(out res);
        Unsafe.As<FE, U4>(ref res) = z;

        if (y < 0)
            c = NegL(res, c, out res);

        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NegL(in FE x, ulong xHi, out FE res)
    {
        ulong b = SubtractUnderflow(0, x, out res) ? 1UL : 0UL;
        SubtractWithBorrow(0, xHi, ref b, out xHi);
        return xHi;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (long, long) UpdateFactorsDecompose(long c)
    {
        c += updateFactorsConversionBias;
        const long low32BitsFilter = 0xFFFFFFFF;
        long f = (c & low32BitsFilter) - 0x7FFFFFFF;
        long g = ((c >> 32) & low32BitsFilter) - 0x7FFFFFFF;
        return (f, g);
    }

    private static void InverseExp(in FE x, out FE z)
    {
        UInt256 qMinusTwo = _modulus.Value - 2;
        Exp(x, qMinusTwo, out z);
    }

    private static ulong Approximate(in FE x, int nBits)
    {
        if (nBits <= 64) return x.u0;

        const ulong mask = ((ulong)1 << (Bytes - 1)) - 1;
        ulong lo = mask & x.u0;

        int hiWordIndex = (nBits - 1) / 64;


        int hiWordBitsAvailable = nBits - (hiWordIndex * 64);
        int hiWordBitsUsed = Math.Min(hiWordBitsAvailable, approxHighBitsN);

        ulong mask1 = CalculateMask(hiWordBitsAvailable - hiWordBitsUsed);
        ulong hi = (x[hiWordIndex] & mask1) << (64 - hiWordBitsAvailable);

        mask1 = CalculateMask(approxLowBitsN + hiWordBitsUsed);
        ulong mid = (mask1 & x[hiWordIndex - 1]) >> hiWordBitsUsed;

        return lo | mid | hi;
    }

    private static ulong CalculateMask(int shift)
    {
        if (shift > 63) return 0;
        return ~(((ulong)1 << shift) - 1);
    }

    private static ulong LinearCombNonModular(in FE x, long xC, in FE y, long yC, out FE res)
    {
        ulong yHi = MulWNonModular(y, yC, out FE yTimes);
        ulong xHi = MulWNonModular(x, xC, out res);

        ulong carry = AddOverflow(res, yTimes, out res) ? 1UL : 0UL;

        AddWithCarry(xHi, yHi, ref carry, out yHi);

        return yHi;
    }

    private static void LinearComb(in FE x, long xC, in FE y, long yC, out FE res)
    {
        // | (hi, z) | < 2 * 2⁶³ * 2²⁵⁵ = 2³¹⁹
        // therefore | hi | < 2⁶³ ≤ 2⁶³
        ulong hi = LinearCombNonModular(x, xC, y, yC, out res);
        MontReducedSigned(res, hi, out res);
    }

    // montReduceSigned z = (xHi * r + x) * r⁻¹ using the SOS algorithm
    // Requires |xHi| < 2⁶³. Most significant bit of xHi is the sign bit.
    private static void MontReducedSigned(in FE x, ulong xHi, out FE res)
    {
        const ulong signBitRemover = ~signBitSelector;
        bool mustNeg = (xHi & signBitSelector) != 0;

        // the SOS implementation requires that most significant bit is 0
        // Let X be xHi*r + x
        // If X is negative we would have initially stored it as 2⁶⁴ r + X (à la 2's complement)
        xHi &= signBitRemover;
        // with this a negative X is now represented as 2⁶³ r + X

        U7 t = new() { u0 = 0 };

        ulong m = x.u0 * QInvNeg;

        ulong c = MAdd0(m, Q0, x.u0);
        c = MAdd2(m, Q1, x.u1, c, out t.u1);
        c = MAdd2(m, Q2, x.u2, c, out t.u2);
        c = MAdd2(m, Q3, x.u3, c, out t.u3);

        // m * qElement.u3 ≤ (2⁶⁴ - 1) * (2⁶³ - 1) = 2¹²⁷ - 2⁶⁴ - 2⁶³ + 1
        // x.u3 + C ≤ 2*(2⁶⁴ - 1) = 2⁶⁵ - 2
        // On LHS, (C, t.u3) ≤ 2¹²⁷ - 2⁶⁴ - 2⁶³ + 1 + 2⁶⁵ - 2 = 2¹²⁷ + 2⁶³ - 1
        // So on LHS, C ≤ 2⁶³
        t.u4 = xHi + c;
        // xHi + C < 2⁶³ + 2⁶³ = 2⁶⁴


        U4 z = new();
        // <standard SOS>

        m = t.u1 * QInvNeg;

        c = MAdd0(m, Q0, t.u1);
        c = MAdd2(m, Q1, t.u2, c, out t.u2);
        c = MAdd2(m, Q2, t.u3, c, out t.u3);
        c = MAdd2(m, Q3, t.u4, c, out t.u4);

        t.u5 += c;

        m = t.u2 * QInvNeg;

        c = MAdd0(m, Q0, t.u2);
        c = MAdd2(m, Q1, t.u3, c, out t.u3);
        c = MAdd2(m, Q2, t.u4, c, out t.u4);
        c = MAdd2(m, Q3, t.u5, c, out t.u5);

        t.u6 += c;

        m = t.u3 * QInvNeg;

        c = MAdd0(m, Q0, t.u3);
        c = MAdd2(m, Q1, t.u4, c, out z.u0);
        c = MAdd2(m, Q2, t.u5, c, out z.u1);
        z.u3 = MAdd2(m, Q3, t.u6, c, out z.u2);


        Unsafe.SkipInit(out res);
        Unsafe.As<FE, U4>(ref res) = z;

        if (!LessThan(res, qElement)) SubtractUnderflow(res, qElement, out res);
        // </standard SOS>

        if (mustNeg)
        {
            // We have computed ( 2⁶³ r + X ) r⁻¹ = 2⁶³ + X r⁻¹ instead
            // Occurs iff x == 0 && xHi < 0, i.e. X = rX' for -2⁶³ ≤ X' < 0

            if (SubtractUnderflow(res, new FE(signBitSelector), out res))
            {
                // z.u3 = -1
                // negative: add q
                const ulong neg1 = 0xFFFFFFFFFFFFFFFF;
                AddOverflow(new FE(res.u0, res.u1, res.u2, neg1), qElement, out res);
            }
        }
    }
}
