//  Copyright (c) 2020 Demerzel Solutions Limited
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

using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Wit;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Wit
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class MessageTests
    {
        [Test]
        public void Message_code_is_correct_in_request()
        {
            new GetBlockWitnessHashesMessage(1, Keccak.Zero).PacketType.Should().Be(1);
        }
        
        [Test]
        public void Message_code_is_correct_in_response()
        {
            new BlockWitnessHashesMessage(1, null).PacketType.Should().Be(2);
        }
    }
    
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetBlockWitnessHashesMessageSerializerTests
    {
        [Test]
        public void Roundtrip_init()
        {
            GetBlockWitnessHashesMessageSerializer serializer = new GetBlockWitnessHashesMessageSerializer();
            GetBlockWitnessHashesMessage message = new GetBlockWitnessHashesMessage(1, Keccak.Zero);
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_handle_null()
        {
            GetBlockWitnessHashesMessageSerializer serializer = new GetBlockWitnessHashesMessageSerializer();
            GetBlockWitnessHashesMessage message = new GetBlockWitnessHashesMessage(1, null);
            SerializerTester.TestZero(serializer, message);
        }
        
        [Test]
        public void Can_deserialize_trinity()
        {
            GetBlockWitnessHashesMessageSerializer serializer = new GetBlockWitnessHashesMessageSerializer();
            var trinityBytes = Bytes.FromHexString("0xea880ea29ca8028d7edea04bf6040124107de018c753ff2a9e464ca13e9d099c45df6a48ddbf436ce30c83");
            var buffer = ByteBufferUtil.DefaultAllocator.Buffer(trinityBytes.Length);
            buffer.WriteBytes(trinityBytes);
            GetBlockWitnessHashesMessage msg =
                ((IZeroMessageSerializer<GetBlockWitnessHashesMessage>) serializer).Deserialize(buffer);
        }
    }
}