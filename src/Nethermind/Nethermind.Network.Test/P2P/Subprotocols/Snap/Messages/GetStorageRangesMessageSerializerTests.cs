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
