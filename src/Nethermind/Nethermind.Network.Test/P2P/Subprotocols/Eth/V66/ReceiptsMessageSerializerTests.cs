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

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class ReceiptsMessageSerializerTests
    {
        //modified test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void RoundTrip()
        {
            string rlp = "f90172820457f9016cf90169f901668001b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000f85ff85d940000000000000000000000000000000000000011f842a0000000000000000000000000000000000000000000000000000000000000deada0000000000000000000000000000000000000000000000000000000000000beef830100ff";
            byte[] bytes = Bytes.FromHexString(rlp);
            var serializer63 = new Network.P2P.Subprotocols.Eth.V63.Messages.ReceiptsMessageSerializer(MainnetSpecProvider.Instance);
            ReceiptsMessageSerializer serializer = new(serializer63);
            ReceiptsMessage deserializedMessage = serializer.Deserialize(bytes);
            byte[] serialized = serializer.Serialize(deserializedMessage);
            
            Assert.AreEqual(bytes,  serialized);
            Network.P2P.Subprotocols.Eth.V63.Messages.ReceiptsMessage ethMessage = deserializedMessage.EthMessage;
            
            TxReceipt txReceipt = ethMessage.TxReceipts[0][0];
            
            txReceipt.StatusCode.Should().Be(0);
            txReceipt.GasUsedTotal.Should().Be(1);
            txReceipt.Bloom.Should().Be(Bloom.Empty);
            
            txReceipt.Logs[0].LoggersAddress.Should().BeEquivalentTo(new Address("0x0000000000000000000000000000000000000011"));
            txReceipt.Logs[0].Topics[0].Should().BeEquivalentTo(new Keccak("0x000000000000000000000000000000000000000000000000000000000000dead"));
            txReceipt.Logs[0].Topics[1].Should().BeEquivalentTo(new Keccak("0x000000000000000000000000000000000000000000000000000000000000beef"));
            txReceipt.Logs[0].Data.Should().BeEquivalentTo(Bytes.FromHexString("0x0100ff"));
            txReceipt.BlockNumber.Should().Be(0x0);
            txReceipt.TxHash.Should().BeNull();
            txReceipt.BlockHash.Should().BeNull();
            txReceipt.Index.Should().Be(0x0);
            
            ReceiptsMessage message = new(1111, ethMessage);

            SerializerTester.TestZero(serializer, message, rlp);
        }
    }
}
