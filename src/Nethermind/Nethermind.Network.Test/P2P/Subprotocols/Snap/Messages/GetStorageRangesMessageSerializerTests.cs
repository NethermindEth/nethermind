// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
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
                StoragetRange = new()
                {
                    RootHash = TestItem.KeccakA,
                    Accounts = TestItem.Keccaks.Select(k => new PathWithAccount(k, null)).ToArray(),
                    StartingHash = new Keccak("0x15d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"),
                    LimitHash = new Keccak("0x20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")
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
                StoragetRange = new()
                {
                    RootHash = Keccak.OfAnEmptyString,
                    Accounts = Array.Empty<PathWithAccount>(),
                    StartingHash = new Keccak("0x15d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"),
                    LimitHash = new Keccak("0x20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")
                },
                ResponseBytes = 1000
            };
            GetStorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }
    }
}
