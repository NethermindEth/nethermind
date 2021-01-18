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
// 

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Wit;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Wit
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class BlockWitnessHashesMessageSerializerTests
    {
        [Test]
        public void Can_handle_zero()
        {
            BlockWitnessHashesMessageSerializer serializer = new BlockWitnessHashesMessageSerializer();
            BlockWitnessHashesMessage message = new BlockWitnessHashesMessage(1, new Keccak[0]);
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_handle_one()
        {
            BlockWitnessHashesMessageSerializer serializer = new BlockWitnessHashesMessageSerializer();
            BlockWitnessHashesMessage message = new BlockWitnessHashesMessage(1, new[] {Keccak.Zero});
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_handle_many()
        {
            BlockWitnessHashesMessageSerializer serializer = new BlockWitnessHashesMessageSerializer();
            BlockWitnessHashesMessage message = new BlockWitnessHashesMessage(1, TestItem.Keccaks);
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_handle_null()
        {
            BlockWitnessHashesMessageSerializer serializer = new BlockWitnessHashesMessageSerializer();
            BlockWitnessHashesMessage message = new BlockWitnessHashesMessage(1, null);
            byte[] serialized = serializer.Serialize(message);
            serialized[0].Should().Be(194);
            serializer.Deserialize(serialized);
        }
    }
}