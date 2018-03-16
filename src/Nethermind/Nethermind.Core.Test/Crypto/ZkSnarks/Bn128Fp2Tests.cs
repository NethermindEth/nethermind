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

using Nethermind.Core.Crypto.ZkSnarks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto.ZkSnarks
{
    [TestFixture]
    public class Bn128Fp2Tests
    {
        [Test]
        public void Equals_works_with_nulls()
        {
            Bn128Fp2 bn128Fp2 = new Bn128Fp2(Fp2.One, Fp2.One, Fp2.One);
            Assert.False(bn128Fp2 == null, "null to the right");
            Assert.False(null == bn128Fp2, "null to the left");
            Assert.True((Bn128Fp2)null == null, "null both sides");
        }

        [Test]
        public void Zero_initializes()
        {   
            Bn128Fp2 p1 = Bn128Fp2.Create(new byte[] {0}, new byte[] {0}, new byte[] {0}, new byte[] {0});
            Assert.NotNull(p1.Zero);
        }
        
        [Test]
        public void Zero_reused()
        {   
            Bn128Fp2 p1 = Bn128Fp2.Create(new byte[] {0}, new byte[] {0}, new byte[] {0}, new byte[] {0});
            // ReSharper disable once EqualExpressionComparison
            Assert.True(ReferenceEquals(p1.Zero, p1.Zero));
        }
    }
}