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
    public class Bn128Fp : Bn128<Fp, Bn128Fp>
    {
        public static readonly Bn128Fp StaticZero = new Bn128Fp(Fp.Zero, Fp.Zero, Fp.Zero);
        
        // the point at infinity
        public override Bn128Fp Zero => StaticZero;

        public Bn128Fp(Fp x, Fp y, Fp z)
            : base(x, y, z)
        {
        }

        public override Bn128Fp New(Fp x, Fp y, Fp z)
        {
            return new Bn128Fp(x, y, z);
        }

        public override Fp B { get; } = Parameters.FpB;
        public override Fp One { get; } = Fp.One;

        /// <summary>
        /// Checks whether x and y belong to Fp,
        /// then checks whether point with (x; y) coordinates lays on the curve.
        /// Returns new point if all checks have been passed,
        /// otherwise returns null
        /// </summary>
        /// <param name="xx"></param>
        /// <param name="yy"></param>
        /// <returns></returns>
        public static Bn128Fp Create(byte[] xx, byte[] yy)
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
        
        /// <summary>
        /// Checks whether point is a member of subgroup,
        /// returns a point if check has been passed and null otherwise
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public static Bn128Fp CreateInG1(byte[] x, byte[] y)
        {
            Bn128Fp p = Create(x, y);
            if (p == null)
            {
                return null;
            }

            return !IsInG1(p) ? null : p;
        }

        /// <summary>
        /// Formally we have to do this check
        /// but in our domain it's not necessary,
        /// thus always return true
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        public static bool IsInG1(Bn128Fp p)
        {
            return true;
        }

        public override string ToString()
        {
            return $"({X}, {Y})";
        }
    }
}