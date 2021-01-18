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

using System;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class NodeDataMessageSerializerTests
    {
        private static void Test(byte[][] data)
        {
            NodeDataMessage message = new NodeDataMessage(data);
            
            NodeDataMessageSerializer serializer = new NodeDataMessageSerializer();
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip()
        {
            byte[][] data = {TestItem.KeccakA.Bytes, TestItem.KeccakB.Bytes, TestItem.KeccakC.Bytes};
            Test(data);
        }
        
        [Test]
        public void Zero_roundtrip()
        {
            byte[][] data = {TestItem.KeccakA.Bytes, TestItem.KeccakB.Bytes, TestItem.KeccakC.Bytes};
            Test(data);
        }

        [Test]
        public void Roundtrip_with_null_top_level()
        {
            Test(null);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            byte[][] data = {TestItem.KeccakA.Bytes, Array.Empty<byte>(), TestItem.KeccakC.Bytes};
            Test(data);
        }
    }
}
