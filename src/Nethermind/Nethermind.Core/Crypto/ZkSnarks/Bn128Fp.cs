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

namespace Nethermind.Core.Crypto.ZkSnarks
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class Bn128Fp : Bn128<Fp>
    {
        // the point at infinity
        public override Bn128<Fp> Zero { get; } = StaticZero;

        private static readonly Bn128Fp StaticZero = new Bn128Fp(Fp.Zero, Fp.Zero, Fp.Zero);

        protected Bn128Fp(Fp x, Fp y, Fp z)
            : base(x, y, z)
        {
        }

        protected override Bn128<Fp> New(Fp x, Fp y, Fp z)
        {
            return new Bn128Fp(x, y, z);
        }

        protected override Fp B { get; } = Parameters.FpB;
        protected override Fp One { get; } = Fp.One;

        /// <summary>
        /// Checks whether x and y belong to Fp,
        /// then checks whether point with (x; y) coordinates lays on the curve.
        /// Returns new point if all checks have been passed,
        /// otherwise returns null
        /// </summary>
        /// <param name="xx"></param>
        /// <param name="yy"></param>
        /// <returns></returns>
        public static Bn128<Fp> Create(byte[] xx, byte[] yy)
        {
            Fp x = new Fp(xx);
            Fp y = new Fp(yy);

            // check for point at infinity
            if (x.IsZero() && y.IsZero())
            {
                return StaticZero;
            }

            Bn128Fp p = new Bn128Fp(x, y, Fp.One);

            // check whether point is a valid one
            return p.IsValid() ? p : null;
        }
    }
}