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
                Hashes = TestItem.Keccaks,
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
                Hashes = Array.Empty<Keccak>(),
                Bytes = 10
            };
            
            GetByteCodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }
    }
}
