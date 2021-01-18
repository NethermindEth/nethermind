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

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetBlockHeadersMessageSerializerTests
    {
        [Test]
        public void Roundtrip_hash()
        {
            GetBlockHeadersMessage message = new GetBlockHeadersMessage();
            message.MaxHeaders = 1;
            message.Skip = 2;
            message.Reverse = 1;
            message.StartBlockHash = Keccak.OfAnEmptyString;
            GetBlockHeadersMessageSerializer serializer = new GetBlockHeadersMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("e4a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470010201");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");

            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.StartBlockHash, deserialized.StartBlockHash, $"{nameof(message.StartBlockHash)}");
            Assert.AreEqual(message.MaxHeaders, deserialized.MaxHeaders, $"{nameof(message.MaxHeaders)}");
            Assert.AreEqual(message.Reverse, deserialized.Reverse, $"{nameof(message.Reverse)}");
            Assert.AreEqual(message.Skip, deserialized.Skip, $"{nameof(message.Skip)}");
            
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip_number()
        {
            GetBlockHeadersMessage message = new GetBlockHeadersMessage();
            message.MaxHeaders = 1;
            message.Skip = 2;
            message.Reverse = 1;
            message.StartBlockNumber = 100;
            GetBlockHeadersMessageSerializer serializer = new GetBlockHeadersMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("c464010201");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");

            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.StartBlockNumber, deserialized.StartBlockNumber, $"{nameof(message.StartBlockNumber)}");
            Assert.AreEqual(message.MaxHeaders, deserialized.MaxHeaders, $"{nameof(message.MaxHeaders)}");
            Assert.AreEqual(message.Reverse, deserialized.Reverse, $"{nameof(message.Reverse)}");
            Assert.AreEqual(message.Skip, deserialized.Skip, $"{nameof(message.Skip)}");
            
            SerializerTester.TestZero(serializer, message);
        }
        
        [Test]
        public void Roundtrip_zero()
        {
            GetBlockHeadersMessage message = new GetBlockHeadersMessage();
            message.MaxHeaders = 1;
            message.Skip = 2;
            message.Reverse = 0;
            message.StartBlockNumber = 100;
            GetBlockHeadersMessageSerializer serializer = new GetBlockHeadersMessageSerializer();
            
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("c464010280");

            Assert.AreEqual(expectedBytes, bytes, "bytes");

            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.StartBlockNumber, deserialized.StartBlockNumber, $"{nameof(message.StartBlockNumber)}");
            Assert.AreEqual(message.MaxHeaders, deserialized.MaxHeaders, $"{nameof(message.MaxHeaders)}");
            Assert.AreEqual(message.Reverse, deserialized.Reverse, $"{nameof(message.Reverse)}");
            Assert.AreEqual(message.Skip, deserialized.Skip, $"{nameof(message.Skip)}");
            
            SerializerTester.TestZero(serializer, message);
        }
        
        [Test]
        public void To_string()
        {
            GetBlockHeadersMessage newBlockMessage = new GetBlockHeadersMessage();
            _ = newBlockMessage.ToString();
        }
    }
}
