// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using System.Net;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NetworkNodeDecoderTests
    {
        [TestCase("127.0.0.1", 30303, 100L)]
        [TestCase("127.0.0.1", 30303, -100L)]
        [TestCase("127.0.0.1", -1, -100L)]
        public void Can_do_roundtrip(string host, int port, long reputation)
        {
            NetworkNode node = new(TestItem.PublicKeyA, host, port, reputation);
            AssertRoundtripPreservesFields(node);
        }

        [Test]
        public void Can_read_regression()
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            Rlp encoded = new(Bytes.FromHexString("f8a7b84013a1107b6f78a4977222d2d5a4cd05a8a042b75222c8ec99129b83793eda3d214208d4e835617512fc8d148d3d1b4d89530861644f531675b1fb64b785c6c152953a3a666666663a38352e3131322e3131332e3138368294c680ce0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            RlpReader context = new(encoded.Bytes);
            NetworkNode decoded = networkNodeDecoder.Decode(ref context);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Host, Is.EqualTo("::ffff:85.112.113.186"));
                Assert.That(decoded.NodeId, Is.EqualTo(new PublicKey(Bytes.FromHexString("0x13a1107b6f78a4977222d2d5a4cd05a8a042b75222c8ec99129b83793eda3d214208d4e835617512fc8d148d3d1b4d89530861644f531675b1fb64b785c6c152"))));
                Assert.That(decoded.Port, Is.EqualTo(38086));
                Assert.That(decoded.Reputation, Is.EqualTo(0L));
            }
        }

        [Test]
        public void Can_read_unbracketed_ipv4_mapped_ipv6_enode_regression()
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            Rlp encoded = new(Bytes.FromHexString("f8b2b8af656e6f64653a2f2f3661353034306166366634643434383035643830373936623237383466656630393136366430623565643862396565643437376639373030346664313138636330623564303734643535613933393763396466653239373137653934356139336336376134623030336634353363306664313237326439663466326531376130403a3a666666663a3134342e37362e3134392e3131393a303f64697363706f72743d333033303380"));
            RlpReader context = new(encoded.Bytes);

            NetworkNode decoded = networkNodeDecoder.Decode(ref context);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Host, Is.EqualTo("144.76.149.119"));
                Assert.That(decoded.Port, Is.Zero);
                Assert.That(decoded.DiscoveryPort, Is.EqualTo(30303));
                Assert.That(decoded.Reputation, Is.Zero);
            }
        }

        private static void AssertRoundtripPreservesFields(NetworkNode node)
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            Rlp encoded = networkNodeDecoder.Encode(node);
            RlpReader context = new(encoded.Bytes);
            NetworkNode decoded = networkNodeDecoder.Decode(ref context);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Host, Is.EqualTo(node.Host));
                Assert.That(decoded.NodeId, Is.EqualTo(node.NodeId));
                Assert.That(decoded.Port, Is.EqualTo(node.Port));
                Assert.That(decoded.DiscoveryPort, Is.EqualTo(node.DiscoveryPort));
                Assert.That(decoded.Reputation, Is.EqualTo(node.Reputation));
            }
        }

        [TestCase("8.8.8.8")]
        [TestCase("fd00:beef:cafe::11")]
        public void Can_do_enode_with_discovery_port_roundtrip(string host)
        {
            NetworkNode node = new(new Enode(TestItem.PublicKeyA, IPAddress.Parse(host), 30303, 30304))
            {
                Reputation = 100L
            };

            AssertRoundtripPreservesFields(node);
        }

        [Test]
        public void Can_do_enr_roundtrip()
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), 30303, 30304);
            NetworkNode node = new(enr.ToString())
            {
                Reputation = 100L
            };

            Rlp encoded = networkNodeDecoder.Encode(node);
            RlpReader context = new(encoded.Bytes);
            NetworkNode decoded = networkNodeDecoder.Decode(ref context);

            using (Assert.EnterMultipleScope())
            {
                NodeRecord? decodedEnr = decoded.Enr;
                Assert.That(decoded.IsEnr, Is.True);
                Assert.That(decodedEnr, Is.Not.Null);
                Assert.That(decodedEnr!.ToString(), Is.EqualTo(enr.ToString()));
                Assert.That(decoded.NodeId, Is.EqualTo(node.NodeId));
                Assert.That(decoded.Host, Is.EqualTo("8.8.8.8"));
                Assert.That(decoded.Port, Is.EqualTo(30303));
                Assert.That(decoded.DiscoveryPort, Is.EqualTo(30304));
                Assert.That(decoded.Reputation, Is.EqualTo(node.Reputation));
            }
        }

        private static NodeRecord CreateTestEnr(PrivateKey privateKey, IPAddress ipAddress, int tcpPort, int udpPort)
        {
            NodeRecord enr = new();
            enr.SetEntry(IdEntry.Instance);
            enr.SetEntry(new IpEntry(ipAddress));
            enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
            enr.SetEntry(new TcpEntry(tcpPort));
            enr.SetEntry(new UdpEntry(udpPort));
            enr.EnrSequence = 1;
            new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(enr);

            return enr;
        }
    }
}
