//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NUnit.Framework;

namespace Nethermind.Core2.Test.Types
{
    [TestFixture]
    public class Bytes32Tests
    {
        [Test]
        public void Zero_is_zero()
        {
            Bytes32 a = new Bytes32(new byte[32]);
            Assert.AreEqual(a, Bytes32.Zero);
        }
        
        [Test]
        public void Same_is_same()
        {
            byte[] bytesA = new byte[32];
            new Random(42).NextBytes(bytesA);
            byte[] bytesB = new byte[32];
            bytesA.AsSpan().CopyTo(bytesB);
            Bytes32 a = new Bytes32(bytesA);
            Bytes32 b = new Bytes32(bytesB);
            Assert.AreEqual(a, b);
            Assert.True(a.Equals(b));
            Assert.True(b.Equals(a));
            Assert.True(a == b);
            Assert.True(!(a != b));
            Assert.True(a.Equals((object)b));
            Assert.True(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.AreEqual(a.ToString(), b.ToString());
        }
        
        [Test]
        public void Xor()
        {
            var random = new Random(42);
            byte[] bytesA = new byte[32];
            random.NextBytes(bytesA);
            byte[] bytesB = new byte[32];
            random.NextBytes(bytesB);
            Bytes32 a = new Bytes32(bytesA);
            Bytes32 b = new Bytes32(bytesB);
            var c = a.Xor(b);
            Assert.AreEqual("0xf1c1929d1dc3cae03774ee8a65a8b65408dcaad4585185ebd4662e36ac2354c8", c.ToString());
        }
        
        [Test]
        public void Diff_is_not_same()
        {
            byte[] bytesA = new byte[32];
            new Random(42).NextBytes(bytesA);
            byte[] bytesB = new byte[32];
            Bytes32 a = new Bytes32(bytesA);
            Bytes32 b = new Bytes32(bytesB);
            Assert.AreNotEqual(a, b);
            Assert.False(a.Equals(b));
            Assert.False(b.Equals(a));
            Assert.False(a == b);
            Assert.False(!(a != b));
            Assert.False(a.Equals((object)b));
            Assert.False(a.Equals(b));
            Assert.AreNotEqual(a.GetHashCode(), b.GetHashCode());
            Assert.AreNotEqual(a.ToString(), b.ToString());
        }
        
        [Test]
        public void Same_before_and_after()
        {
            byte[] bytesA = new byte[32];
            new Random(42).NextBytes(bytesA);
            Bytes32 a = new Bytes32(bytesA);
            Assert.AreEqual(bytesA.ToHexString(true), a.ToString());
        }
    }
}