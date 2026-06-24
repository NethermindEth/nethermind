// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test.Stats
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeTests
    {
        [Test]
        public void Can_parse_ipv6_prefixed_ip()
        {
            Node node = new(TestItem.PublicKeyA, "::ffff:73.224.122.50", 65535);
            Assert.That(node.Port, Is.EqualTo(65535));
            Assert.That(node.Address.Address.MapToIPv4().ToString(), Is.EqualTo("73.224.122.50"));
            Assert.That(node.Host, Is.EqualTo("73.224.122.50"));
        }

        [Test]
        public void Can_parse_native_ipv6_ip()
        {
            Node node = new(TestItem.PublicKeyA, "2001:4860:4860::8888", 65535);
            Assert.That(node.Port, Is.EqualTo(65535));
            Assert.That(node.Host, Is.EqualTo("2001:4860:4860::8888"));
        }

        [Test]
        public void Not_equal_to_another_type()
        {
            Node node = new(TestItem.PublicKeyA, "::ffff:73.224.122.50", 65535);
            // ReSharper disable once SuspiciousTypeConversion.Global
            Assert.That(node.Equals(1), Is.False);
        }

        [TestCase(NodeFromEnrMode.PeerCandidate, 30303)]
        [TestCase(NodeFromEnrMode.Discovery, 30304)]
        public void TryFromEnr_uses_expected_endpoint(NodeFromEnrMode mode, int expectedPort)
        {
            NodeRecord enr = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: 30304);

            bool result = TryCreateNodeFromEnr(mode, enr, out Node? node);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(node, Is.Not.Null);
                Assert.That(node!.Host, Is.EqualTo("8.8.8.8"));
                Assert.That(node.Port, Is.EqualTo(expectedPort));
                Assert.That(node.Enr, Is.EqualTo(enr.EnrString));
            }
        }

        [Test]
        public void TryFromEnr_rejects_udp_only_record()
        {
            NodeRecord enr = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: null, udpPort: 30304);

            bool result = Node.TryFromEnr(enr, out Node? node);

            Assert.That(result, Is.False);
            Assert.That(node, Is.Null);
        }

        [TestCase("s", "127.0.0.1:303")]
        [TestCase("a", "      127.0.0.1:  303")]
        [TestCase("c", "[Node|127.0.0.1:303|Details|ClientId]")]
        [TestCase("f", "enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@127.0.0.1:303|ClientId")]
        [TestCase("e", "enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@127.0.0.1:303")]
        [TestCase("p", "enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@127.0.0.1:303|0xb7705ae4c6f81b66cdb323c65f4e8133690fc099")]
        [TestCase("zzz", "enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@127.0.0.1:303")]
        public void To_string_formats(string format, string expectedFormat)
        {
            static Node GetNode(string host) =>
                new(TestItem.PublicKeyA, host, 303) { ClientId = "ClientId", EthDetails = "Details" };

            Node node = GetNode("127.0.0.1");
            Assert.That(node.ToString(format), Is.EqualTo(expectedFormat));

            node = GetNode("::ffff:127.0.0.1");
            Assert.That(node.ToString(format), Is.EqualTo(expectedFormat));
        }

        private static NodeRecord CreateEnr(PrivateKey privateKey, IPAddress ipAddress, int? tcpPort, int? udpPort)
        {
            NodeRecord enr = new();
            enr.SetEntry(IdEntry.Instance);
            enr.SetEntry(new IpEntry(ipAddress));
            enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
            if (tcpPort is not null)
            {
                enr.SetEntry(new TcpEntry(tcpPort.Value));
            }
            if (udpPort is not null)
            {
                enr.SetEntry(new UdpEntry(udpPort.Value));
            }
            enr.EnrSequence = 1;
            new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(enr);
            return enr;
        }

        private static bool TryCreateNodeFromEnr(NodeFromEnrMode mode, NodeRecord enr, out Node? node) =>
            mode switch
            {
                NodeFromEnrMode.PeerCandidate => Node.TryFromEnr(enr, out node),
                NodeFromEnrMode.Discovery => Node.TryFromDiscoveryEnr(enr, out node),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };

        public enum NodeFromEnrMode
        {
            PeerCandidate,
            Discovery
        }
    }
}
