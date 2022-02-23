//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using Nethermind.Core;
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
            StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Slots = Array.Empty<PathWithStorageSlot[]>(),
                Proofs = Array.Empty<byte[]>()
            };
            StorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }
        
        [Test]
        public void Roundtrip_OneProof()
        {
            StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Slots = Array.Empty<PathWithStorageSlot[]>(),
                Proofs = new[] { TestItem.RandomDataA }
            };

            StorageRangesMessageSerializer serializer = new();

            var serialized = serializer.Serialize(msg);
            var deserialized = serializer.Deserialize(serialized);
            
            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_OneSlot()
        {
            StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Slots = new[] { new PathWithStorageSlot[] { new PathWithStorageSlot(new Keccak("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), TestItem.RandomDataA) } },
                Proofs = Array.Empty<byte[]>()
            };

            StorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_Many()
        {
            StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                Slots = new[] { 
                    new PathWithStorageSlot[] { 
                        new PathWithStorageSlot(new Keccak("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataA).Bytes) ,
                        new PathWithStorageSlot(new Keccak("0x12d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataB).Bytes)
                    },
                    new PathWithStorageSlot[] {
                        new PathWithStorageSlot(new Keccak("0x21d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataB).Bytes) ,
                        new PathWithStorageSlot(new Keccak("0x22d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Rlp.Encode(TestItem.RandomDataC).Bytes)
                    }
                },
                Proofs = new[] { TestItem.RandomDataA, TestItem.RandomDataB }
            };

            StorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }
    }
}
