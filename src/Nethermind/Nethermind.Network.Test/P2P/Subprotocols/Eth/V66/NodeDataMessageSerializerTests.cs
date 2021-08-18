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
// 

using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class NodeDataMessageSerializerTests
    {
        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void Roundtrip()
        {
            byte[][] data = {new byte[]{0xde, 0xad, 0xc0, 0xde}, new byte[]{0xfe, 0xed, 0xbe, 0xef}};
            var ethMessage = new Network.P2P.Subprotocols.Eth.V63.NodeDataMessage(data);

            NodeDataMessage message = new NodeDataMessage(1111, ethMessage);
            
            NodeDataMessageSerializer serializer = new NodeDataMessageSerializer();
            
            SerializerTester.TestZero(serializer, message, "ce820457ca84deadc0de84feedbeef");
        }
    }
}
