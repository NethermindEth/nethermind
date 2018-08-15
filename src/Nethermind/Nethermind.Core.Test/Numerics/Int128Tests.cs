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
using Nethermind.Core.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Numerics
{
    [TestFixture]
    public class Int128Tests
    {
        [Test]
        public void Add_0_0()
        {
            Assert.AreEqual(Int128.Zero, Int128.Zero + Int128.Zero);
        }

        [Test]
        public void Add_0_1()
        {
            Assert.AreEqual(Int128.One, Int128.Zero + Int128.One);
        }

        [Test]
        public void Add_1_0()
        {
            Assert.AreEqual(Int128.One, Int128.One + Int128.Zero);
        }

        [Test]
        public void Add_0_minus1()
        {
            Assert.AreEqual(Int128.MinusOne, Int128.Zero + Int128.MinusOne);
        }

        [Test]
        public void Add_minus1_0()
        {
            Assert.AreEqual(Int128.MinusOne, Int128.MinusOne + Int128.Zero);
        }

        [Test]
        public void Add_max_values()
        {
            Assert.AreEqual(new Int128(-2), Int128.MaxValue + Int128.MaxValue);
        }
        
        [Test]
        public void Add_max_int64()
        {
            Assert.AreEqual(2 * new Int128(Int64.MaxValue), new Int128(Int64.MaxValue) + new Int128(Int64.MaxValue));
        }
        
        [Test]
        public void Square_int64_max()
        {
            Assert.AreEqual(Int128.Square(new Int128(Int64.MaxValue)), new Int128(Int64.MaxValue) * new Int128(Int64.MaxValue));
        }
        
        [Test]
        public void Subtract_0_0()
        {
            Assert.AreEqual(Int128.Zero, Int128.Zero + Int128.Zero);
        }

        [Test]
        public void Subtract_0_1()
        {
            Assert.AreEqual(Int128.One, Int128.Zero + Int128.One);
        }

        [Test]
        public void Subtract_1_0()
        {
            Assert.AreEqual(Int128.One, Int128.One + Int128.Zero);
        }

        [Test]
        public void Subtract_0_minus1()
        {
            Assert.AreEqual(Int128.MinusOne, Int128.Zero + Int128.MinusOne);
        }

        [Test]
        public void Subtract_minus1_0()
        {
            Assert.AreEqual(Int128.MinusOne, Int128.MinusOne + Int128.Zero);
        }
    }
}