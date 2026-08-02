// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;
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

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_Defaults()
        {
            GetAccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                AccountRange = new(Keccak.OfAnEmptyString, Keccak.Zero)
            };
            GetAccountRangeMessageSerializer serializer = new();

            byte[] bytes = serializer.Serialize(msg);
            GetAccountRangeMessage deserializedMsg = serializer.Deserialize(bytes);

            Assert.That(deserializedMsg.AccountRange.LimitHash, Is.EqualTo(Keccak.MaxValue));
            Assert.That(deserializedMsg.ResponseBytes, Is.EqualTo(1000_000));

            SerializerTester.TestZero(serializer, msg);
        }

        [TestCase("root")]
        [TestCase("starting")]
        [TestCase("limit")]
        public void Deserialize_throws_on_null_required_hash(string fieldName)
        {
            byte[] bytes = EncodeMessageWithNullHash(fieldName);
            GetAccountRangeMessageSerializer serializer = new();

            Assert.That(() => serializer.Deserialize(bytes), Throws.TypeOf<RlpException>());
        }

        private static byte[] EncodeMessageWithNullHash(string fieldName)
        {
            Hash256? rootHash = fieldName == "root" ? null : Keccak.OfAnEmptyString;
            ValueHash256? startingHash = Keccak.Zero.ValueHash256;
            ValueHash256? limitHash = Keccak.MaxValue.ValueHash256;

            if (fieldName == "starting")
            {
                startingHash = null;
            }
            else if (fieldName == "limit")
            {
                limitHash = null;
            }

            int contentLength = Rlp.LengthOf(1L)
                + Rlp.LengthOf(rootHash)
                + Rlp.LengthOf(startingHash)
                + Rlp.LengthOf(limitHash)
                + Rlp.LengthOf(10L);
            byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
            RlpWriter writer = new(bytes);
            writer.StartSequence(contentLength);
            writer.Encode(1L);
            writer.Encode(rootHash);
            WriteNullableValueHash(ref writer, startingHash);
            WriteNullableValueHash(ref writer, limitHash);
            writer.Encode(10L);
            return bytes;
        }

        private static void WriteNullableValueHash(ref RlpWriter writer, ValueHash256? hash)
        {
            if (!hash.HasValue)
            {
                writer.EncodeEmptyByteArray();
            }
            else
            {
                writer.Encode(hash.Value);
            }
        }
    }
}
