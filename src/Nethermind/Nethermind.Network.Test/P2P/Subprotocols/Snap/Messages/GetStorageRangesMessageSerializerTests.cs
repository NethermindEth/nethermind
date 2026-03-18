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
            var serialized = serializer.Serialize(msg);

            Assert.Throws<RlpLimitException>(() => serializer.Deserialize(serialized));
        }
    }
}
