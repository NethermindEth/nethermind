// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
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
            DisconnectMessage msg = new(EthDisconnectReason.AlreadyConnected);
            DisconnectMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(msg);
            Assert.That(serialized.ToHexString(true), Is.EqualTo("0xc105"), "bytes");
            DisconnectMessage deserialized = serializer.Deserialize(serialized);
            Assert.That(deserialized.Reason, Is.EqualTo(msg.Reason), "reason");
        }

        [Test]
        public void Can_read_single_byte_message()
        {
            DisconnectMessageSerializer serializer = new();
            byte[] serialized = new byte[] { 16 };
            DisconnectMessage deserialized = serializer.Deserialize(serialized);
            Assert.That((EthDisconnectReason)deserialized.Reason, Is.EqualTo(EthDisconnectReason.Other), "reason");
        }

        [TestCase("", EthDisconnectReason.DisconnectRequested)]
        [TestCase("00", EthDisconnectReason.DisconnectRequested)]
        [TestCase("10", EthDisconnectReason.Other)]
        [TestCase("82c104", EthDisconnectReason.TooManyPeers)]
        public void Can_read_other_format_message(string hex, EthDisconnectReason expectedReason)
        {
            DisconnectMessageSerializer serializer = new DisconnectMessageSerializer();
            byte[] serialized = Bytes.FromHexString(hex);
            DisconnectMessage deserialized = serializer.Deserialize(serialized);
            deserialized.Reason.Should().Be((int)expectedReason);
        }
    }
}
