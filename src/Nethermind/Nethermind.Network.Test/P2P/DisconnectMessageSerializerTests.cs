// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            DisconnectMessage msg = new(DisconnectReason.AlreadyConnected);
            DisconnectMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(msg);
            Assert.AreEqual("0xc105", serialized.ToHexString(true), "bytes");
            DisconnectMessage deserialized = serializer.Deserialize(serialized);
            Assert.AreEqual(msg.Reason, deserialized.Reason, "reason");
        }

        [Test]
        public void Can_read_single_byte_message()
        {
            DisconnectMessageSerializer serializer = new();
            byte[] serialized = new byte[] { 16 };
            DisconnectMessage deserialized = serializer.Deserialize(serialized);
            Assert.AreEqual(DisconnectReason.Other, (DisconnectReason)deserialized.Reason, "reason");
        }

        // does this format happen more often?
        //        [Test]
        //        public void Can_read_other_format_message()
        //        {
        //            DisconnectMessageSerializer serializer = new DisconnectMessageSerializer();
        //            byte[] serialized = Bytes.FromHexString("0204c108");
        //            DisconnectMessage deserialized = serializer.Deserialize(serialized);
        //            Assert.AreEqual(DisconnectReason.Other, (DisconnectReason)deserialized.Reason, "reason");
        //        }
    }
}
