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
    ///     also based on the paper linked below:
    /// https://eprint.iacr.org/2006/471.pdf
    /// We construct a quadratic extension as Fp2 = Fp[X]/(X2 − β), where β is a
    /// quadratic non-residue in Fp. An element α ∈ Fp2 is represented as α0 + α1X,
    /// where αi ∈ Fp
    /// </summary>
    public class Fp2 : QuadraticExtension<Fp, Fp2>
    {
        static Fp2()
        {
            FieldParams<Fp2>.Zero = Zero;
            FieldParams<Fp2>.One = One;
        }

        public static Fp2 Zero = new Fp2(Fp.Zero, Fp.Zero);
        public static Fp2 One = new Fp2(Fp.One, Fp.Zero);
        public static Fp2 NonResidue = new Fp2(9, 1);

        // https://github.com/scipr-lab/libff/blob/master/libff/algebra/curves/alt_bn128/alt_bn128_init.cpp
        public static readonly Fp[] FrobeniusCoefficientsB = new Fp[]
        {
            new Fp(BigInteger.One),
            new Fp(BigInteger.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208582"))
        };

        public Fp2(byte[] aa, byte[] bb)
            : this(new Fp(aa), new Fp(bb))
        {
        }

        public Fp2(Fp a, Fp b)
        {
            A = a;
            B = b;
        }

        public Fp2()
        {
        }

        public Fp2 FrobeniusMap(int power)
        {
            Fp ra = A;
            Fp rb = FrobeniusCoefficientsB[power % 2].Mul(B);

            return new Fp2(ra, rb);
        }

        public override Fp2 MulByNonResidue()
        {
            return NonResidue.Mul(this);
        }

        public static Fp2 operator +(Fp2 a, Fp2 b)
        {
            return a.Add(b);
        }
        
        public static Fp2 operator *(Fp2 a, Fp2 b)
        {
            return a.Mul(b);
        }
        
        public static Fp2 operator -(Fp2 a, Fp2 b)
        {
            return a.Sub(b);
        }
        
        public static Fp2 operator -(Fp2 a)
        {
            return a.Negate();
        }
    }
}