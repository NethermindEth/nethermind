/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Numerics;

namespace Nethermind.Core.Crypto.ZkSnarks
{
    /// <summary>
    ///     Based on https://eprint.iacr.org/2010/354.pdf
    ///     High-Speed Software Implementation of the Optimal Ate Pairing over Barreto–Naehrig Curves
    ///     and ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class Fp6 : IField<Fp6>
    {
        static Fp6()
        {
            FieldParams<Fp6>.Zero = Zero;
            FieldParams<Fp6>.One = One;
        }

        public static readonly Fp6 Zero = new Fp6(Fp2.Zero, Fp2.Zero, Fp2.Zero);
        public static readonly Fp6 One = new Fp6(Fp2.One, Fp2.Zero, Fp2.Zero);
        public static readonly Fp2 NonResidue = Fp2.NonResidue;

        public Fp2 A { get; }
        public Fp2 B { get; }
        public Fp2 C { get; }

        public Fp6(Fp2 a, Fp2 b, Fp2 c)
        {
            A = a;
            B = b;
            C = c;
        }

        /// <summary>
        /// https://eprint.iacr.org/2010/354.pdf
        /// Algorithm 10
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        public Fp6 Add(Fp6 o)
        {
            Fp2 a0 = A;
            Fp2 a1 = B;
            Fp2 a2 = C;
            Fp2 b0 = o.A;
            Fp2 b1 = o.B;
            Fp2 b2 = o.C;

            Fp2 c0 = a0 + b0;
            Fp2 c1 = a1 + b1;
            Fp2 c2 = a2 + b2;

            return new Fp6(c0, c1, c2);
        }

        /// <summary>
        /// https://eprint.iacr.org/2010/354.pdf
        /// Algorithm 14
        /// </summary>
        /// <param name="b0"></param>
        /// <returns></returns>
        public Fp6 Mul(Fp2 b0)
        {
            Fp2 a0 = A;
            Fp2 a1 = B;
            Fp2 a2 = C;
            
            return new Fp6(
                a0 * b0,
                a1 * b0,
                a2 * b0);
        }

        /// <summary>
        /// https://eprint.iacr.org/2010/354.pdf
        /// Algorithm 13 
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        public Fp6 Mul(Fp6 o)
        {
            Fp2 a0 = A;
            Fp2 a1 = B;
            Fp2 a2 = C;
            Fp2 b0 = o.A;
            Fp2 b1 = o.B;
            Fp2 b2 = o.C;

            Fp2 v0 = a0 * b0;
            Fp2 v1 = a1 * b1;
            Fp2 v2 = a2 * b2;

            Fp2 c0 = v0 + ((a1 + a2) * (b1 + b2) - v1 - v2).MulByNonResidue();
            Fp2 c1 = (a0 + a1) * (b0 + b1) - v0 - v1 + v2.MulByNonResidue();
            Fp2 c2 = (a0 + a2) * (b0 + b2) - v0 + v1 - v2;

            return new Fp6(c0, c1, c2);
        }

        /// <summary>
        /// https://eprint.iacr.org/2010/354.pdf
        /// Algorithm 11 
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        public Fp6 Sub(Fp6 o)
        {
            Fp2 a0 = A;
            Fp2 a1 = B;
            Fp2 a2 = C;
            Fp2 b0 = o.A;
            Fp2 b1 = o.B;
            Fp2 b2 = o.C;

            Fp2 c0 = a0 - b0;
            Fp2 c1 = a1 - b1;
            Fp2 c2 = a2 - b2;

            return new Fp6(c0, c1, c2);
        }

        /// <summary>
        /// https://eprint.iacr.org/2010/354.pdf
        /// Algorithm 16 
        /// </summary>
        /// <returns></returns>
        public Fp6 Squared()
        {
            Fp2 a0 = A;
            Fp2 a1 = B;
            Fp2 a2 = C;

            Fp2 c4 = (a0 * a1).Double();
            Fp2 c5 = a2.Squared();
            Fp2 c1 = c5.MulByNonResidue() + c4;
            Fp2 c2 = c4 - c5;
            Fp2 c3 = a0.Squared();
            c4 = a0 - a1 + a2;
            c5 = (a1 * a2).Double();
            c4 = c4.Squared();
            Fp2 c0 = c5.MulByNonResidue() + c3;
            c2 = c2 + c4 + c5 - c3;

            return new Fp6(c0, c1, c2);
        }

        public Fp6 Double()
        {
            return Add(this);
        }

        /// <summary>
        /// https://eprint.iacr.org/2010/354.pdf
        /// Algorithm 17
        /// </summary>
        /// <returns></returns>
        public Fp6 Inverse()
        {
            Fp2 a0 = A;
            Fp2 a1 = B;
            Fp2 a2 = C;

            Fp2 t0 = a0.Squared();
            Fp2 t1 = a1.Squared();
            Fp2 t2 = a2.Squared();
            Fp2 t3 = a0 * a1;
            Fp2 t4 = a0 * a2;
            Fp2 t5 = a1 * a2; // typo (a2 * a3 in paper)?
            Fp2 c0 = t0 - t5.MulByNonResidue();
            Fp2 c1 = t2.MulByNonResidue() - t3;
            Fp2 c2 = t1 - t4; // typo in paper referenced above. should be "-" as per Scott, but is "*"
            Fp2 t6 = a0 * c0;
            t6 = t6 + a2.MulByNonResidue() * c1;
            t6 = t6 + a1.MulByNonResidue() * c2;
            t6 = t6.Inverse();

            c0 = c0 * t6;
            c1 = c1 * t6;
            c2 = c2 * t6;

            return new Fp6(c0, c1, c2);
        }

        public Fp6 Negate()
        {
            return new Fp6(A.Negate(), B.Negate(), C.Negate());
        }

        public bool IsZero()
        {
            return Equals(Zero);
        }

        public bool IsValid()
        {
            return A.IsValid() && B.IsValid() && C.IsValid();
        }

        public Fp6 MulByNonResidue()
        {
            return new Fp6(NonResidue.Mul(C), A, B);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (!(obj is Fp6 other))
            {
                return false;
            }

            return Equals(other);
        }

        public bool Equals(Fp6 other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return Equals(A, other.A) && Equals(B, other.B) && Equals(C, other.C);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = (A != null ? A.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (B != null ? B.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (C != null ? C.GetHashCode() : 0);
                return hashCode;
            }
        }

        public Fp6 FrobeniusMap(int power)
        {
            Fp2 ra = A.FrobeniusMap(power);
            Fp2 rb = FrobeniusCoefficientsB[power % 6].Mul(B.FrobeniusMap(power));
            Fp2 rc = FrobeniusCoefficientsC[power % 6].Mul(C.FrobeniusMap(power));

            return new Fp6(ra, rb, rc);
        }

        // https://github.com/scipr-lab/libff/blob/master/libff/algebra/curves/alt_bn128/alt_bn128_init.cpp
        public static readonly Fp2[] FrobeniusCoefficientsB =
        {
            new Fp2(
                BigInteger.One,
                BigInteger.Zero),

            // xiToPMinus1Over3 is ξ^((p-1)/3) where ξ = i+9.
            new Fp2(
                BigInteger.Parse("21575463638280843010398324269430826099269044274347216827212613867836435027261"),
                BigInteger.Parse("10307601595873709700152284273816112264069230130616436755625194854815875713954")),

            // xiToPSquaredMinus1Over3 is ξ^((p²-1)/3) where ξ = i+9.
            new Fp2(
                BigInteger.Parse("21888242871839275220042445260109153167277707414472061641714758635765020556616"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("3772000881919853776433695186713858239009073593817195771773381919316419345261"),
                BigInteger.Parse("2236595495967245188281701248203181795121068902605861227855261137820944008926")),

            // xiTo2PSquaredMinus2Over3 is ξ^((2p²-2)/3) where ξ = i+9 (a cubic root of unity, mod p).
            new Fp2(
                BigInteger.Parse("2203960485148121921418603742825762020974279258880205651966"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("18429021223477853657660792034369865839114504446431234726392080002137598044644"),
                BigInteger.Parse("9344045779998320333812420223237981029506012124075525679208581902008406485703"))
        };

        // https://github.com/scipr-lab/libff/blob/master/libff/algebra/curves/alt_bn128/alt_bn128_init.cpp
        public static readonly Fp2[] FrobeniusCoefficientsC =
        {
            new Fp2(
                BigInteger.One,
                BigInteger.Zero),

            // xiTo2PMinus2Over3 is ξ^((2p-2)/3) where ξ = i+9.
            new Fp2(
                BigInteger.Parse("2581911344467009335267311115468803099551665605076196740867805258568234346338"),
                BigInteger.Parse("19937756971775647987995932169929341994314640652964949448313374472400716661030")),

            // xiTo2PSquaredMinus2Over3 is ξ^((2p²-2)/3) where ξ = i+9 (a cubic root of unity, mod p).
            new Fp2(
                BigInteger.Parse("2203960485148121921418603742825762020974279258880205651966"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("5324479202449903542726783395506214481928257762400643279780343368557297135718"),
                BigInteger.Parse("16208900380737693084919495127334387981393726419856888799917914180988844123039")),

            // xiToPSquaredMinus1Over3 is ξ^((p²-1)/3) where ξ = i+9.
            new Fp2(
                BigInteger.Parse("21888242871839275220042445260109153167277707414472061641714758635765020556616"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("13981852324922362344252311234282257507216387789820983642040889267519694726527"),
                BigInteger.Parse("7629828391165209371577384193250820201684255241773809077146787135900891633097"))
        };

        public override string ToString()
        {
            return $"({A}, {B}, {C})";
        }
    }
}