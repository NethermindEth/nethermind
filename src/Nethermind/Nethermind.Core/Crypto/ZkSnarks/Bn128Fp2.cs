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
    public class Bn128Fp2 : Bn128<Fp2, Bn128Fp2>
    {
        public static readonly Bn128Fp2 StaticZero = new Bn128Fp2(Fp2.Zero, Fp2.Zero, Fp2.Zero);

        // the point at infinity
        public override Bn128Fp2 Zero => StaticZero;

        public Bn128Fp2(Fp2 x, Fp2 y, Fp2 z)
            : base(x, y, z)
        {
        }

        public override Bn128Fp2 New(Fp2 x, Fp2 y, Fp2 z)
        {
            return new Bn128Fp2(x, y, z);
        }

        public override Fp2 B { get; } = Parameters.Fp2B;

        public override Fp2 One { get; } = Fp2.One;

        protected Bn128Fp2(BigInteger a, BigInteger b, BigInteger c, BigInteger d)
            : base(new Fp2(a, b), new Fp2(c, d), Fp2.One)
        {
        }
       
        /// <summary>
        /// Checks whether provided data are coordinates of a point on the curve,
        /// then checks if this point is a member of subgroup of order "r"
        /// and if checks have been passed it returns a point, otherwise returns null
        /// </summary>
        /// <param name="aa"></param>
        /// <param name="bb"></param>
        /// <param name="cc"></param>
        /// <param name="dd"></param>
        /// <returns></returns>
        public static Bn128Fp2 Create(byte[] aa, byte[] bb, byte[] cc, byte[] dd)
        {
            Fp2 x = new Fp2(aa, bb);
            Fp2 y = new Fp2(cc, dd);

            // check for point at infinity
            if (x.IsZero() && y.IsZero())
            {
                return StaticZero;
            }

            Bn128Fp2 p = new Bn128Fp2(x, y, Fp2.One);

            // check whether point is a valid one
            return p.IsValid() ? p : null;
        }
        
        /// <summary>
        /// Checks whether provided data are coordinates of a point belonging to subgroup,
        /// if check has been passed it returns a point, otherwise returns null
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="c"></param>
        /// <param name="d"></param>
        /// <returns></returns>
        public static Bn128Fp2 CreateInG2(byte[] a, byte[] b, byte[] c, byte[] d)
        {
            Bn128Fp2 p = Create(a, b, c, d);

            // fails if point is invalid
            if (p == null)
            {
                return null;
            }

            // check whether point is a subgroup member
            return !IsInG2(p) ? null : p;
        }
        
        public static bool IsInG2(Bn128Fp2 p)
        {
            Bn128Fp2 left = p.Mul(FrNegOne).Add(p);
            return left.IsZero(); // should satisfy condition: -1 * p + p == 0, where -1 belongs to F_r
        }

        private static readonly BigInteger FrNegOne = -BigInteger.One + Parameters.R;

        public Bn128Fp2 MulByP()
        {
            Fp2 rx = Parameters.TwistMulByPx.Mul(X.FrobeniusMap(1));
            Fp2 ry = Parameters.TwistMulByPy.Mul(Y.FrobeniusMap(1));
            Fp2 rz = Z.FrobeniusMap(1);

            return new Bn128Fp2(rx, ry, rz);
        }
        
        public override string ToString()
        {
            return $"({X}, {Y})";
        }
    }
}