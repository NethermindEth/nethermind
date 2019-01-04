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
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BigIntegerTests
    {
        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(2, 1)]
        [TestCase(4, 2)]
        [TestCase(1000, 31)]
        public void Square_root(int n, int expectedValue)
        {
            Assert.AreEqual(new BigInteger(expectedValue), new BigInteger(n).SquareRoot());
        }
        
        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(3, 2)]
        [TestCase(4, 3)]
        [TestCase(1024, 11)]
        public void Bit_length_is_calculated_properly(int value, int expectedBitLength)
        {
            Assert.AreEqual(expectedBitLength, new BigInteger(value).BitLength());
        }

        [TestCase(0, 0, false)]
        [TestCase(1, 0, true)]
        [TestCase(2, 0, false)]
        [TestCase(2, 1, true)]
        [TestCase(2, 2, false)]
        [TestCase(1024, 10, true)]
        public void Test_bit_is_calculated_properly(int value, int bitIndex, bool expectedResult)
        {
            Assert.AreEqual(expectedResult, new BigInteger(value).TestBit(bitIndex));
        }
        
        [Test]
        public void Test_bit_regression()
        {
            Assert.AreEqual(true, BigInteger.Pow(2, 128).TestBit(128), "128");
            Assert.AreEqual(false, BigInteger.Pow(2, 128).TestBit(95), "95");
            Assert.AreEqual(false, BigInteger.Pow(2, 128).TestBit(0), "0");
        }
        
        [Test]
        public void Bit_length_one_more_test()
        {
            Assert.AreEqual(65, BigInteger.Parse("29793968203157093288").BitLength());
        }
        
        [TestCase(1, 11, 1)]
        [TestCase(2, 11, 6)]
        [TestCase(3, 11, 4)]
        [TestCase(4, 11, 3)]
        [TestCase(5, 11, 9)]
        [TestCase(6, 11, 2)]
        [TestCase(7, 11, 8)]
        [TestCase(8, 11, 7)]
        [TestCase(9, 11, 5)]
        [TestCase(10, 11, 10)]
        [TestCase(0, 11, 0)]
        public void Mod_inverse_test(int a, int mod, int expected)
        {
            Assert.AreEqual(new BigInteger(expected), new BigInteger(a).ModInverse(mod));
        }
    }
}