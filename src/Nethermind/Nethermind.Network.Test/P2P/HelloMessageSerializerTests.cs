// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

            Assert.AreEqual(helloMessage.P2PVersion, deserialized.P2PVersion);
            Assert.AreEqual(helloMessage.ClientId, deserialized.ClientId);
            Assert.AreEqual(helloMessage.NodeId, deserialized.NodeId);
            Assert.AreEqual(helloMessage.ListenPort, deserialized.ListenPort);
            Assert.AreEqual(helloMessage.Capabilities.Count, deserialized.Capabilities.Count);
            Assert.AreEqual(helloMessage.Capabilities[0].ProtocolCode, deserialized.Capabilities[0].ProtocolCode);
            Assert.AreEqual(helloMessage.Capabilities[0].Version, deserialized.Capabilities[0].Version);
        }

        [Test]
        public void Can_deserialize_sample_from_ethereumJ()
        {
            byte[] helloMessageRaw = Bytes.FromHexString("f87902a5457468657265756d282b2b292f76302e372e392f52656c656173652f4c696e75782f672b2bccc58365746827c583736868018203e0b8401fbf1e41f08078918c9f7b6734594ee56d7f538614f602c71194db0a1af5a77f9b86eb14669fe7a8a46a2dd1b7d070b94e463f4ecd5b337c8b4d31bbf8dd5646");
            HelloMessageSerializer serializer = new();
            HelloMessage helloMessage = serializer.Deserialize(helloMessageRaw);
            Assert.AreEqual("Ethereum(++)/v0.7.9/Release/Linux/g++", helloMessage.ClientId, $"{nameof(HelloMessage.ClientId)}");
            Assert.AreEqual(992, helloMessage.ListenPort, $"{nameof(HelloMessage.ListenPort)}");
            Assert.AreEqual(2, helloMessage.P2PVersion, $"{nameof(HelloMessage.P2PVersion)}");
            Assert.AreEqual(2, helloMessage.Capabilities.Count, $"{nameof(helloMessage.Capabilities.Count)}");
            Assert.AreEqual(
                new PublicKey("1fbf1e41f08078918c9f7b6734594ee56d7f538614f602c71194db0a1af5a77f9b86eb14669fe7a8a46a2dd1b7d070b94e463f4ecd5b337c8b4d31bbf8dd5646"),
                helloMessage.NodeId, $"{nameof(HelloMessage.NodeId)}");
        }

        [Test]
        public void Can_deserialize_sample_from_eip8_ethereumJ()
        {
            byte[] helloMessageRaw = Bytes.FromHexString("f87137916b6e6574682f76302e39312f706c616e39cdc5836574683dc6846d6f726b1682270fb840" +
                                  "fda1cff674c90c9a197539fe3dfb53086ace64f83ed7c6eabec741f7f381cc803e52ab2cd55d5569" +
                                  "bce4347107a310dfd5f88a010cd2ffd1005ca406f1842877c883666f6f836261720304");
            HelloMessageSerializer serializer = new();
            HelloMessage helloMessage = serializer.Deserialize(helloMessageRaw);
            Assert.AreEqual("kneth/v0.91/plan9", helloMessage.ClientId, $"{nameof(HelloMessage.ClientId)}");
            Assert.AreEqual(9999, helloMessage.ListenPort, $"{nameof(HelloMessage.ListenPort)}");
            Assert.AreEqual(55, helloMessage.P2PVersion, $"{nameof(HelloMessage.P2PVersion)}");
            Assert.AreEqual(2, helloMessage.Capabilities.Count, $"{nameof(helloMessage.Capabilities.Count)}");
            Assert.AreEqual(
                new PublicKey("fda1cff674c90c9a197539fe3dfb53086ace64f83ed7c6eabec741f7f381cc803e52ab2cd55d5569bce4347107a310dfd5f88a010cd2ffd1005ca406f1842877"),
                helloMessage.NodeId, $"{nameof(HelloMessage.NodeId)}");
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
            Assert.AreEqual(55, helloMessage.P2PVersion);
        }
    }
}
