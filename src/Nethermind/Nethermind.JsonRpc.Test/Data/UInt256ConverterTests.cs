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

using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class UInt256ConverterTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip()
        {
            TestRoundtrip((UInt256) 123456789);
        }
        
        [Test]
        public void Can_do_roundtrip_big()
        {
            TestRoundtrip(UInt256.Parse("1321312414124781461278412647816487146817246816418746187246187468714681"));
        }
    }
}
