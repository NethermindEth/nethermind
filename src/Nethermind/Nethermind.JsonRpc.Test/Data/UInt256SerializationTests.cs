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

using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [TestFixture]
    public class UInt256SerializationTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip()
        {
            TestSerialization((UInt256) 123456789, (a, b) => a.Equals(b));
        }
        
        [Test]
        public void Can_do_roundtrip_big()
        {
            TestSerialization(UInt256.Parse("1321312414124781461278412647816487146817246816418746187246187468714681"), (a, b) => a.Equals(b));
        }
    }
}