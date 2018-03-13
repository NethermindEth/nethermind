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

using System;
using System.Numerics;

namespace Nethermind.Core.Crypto.ZkSnarks
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class Fp2 : Field<Fp2>
    {
        public static readonly Fp[] FrobeniusCoefficientsB = new Fp[]
        {
            new Fp(BigInteger.One),
            new Fp(BigInteger.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208582"))
        };

        public static readonly Fp2 Zero = new Fp2(Fp.Zero, Fp.Zero);
        public static readonly Fp2 One = new Fp2(Fp.One, Fp.Zero);
        public static readonly Fp2 NonResidue = new Fp2(9, 1);

        public Fp A { get; }
        public Fp B { get; }

        public Fp2(byte[] aa, byte[] bb)
            : this(new Fp(aa), new Fp(bb))
        {
        }

        public Fp2(Fp a, Fp b)
        {
            A = a;
            B = b;
        }

        public Fp2 MulByNonResidue()
        {
            return NonResidue.Mul(this);
        }

        public Fp2 FrobeniusMap(int power)
        {
            Fp ra = A;
            Fp rb = FrobeniusCoefficientsB[power % 2].Mul(B);

            return new Fp2(ra, rb);
        }

        public override Fp2 Add(Fp2 o)
        {
            return new Fp2(A.Add(o.A), B.Add(o.B));
        }

        public override Fp2 Mul(Fp2 o)
        {
            Fp aa = A.Mul(o.A);
            Fp bb = B.Mul(o.B);

            Fp ra = bb.Mul(Fp.NonResidue).Add(aa); // ra = a1 * a2 + NON_RESIDUE * b1 * b2
            Fp rb = A.Add(B).Mul(o.A.Add(o.B)).Sub(aa).Sub(bb); // rb = (a1 + b1)(a2 + b2) - a1 * a2 - b1 * b2

            return new Fp2(ra, rb);
        }

        public override Fp2 Sub(Fp2 o)
        {
            return new Fp2(A.Sub(o.A), B.Sub(o.B));
        }

        public override Fp2 Square()
        {
            // using Complex squaring

            Fp ab = A.Mul(B);

            Fp ra = A.Add(B).Mul(B.Mul(Fp.NonResidue).Add(A))
                .Sub(ab).Sub(ab.Mul(Fp.NonResidue)); // ra = (a + b)(a + NON_RESIDUE * b) - ab - NON_RESIDUE * b
            Fp rb = ab.Double();

            return new Fp2(ra, rb);
        }

        public override Fp2 Double()
        {
            return Add(this);
        }

        public override Fp2 Inverse()
        {
            Fp t0 = A.Square();
            Fp t1 = B.Square();
            Fp t2 = t0.Sub(Fp.NonResidue.Mul(t1));
            Fp t3 = t2.Inverse();

            Fp ra = A.Mul(t3); // ra = a * t3
            Fp rb = B.Mul(t3).Negate(); // rb = -(b * t3)

            return new Fp2(ra, rb);
        }

        public override Fp2 Negate()
        {
            return new Fp2(A.Negate(), B.Negate());
        }

        public override bool IsZero()
        {
            return Equals(Zero);
        }

        public override bool IsValid()
        {
            return A.IsValid() && B.IsValid();
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (!(obj is Fp2 other))
            {
                return false;
            }

            return Equals(other);
        }

        public override bool Equals(Fp2 other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }
            
            return Equals(A, other.A) && Equals(B, other.B);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((A != null ? A.GetHashCode() : 0) * 397) ^ (B != null ? B.GetHashCode() : 0);
            }
        }

        public static bool operator ==(Fp2 a, Fp2 b)
        {
            if (ReferenceEquals(a, null) && !ReferenceEquals(b, null))
            {
                return false;
            }

            // ReSharper disable once PossibleNullReferenceException
            return a.Equals(b);
        }

        public static bool operator !=(Fp2 a, Fp2 b)
        {
            return !(a == b);
        }
    }
}