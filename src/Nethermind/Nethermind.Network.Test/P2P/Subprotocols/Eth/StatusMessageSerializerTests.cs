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
    public class StatusMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            StatusMessage statusMessage = new StatusMessage();            
            statusMessage.ProtocolVersion = 63;
            statusMessage.BestHash = Keccak.Compute("1");
            statusMessage.GenesisHash = Keccak.Compute("0");
            statusMessage.TotalDifficulty = 131200;
            statusMessage.NetworkId = 1;
            
            StatusMessageSerializer serializer = new StatusMessageSerializer();
            byte[] bytes = serializer.Serialize(statusMessage);
            StatusMessage deserialized = serializer.Deserialize(bytes);
            
            Assert.AreEqual(statusMessage.BestHash, deserialized.BestHash, $"{nameof(deserialized.BestHash)}");
            Assert.AreEqual(statusMessage.GenesisHash, deserialized.GenesisHash, $"{nameof(deserialized.GenesisHash)}");
            Assert.AreEqual(statusMessage.TotalDifficulty, deserialized.TotalDifficulty, $"{nameof(deserialized.TotalDifficulty)}");
            Assert.AreEqual(statusMessage.NetworkId, deserialized.NetworkId, $"{nameof(deserialized.NetworkId)}");
            Assert.AreEqual(statusMessage.ProtocolVersion, deserialized.ProtocolVersion, $"{nameof(deserialized.ProtocolVersion)}");   
        }
    }
}