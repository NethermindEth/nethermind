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

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Les;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class GetBlockHeadersMessageSerializerTests
    {
        [Test]
        public void RoundTripWithHash()

        {
            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.GetBlockHeadersMessage();
            ethMessage.StartingBlockHash = Keccak.Compute("1");
            ethMessage.MaxHeaders = 10;
            ethMessage.Skip = 2;
            ethMessage.Reverse = 0;

            var message = new GetBlockHeadersMessage(ethMessage, 2);

            GetBlockHeadersMessageSerializer serializer = new GetBlockHeadersMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("e602e4a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc60a0280");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");

            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);

            Assert.AreEqual(message.RequestId, deserialized.RequestId);
            Assert.AreEqual(message.EthMessage.StartingBlockHash, deserialized.EthMessage.StartingBlockHash);
            Assert.AreEqual(message.EthMessage.MaxHeaders, deserialized.EthMessage.MaxHeaders);
            Assert.AreEqual(message.EthMessage.Skip, deserialized.EthMessage.Skip);
            Assert.AreEqual(message.EthMessage.Reverse, deserialized.EthMessage.Reverse);
        }
        [Test]
        public void RoundTripWithNumber()
        {
            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.GetBlockHeadersMessage();
            ethMessage.StartingBlockNumber = 1;
            ethMessage.MaxHeaders = 10;
            ethMessage.Skip = 2;
            ethMessage.Reverse = 0;

            var message = new GetBlockHeadersMessage(ethMessage, 2);

            GetBlockHeadersMessageSerializer serializer = new GetBlockHeadersMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("c602c4010a0280");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");

            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);

            Assert.AreEqual(message.RequestId, deserialized.RequestId);
            Assert.AreEqual(message.EthMessage.StartingBlockHash, deserialized.EthMessage.StartingBlockHash);
            Assert.AreEqual(message.EthMessage.MaxHeaders, deserialized.EthMessage.MaxHeaders);
            Assert.AreEqual(message.EthMessage.Skip, deserialized.EthMessage.Skip);
            Assert.AreEqual(message.EthMessage.Reverse, deserialized.EthMessage.Reverse);
        }
    }
}
