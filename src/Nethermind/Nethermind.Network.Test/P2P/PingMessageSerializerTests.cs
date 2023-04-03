// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PingMessageSerializerTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            PingMessage msg = PingMessage.Instance;
            PingMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(msg);
            Assert.AreEqual(0xc0, serialized[0]);
            PingMessage deserialized = serializer.Deserialize(serialized);
            Assert.NotNull(deserialized);
        }
    }
}
