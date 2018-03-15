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
    ///     also based on the paper linked below:
    /// https://eprint.iacr.org/2006/471.pdf
    /// We construct a quadratic extension as Fp2 = Fp[X]/(X2 − β), where β is a
    /// quadratic non-residue in Fp. An element α ∈ Fp2 is represented as α0 + α1X,
    /// where αi ∈ Fp
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
        
        public override Fp2 Sub(Fp2 o)
        {
            return new Fp2(A.Sub(o.A), B.Sub(o.B));
        }

        /// <summary>
        /// The Schoolbook method computes the product c = ab as
        /// c0 = a0b0 + βa1b1
        /// c1 = a0b1 + a1b0,
        /// which costs 4M + 2A + B
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        internal Fp2 MulSchoolbook(Fp2 o)
        {
            Fp a0 = A;
            Fp a1 = B;
            Fp b0 = o.A;
            Fp b1 = o.B;

            Fp c0 = a0 * b0 + Fp.NonResidue * a1 * b1;
            Fp c1 = a0 * b1 + a1 * b0;
            return new Fp2(c0, c1);
        }

        /// <summary>
        /// https://eprint.iacr.org/2006/471.pdf
        /// The Karatsuba method computes c = ab by first precomputing the values
        /// v0 = a0b0
        /// v1 = a1b1
        /// Then the multiplication is performed as
        /// c0 = v0 + βv1
        /// c1 = (a0 + a1)(b0 + b1) − v0 − v1
        /// which costs 3M + 5A + B
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        public Fp2 MulKaratsuba(Fp2 o)
        {
            Fp a0 = A;
            Fp a1 = B;
            Fp b0 = o.A;
            Fp b1 = o.B;

            Fp v0 = a0 * b0;
            Fp v1 = a1 * b1;

            Fp c0 = v0 + v1 * Fp.NonResidue;
            Fp c1 = (a0 + a1) * (b0 + b1) - v0 - v1;

            return new Fp2(c0, c1);
        }

        public override Fp2 Mul(Fp2 o)
        {
            return MulKaratsuba(o);
        }

        /// <summary>
        /// c0 = a0^2 + βa1^2
        /// c1 = 2a0a1
        /// M + 2S + 2A + B
        /// </summary>
        /// <returns></returns>
        public Fp2 SquaredSchoolbook()
        {
            Fp a0 = A;
            Fp a1 = B;

            Fp c0 = a0.Squared() + Fp.NonResidue * a1.Squared();
            Fp c1 = (a0 * a1).Double();

            return new Fp2(c0, c1);
        }

        /// <summary>
        /// v0 = a0^2, v1 = a1^2
        /// c0 = v0 + βv1
        /// c1 = (a0 + a1)^2 - v0 - v1
        /// which costs 3S + 4A + B.
        /// </summary>
        /// <returns></returns>
        public Fp2 SquaredKaratsuba()
        {
            Fp a0 = A;
            Fp a1 = B;

            Fp v0 = a0.Squared();
            Fp v1 = a1.Squared();

            Fp c0 = v0 + Fp.NonResidue * v1;
            Fp c1 = (a0 + a1).Squared() - v0 - v1;
            
            return new Fp2(c0, c1);
        }

        /// <summary>
        /// c0 = (a0 + a1)(a0 − a1)
        /// c1 = 2a0a1
        /// which takes 2M + 4A + 2B
        /// </summary>
        /// <returns></returns>
        public Fp2 SquaredComplex()
        {
            Fp a0 = A;
            Fp a1 = B;

            Fp v0 = a0 * a1;

            // using Complex squaring
            Fp c0 = (a0 + a1) * (a0 + Fp.NonResidue * a1) - v0 - Fp.NonResidue * v0;
            Fp c1 = v0.Double();

            return new Fp2(c0, c1);
        }
        
        public override Fp2 Squared()
        {
            return SquaredComplex();
        }

        public override Fp2 Double()
        {
            return Add(this);
        }

        public override Fp2 Inverse()
        {
            Fp t0 = A.Squared();
            Fp t1 = B.Squared();
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
            return a?.Equals(b) ?? ReferenceEquals(b, null);
        }

        public static bool operator !=(Fp2 a, Fp2 b)
        {
            return !(a == b);
        }

        public static Fp2 operator +(Fp2 a, Fp2 b)
        {
            return a.Add(b);
        }

        public static Fp2 operator -(Fp2 a, Fp2 b)
        {
            return a.Sub(b);
        }

        public static Fp2 operator *(Fp2 a, Fp2 b)
        {
            return a.Mul(b);
        }
        
        public static Fp2 operator -(Fp2 a)
        {
            return a.Negate();
        }

        public override string ToString()
        {
            return $"({A}, {B})";
        }
    }
}