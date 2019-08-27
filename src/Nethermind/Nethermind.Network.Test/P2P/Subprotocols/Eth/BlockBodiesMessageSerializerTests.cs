/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class BlockBodiesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            BlockHeader header = Build.A.BlockHeader.TestObject;
            Address to = Build.An.Address.FromNumber(1).TestObject;
            Transaction tx = Build.A.Transaction.WithTo(to).SignedAndResolved(new EthereumEcdsa(RopstenSpecProvider.Instance, NullLogManager.Instance), TestItem.PrivateKeyA, 1).TestObject;
            BlockBodiesMessage message = new BlockBodiesMessage();
            message.Bodies = new [] {new BlockBody(new [] {tx}, new [] {header})};
            
            var serializer = new BlockBodiesMessageSerializer();
            SerializerTester.Test(serializer, message);
            SerializerTester.TestZero(serializer, message);
        }
        
        [Test]
        public void Roundtrip_with_nulls()
        {
            BlockBodiesMessage message = new BlockBodiesMessage {Bodies = new BlockBody[1] {null}};
            var serializer = new BlockBodiesMessageSerializer();
            SerializerTester.Test(serializer, message);
            SerializerTester.TestZero(serializer, message);
        }
    }
}