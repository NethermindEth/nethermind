// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetBlockBodiesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            GetBlockBodiesMessageSerializer serializer = new();
            GetBlockBodiesMessage message = new(Keccak.OfAnEmptySequenceRlp, Keccak.Zero, Keccak.EmptyTreeHash);
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("f863a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347a00000000000000000000000000000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");

            GetBlockBodiesMessage deserialized = serializer.Deserialize(bytes);
            Assert.That(deserialized.BlockHashes.Count, Is.EqualTo(message.BlockHashes.Count), $"count");
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                Assert.That(deserialized.BlockHashes[i], Is.EqualTo(message.BlockHashes[i]), $"hash {i}");
            }

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void To_string()
        {
            GetBlockBodiesMessage newBlockMessage = new();
            _ = newBlockMessage.ToString();
        }
    }
}
