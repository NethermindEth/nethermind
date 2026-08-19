// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.V2.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetBlockAccessListsMessageSerializerTests
    {
        [Test]
        public void Roundtrip_Many()
        {
            GetBlockAccessListsMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                BlockHashes = TestItem.ValueKeccaks.ToPooledList(),
                Bytes = 10
            };

            GetBlockAccessListsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_Empty()
        {
            GetBlockAccessListsMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                BlockHashes = ArrayPoolList<ValueHash256>.Empty(),
                Bytes = 10
            };

            GetBlockAccessListsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Deserialize_Throws_On_TooMany_BlockHashes()
        {
            GetBlockAccessListsMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                BlockHashes = Enumerable.Repeat(TestItem.ValueKeccaks[0], SnapMessageLimits.MaxRequestHashes + 1)
                    .ToPooledList(SnapMessageLimits.MaxRequestHashes + 1),
                Bytes = 10
            };

            GetBlockAccessListsMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(msg);

            Assert.Throws<RlpLimitException>(() => serializer.Deserialize(serialized));
        }
    }
}
