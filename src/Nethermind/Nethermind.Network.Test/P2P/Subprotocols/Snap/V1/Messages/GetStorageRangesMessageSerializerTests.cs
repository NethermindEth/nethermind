// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Nethermind.State.Snap;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.V1.Messages
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
                    StartingHash = SnapSerializerGoldens.RangeStart,
                    LimitHash = SnapSerializerGoldens.RangeLimit
                },
                ResponseBytes = 1000
            };

            GetStorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_Empty_With_Null_LimitHash()
        {
            GetStorageRangeMessage msg = new()
            {
                RequestId = SnapSerializerGoldens.RequestId1111,
                StorageRange = new()
                {
                    RootHash = Keccak.OfAnEmptyString,
                    Accounts = ArrayPoolList<PathWithAccount>.Empty(),
                    StartingHash = SnapSerializerGoldens.RangeStart,
                    LimitHash = null
                },
                ResponseBytes = 1000
            };
            GetStorageRangesMessageSerializer serializer = new();

            // The message encodes as [requestId, rootHash, accountPaths, startingHash, limitHash, responseBytes].
            SerializerTester.TestZero(serializer, msg,
                "f84a" + SnapSerializerGoldens.RequestId1111Rlp +
                SnapSerializerGoldens.EmptyStringKeccakRlp +
                "c0" +
                SnapSerializerGoldens.RangeStartRlp +
                "80" +
                "8203e8");
        }

        [Test]
        public void Serialize_throws_on_null_root_hash()
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

            Assert.That(() => serializer.Serialize(msg), Throws.InvalidOperationException);
        }

        [Test]
        public void Roundtrip_preserves_null_hash_bounds()
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

            using (Assert.EnterMultipleScope())
            {
                Assert.That(deserialized.StorageRange.StartingHash, Is.Null);
                Assert.That(deserialized.StorageRange.LimitHash, Is.Null);
            }
        }

        [TestCase("root")]
        [TestCase("account")]
        [TestCase("account-list")]
        public void Deserialize_throws_on_null_required_hash(string fieldName)
        {
            byte[] serialized = EncodeMessageWithNullHash(fieldName);
            GetStorageRangesMessageSerializer serializer = new();

            Assert.That(() => serializer.Deserialize(serialized), Throws.InstanceOf<RlpException>());
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
            Hash256? rootHash = fieldName == "root" ? null : TestItem.KeccakB;
            Hash256? accountPath = fieldName == "account" ? null : TestItem.KeccakA;
            ValueHash256 startingHash = ValueKeccak.Zero;
            ValueHash256 limitHash = ValueKeccak.MaxValue;

            int accountsContentLength = fieldName == "account-list"
                ? Rlp.OfEmptyList.Length
                : Rlp.LengthOf(accountPath);
            int contentLength = Rlp.LengthOf(1L)
                + Rlp.LengthOf(rootHash)
                + Rlp.LengthOfSequence(accountsContentLength)
                + Rlp.LengthOf(startingHash)
                + Rlp.LengthOf(limitHash)
                + Rlp.LengthOf(1000L);
            byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
            RlpWriter writer = new(bytes);
            writer.StartSequence(contentLength);
            writer.Encode(1L);
            writer.Encode(rootHash);
            writer.StartSequence(accountsContentLength);
            if (fieldName == "account-list")
            {
                writer.Encode(Rlp.OfEmptyList);
            }
            else
            {
                writer.Encode(accountPath);
            }
            writer.Encode(startingHash);
            writer.Encode(limitHash);
            writer.Encode(1000L);
            return bytes;
        }
    }
}
