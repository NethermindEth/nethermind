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
    public class NewBlockHashesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            NewBlockHashesMessage message = new NewBlockHashesMessage((Keccak.Compute("1"), 1), (Keccak.Compute("2"), 2)); 
            
            NewBlockHashesMessageSerializer serializer = new NewBlockHashesMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            NewBlockHashesMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.PacketType, deserialized.PacketType, $"{nameof(message.PacketType)}");
            Assert.AreEqual(message.Protocol, deserialized.Protocol, $"{nameof(message.Protocol)}");
            Assert.AreEqual(message.BlockHashes.Length, deserialized.BlockHashes.Length, $"number of block hashes");
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                Assert.AreEqual(message.BlockHashes[i].Item1, deserialized.BlockHashes[i].Item1, $"{i} hash");
                Assert.AreEqual(message.BlockHashes[i].Item2, deserialized.BlockHashes[i].Item2, $"{i} number");
            }
        }
    }
}