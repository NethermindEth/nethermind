// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class DisconnectMessageSerializerTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            using DisconnectMessage msg = new(EthDisconnectReason.AlreadyConnected);
            DisconnectMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(msg);
            Assert.That(serialized.ToHexString(true), Is.EqualTo("0xc105"), "bytes");
            using DisconnectMessage deserialized = serializer.Deserialize(serialized);
            Assert.That(deserialized.Reason, Is.EqualTo(msg.Reason), "reason");
        }

        [Test]
        public void Can_read_single_byte_message([Values] bool offset)
        {
            DisconnectMessageSerializer serializer = new();
            using DisposableByteBuffer serialized = Unpooled.Buffer(2).AsDisposable();
            if (offset) serialized.WriteByte(0xab);
            serialized.WriteByte(16);
            if (offset) serialized.SkipBytes(1);
            using DisconnectMessage deserialized = serializer.Deserialize(serialized);
            using (Assert.EnterMultipleScope())
            {
                Assert.That((EthDisconnectReason)deserialized.Reason, Is.EqualTo(EthDisconnectReason.Other), "reason");
                Assert.That(serialized.ReadableBytes, Is.Zero);
            }
        }

        [TestCase("", EthDisconnectReason.DisconnectRequested)]
        [TestCase("00", EthDisconnectReason.DisconnectRequested)]
        [TestCase("10", EthDisconnectReason.Other)]
        [TestCase("80", EthDisconnectReason.DisconnectRequested)]
        [TestCase("8000", EthDisconnectReason.DisconnectRequested)]
        [TestCase("c0", EthDisconnectReason.DisconnectRequested)]
        [TestCase("81c0", EthDisconnectReason.DisconnectRequested)]
        [TestCase("c180", EthDisconnectReason.DisconnectRequested)]
        [TestCase("c104", EthDisconnectReason.TooManyPeers)]
        [TestCase("82c104", EthDisconnectReason.TooManyPeers)]
        [TestCase("c10400", EthDisconnectReason.TooManyPeers)]
        [TestCase("c20405", EthDisconnectReason.TooManyPeers)]
        [TestCase("82c10400", EthDisconnectReason.TooManyPeers)]
        [TestCase("83c10400", EthDisconnectReason.TooManyPeers)]
        public void Can_read_other_format_message(string hex, EthDisconnectReason expectedReason)
        {
            DisconnectMessageSerializer serializer = new();
            byte[] serialized = Bytes.FromHexString(hex);
            using DisconnectMessage deserialized = serializer.Deserialize(serialized);
            Assert.That(deserialized.Reason, Is.EqualTo((int)expectedReason));
        }

        [Test]
        public void Malformed_disconnect_payload_has_a_protocol_error(
            [Values("820102", "c1", "c2", "c280", "c281", "c18180", "c2c180", "c50000000000", "f8", "f838", "b8", "b838")] string hex)
        {
            DisconnectMessageSerializer serializer = new();
            Assert.That(() =>
            {
                using DisconnectMessage message = serializer.Deserialize(Bytes.FromHexString(hex));
            }, Throws.InstanceOf<RlpException>());
        }
    }
}
