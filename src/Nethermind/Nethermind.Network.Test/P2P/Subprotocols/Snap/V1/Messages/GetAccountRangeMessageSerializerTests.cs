// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.V1.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetAccountRangeMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            GetAccountRangeMessage msg = new()
            {
                RequestId = 1111,
                AccountRange = new(Keccak.OfAnEmptyString, new Hash256("0x15d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), new Hash256("0x20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")),
                ResponseBytes = 10
            };
            GetAccountRangeMessageSerializer serializer = new();

            byte[] bytes = serializer.Serialize(msg);
            GetAccountRangeMessage deserializedMsg = serializer.Deserialize(bytes);

            Assert.That(deserializedMsg.RequestId, Is.EqualTo(msg.RequestId));
            Assert.That(deserializedMsg.PacketType, Is.EqualTo(msg.PacketType));
            Assert.That(deserializedMsg.AccountRange.RootHash, Is.EqualTo(msg.AccountRange.RootHash));
            Assert.That(deserializedMsg.AccountRange.StartingHash, Is.EqualTo(msg.AccountRange.StartingHash));
            Assert.That(deserializedMsg.AccountRange.LimitHash, Is.EqualTo(msg.AccountRange.LimitHash));
            Assert.That(deserializedMsg.ResponseBytes, Is.EqualTo(msg.ResponseBytes));

            // The message encodes as [requestId, rootHash, startingHash, limitHash, responseBytes].
            SerializerTester.TestZero(serializer, msg,
                "f867" + "820457" +
                "a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470" +
                "a015d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470" +
                "a020d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470" +
                "0a");
        }

        [Test]
        public void Roundtrip_Defaults()
        {
            GetAccountRangeMessage msg = new()
            {
                // long.MaxValue also pins the eight-byte request-id encoding.
                RequestId = long.MaxValue,
                AccountRange = new(Keccak.OfAnEmptyString, Keccak.Zero)
            };
            GetAccountRangeMessageSerializer serializer = new();

            byte[] bytes = serializer.Serialize(msg);
            GetAccountRangeMessage deserializedMsg = serializer.Deserialize(bytes);

            Assert.That(deserializedMsg.AccountRange.LimitHash, Is.EqualTo(Keccak.MaxValue));
            Assert.That(deserializedMsg.ResponseBytes, Is.EqualTo(1000_000));

            // A null limit hash goes on the wire as Keccak.MaxValue; response bytes 0 as 1000000.
            SerializerTester.TestZero(serializer, msg,
                "f870" + "887fffffffffffffff" +
                "a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470" +
                "a00000000000000000000000000000000000000000000000000000000000000000" +
                "a0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" +
                "830f4240");
        }
    }
}
