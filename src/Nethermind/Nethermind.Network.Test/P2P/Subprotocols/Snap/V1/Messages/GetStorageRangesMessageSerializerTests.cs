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
        public void Roundtrip_Empty()
        {
            GetStorageRangeMessage msg = new()
            {
                RequestId = 1111,
                StorageRange = new()
                {
                    RootHash = Keccak.OfAnEmptyString,
                    Accounts = ArrayPoolList<PathWithAccount>.Empty(),
                    StartingHash = SnapSerializerGoldens.RangeStart,
                    LimitHash = SnapSerializerGoldens.RangeLimit
                },
                ResponseBytes = 1000
            };
            GetStorageRangesMessageSerializer serializer = new();

            // The message encodes as [requestId, rootHash, accountPaths, startingHash, limitHash, responseBytes].
            SerializerTester.TestZero(serializer, msg,
                "f86a" + SnapSerializerGoldens.RequestId1111Rlp +
                SnapSerializerGoldens.EmptyStringKeccakRlp +
                "c0" +
                SnapSerializerGoldens.RangeStartRlp +
                SnapSerializerGoldens.RangeLimitRlp +
                "8203e8");
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
    }
}
