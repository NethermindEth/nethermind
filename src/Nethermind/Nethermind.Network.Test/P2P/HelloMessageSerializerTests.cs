// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class HelloMessageSerializerTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            HelloMessage helloMessage = new();
            helloMessage.P2PVersion = 1;
            helloMessage.Capabilities = new List<Capability>();
            helloMessage.Capabilities.Add(new Capability(Protocol.Eth, 1));
            helloMessage.ClientId = "Nethermind/alpha";
            helloMessage.ListenPort = 8002;
            helloMessage.NodeId = NetTestVectors.StaticKeyA.PublicKey;

            HelloMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(helloMessage);
            byte[] expectedBytes = Bytes.FromHexString("f85e01904e65746865726d696e642f616c706861c6c58365746801821f42b840fda1cff674c90c9a197539fe3dfb53086ace64f83ed7c6eabec741f7f381cc803e52ab2cd55d5569bce4347107a310dfd5f88a010cd2ffd1005ca406f1842877");

            Assert.True(Bytes.AreEqual(serialized, expectedBytes), "bytes");

            HelloMessage deserialized = serializer.Deserialize(serialized);

            Assert.That(deserialized.P2PVersion, Is.EqualTo(helloMessage.P2PVersion));
            Assert.That(deserialized.ClientId, Is.EqualTo(helloMessage.ClientId));
            Assert.That(deserialized.NodeId, Is.EqualTo(helloMessage.NodeId));
            Assert.That(deserialized.ListenPort, Is.EqualTo(helloMessage.ListenPort));
            Assert.That(deserialized.Capabilities.Count, Is.EqualTo(helloMessage.Capabilities.Count));
            Assert.That(deserialized.Capabilities[0].ProtocolCode, Is.EqualTo(helloMessage.Capabilities[0].ProtocolCode));
            Assert.That(deserialized.Capabilities[0].Version, Is.EqualTo(helloMessage.Capabilities[0].Version));
        }

        [Test]
        public void Can_deserialize_sample_from_ethereumJ()
        {
            byte[] helloMessageRaw = Bytes.FromHexString("f87902a5457468657265756d282b2b292f76302e372e392f52656c656173652f4c696e75782f672b2bccc58365746827c583736868018203e0b8401fbf1e41f08078918c9f7b6734594ee56d7f538614f602c71194db0a1af5a77f9b86eb14669fe7a8a46a2dd1b7d070b94e463f4ecd5b337c8b4d31bbf8dd5646");
            HelloMessageSerializer serializer = new();
            HelloMessage helloMessage = serializer.Deserialize(helloMessageRaw);
            Assert.That(helloMessage.ClientId, Is.EqualTo("Ethereum(++)/v0.7.9/Release/Linux/g++"), $"{nameof(HelloMessage.ClientId)}");
            Assert.That(helloMessage.ListenPort, Is.EqualTo(992), $"{nameof(HelloMessage.ListenPort)}");
            Assert.That(helloMessage.P2PVersion, Is.EqualTo(2), $"{nameof(HelloMessage.P2PVersion)}");
            Assert.That(helloMessage.Capabilities.Count, Is.EqualTo(2), $"{nameof(helloMessage.Capabilities.Count)}");
            Assert.That(
                helloMessage.NodeId, Is.EqualTo(new PublicKey("1fbf1e41f08078918c9f7b6734594ee56d7f538614f602c71194db0a1af5a77f9b86eb14669fe7a8a46a2dd1b7d070b94e463f4ecd5b337c8b4d31bbf8dd5646")), $"{nameof(HelloMessage.NodeId)}");
        }

        [Test]
        public void Can_deserialize_sample_from_eip8_ethereumJ()
        {
            byte[] helloMessageRaw = Bytes.FromHexString("f87137916b6e6574682f76302e39312f706c616e39cdc5836574683dc6846d6f726b1682270fb840" +
                                  "fda1cff674c90c9a197539fe3dfb53086ace64f83ed7c6eabec741f7f381cc803e52ab2cd55d5569" +
                                  "bce4347107a310dfd5f88a010cd2ffd1005ca406f1842877c883666f6f836261720304");
            HelloMessageSerializer serializer = new();
            HelloMessage helloMessage = serializer.Deserialize(helloMessageRaw);
            Assert.That(helloMessage.ClientId, Is.EqualTo("kneth/v0.91/plan9"), $"{nameof(HelloMessage.ClientId)}");
            Assert.That(helloMessage.ListenPort, Is.EqualTo(9999), $"{nameof(HelloMessage.ListenPort)}");
            Assert.That(helloMessage.P2PVersion, Is.EqualTo(55), $"{nameof(HelloMessage.P2PVersion)}");
            Assert.That(helloMessage.Capabilities.Count, Is.EqualTo(2), $"{nameof(helloMessage.Capabilities.Count)}");
            Assert.That(
                helloMessage.NodeId, Is.EqualTo(new PublicKey("fda1cff674c90c9a197539fe3dfb53086ace64f83ed7c6eabec741f7f381cc803e52ab2cd55d5569bce4347107a310dfd5f88a010cd2ffd1005ca406f1842877")), $"{nameof(HelloMessage.NodeId)}");
        }

        [Test]
        public void Can_deserialize_ethereumJ_eip8_sample()
        {
            byte[] bytes = Bytes.FromHexString(
                "f87137916b6e6574682f76302e39312f706c616e39cdc5836574683dc6846d6f726b1682270fb840" +
                "fda1cff674c90c9a197539fe3dfb53086ace64f83ed7c6eabec741f7f381cc803e52ab2cd55d5569" +
                "bce4347107a310dfd5f88a010cd2ffd1005ca406f1842877c883666f6f836261720304");

            HelloMessageSerializer serializer = new();
            HelloMessage helloMessage = serializer.Deserialize(bytes);
            Assert.That(helloMessage.P2PVersion, Is.EqualTo(55));
        }
    }
}
