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
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StorageRangesMessageSerializerTests
    {
        // [Test]
        // public void Roundtrip_NullSlotsNullProofs()
        // {
        //     StorageRangesMessage msg = new()
        //     {
        //         RequestId = MessageConstants.Random.NextLong(), 
        //         Slots = null,
        //         Proof = null
        //     };
        //     StorageRangesMessageSerializer serializer = new();
        //
        //     SerializerTester.TestZero(serializer, msg);
        // }
        
        [Test]
        public void Roundtrip_NoSlotsNoProofs()
        {
            StorageRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(), 
                //Slots = new MeasuredArray<MeasuredArray<Slot>>(Array.Empty<MeasuredArray<Slot>>()) ,
                //Proof = new MeasuredArray<byte[]>(Array.Empty<byte[]>())
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
                //Slots = new MeasuredArray<MeasuredArray<Slot>>(Array.Empty<MeasuredArray<Slot>>()) ,
                //Proof = new MeasuredArray<byte[]>(new []{TestItem.RandomDataA})
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
                //Slots = new MeasuredArray<MeasuredArray<Slot>>(new MeasuredArray<Slot>[]
                //{
                //    new(
                //        new[] { new Slot { Hash = new Keccak("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Data = TestItem.RandomDataA } })
                //}),
                //Proof = new MeasuredArray<byte[]>(Array.Empty<byte[]>())
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
                //Slots = new MeasuredArray<MeasuredArray<Slot>>(
                //    new MeasuredArray<Slot>[]
                //    {
                //        new(new[]
                //        {
                //            new Slot { Hash = new Keccak("0x11d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Data = TestItem.RandomDataA },
                //            new Slot { Hash = new Keccak("0x12d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Data = TestItem.RandomDataB }
                //        }),
                //        new(new[]
                //        {
                //            new Slot { Hash = new Keccak("0x21d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Data = TestItem.RandomDataB },
                //            new Slot { Hash = new Keccak("0x22d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"), Data = TestItem.RandomDataC }
                //        })
                //    }),
                //Proof = new MeasuredArray<byte[]>(new[] { TestItem.RandomDataA, TestItem.RandomDataB })
            };

            StorageRangesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }
    }
}
