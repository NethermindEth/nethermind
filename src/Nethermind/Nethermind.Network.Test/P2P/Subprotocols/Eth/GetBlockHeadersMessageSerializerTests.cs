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

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class GetBlockHeadersMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            GetBlockHeadersMessage message = new GetBlockHeadersMessage();
            message.MaxHeaders = 1;
            message.Skip = 2;
            message.Reverse = 1;
            message.StartingBlock = (100, Keccak.OfAnEmptyString);
            GetBlockHeadersMessageSerializer serializer = new GetBlockHeadersMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.StartingBlock.Hash, deserialized.StartingBlock.Hash, $"{nameof(message.StartingBlock.Hash)}");
            Assert.AreEqual(message.StartingBlock.Number, deserialized.StartingBlock.Number, $"{nameof(message.StartingBlock.Number)}");
            Assert.AreEqual(message.MaxHeaders, deserialized.MaxHeaders, $"{nameof(message.MaxHeaders)}");
            Assert.AreEqual(message.Reverse, deserialized.Reverse, $"{nameof(message.Reverse)}");
            Assert.AreEqual(message.Skip, deserialized.Skip, $"{nameof(message.Skip)}");
        }
    }
}