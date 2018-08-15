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

using Nethermind.Core.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Numerics
{
    [TestFixture]
    public class UInt128Tests
    {
        [Test]
        public void Add_0_0()
        {
            Assert.AreEqual(UInt128.Zero, UInt128.Zero + UInt128.Zero);
        }
        
        [Test]
        public void Add_0_1()
        {
            Assert.AreEqual(UInt128.One, UInt128.Zero + UInt128.One);
        }
        
        [Test]
        public void Add_1_0()
        {
            Assert.AreEqual(UInt128.One, UInt128.One + UInt128.Zero);
        }
        
        [Test]
        public void Add_max_values()
        {
            Assert.AreEqual(UInt128.MaxValue - 1, UInt128.MaxValue + UInt128.MaxValue);
        }
    }
}