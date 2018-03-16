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
    public class Parameters
    {
        /// <summary>
        /// P is a prime over which we form a basic field: 36u⁴+36u³+24u²+6u+1.
        /// </summary>
        public static readonly BigInteger P = BigInteger.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208583");
        
        // ReSharper disable once InconsistentNaming
        public static readonly BigInteger u = BigInteger.Parse("4965661367192848881");
        
        // ReSharper disable once InconsistentNaming
        public static readonly BigInteger FrobeniusTrace = BigInteger.Parse("147946756881789318990833708069417712967");

        /// <summary>
        /// Order is the number of elements in both G₁ and G₂: 36u⁴+36u³+18u²+6u+1.
        /// R order of the BN128G2 cyclic subgroup
        /// </summary>
        public static readonly BigInteger R = BigInteger.Parse("21888242871839275222246405745257275088548364400416034343698204186575808495617");
        
        /// <summary>
        /// embedding degree for group Gt (the smallest k so that r | p^k - 1)
        /// </summary>
        // ReSharper disable once InconsistentNaming
        public static readonly int k = 12;

        /// <summary>
        /// 'b' curve parameter for <see cref="Bn128Fp"/>
        /// </summary>
        public static readonly Fp FpB = new Fp(3);

        /// <summary>
        /// Twist parameter for the curves
        /// </summary>
        public static readonly Fp2 Twist = new Fp2(9, 1);

        /// <summary>
        /// 'b' curve parameter for <see cref="Bn128Fp2"/>
        /// </summary>
        public static readonly Fp2 Fp2B = FpB.Mul(Twist.Inverse());

        public static readonly Fp2 TwistMulByPx = new Fp2(
            BigInteger.Parse("21575463638280843010398324269430826099269044274347216827212613867836435027261"),
            BigInteger.Parse("10307601595873709700152284273816112264069230130616436755625194854815875713954"));

        public static readonly Fp2 TwistMulByPy = new Fp2(
            BigInteger.Parse("2821565182194536844548159561693502659359617185244120367078079554186484126554"),
            BigInteger.Parse("3505843767911556378687030309984248845540243509899259641013678093033130930403")
        );

        /// <summary>
        /// t so that p(t) and r(t) are prime
        /// </summary>
        public static readonly BigInteger PairingFinalExponentZ = BigInteger.Parse("4965661367192848881");
    }
}