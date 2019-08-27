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
using Nethermind.Core.Extensions;
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
            statusMessage.ChainId = 1;
            
            StatusMessageSerializer serializer = new StatusMessageSerializer();
            byte[] bytes = serializer.Serialize(statusMessage);
            byte[] expectedBytes = Bytes.FromHexString("f8483f0183020080a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6a0044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116d");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");
            
            StatusMessage deserialized = serializer.Deserialize(bytes);
            
            Assert.AreEqual(statusMessage.BestHash, deserialized.BestHash, $"{nameof(deserialized.BestHash)}");
            Assert.AreEqual(statusMessage.GenesisHash, deserialized.GenesisHash, $"{nameof(deserialized.GenesisHash)}");
            Assert.AreEqual(statusMessage.TotalDifficulty, deserialized.TotalDifficulty, $"{nameof(deserialized.TotalDifficulty)}");
            Assert.AreEqual(statusMessage.ChainId, deserialized.ChainId, $"{nameof(deserialized.ChainId)}");
            Assert.AreEqual(statusMessage.ProtocolVersion, deserialized.ProtocolVersion, $"{nameof(deserialized.ProtocolVersion)}");
        }
        
        [Test]
        public void Hobbit()
        {
            StatusMessage message = new StatusMessage();            
            message.ProtocolVersion = 63;
            message.BestHash = Keccak.Compute("1");
            message.GenesisHash = Keccak.Compute("0");
            message.TotalDifficulty = 131200;
            message.ChainId = 1;
            
            StatusMessageSerializer serializer = new StatusMessageSerializer();
            SerializerTester.Test(serializer, message);
            SerializerTester.TestZero(serializer, message);
        }
        
        [Test]
        public void Can_deserialize_example_from_ethereumJ()
        {
            byte[] bytes = Bytes.FromHexString("f84927808425c60144a0832056d3c93ff2739ace7199952e5365aa29f18805be05634c4db125c5340216a0955f36d073ccb026b78ab3424c15cf966a7563aa270413859f78702b9e8e22cb");
            StatusMessageSerializer serializer = new StatusMessageSerializer();
            StatusMessage message = serializer.Deserialize(bytes);
            Assert.AreEqual(39, message.ProtocolVersion, "ProtocolVersion");
            
            Assert.AreEqual(0x25c60144, (int)message.TotalDifficulty, "Difficulty");
            Assert.AreEqual(new Keccak("832056d3c93ff2739ace7199952e5365aa29f18805be05634c4db125c5340216"), message.BestHash, "BestHash");
            Assert.AreEqual(new Keccak("0x955f36d073ccb026b78ab3424c15cf966a7563aa270413859f78702b9e8e22cb"), message.GenesisHash, "GenesisHash");

            byte[] serialized = serializer.Serialize(message);
            Assert.AreEqual(bytes, serialized, "serializing to same format");
        }
    }
}