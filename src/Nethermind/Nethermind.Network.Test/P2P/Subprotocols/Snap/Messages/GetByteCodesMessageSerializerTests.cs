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

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetByteCodesMessageSerializerTests
    {
        [Test]
        public void Roundtrip_Many()
        {
            GetByteCodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Hashes = TestItem.ValueKeccaks.ToPooledList(),
                Bytes = 10
            };

            GetByteCodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_Empty()
        {
            GetByteCodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Hashes = ArrayPoolList<ValueHash256>.Empty(),
                Bytes = 10
            };

            GetByteCodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Deserialize_Throws_On_TooMany_Hashes()
        {
            GetByteCodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Hashes = Enumerable.Repeat(TestItem.ValueKeccaks[0], SnapMessageLimits.MaxRequestHashes + 1).ToPooledList(SnapMessageLimits.MaxRequestHashes + 1),
                Bytes = 10
            };

            GetByteCodesMessageSerializer serializer = new();
            var serialized = serializer.Serialize(msg);

            Assert.Throws<RlpLimitException>(() => serializer.Deserialize(serialized));
        }
    }
}
