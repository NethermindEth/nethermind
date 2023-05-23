// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetAccountRangeMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            GetAccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                AccountRange = new(Keccak.OfAnEmptyString, new Keccak("0x15d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), new Keccak("0x20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")),
                ResponseBytes = 10
            };
            GetAccountRangeMessageSerializer serializer = new();

            var bytes = serializer.Serialize(msg);
            var deserializedMsg = serializer.Deserialize(bytes);

            Assert.That(deserializedMsg.RequestId, Is.EqualTo(msg.RequestId));
            Assert.That(deserializedMsg.PacketType, Is.EqualTo(msg.PacketType));
            Assert.That(deserializedMsg.AccountRange.RootHash, Is.EqualTo(msg.AccountRange.RootHash));
            Assert.That(deserializedMsg.AccountRange.StartingHash, Is.EqualTo(msg.AccountRange.StartingHash));
            Assert.That(deserializedMsg.AccountRange.LimitHash, Is.EqualTo(msg.AccountRange.LimitHash));
            Assert.That(deserializedMsg.ResponseBytes, Is.EqualTo(msg.ResponseBytes));

            SerializerTester.TestZero(serializer, msg);
        }
    }
}
