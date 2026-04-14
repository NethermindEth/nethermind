// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private const int MaxStorageSlotValueLength = 33;

        [Test]
        public void Roundtrip_NoSlotsNoProofs()
        {
            using StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Slots = ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(),
                Proofs = new ByteArrayListAdapter(ArrayPoolList<byte[]>.Empty())
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
                Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(2) { TestItem.RandomDataA })
            };

            StorageRangesMessageSerializer serializer = new();

            byte[] serialized = serializer.Serialize(msg);
            using StorageRangeMessage deserialized = serializer.Deserialize(serialized);

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
                        new(new Hash256("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), TestItem.RandomDataA)
                    }
                },
                Proofs = new ByteArrayListAdapter(ArrayPoolList<byte[]>.Empty())
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
                        new(new Hash256("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataA).Bytes) ,
                        new(new Hash256("0x12d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataB).Bytes)
                    },
                    new ArrayPoolList<PathWithStorageSlot>(2) {
                        new(new Hash256("0x21d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataB).Bytes) ,
                        new(new Hash256("0x22d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataC).Bytes)
                    }
                },
                Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(2) { TestItem.RandomDataA, TestItem.RandomDataB })
            };

            StorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Deserialize_allows_storage_slot_value_at_current_limit()
        {
            using StorageRangeMessage msg = CreateMessageWithSingleSlotValue(CreateStorageSlotValue(MaxStorageSlotValueLength));
            StorageRangesMessageSerializer serializer = new();

            byte[] serialized = serializer.Serialize(msg);
            using StorageRangeMessage deserialized = serializer.Deserialize(serialized);

            Assert.That(deserialized.Slots[0][0].SlotRlpValue.Length, Is.EqualTo(MaxStorageSlotValueLength));
        }

        [Test]
        public void Deserialize_throws_on_storage_slot_value_above_current_limit()
        {
            using StorageRangeMessage msg = CreateMessageWithSingleSlotValue(CreateStorageSlotValue(MaxStorageSlotValueLength + 1));
            StorageRangesMessageSerializer serializer = new();

            byte[] serialized = serializer.Serialize(msg);

            Assert.Throws<RlpLimitException>(() => serializer.Deserialize(serialized));
        }

        private static StorageRangeMessage CreateMessageWithSingleSlotValue(byte[] slotRlpValue) => new()
        {
            RequestId = MessageConstants.Random.NextLong(),
            Slots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
                {
                    new ArrayPoolList<PathWithStorageSlot>(1)
                    {
                        new(new Hash256("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), slotRlpValue)
                    }
                },
            Proofs = new ByteArrayListAdapter(ArrayPoolList<byte[]>.Empty())
        };

        private static byte[] CreateStorageSlotValue(int length)
        {
            byte[] slotRlpValue = new byte[length];
            if (length == 0)
            {
                return slotRlpValue;
            }

            slotRlpValue[0] = (byte)(0x80 + length - 1);
            for (int i = 1; i < slotRlpValue.Length; i++)
            {
                slotRlpValue[i] = (byte)i;
            }

            return slotRlpValue;
        }
    }
}
