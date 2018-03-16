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
using Nethermind.Core.Crypto.ZkSnarks;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto.ZkSnarks
{
    [TestFixture]
    public class ParametersTests
    {
        [Test]
        public void P_is_fine()
        {
            Assert.NotNull(Parameters.P);
        }
        
        [Test]
        public void R_is_fine()
        {
            Assert.NotNull(Parameters.R);
        }
        
        [Test]
        public void FpB_is_fine()
        {
            Assert.NotNull(Parameters.FpB);
        }
        
        [Test]
        public void Fp2B_is_fine()
        {
            Assert.NotNull(Parameters.Fp2B);
        }
        
        [Test]
        public void Twist_is_fine()
        {
            Assert.NotNull(Parameters.Twist);
        }
        
        [Test]
        public void PairingFinalExponentZ_is_fine()
        {
            Assert.NotNull(Parameters.PairingFinalExponentZ);
        }
        
        [Test]
        public void TwistMulByPx_is_ine()
        {
            Assert.NotNull(Parameters.TwistMulByPx);
        }
        
        [Test]
        public void TwistMulByPy_is_fine()
        {
            Assert.NotNull(Parameters.TwistMulByPy);
        }
        
                /// <summary>
        /// The Tate pairing is only of interest if it is calculated on a “pairing-friendly” elliptic curve.
        /// This pairing-friendliness entails that r | (p^k − 1) for some reasonably small value of k,
        /// that is, the rth roots of unity in Fp are contained in Fp^k , the codomain of the pairing.
        /// </summary>
        [Test]
        public void R_divides_P_to_k_minus_one_k_is_mebedding_degree_of_Gt()
        {
            Assert.AreEqual(BigInteger.Zero, (BigInteger.Pow(Parameters.P, Parameters.k) - 1) % Parameters.R);
            
            // k is embedding degree iff k is the smallest n such that r | (p^n − 1) 
            for (int k = 1; k < Parameters.k; k++)
            {
                Assert.AreNotEqual(BigInteger.Zero, (BigInteger.Pow(Parameters.P, k) - 1) % Parameters.R, $"{k}");    
            }
        }

        [Test]
        public void R_check()
        {
            // r(t) = 36t^4 + 36t^3 + 18t^2 + 6t + 1;
            Assert.AreEqual(BigInteger.Zero, Parameters.R - (36 * BigInteger.Pow(Parameters.u, 4) + 36 * BigInteger.Pow(Parameters.u, 3) + 18 * BigInteger.Pow(Parameters.u, 2) + 6 * Parameters.u + 1), "value correct assuming u correct");
            Assert.True(Parameters.R.IsProbablePrime(1), "is prime");
        }
        
        [Test]
        public void P_check()
        {
            // p(t) = 36t^4 + 36t^3 + 24t^2 + 6t + 1;
            Assert.AreEqual(BigInteger.Zero, Parameters.P - (36 * BigInteger.Pow(Parameters.u, 4) + 36 * BigInteger.Pow(Parameters.u, 3) + 24 * BigInteger.Pow(Parameters.u, 2) + 6 * Parameters.u + 1), "value correct assuming u correct");
            Assert.True(Parameters.P.IsProbablePrime(1), "is prime");
        }
        
        [Test]
        public void Frobenius_trace_check()
        {
            // tr(t) = 6t^2 + 1,
            Assert.AreEqual(BigInteger.Zero, Parameters.FrobeniusTrace - (6 * BigInteger.Pow(Parameters.u, 2) + 1), "value correct assuming u correct");
        }
        
        [Test]
        public void Hasse_bound()
        {
            // u <= 2 * sqrt(p(u))
            Assert.LessOrEqual(Parameters.u, 2 * Parameters.P.SquareRoot());
        }
        
        /// <summary>
        /// It is at 254 so probably similar to: https://crypto.stackexchange.com/questions/22331/isnt-the-security-of-ec-curve-25519-126-bits
        /// </summary>
        [Test]
        [Ignore("This one is failing. See the comment.")]
        public void Security_check()
        {
            // log2(r(t)) >= 256
            Assert.GreaterOrEqual(Parameters.R.BitLength(), 256, "log2(r(T)) >= 256");
            
            // 3000 <= k * log2(p(t)) <= 5000
            Assert.GreaterOrEqual(Parameters.k * Parameters.P.BitLength(), 3000, "3000 <= k * log2(p(t))");
            Assert.LessOrEqual(Parameters.k * Parameters.P.BitLength(), 5000, "k * log2(p(t)) <= 5000");
        }
        
        /// <summary>
        /// Probably somebody copied the value of v = 1868033 from 'New software speed records for cryptographic pairings' and included in the comment without checking (which was later copied to geth, Clearmatics, ethereumJ and so on)
        /// v^3 is not 4965661367192848881, still 4965661367192848881 is accepted as an arbitrary number where P = p(t) and R = r(t) are prime.
        /// </summary>
        [Test]
        [Ignore("This one is failing. See the comment.")]
        public void T_check()
        {         
            BigInteger v = 1868033;
            BigInteger v3 = BigInteger.Pow(v, 3);
            Console.WriteLine($"v^3 = {v3} != {Parameters.u} = u");
            Assert.AreEqual(BigInteger.Zero, Parameters.u - v3);
        }
    }
}