//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class Keccak512Tests
    {
        [Test]
        public void Empty_string()
        {
            string result = Keccak512.Compute(string.Empty).ToString();
            Assert.AreEqual(Keccak512.OfAnEmptyString.ToString(), result);
        }

        [Test]
        public void Null_string()
        {
            string result = Keccak512.Compute((string)null).ToString();
            Assert.AreEqual(Keccak512.OfAnEmptyString.ToString(), result);
        }

        [Test]
        public void Null_bytes()
        {
            string result = Keccak512.Compute((byte[])null).ToString();
            Assert.AreEqual(Keccak512.OfAnEmptyString.ToString(), result);
        }

        [Test]
        public void Zero()
        {
            string result = Keccak512.Zero.ToString();
            Assert.AreEqual("0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", result);
        }
    }
}
