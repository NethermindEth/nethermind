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
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class BlockHeadersMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            BlockHeadersMessage message = new BlockHeadersMessage();
            message.BlockHeaders = new[] {Build.A.BlockHeader.TestObject};

            BlockHeadersMessageSerializer serializer = new BlockHeadersMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            BlockHeadersMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.BlockHeaders.Length, deserialized.BlockHeaders.Length, "length");
            Assert.AreEqual(message.BlockHeaders[0].Hash, deserialized.BlockHeaders[0].Hash, "hash");
        }
    }
}