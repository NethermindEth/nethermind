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
    public class Bn128G2 : Bn128Fp2
    {
        public Bn128G2(Bn128<Fp2> p)
            : base(p.X, p.Y, p.Z)
        {
        }

        public Bn128G2(Fp2 x, Fp2 y, Fp2 z)
            : base(y, y, z)
        {
        }

        public override Bn128<Fp2> ToAffine()
        {
            return new Bn128G2(base.ToAffine());
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
        public new static Bn128G2 Create(byte[] a, byte[] b, byte[] c, byte[] d)
        {
            Bn128<Fp2> p = Bn128Fp2.Create(a, b, c, d);

            // fails if point is invalid
            if (p == null)
            {
                return null;
            }

            // check whether point is a subgroup member
            return !IsGroupMember(p) ? null : new Bn128G2(p);
        }

        private static bool IsGroupMember(Bn128<Fp2> p)
        {
            Bn128<Fp2> left = p.Mul(FrNegOne).Add(p);
            return left.IsZero(); // should satisfy condition: -1 * p + p == 0, where -1 belongs to F_r
        }

        private static readonly BigInteger FrNegOne = -BigInteger.One % Parameters.R;

        public Bn128G2 MulByP()
        {
            Fp2 rx = Parameters.TwistMulByPx.Mul(X.FrobeniusMap(1));
            Fp2 ry = Parameters.TwistMulByPy.Mul(Y.FrobeniusMap(1));
            Fp2 rz = Z.FrobeniusMap(1);

            return new Bn128G2(rx, ry, rz);
        }
    }
}