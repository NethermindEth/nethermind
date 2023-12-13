// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PongMessageSerializerTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            PongMessage msg = PongMessage.Instance;
            PongMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(msg);
            Assert.That(serialized[0], Is.EqualTo(0xc0));
            PongMessage deserialized = serializer.Deserialize(serialized);
            Assert.NotNull(deserialized);
        }
    }
}
