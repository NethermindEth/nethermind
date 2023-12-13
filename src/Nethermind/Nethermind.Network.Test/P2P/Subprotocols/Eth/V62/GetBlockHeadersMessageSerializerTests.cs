// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetBlockHeadersMessageSerializerTests
    {
        [Test]
        public void Roundtrip_hash()
        {
            GetBlockHeadersMessage message = new();
            message.MaxHeaders = 1;
            message.Skip = 2;
            message.Reverse = 1;
            message.StartBlockHash = Keccak.OfAnEmptyString;
            GetBlockHeadersMessageSerializer serializer = new();
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("e4a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470010201");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");

            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
            Assert.That(deserialized.StartBlockHash, Is.EqualTo(message.StartBlockHash), $"{nameof(message.StartBlockHash)}");
            Assert.That(deserialized.MaxHeaders, Is.EqualTo(message.MaxHeaders), $"{nameof(message.MaxHeaders)}");
            Assert.That(deserialized.Reverse, Is.EqualTo(message.Reverse), $"{nameof(message.Reverse)}");
            Assert.That(deserialized.Skip, Is.EqualTo(message.Skip), $"{nameof(message.Skip)}");

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip_number()
        {
            GetBlockHeadersMessage message = new();
            message.MaxHeaders = 1;
            message.Skip = 2;
            message.Reverse = 1;
            message.StartBlockNumber = 100;
            GetBlockHeadersMessageSerializer serializer = new();
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("c464010201");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");

            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
            Assert.That(deserialized.StartBlockNumber, Is.EqualTo(message.StartBlockNumber), $"{nameof(message.StartBlockNumber)}");
            Assert.That(deserialized.MaxHeaders, Is.EqualTo(message.MaxHeaders), $"{nameof(message.MaxHeaders)}");
            Assert.That(deserialized.Reverse, Is.EqualTo(message.Reverse), $"{nameof(message.Reverse)}");
            Assert.That(deserialized.Skip, Is.EqualTo(message.Skip), $"{nameof(message.Skip)}");

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip_zero()
        {
            GetBlockHeadersMessage message = new();
            message.MaxHeaders = 1;
            message.Skip = 2;
            message.Reverse = 0;
            message.StartBlockNumber = 100;
            GetBlockHeadersMessageSerializer serializer = new();

            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("c464010280");

            Assert.That(bytes, Is.EqualTo(expectedBytes), "bytes");

            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
            Assert.That(deserialized.StartBlockNumber, Is.EqualTo(message.StartBlockNumber), $"{nameof(message.StartBlockNumber)}");
            Assert.That(deserialized.MaxHeaders, Is.EqualTo(message.MaxHeaders), $"{nameof(message.MaxHeaders)}");
            Assert.That(deserialized.Reverse, Is.EqualTo(message.Reverse), $"{nameof(message.Reverse)}");
            Assert.That(deserialized.Skip, Is.EqualTo(message.Skip), $"{nameof(message.Skip)}");

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void To_string()
        {
            GetBlockHeadersMessage newBlockMessage = new();
            _ = newBlockMessage.ToString();
        }
    }
}
