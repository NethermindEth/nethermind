// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StorageRangesMessageSerializerTests
    {

        [Test]
        public void Roundtrip_NoSlotsNoProofs()
        {
            using StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Slots = ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(),
                Proofs = ArrayPoolList<byte[]>.Empty()
            };
            StorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_OneProof()
        {
            using StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Slots = ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(),
                Proofs = new ArrayPoolList<byte[]>(2) { TestItem.RandomDataA }
            };

            StorageRangesMessageSerializer serializer = new();

            var serialized = serializer.Serialize(msg);
            using var deserialized = serializer.Deserialize(serialized);

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_OneSlot()
        {
            using StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Slots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
                {
                    new ArrayPoolList<PathWithStorageSlot> (1)
                    {
                        new PathWithStorageSlot(new Hash256("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), TestItem.RandomDataA)
                    }
                },
                Proofs = ArrayPoolList<byte[]>.Empty()
            };

            StorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_Many()
        {
            using StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Slots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(2) {
                    new ArrayPoolList<PathWithStorageSlot>(2) {
                        new PathWithStorageSlot(new Hash256("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataA).Bytes) ,
                        new PathWithStorageSlot(new Hash256("0x12d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataB).Bytes)
                    },
                    new ArrayPoolList<PathWithStorageSlot>(2) {
                        new PathWithStorageSlot(new Hash256("0x21d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataB).Bytes) ,
                        new PathWithStorageSlot(new Hash256("0x22d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataC).Bytes)
                    }
                },
                Proofs = new ArrayPoolList<byte[]>(2) { TestItem.RandomDataA, TestItem.RandomDataB }
            };

            StorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }
    }
}
