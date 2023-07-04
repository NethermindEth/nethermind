// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
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
                Hashes = TestItem.ValueKeccaks,
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
                Hashes = Array.Empty<ValueKeccak>(),
                Bytes = 10
            };

            GetByteCodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }
    }
}
