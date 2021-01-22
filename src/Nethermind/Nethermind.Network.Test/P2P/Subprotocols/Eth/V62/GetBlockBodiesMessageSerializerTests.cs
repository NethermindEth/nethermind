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
    public class GetBlockBodiesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            GetBlockBodiesMessageSerializer serializer = new GetBlockBodiesMessageSerializer();
            GetBlockBodiesMessage message = new GetBlockBodiesMessage(Keccak.OfAnEmptySequenceRlp, Keccak.Zero, Keccak.EmptyTreeHash);
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("f863a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347a00000000000000000000000000000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421");
            
            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");
            
            GetBlockBodiesMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.BlockHashes.Count, deserialized.BlockHashes.Count, $"count");
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                Assert.AreEqual(message.BlockHashes[i], deserialized.BlockHashes[i], $"hash {i}");
            }
            
            SerializerTester.TestZero(serializer, message);
        }
        
        [Test]
        public void To_string()
        {
            GetBlockBodiesMessage newBlockMessage = new GetBlockBodiesMessage();
            _ = newBlockMessage.ToString();
        }
    }
}
