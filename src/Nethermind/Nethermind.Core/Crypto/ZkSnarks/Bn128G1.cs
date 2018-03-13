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
    public class Bn128G1 : Bn128Fp
    {
        public Bn128G1(Bn128<Fp> p)
            : base(p.X, p.Y, p.Z)
        {
        }

        public override Bn128<Fp> ToAffine()
        {
            return new Bn128G1(base.ToAffine());
        }
        
        /// <summary>
        /// Checks whether point is a member of subgroup,
        /// returns a point if check has been passed and null otherwise
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public new static Bn128G1 Create(byte[] x, byte[] y)
        {
            Bn128<Fp> p = Bn128Fp.Create(x, y);

            if (p == null) return null;

            if (!IsGroupMember(p)) return null;

            return new Bn128G1(p);
        }

        /// <summary>
        /// Formally we have to do this check
        /// but in our domain it's not necessary,
        /// thus always return true
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        private static bool IsGroupMember(Bn128<Fp> p)
        {
            return true;
        }
    }
}