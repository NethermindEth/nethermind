// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
            Assert.That(node.DiscoveryPort, Is.EqualTo(65535));
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

        [TestCase(NodeFromEnrMode.PeerCandidate)]
        [TestCase(NodeFromEnrMode.Discovery)]
        public void TryFromEnr_keeps_tcp_and_discovery_ports(NodeFromEnrMode mode)
        {
            NodeRecord enr = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: 30304);

            bool result = TryCreateNodeFromEnr(mode, enr, out Node? node);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(node, Is.Not.Null);
                Assert.That(node!.Host, Is.EqualTo("8.8.8.8"));
                Assert.That(node.Port, Is.EqualTo(30303));
                Assert.That(node.DiscoveryPort, Is.EqualTo(30304));
                Assert.That(node.DiscoveryAddress.Port, Is.EqualTo(30304));
                Assert.That(node.HasDiscoveryEndpoint, Is.True);
                Assert.That(node.Enr, Is.SameAs(enr));
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

        [Test]
        public void TryFromDiscoveryEnr_accepts_udp_only_record_without_tcp_port()
        {
            NodeRecord enr = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: null, udpPort: 30304);

            bool result = Node.TryFromDiscoveryEnr(enr, out Node? node);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(node, Is.Not.Null);
                Assert.That(node!.Port, Is.Zero);
                Assert.That(node.DiscoveryPort, Is.EqualTo(30304));
                Assert.That(node.HasDiscoveryEndpoint, Is.True);
            }
        }

        [Test]
        public void TryFromEnr_marks_missing_discovery_endpoint()
        {
            NodeRecord enr = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: null);

            bool result = Node.TryFromEnr(enr, out Node? node);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(node, Is.Not.Null);
                Assert.That(node!.Port, Is.EqualTo(30303));
                Assert.That(node.DiscoveryPort, Is.EqualTo(30303));
                Assert.That(node.HasDiscoveryEndpoint, Is.False);
            }
        }

        [TestCase(NodeFromEnrMode.PeerCandidate)]
        [TestCase(NodeFromEnrMode.Discovery)]
        public void TryFromEnr_uses_ipv6_endpoint_when_ipv4_port_is_missing(NodeFromEnrMode mode)
        {
            NodeRecord enr = CreateDualStackIpv6EndpointEnr(TestItem.PrivateKeyA);

            bool result = TryCreateNodeFromEnr(mode, enr, out Node? node);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(node, Is.Not.Null);
                Assert.That(node!.Host, Is.EqualTo("2001:db8::1"));
                Assert.That(node.Port, Is.EqualTo(30303));
                Assert.That(node.DiscoveryPort, Is.EqualTo(30304));
                Assert.That(node.HasDiscoveryEndpoint, Is.True);
            }
        }

        [Test]
        public void TryFromEnr_keeps_independent_tcp_and_discovery_addresses()
        {
            NodeRecord enr = CreateSplitEndpointEnr(TestItem.PrivateKeyA);

            bool result = Node.TryFromEnr(enr, out Node? node);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(node!.Address, Is.EqualTo(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 30303)));
                Assert.That(node.DiscoveryAddress, Is.EqualTo(new IPEndPoint(IPAddress.Parse("2001:4860:4860::8888"), 30304)));
            }
        }

        [TestCaseSource(nameof(TryRequestEnrSequenceCases))]
        public void TryRequestEnrSequence_tracks_active_request(
            ulong initialSequence,
            ulong advertisedSequence,
            bool expectedStarted,
            ulong expectedSequence)
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            if (initialSequence != 0)
            {
                Assert.That(node.TryRequestEnrSequence(initialSequence), Is.True);
            }

            bool started = node.TryRequestEnrSequence(advertisedSequence);

            Assert.That(started, Is.EqualTo(expectedStarted));
            Assert.That(node.RequestingEnrSequence, Is.EqualTo(expectedSequence));
        }

        [TestCaseSource(nameof(TryClearEnrRequestCases))]
        public void TryClearEnrRequest_clears_only_when_completed_sequence_satisfies_current_request(
            ulong initialSequence,
            ulong latestAdvertisedSequence,
            ulong completedSequence,
            bool expectedCleared,
            ulong expectedSequence)
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            Assert.That(node.TryRequestEnrSequence(initialSequence), Is.True);
            if (latestAdvertisedSequence != initialSequence)
            {
                Assert.That(node.TryRequestEnrSequence(latestAdvertisedSequence), Is.False);
            }

            bool cleared = node.TryClearEnrRequest(completedSequence);

            Assert.That(cleared, Is.EqualTo(expectedCleared));
            Assert.That(node.RequestingEnrSequence, Is.EqualTo(expectedSequence));
        }

        [TestCaseSource(nameof(EnrRequestClearOnRecordUpdateCases))]
        public void Enr_request_sequence_clears_when_enr_sequence_satisfies_request(
            ulong requestedSequence,
            ulong recordSequence,
            ulong expectedRequestingSequence)
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            NodeRecord enr = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: 30304);
            enr.EnrSequence = recordSequence;

            Assert.That(node.TryRequestEnrSequence(requestedSequence), Is.True);

            node.Enr = enr;

            Assert.That(node.RequestingEnrSequence, Is.EqualTo(expectedRequestingSequence));
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

        [Test]
        public void To_string_aligned_short_uses_common_port_cache()
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);

            Assert.That(node.ToString(Node.Format.AlignedShort), Is.EqualTo("      127.0.0.1:30303"));
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

        private static NodeRecord CreateDualStackIpv6EndpointEnr(PrivateKey privateKey)
        {
            NodeRecord enr = new();
            enr.SetEntry(IdEntry.Instance);
            enr.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
            enr.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
            enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
            enr.SetEntry(new Tcp6Entry(30303));
            enr.SetEntry(new Udp6Entry(30304));
            enr.EnrSequence = 1;
            new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(enr);
            return enr;
        }

        private static NodeRecord CreateSplitEndpointEnr(PrivateKey privateKey)
        {
            NodeRecord enr = new();
            enr.SetEntry(IdEntry.Instance);
            enr.SetEntry(new IpEntry(IPAddress.Parse("8.8.8.8")));
            enr.SetEntry(new Ip6Entry(IPAddress.Parse("2001:4860:4860::8888")));
            enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
            enr.SetEntry(new TcpEntry(30303));
            enr.SetEntry(new Udp6Entry(30304));
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

        private static IEnumerable<TestCaseData> TryRequestEnrSequenceCases()
        {
            yield return new TestCaseData(0UL, 0UL, false, 0UL)
                .SetName("TryRequestEnrSequence_rejects_zero_sequence");
            yield return new TestCaseData(0UL, 5UL, true, 5UL)
                .SetName("TryRequestEnrSequence_starts_first_request");
            yield return new TestCaseData(5UL, 4UL, false, 5UL)
                .SetName("TryRequestEnrSequence_ignores_lower_sequence");
            yield return new TestCaseData(5UL, 5UL, false, 5UL)
                .SetName("TryRequestEnrSequence_ignores_same_sequence");
            yield return new TestCaseData(5UL, 7UL, false, 7UL)
                .SetName("TryRequestEnrSequence_records_higher_sequence_without_starting_new_request");
        }

        private static IEnumerable<TestCaseData> TryClearEnrRequestCases()
        {
            yield return new TestCaseData(5UL, 5UL, 4UL, false, 5UL)
                .SetName("TryClearEnrRequest_keeps_request_when_completed_sequence_is_lower");
            yield return new TestCaseData(5UL, 5UL, 5UL, true, 0UL)
                .SetName("TryClearEnrRequest_clears_matching_request");
            yield return new TestCaseData(5UL, 5UL, 6UL, true, 0UL)
                .SetName("TryClearEnrRequest_clears_request_satisfied_by_higher_response");
            yield return new TestCaseData(5UL, 7UL, 5UL, false, 7UL)
                .SetName("TryClearEnrRequest_keeps_newer_request_after_higher_sequence_was_advertised");
            yield return new TestCaseData(5UL, 7UL, 7UL, true, 0UL)
                .SetName("TryClearEnrRequest_clears_newer_request");
        }

        private static IEnumerable<TestCaseData> EnrRequestClearOnRecordUpdateCases()
        {
            yield return new TestCaseData(5UL, 5UL, 0UL)
                .SetName("Enr_setter_clears_matching_request_sequence");
            yield return new TestCaseData(7UL, 5UL, 7UL)
                .SetName("Enr_setter_keeps_request_when_record_sequence_is_lower");
            yield return new TestCaseData(7UL, 8UL, 0UL)
                .SetName("Enr_setter_clears_request_when_record_sequence_is_higher");
        }

        public enum NodeFromEnrMode
        {
            PeerCandidate,
            Discovery
        }
    }
}
