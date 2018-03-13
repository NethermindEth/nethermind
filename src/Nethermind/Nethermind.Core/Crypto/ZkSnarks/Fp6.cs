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
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class Fp6 : Field<Fp6>
    {
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

        public override Fp6 Add(Fp6 o)
        {
            return new Fp6(A.Add(o.A), B.Add(o.B), C.Add(o.C));
        }

        public Fp6 Mul(Fp2 o)
        {
            return new Fp6(A.Mul(o), B.Mul(o), C.Mul(o));
        }
        
        public override Fp6 Mul(Fp6 o)
        {
            Fp2 a1 = A, b1 = B, c1 = B;
            Fp2 a2 = o.A, b2 = o.B, c2 = o.C;

            Fp2 a1A2 = a1.Mul(a2);
            Fp2 b1B2 = b1.Mul(b2);
            Fp2 c1C2 = c1.Mul(c2);

            Fp2 ra = a1A2.Add(b1.Add(c1).Mul(b2.Add(c2)).Sub(b1B2).Sub(c1C2).MulByNonResidue());
            Fp2 rb = a1.Add(b1).Mul(a2.Add(b2)).Sub(a1A2).Sub(b1B2).Add(c1C2.MulByNonResidue());
            Fp2 rc = a1.Add(c1).Mul(a2.Add(c2)).Sub(a1A2).Add(b1B2).Sub(c1C2);

            return new Fp6(ra, rb, rc);
        }

        public override Fp6 Sub(Fp6 o)
        {
            Fp2 ra = A.Sub(o.A);
            Fp2 rb = B.Sub(o.B);
            Fp2 rc = C.Sub(o.C);

            return new Fp6(ra, rb, rc);
        }

        public override Fp6 Square()
        {
            Fp2 s0 = A.Square();
            Fp2 ab = A.Mul(B);
            Fp2 s1 = ab.Double();
            Fp2 s2 = A.Sub(B).Add(C).Square();
            Fp2 bc = B.Mul(C);
            Fp2 s3 = bc.Double();
            Fp2 s4 = C.Square();

            Fp2 ra = s0.Add(s3.MulByNonResidue());
            Fp2 rb = s1.Add(s4.MulByNonResidue());
            Fp2 rc = s1.Add(s2).Add(s3).Sub(s0).Sub(s4);

            return new Fp6(ra, rb, rc);
        }

        public override Fp6 Double()
        {
            return Add(this);
        }

        public override Fp6 Inverse()
        {
            /* From "High-Speed Software Implementation of the Optimal Ate Pairing over Barreto-Naehrig Curves"; Algorithm 17 */

            Fp2 t0 = A.Square();
            Fp2 t1 = B.Square();
            Fp2 t2 = C.Square();
            Fp2 t3 = A.Mul(B);
            Fp2 t4 = A.Mul(C);
            Fp2 t5 = B.Mul(C);
            Fp2 c0 = t0.Sub(t5.MulByNonResidue());
            Fp2 c1 = t2.MulByNonResidue().Sub(t3);
            Fp2 c2 = t1.Sub(t4); // typo in paper referenced above. should be "-" as per Scott, but is "*"
            Fp2 t6 = A.Mul(c0).Add(C.Mul(c1).Add(B.Mul(c2)).MulByNonResidue()).Inverse();

            Fp2 ra = t6.Mul(c0);
            Fp2 rb = t6.Mul(c1);
            Fp2 rc = t6.Mul(c2);

            return new Fp6(ra, rb, rc);
        }

        public override Fp6 Negate()
        {
            return new Fp6(A.Negate(), B.Negate(), C.Negate());
        }

        public override bool IsZero()
        {
            return Equals(Zero);
        }

        public override bool IsValid()
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

        public override bool Equals(Fp6 other)
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

        public static bool operator ==(Fp6 a, Fp6 b)
        {
            if (ReferenceEquals(a, null) && !ReferenceEquals(b, null))
            {
                return false;
            }

            // ReSharper disable once PossibleNullReferenceException
            return a.Equals(b);
        }

        public static bool operator !=(Fp6 a, Fp6 b)
        {
            return !(a == b);
        }

        public Fp6 FrobeniusMap(int power) {

            Fp2 ra = A.FrobeniusMap(power);
            Fp2 rb = FrobeniusCoefficientsB[power % 6].Mul(B.FrobeniusMap(power));
            Fp2 rc = FrobeniusCoefficientsC[power % 6].Mul(C.FrobeniusMap(power));

            return new Fp6(ra, rb, rc);
        }
        
        public static readonly Fp2[] FrobeniusCoefficientsB =
        {
            new Fp2(
                BigInteger.One,
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("21575463638280843010398324269430826099269044274347216827212613867836435027261"),
                BigInteger.Parse("10307601595873709700152284273816112264069230130616436755625194854815875713954")),

            new Fp2(
                BigInteger.Parse("21888242871839275220042445260109153167277707414472061641714758635765020556616"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("3772000881919853776433695186713858239009073593817195771773381919316419345261"),
                BigInteger.Parse("2236595495967245188281701248203181795121068902605861227855261137820944008926")),

            new Fp2(
                BigInteger.Parse("2203960485148121921418603742825762020974279258880205651966"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("18429021223477853657660792034369865839114504446431234726392080002137598044644"),
                BigInteger.Parse("9344045779998320333812420223237981029506012124075525679208581902008406485703"))
        };

        public static readonly Fp2[] FrobeniusCoefficientsC =
        {
            new Fp2(
                BigInteger.One,
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("2581911344467009335267311115468803099551665605076196740867805258568234346338"),
                BigInteger.Parse("19937756971775647987995932169929341994314640652964949448313374472400716661030")),

            new Fp2(
                BigInteger.Parse("2203960485148121921418603742825762020974279258880205651966"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("5324479202449903542726783395506214481928257762400643279780343368557297135718"),
                BigInteger.Parse("16208900380737693084919495127334387981393726419856888799917914180988844123039")),

            new Fp2(
                BigInteger.Parse("21888242871839275220042445260109153167277707414472061641714758635765020556616"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("13981852324922362344252311234282257507216387789820983642040889267519694726527"),
                BigInteger.Parse("7629828391165209371577384193250820201684255241773809077146787135900891633097"))
        };
    }
}