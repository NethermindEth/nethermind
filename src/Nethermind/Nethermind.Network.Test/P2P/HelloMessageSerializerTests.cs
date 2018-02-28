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

using System.Collections.Generic;
using Nethermind.Network.P2P;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class HelloMessageSerializerTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            HelloMessage helloMessage = new HelloMessage();
            helloMessage.P2PVersion = 1;
            helloMessage.Capabilities = new Dictionary<Capability, int>();
            helloMessage.Capabilities.Add(Capability.Eth, 1);
            helloMessage.ClientId = "Nethermind/alpha";
            helloMessage.ListenPort = 8002;
            helloMessage.NodeId = NetTestVectors.StaticKeyA.PublicKey;

            HelloMessageSerializer serializer = new HelloMessageSerializer();
            byte[] serialized = serializer.Serialize(helloMessage);
            HelloMessage deserialized = serializer.Deserialize(serialized);

            Assert.AreEqual(helloMessage.P2PVersion, deserialized.P2PVersion);
            Assert.AreEqual(helloMessage.ClientId, deserialized.ClientId);
            Assert.AreEqual(helloMessage.NodeId, deserialized.NodeId);
            Assert.AreEqual(helloMessage.ListenPort, deserialized.ListenPort);
            Assert.AreEqual(helloMessage.Capabilities.Count, deserialized.Capabilities.Count);
            Assert.AreEqual(helloMessage.Capabilities[Capability.Eth], deserialized.Capabilities[Capability.Eth]);
        }
    }
}