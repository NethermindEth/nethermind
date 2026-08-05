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
                AccountRange = new(Keccak.OfAnEmptyString, SnapSerializerGoldens.RangeStart, SnapSerializerGoldens.RangeLimit),
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
                "f867" + SnapSerializerGoldens.RequestId1111Rlp +
                SnapSerializerGoldens.EmptyStringKeccakRlp +
                SnapSerializerGoldens.RangeStartRlp +
                SnapSerializerGoldens.RangeLimitRlp +
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
                SnapSerializerGoldens.EmptyStringKeccakRlp +
                "a00000000000000000000000000000000000000000000000000000000000000000" +
                "a0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" +
                "830f4240");
        }
    }
}
