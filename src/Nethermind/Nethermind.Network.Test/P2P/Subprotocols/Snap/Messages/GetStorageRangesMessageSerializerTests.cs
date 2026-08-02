// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Nethermind.State.Snap;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetStorageRangesMessageSerializerTests
    {
        [Test]
        public void Roundtrip_Many()
        {
            GetStorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                StorageRange = new()
                {
                    RootHash = TestItem.KeccakA,
                    Accounts = TestItem.Keccaks.Select(static k => new PathWithAccount(k, null)).ToPooledList(TestItem.Keccaks.Length),
                    StartingHash = new Hash256("0x15d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"),
                    LimitHash = new Hash256("0x20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")
                },
                ResponseBytes = 1000
            };

            GetStorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_Empty()
        {
            GetStorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                StorageRange = new()
                {
                    RootHash = Keccak.OfAnEmptyString,
                    Accounts = ArrayPoolList<PathWithAccount>.Empty(),
                    StartingHash = new Hash256("0x15d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"),
                    LimitHash = new Hash256("0x20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")
                },
                ResponseBytes = 1000
            };
            GetStorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Deserialize_throws_on_null_root_hash()
        {
            GetStorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                StorageRange = new()
                {
                    RootHash = null!,
                    Accounts = ArrayPoolList<PathWithAccount>.Empty(),
                    StartingHash = TestItem.KeccakB,
                    LimitHash = TestItem.KeccakC
                },
                ResponseBytes = 1000
            };
            GetStorageRangesMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(msg);

            Assert.That(() => serializer.Deserialize(serialized), Throws.TypeOf<RlpException>());
        }

        [Test]
        public void Roundtrip_defaults_normalizes_hash_bounds()
        {
            GetStorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                StorageRange = new()
                {
                    RootHash = TestItem.KeccakA,
                    Accounts = ArrayPoolList<PathWithAccount>.Empty()
                },
                ResponseBytes = 1000
            };
            GetStorageRangesMessageSerializer serializer = new();

            GetStorageRangeMessage deserialized = serializer.Deserialize(serializer.Serialize(msg));

            Assert.That(deserialized.StorageRange.StartingHash, Is.EqualTo(ValueKeccak.Zero));
            Assert.That(deserialized.StorageRange.LimitHash, Is.EqualTo(ValueKeccak.MaxValue));
        }

        [TestCase("account")]
        [TestCase("starting")]
        [TestCase("limit")]
        public void Deserialize_throws_on_null_required_hash(string fieldName)
        {
            byte[] serialized = EncodeMessageWithNullHash(fieldName);
            GetStorageRangesMessageSerializer serializer = new();

            Assert.That(() => serializer.Deserialize(serialized), Throws.TypeOf<RlpException>());
        }

        [Test]
        public void Deserialize_Throws_On_TooMany_Accounts()
        {
            GetStorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                StorageRange = new()
                {
                    RootHash = TestItem.KeccakA,
                    Accounts = Enumerable.Repeat(new PathWithAccount(TestItem.KeccakA, null), SnapMessageLimits.MaxRequestAccounts + 1).ToPooledList(SnapMessageLimits.MaxRequestAccounts + 1),
                    StartingHash = TestItem.KeccakB,
                    LimitHash = TestItem.KeccakC
                },
                ResponseBytes = 1000
            };

            GetStorageRangesMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(msg);

            Assert.Throws<RlpLimitException>(() => serializer.Deserialize(serialized));
        }

        private static byte[] EncodeMessageWithNullHash(string fieldName)
        {
            Hash256? accountPath = fieldName == "account" ? null : TestItem.KeccakA;
            ValueHash256? startingHash = ValueKeccak.Zero;
            ValueHash256? limitHash = ValueKeccak.MaxValue;

            if (fieldName == "starting")
            {
                startingHash = null;
            }
            else if (fieldName == "limit")
            {
                limitHash = null;
            }

            int accountsContentLength = Rlp.LengthOf(accountPath);
            int contentLength = Rlp.LengthOf(1L)
                + Rlp.LengthOf(TestItem.KeccakB)
                + Rlp.LengthOfSequence(accountsContentLength)
                + Rlp.LengthOf(startingHash)
                + Rlp.LengthOf(limitHash)
                + Rlp.LengthOf(1000L);
            byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
            RlpWriter writer = new(bytes);
            writer.StartSequence(contentLength);
            writer.Encode(1L);
            writer.Encode(TestItem.KeccakB);
            writer.StartSequence(accountsContentLength);
            writer.Encode(accountPath);
            WriteNullableValueHash(ref writer, startingHash);
            WriteNullableValueHash(ref writer, limitHash);
            writer.Encode(1000L);
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
