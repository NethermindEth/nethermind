// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
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
        public void Canonicalizes_mapped_ipv4()
        {
            Node node = new(TestItem.PublicKeyA, "::ffff:73.224.122.50", 65535);
            Assert.That(node.Port, Is.EqualTo(65535));
            Assert.That(node.DiscoveryPort, Is.EqualTo(65535));
            Assert.That(node.Address.Address, Is.EqualTo(IPAddress.Parse("73.224.122.50")));
            Assert.That(node.DiscoveryAddress.Address, Is.EqualTo(IPAddress.Parse("73.224.122.50")));
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

            Assert.That(result, Is.True);
            Assert.That(node, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
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

            Assert.That(result, Is.True);
            Assert.That(node, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
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

            Assert.That(result, Is.True);
            Assert.That(node, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node!.Port, Is.EqualTo(30303));
                Assert.That(node.DiscoveryPort, Is.EqualTo(30303));
                Assert.That(node.HasDiscoveryEndpoint, Is.False);
            }
        }

        [TestCase(NodeFromEnrMode.PeerCandidate)]
        [TestCase(NodeFromEnrMode.Discovery)]
        public void TryFromEnr_uses_ipv6_endpoint_when_ipv4_port_is_missing(NodeFromEnrMode mode)
        {
            NodeRecord enr = CreateDualStackEnr(TestItem.PrivateKeyA, includeIpv4Ports: false);

            bool result = TryCreateNodeFromEnr(mode, enr, out Node? node);

            Assert.That(result, Is.True);
            Assert.That(node, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node!.Host, Is.EqualTo("2001:db8::1"));
                Assert.That(node.Port, Is.EqualTo(30303));
                Assert.That(node.DiscoveryPort, Is.EqualTo(30304));
                Assert.That(node.HasDiscoveryEndpoint, Is.True);
            }
        }

        [TestCase(NodeFromEnrMode.PeerCandidate)]
        [TestCase(NodeFromEnrMode.Discovery)]
        public void TryFromEnr_accepts_dual_stack_endpoint_entries(NodeFromEnrMode mode)
        {
            NodeRecord enr = CreateDualStackEnr(TestItem.PrivateKeyA, includeIpv4Ports: true);

            bool result = TryCreateNodeFromEnr(mode, enr, out Node? node);

            Assert.That(result, Is.True);
            Assert.That(node, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node!.Host, Is.EqualTo("192.0.2.1"));
                Assert.That(node.Port, Is.EqualTo(30303));
                Assert.That(node.DiscoveryPort, Is.EqualTo(30304));
                Assert.That(node.HasDiscoveryEndpoint, Is.True);
            }
        }

        [TestCase(NodeFromEnrMode.PeerCandidate)]
        [TestCase(NodeFromEnrMode.Discovery)]
        public void TryFromEnr_selects_requested_address_family(NodeFromEnrMode mode)
        {
            NodeRecord enr = CreateDualStackEnr(TestItem.PrivateKeyA, includeIpv4Ports: true);

            bool result = TryCreateNodeFromEnr(mode, enr, AddressFamily.InterNetworkV6, out Node? node);

            Assert.That(result, Is.True);
            Assert.That(node, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node!.Address.AddressFamily, Is.EqualTo(AddressFamily.InterNetworkV6));
                Assert.That(node.Host, Is.EqualTo("2001:db8::1"));
                Assert.That(node.Port, Is.EqualTo(30303));
                Assert.That(node.DiscoveryPort, Is.EqualTo(30304));
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

        [TestCase(5UL, 5UL, true, 0UL)]
        [TestCase(5UL, 7UL, false, 7UL)]
        public void ObserveEnrSequence_tracks_sequence_and_request_ownership(
            ulong observedSequence,
            ulong latestAdvertisedSequence,
            bool expectedCleared,
            ulong expectedRequestingSequence)
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            Assert.That(node.TryRequestEnrSequence(observedSequence), Is.True);
            if (latestAdvertisedSequence != observedSequence)
            {
                Assert.That(node.TryRequestEnrSequence(latestAdvertisedSequence), Is.False);
            }

            bool cleared = node.ObserveEnrSequence(observedSequence);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(cleared, Is.EqualTo(expectedCleared));
                Assert.That(node.HighestObservedEnrSequence, Is.EqualTo(observedSequence));
                Assert.That(node.RequestingEnrSequence, Is.EqualTo(expectedRequestingSequence));
            }
        }

        [Test]
        public void Enr_assignment_does_not_mark_record_as_verified()
        {
            const ulong sequence = 5;
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            NodeRecord enr = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: 30304);
            enr.EnrSequence = sequence;
            Assert.That(node.TryRequestEnrSequence(sequence), Is.True);

            node.Enr = enr;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.IsVerifiedEnr(enr), Is.False);
                Assert.That(node.HighestObservedEnrSequence, Is.Zero);
                Assert.That(node.RequestingEnrSequence, Is.EqualTo(sequence));
            }
        }

        [TestCaseSource(nameof(EnrRequestClearOnRecordUpdateCases))]
        public void Verified_enr_clears_request_when_sequence_satisfies_request(
            ulong requestedSequence,
            ulong recordSequence,
            ulong expectedRequestingSequence)
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            NodeRecord enr = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: 30304);
            enr.EnrSequence = recordSequence;

            Assert.That(node.TryRequestEnrSequence(requestedSequence), Is.True);

            node.SetVerifiedEnr(enr);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.IsVerifiedEnr(enr), Is.True);
                Assert.That(node.RequestingEnrSequence, Is.EqualTo(expectedRequestingSequence));
            }
        }

        [Test]
        public void Verification_provenance_is_specific_to_the_record_instance()
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            NodeRecord unverified = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: 30304);
            NodeRecord verified = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.4.4"), tcpPort: 30303, udpPort: 30304);
            node.Enr = unverified;

            node.SetVerifiedEnr(verified);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.IsVerifiedEnr(unverified), Is.False);
                Assert.That(node.IsVerifiedEnr(verified), Is.True);
            }
        }

        [Test]
        public void SetVerifiedEnr_rejects_record_below_authenticated_high_water()
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            NodeRecord retained = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), 30303, 30304, enrSequence: 10);
            NodeRecord stale = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.4.4"), 30303, 30304, enrSequence: 11);
            node.SetVerifiedEnr(retained);
            node.ObserveEnrSequence(12);

            bool stored = node.SetVerifiedEnr(stale);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(stored, Is.False);
                Assert.That(node.Enr, Is.SameAs(retained));
                Assert.That(node.IsVerifiedEnr(retained), Is.True);
                Assert.That(node.HighestObservedEnrSequence, Is.EqualTo(12));
            }
        }

        [Test]
        public void Shared_enr_state_prevents_stale_replacement_publication()
        {
            NodeRecord firstRecord = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.4.4"), 30303, 30304, enrSequence: 11);
            NodeRecord newerRecord = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("1.1.1.1"), 30303, 30304, enrSequence: 12);
            Node known = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            Node firstReplacement = new(TestItem.PublicKeyA, "127.0.0.2", 30303);
            Node newerReplacement = new(TestItem.PublicKeyA, "127.0.0.3", 30303);
            firstReplacement.MergeEnrStateFrom(known);
            newerReplacement.MergeEnrStateFrom(known);

            Parallel.Invoke(
                () => firstReplacement.SetVerifiedEnr(firstRecord),
                () => newerReplacement.SetVerifiedEnr(newerRecord));
            Assert.That(firstReplacement.SetVerifiedEnr(firstRecord), Is.False);

            foreach (Node node in new[] { known, firstReplacement, newerReplacement })
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(node.Enr, Is.SameAs(newerRecord));
                    Assert.That(node.IsVerifiedEnr(newerRecord), Is.True);
                    Assert.That(node.HighestObservedEnrSequence, Is.EqualTo(newerRecord.EnrSequence));
                }
            }
        }

        [Test]
        public void MergeEnrState_unifies_existing_alias_groups()
        {
            Node candidate = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            Node candidateAlias = new(TestItem.PublicKeyA, "127.0.0.2", 30303);
            candidateAlias.MergeEnrStateFrom(candidate);
            Node existing = new(TestItem.PublicKeyA, "127.0.0.3", 30303);
            Node existingAlias = new(TestItem.PublicKeyA, "127.0.0.4", 30303);
            existingAlias.MergeEnrStateFrom(existing);
            NodeRecord record = CreateEnr(
                TestItem.PrivateKeyA,
                IPAddress.Parse("8.8.8.8"),
                30303,
                30304,
                enrSequence: 3);

            candidate.MergeEnrStateFrom(existing);
            candidateAlias.SetVerifiedEnr(record);

            foreach (Node node in new[] { candidate, candidateAlias, existing, existingAlias })
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(node.Enr, Is.SameAs(record));
                    Assert.That(node.IsVerifiedEnr(record), Is.True);
                    Assert.That(node.HighestObservedEnrSequence, Is.EqualTo(record.EnrSequence));
                }
            }
        }

        [Test]
        public void Concurrent_enr_state_merge_forwards_racing_alias_update()
        {
            NodeRecord record = CreateEnr(
                TestItem.PrivateKeyA,
                IPAddress.Parse("8.8.8.8"),
                30303,
                30304,
                enrSequence: 3);

            for (int i = 0; i < 64; i++)
            {
                Node candidate = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
                Node candidateAlias = new(TestItem.PublicKeyA, "127.0.0.2", 30303);
                candidateAlias.MergeEnrStateFrom(candidate);
                Node existing = new(TestItem.PublicKeyA, "127.0.0.3", 30303);
                existing.ObserveEnrSequence(1);
                using Barrier start = new(2);

                Parallel.Invoke(
                    () =>
                    {
                        start.SignalAndWait();
                        candidate.MergeEnrStateFrom(existing);
                    },
                    () =>
                    {
                        start.SignalAndWait();
                        candidateAlias.SetVerifiedEnr(record);
                    });

                Assert.That(existing.Enr, Is.SameAs(record));
                Assert.That(existing.HighestObservedEnrSequence, Is.EqualTo(record.EnrSequence));
            }
        }

        [Test]
        public void MergeEnrState_retains_verified_record_below_merged_high_water()
        {
            NodeRecord retainedRecord = CreateEnr(
                TestItem.PrivateKeyA,
                IPAddress.Parse("8.8.8.8"),
                30303,
                30304,
                enrSequence: 1);
            Node candidate = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            candidate.SetVerifiedEnr(retainedRecord);
            candidate.ObserveEnrSequence(2);
            Node existing = new(TestItem.PublicKeyA, "127.0.0.2", 30303);
            existing.ObserveEnrSequence(2);

            candidate.MergeEnrStateFrom(existing);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(existing.Enr, Is.SameAs(retainedRecord));
                Assert.That(existing.IsVerifiedEnr(retainedRecord), Is.True);
                Assert.That(existing.HighestObservedEnrSequence, Is.EqualTo(2));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MergeEnrState_preserves_unverified_candidate_only_without_verified_record(bool existingRecordIsVerified)
        {
            NodeRecord existingRecord = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), 30303, 30304, enrSequence: 1);
            NodeRecord candidateRecord = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.4.4"), 30303, 30304, enrSequence: 2);
            Node existing = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            if (existingRecordIsVerified)
            {
                existing.SetVerifiedEnr(existingRecord);
            }

            Node candidate = new(TestItem.PublicKeyA, "127.0.0.2", 30303)
            {
                Enr = candidateRecord
            };

            candidate.MergeEnrStateFrom(existing);

            NodeRecord expectedRecord = existingRecordIsVerified ? existingRecord : candidateRecord;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(candidate.Enr, Is.SameAs(expectedRecord));
                Assert.That(existing.Enr, Is.SameAs(expectedRecord));
                Assert.That(candidate.IsVerifiedEnr(expectedRecord), Is.EqualTo(existingRecordIsVerified));
                Assert.That(candidate.HighestObservedEnrSequence, Is.EqualTo(existingRecordIsVerified ? 1 : 0));
            }
        }

        [TestCase(1UL, false)]
        [TestCase(2UL, false)]
        [TestCase(3UL, true)]
        public void MergeEnrState_keeps_highest_sequence_unverified_record(
            ulong candidateSequence,
            bool expectsCandidate)
        {
            NodeRecord existingRecord = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), 30303, 30304, enrSequence: 2);
            NodeRecord candidateRecord = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.4.4"), 30303, 30304, enrSequence: candidateSequence);
            Node existing = new(TestItem.PublicKeyA, "127.0.0.1", 30303) { Enr = existingRecord };
            Node candidate = new(TestItem.PublicKeyA, "127.0.0.2", 30303) { Enr = candidateRecord };

            candidate.MergeEnrStateFrom(existing);

            NodeRecord expected = expectsCandidate ? candidateRecord : existingRecord;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(candidate.Enr, Is.SameAs(expected));
                Assert.That(existing.Enr, Is.SameAs(expected));
                Assert.That(candidate.IsVerifiedEnr(expected), Is.False);
                Assert.That(candidate.HighestObservedEnrSequence, Is.Zero);
            }
        }

        [TestCase(2UL, 0UL)]
        [TestCase(3UL, 3UL)]
        public void MergeEnrState_keeps_only_candidate_requests_above_merged_high_water(
            ulong candidateRequest,
            ulong expectedRequest)
        {
            Node existing = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            existing.ObserveEnrSequence(2);
            Node candidate = new(TestItem.PublicKeyA, "127.0.0.2", 30303);
            candidate.TryRequestEnrSequence(candidateRequest);

            candidate.MergeEnrStateFrom(existing);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(candidate.HighestObservedEnrSequence, Is.EqualTo(2));
                Assert.That(candidate.RequestingEnrSequence, Is.EqualTo(expectedRequest));
                Assert.That(existing.RequestingEnrSequence, Is.EqualTo(expectedRequest));
            }
        }

        [Test]
        public void Concurrent_verified_enr_updates_keep_highest_sequence()
        {
            const int recordCount = 16;
            NodeRecord[] records = new NodeRecord[recordCount];
            for (int i = 0; i < records.Length; i++)
            {
                records[i] = CreateEnr(
                    TestItem.PrivateKeyA,
                    IPAddress.Parse($"8.8.8.{i + 1}"),
                    30303,
                    30304,
                    enrSequence: (ulong)i + 1);
            }

            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);

            Parallel.For(0, records.Length, i => node.SetVerifiedEnr(records[i]));

            NodeRecord expected = records[^1];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.Enr, Is.SameAs(expected));
                Assert.That(node.IsVerifiedEnr(expected), Is.True);
                Assert.That(node.HighestObservedEnrSequence, Is.EqualTo(expected.EnrSequence));
            }
        }

        [Test]
        public void NetworkNode_constructor_uses_discovery_endpoint_matching_selected_tcp_family()
        {
            NodeRecord enr = new();
            enr.SetEntry(IdEntry.Instance);
            enr.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
            enr.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
            enr.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
            enr.SetEntry(new UdpEntry(30304));
            enr.SetEntry(new Tcp6Entry(30303));
            enr.SetEntry(new Udp6Entry(30305));
            enr.EnrSequence = 1;
            new NodeRecordSigner(new EthereumEcdsa(0), TestItem.PrivateKeyA).Sign(enr);

            Node node = new(new NetworkNode(enr.ToString()));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.Address, Is.EqualTo(IPEndPoint.Parse("[2001:db8::1]:30303")));
                Assert.That(node.DiscoveryAddress, Is.EqualTo(IPEndPoint.Parse("[2001:db8::1]:30305")));
                Assert.That(node.HasDiscoveryEndpoint, Is.True);
            }
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

        [TestCase("fd00:beef:cafe::11", "@[fd00:beef:cafe::11]:30303", "fd00:beef:cafe::11")]
        [TestCase("::ffff:172.217.12.36", "@172.217.12.36:30303", "172.217.12.36")]
        public void To_string_brackets_native_ipv6_enode_host(string host, string expectedTail, string expectedReparsedHost)
        {
            Node node = new(TestItem.PublicKeyA, host, 30303);

            string enode = node.ToString(Node.Format.ENode);

            Assert.That(enode, Does.Contain(expectedTail));
            Assert.That(Enode.IsEnode(enode, out _), Is.True);
            Enode reparsed = new(enode);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reparsed.HostIp, Is.EqualTo(IPAddress.Parse(expectedReparsedHost)));
                Assert.That(reparsed.Port, Is.EqualTo(30303));
            }
        }

        [Test]
        public void To_string_aligned_short_uses_common_port_cache()
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);

            Assert.That(node.ToString(Node.Format.AlignedShort), Is.EqualTo("      127.0.0.1:30303"));
        }

        private static NodeRecord CreateEnr(
            PrivateKey privateKey,
            IPAddress ipAddress,
            int? tcpPort,
            int? udpPort,
            ulong enrSequence = 1)
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
            enr.EnrSequence = enrSequence;
            new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(enr);
            return enr;
        }

        private static NodeRecord CreateDualStackEnr(PrivateKey privateKey, bool includeIpv4Ports)
        {
            NodeRecord enr = new();
            enr.SetEntry(IdEntry.Instance);
            enr.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
            enr.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
            enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
            if (includeIpv4Ports)
            {
                enr.SetEntry(new TcpEntry(30303));
                enr.SetEntry(new UdpEntry(30304));
            }
            enr.SetEntry(new Tcp6Entry(30303));
            enr.SetEntry(new Udp6Entry(30304));
            enr.EnrSequence = 1;
            new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(enr);
            return enr;
        }

        private static NodeRecord CreateDualStackEnr(PrivateKey privateKey)
        {
            NodeRecord enr = new();
            enr.SetEntry(IdEntry.Instance);
            enr.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
            enr.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
            enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
            enr.SetEntry(new TcpEntry(30303));
            enr.SetEntry(new UdpEntry(30304));
            enr.SetEntry(new Tcp6Entry(30303));
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

        private static bool TryCreateNodeFromEnr(NodeFromEnrMode mode, NodeRecord enr, AddressFamily addressFamily, out Node? node) =>
            mode switch
            {
                NodeFromEnrMode.PeerCandidate => Node.TryFromEnr(enr, addressFamily, out node),
                NodeFromEnrMode.Discovery => Node.TryFromDiscoveryEnr(enr, addressFamily, out node),
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
