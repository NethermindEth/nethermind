// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Enr;
using NSubstitute;
using NUnit.Framework;
using KeyType = Nethermind.Libp2p.Core.Dto.KeyType;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

public class BeaconDiscoveryTests
{
    [Test]
    public async Task A_discovery_bind_failure_is_retried_until_the_port_is_available()
    {
        using Socket occupied = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        occupied.ExclusiveAddressUse = true;
        occupied.Bind(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)occupied.LocalEndPoint!).Port;
        await using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig { Discv5Port = port, Bootnodes = " " }).Build();
        BeaconDiscovery discovery = container.Resolve<BeaconDiscovery>();
        BeaconSyncOrchestrator orchestrator = container.Resolve<BeaconSyncOrchestrator>();
        orchestrator.ComponentStartRetryDelay = TimeSpan.Zero;
        int attempts = 0;
        NodeRecord? failedRecord = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        await orchestrator.StartComponentAsync(async token =>
        {
            attempts++;
            try
            {
                await discovery.Start(token);
            }
            catch
            {
                failedRecord = discovery.LocalNodeRecord;
                occupied.Close();
                throw;
            }
        }, timeout.Token);

        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(failedRecord, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(discovery.LocalNodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1), Is.EqualTo(failedRecord!.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)));
            Assert.That(discovery.LocalNodeRecord.EnrSequence, Is.GreaterThan(failedRecord.EnrSequence));
        }
        await discovery.Stop();
    }

    private static readonly IPAddress PublicIp = IPAddress.Parse("8.8.8.8");
    private static readonly byte[] CurrentDigest = Bytes.FromHexString("0x8c9f62fe"); // mainnet BPO2 digest
    private static readonly byte[] NextDigest = Bytes.FromHexString("0xcb0d1acc");
    private static readonly byte[] ForeignDigest = Bytes.FromHexString("0xdeadbeef");
    private static readonly byte[] FuluVersion = Bytes.FromHexString("0x06000000");

    private static readonly EnrForkId TestForkId = new(CurrentDigest, FuluVersion, Presets.FarFutureEpoch);

    [Test]
    [CancelAfter(15_000)]
    public async Task Concurrent_enr_updates_wait_for_the_earlier_sequence_to_be_persisted(CancellationToken token)
    {
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        bool hold = false;
        using GatedEnrMetadata metadata = new(() =>
        {
            if (Volatile.Read(ref hold) && Interlocked.Increment(ref writes) == 1)
            {
                entered.TrySetResult();
                release.Wait();
            }
        });
        using MemColumnsDb<BeaconChainDbColumns> memory = new();
        IColumnsDb<BeaconChainDbColumns> columns = Substitute.For<IColumnsDb<BeaconChainDbColumns>>();
        columns.GetColumnDb(Arg.Any<BeaconChainDbColumns>()).Returns(call => memory.GetColumnDb(call.Arg<BeaconChainDbColumns>()));
        columns.GetColumnDb(BeaconChainDbColumns.Metadata).Returns(metadata);
        BeaconChainStore store = new(columns);
        ulong beforeBpo1 = BeaconChainSpec.Mainnet.GenesisTime + 412_671UL * BeaconChainSpec.Mainnet.SlotsPerEpoch * BeaconChainSpec.Mainnet.SecondsPerSlot + 1;
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(beforeBpo1));
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, BeaconChainSpec.Mainnet, store, new RangeSyncTests.FixedIPResolver(PublicIp), clock, LimboLogs.Instance);
        discovery.CreateDiscv5Services(PublicIp);
        clock.Add(TimeSpan.FromSeconds(BeaconChainSpec.Mainnet.SlotsPerEpoch * BeaconChainSpec.Mainnet.SecondsPerSlot));
        Volatile.Write(ref hold, true);
        Task<bool> first = Task.Run(discovery.UpdateLocalEnr, token);
        Task<bool>? second = null;
        bool overtook = false;
        try
        {
            await entered.Task.WaitAsync(token);
            second = Task.Run(() =>
            {
                secondStarted.TrySetResult();
                return discovery.UpdateLocalEnr();
            }, token);
            await secondStarted.Task.WaitAsync(token);
            overtook = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1), token)) == second;
        }
        finally
        {
            release.Set();
            await first;
            if (second is not null) await second;
        }

        Assert.That(overtook, Is.False, "a later update published while the preceding sequence write was blocked");
    }

    private sealed class GatedEnrMetadata(Action beforeSequenceWrite) : MemDb
    {
        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (key.SequenceEqual("p2pEnrSequence"u8)) beforeSequenceWrite();
            base.Set(key, value, flags);
        }
    }

    [Test]
    public void An_ipv4_mapped_address_is_encoded_as_ipv4()
    {
        NodeRecord record = new BeaconNodeRecordProvider(TestItem.PrivateKeyA, PublicIp.MapToIPv6(), 9000, 9001, TestForkId, 4).Current;
        Assert.That(record.TryGetTcpEndpoint(AddressFamily.InterNetwork, out IPEndPoint? endpoint), Is.True);
        Assert.That(endpoint!.Address, Is.EqualTo(PublicIp));
    }

    [Test]
    public void A_dual_address_record_preserves_both_tcp_targets([Values] bool ipv4)
    {
        NodeRecord record = new();
        if (ipv4)
        {
            record.SetEntry(new IpEntry(PublicIp));
            record.SetEntry(new TcpEntry(9000));
        }

        record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:4860:4860::8888")));
        record.SetEntry(new Tcp6Entry(9002));
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new Eth2Entry(TestForkId.Encode()));
        new NodeRecordSigner(new Ecdsa(), TestItem.PrivateKeyA).Sign(record);

        Assert.That(BeaconDiscovery.TryCreateCandidate(NodeRecord.FromEnrString(record.ToString()), CurrentDigest, null, out BeaconPeerCandidate? candidate), Is.True);
        Assert.That(candidate!.Addresses.Count, Is.EqualTo(ipv4 ? 2 : 1));
        Assert.That(candidate.Addresses[^1], Does.StartWith("/ip6/2001:4860:4860::8888/tcp/9002/"));
    }

    [Test]
    public void Next_fork_mismatch_changes_preference_without_rejecting_a_current_peer([Values] bool matching)
    {
        BeaconNodeRecordProvider provider = new(TestItem.PrivateKeyA, PublicIp, 9000, 9001, TestForkId, 4, matching ? NextDigest : ForeignDigest);
        Assert.That(BeaconDiscovery.TryCreateCandidate(NodeRecord.FromEnrString(provider.Current.ToString()), CurrentDigest, NextDigest, out BeaconPeerCandidate? candidate), Is.True);
        Assert.That(candidate!.ForkPreference, Is.EqualTo(matching ? 3 : 2));
    }

    [Test]
    public void Next_fork_version_and_epoch_are_evaluated_alongside_nfd([Values] bool matching)
    {
        EnrForkId advertised = matching ? TestForkId : new EnrForkId(CurrentDigest, FuluVersion, 10);
        BeaconNodeRecordProvider provider = new(TestItem.PrivateKeyA, PublicIp, 9000, 9001, advertised, 4, NextDigest);
        Assert.That(BeaconDiscovery.TryCreateCandidate(NodeRecord.FromEnrString(provider.Current.ToString()), CurrentDigest, NextDigest,
            out BeaconPeerCandidate? candidate, TestForkId), Is.True);
        Assert.That(candidate!.ForkPreference, Is.EqualTo(matching ? 4 : 3));
    }

    [Test]
    public void An_ipv6_address_is_advertised_under_ip6_and_never_as_an_ipv4_ip_entry()
    {
        IPAddress ipv6 = IPAddress.Parse("2001:4860:4860::8888");
        NodeRecord decoded = NodeRecord.FromEnrString(new BeaconNodeRecordProvider(TestItem.PrivateKeyA, ipv6, 9000, 9001, TestForkId, 4).Current.ToString());

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(decoded.HasEntry(EnrContentKey.Ip), Is.False, "an IPv6 address was encoded as an IPv4 ip entry");
        Assert.That(decoded.TryGetTcpEndpoint(AddressFamily.InterNetworkV6, out IPEndPoint? tcp6), Is.True);
        Assert.That(tcp6?.Address, Is.EqualTo(ipv6));
        Assert.That(tcp6?.Port, Is.EqualTo(9000));
        Assert.That(decoded.TryGetDiscoveryEndpoint(AddressFamily.InterNetworkV6, out IPEndPoint? udp6) ? udp6.Port : 0, Is.EqualTo(9001));
    }

    [TestCase("8.8.8.8", "8.8.8.8", TestName = "The external IPv4 address is advertised")]
    [TestCase("2001:4860:4860::8888", null, TestName = "An IPv6-only external address is not advertised while the listeners are IPv4 only")]
    public async Task The_local_enr_advertises_only_an_endpoint_this_node_listens_on(string external, string? advertised)
    {
        IPAddress? address = BeaconDiscovery.AdvertisedAddress(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse(external)));
        Assert.That(address, Is.EqualTo(advertised is null ? null : IPAddress.Parse(advertised)));

        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, BeaconChainSpec.Mainnet, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            new RangeSyncTests.FixedIPResolver(PublicIp), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(address);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(discovery.LocalNodeRecord.HasEntry(EnrContentKey.Ip), Is.EqualTo(advertised is not null));
        Assert.That(discovery.LocalNodeRecord.HasEntry(EnrContentKey.Ip6), Is.False);
    }

    [Test]
    public async Task The_enr_sequence_keeps_growing_across_restarts_and_updates()
    {
        // One second into the last epoch before mainnet BPO1 (412672), so the next epoch republishes the record.
        ulong beforeBpo1 = BeaconChainSpec.Mainnet.GenesisTime + 412_671UL * BeaconChainSpec.Mainnet.SlotsPerEpoch * BeaconChainSpec.Mainnet.SecondsPerSlot + 1;
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(beforeBpo1));
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        ulong updated;
        await using (BeaconDiscovery discovery = CreateDiscovery(store, clock))
        {
            discovery.CreateDiscv5Services(PublicIp);
            ulong first = discovery.LocalNodeRecord.EnrSequence;
            clock.Add(TimeSpan.FromSeconds(BeaconChainSpec.Mainnet.SlotsPerEpoch * BeaconChainSpec.Mainnet.SecondsPerSlot));
            Task<bool>[] updates = new Task<bool>[8];
            for (int i = 0; i < updates.Length; i++) updates[i] = Task.Run(discovery.UpdateLocalEnr);
            bool[] changed = await Task.WhenAll(updates);
            Assert.That(Array.FindAll(changed, static value => value).Length, Is.EqualTo(1), "crossing BPO1 must republish once");
            updated = discovery.LocalNodeRecord.EnrSequence;
            Assert.That(updated, Is.GreaterThan(first));
        }

        await using BeaconDiscovery restarted = CreateDiscovery(store, clock);
        restarted.CreateDiscv5Services(PublicIp);
        Assert.That(restarted.LocalNodeRecord.EnrSequence, Is.GreaterThan(updated), "EIP-778: a restart must not publish a sequence peers already hold");

        static BeaconDiscovery CreateDiscovery(BeaconChainStore store, ITimestamper clock) =>
            new(new BeaconChainConfig { Discv5Port = 0 }, BeaconChainSpec.Mainnet, store, new RangeSyncTests.FixedIPResolver(PublicIp), clock, LimboLogs.Instance);
    }

    [Test]
    public void Local_enr_round_trips_and_update_bumps_sequence()
    {
        BeaconNodeRecordProvider provider = new(TestItem.PrivateKeyA, PublicIp, tcpPort: 9000, udpPort: 9001, TestForkId, custodyGroupCount: 4);
        NodeRecord decoded = NodeRecord.FromEnrString(provider.Current.ToString());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.EnrSequence, Is.EqualTo(1ul));
            Assert.That(decoded.Ip, Is.EqualTo(PublicIp));
            Assert.That(decoded.TcpPort, Is.EqualTo(9000));
            Assert.That(decoded.DiscoveryPort, Is.EqualTo(9001));
            Assert.That(decoded.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1), Is.EqualTo(TestItem.PrivateKeyA.CompressedPublicKey));
            Assert.That(BeaconDiscovery.TryGetForkId(decoded, out EnrForkId? forkId), Is.True);
            Assert.That(forkId, Is.EqualTo(TestForkId));
        }

        Assert.That(provider.Update(TestForkId), Is.False, "republishing an unchanged fork id should be a no-op");

        EnrForkId rotated = new(NextDigest, FuluVersion, 419072);
        Assert.That(provider.Update(rotated), Is.True);
        NodeRecord updated = NodeRecord.FromEnrString(provider.Current.ToString());
        Assert.That(BeaconDiscovery.TryGetForkId(updated, out EnrForkId? updatedForkId), Is.True);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(updated.EnrSequence, Is.EqualTo(2ul));
        Assert.That(updatedForkId, Is.EqualTo(rotated));
    }

    [Test]
    public void Local_enr_carries_the_nfd_entry_and_rotates_it_across_a_real_mainnet_bpo_boundary()
    {
        // Real, shipped mainnet epochs either side of BPO1 (412672), not a synthetic schedule.
        EnrForkId preBpo1 = EnrForkId.Compute(BeaconChainSpec.Mainnet, 412671ul);
        byte[]? nextBeforeBpo1 = EnrForkId.NextForkDigest(BeaconChainSpec.Mainnet, 412671ul);
        BeaconNodeRecordProvider provider = new(TestItem.PrivateKeyA, PublicIp, tcpPort: 9000, udpPort: 9001, preBpo1, custodyGroupCount: 4, nextBeforeBpo1);

        // Parsed back from the ENR string, taking the same wire path a remote peer would.
        NodeRecord initial = NodeRecord.FromEnrString(provider.Current.ToString());
        Assert.That(BeaconDiscovery.TryGetNextForkDigest(initial, out byte[]? decodedNext), Is.True);
        Assert.That(decodedNext, Is.EqualTo(nextBeforeBpo1));

        EnrForkId atBpo1 = EnrForkId.Compute(BeaconChainSpec.Mainnet, 412672ul);
        byte[]? nextAtBpo1 = EnrForkId.NextForkDigest(BeaconChainSpec.Mainnet, 412672ul);
        Assert.That(provider.Update(atBpo1, nextAtBpo1), Is.True, "crossing the BPO1 boundary must republish");
        Assert.That(nextAtBpo1, Is.Not.EqualTo(nextBeforeBpo1), "the boundary should actually rotate nfd in this fixture");

        NodeRecord updated = NodeRecord.FromEnrString(provider.Current.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.EnrSequence, Is.EqualTo(2ul));
            Assert.That(BeaconDiscovery.TryGetNextForkDigest(updated, out byte[]? decodedRotated), Is.True);
            Assert.That(decodedRotated, Is.EqualTo(nextAtBpo1));
        }

        Assert.That(provider.Update(atBpo1, nextAtBpo1), Is.False, "republishing an unchanged nfd should be a no-op");
    }

    [Test]
    public void Local_enr_advertises_the_zero_nfd_default_once_nothing_is_scheduled()
    {
        EnrForkId forkId = EnrForkId.Compute(BeaconChainSpec.Mainnet, 419072ul);
        BeaconNodeRecordProvider provider = new(TestItem.PrivateKeyA, PublicIp, tcpPort: 9000, udpPort: 9001, forkId, custodyGroupCount: 4,
            EnrForkId.NextForkDigest(BeaconChainSpec.Mainnet, 419072ul));

        Assert.That(provider.Current.GetObj<byte[]>("nfd"), Is.EqualTo(NfdEntry.NoneScheduled));

        NodeRecord parsed = NodeRecord.FromEnrString(provider.Current.ToString());
        Assert.That(BeaconDiscovery.TryGetNextForkDigest(parsed, out byte[]? decoded), Is.True);
        Assert.That(decoded, Is.Null, "the zero default means no next fork is scheduled");
    }

    [TestCase(4ul)]
    [TestCase(128ul)]
    public void Local_enr_carries_the_cgc_entry_at_the_configured_custody_group_count(ulong custodyGroupCount)
    {
        // Read directly off the freshly-built record: CustodyGroupCountEntryTests already covers the
        // RLP byte-encoding rule exhaustively, including the round trip through raw bytes that a
        // record parsed off the wire (NodeRecord.FromEnrString) would decode this entry as instead.
        BeaconNodeRecordProvider provider = new(TestItem.PrivateKeyA, PublicIp, tcpPort: 9000, udpPort: 9001, TestForkId, custodyGroupCount);

        Assert.That(provider.Current.GetValue<ulong>("cgc"), Is.EqualTo(custodyGroupCount));
    }

    [TestCase("current", true, ExpectedResult = true)]
    [TestCase("next", true, ExpectedResult = true)]
    [TestCase("nextWhenNoRotationScheduled", true, ExpectedResult = false)]
    [TestCase("foreign", true, ExpectedResult = false)]
    [TestCase("missing", true, ExpectedResult = false)]
    [TestCase("current", false, ExpectedResult = false)]
    public bool Candidate_filter_requires_matching_digest_and_tcp_endpoint(string eth2, bool includeTcp)
    {
        byte[]? ssz = eth2 switch
        {
            "current" => new EnrForkId(CurrentDigest, FuluVersion, Presets.FarFutureEpoch).Encode(),
            "next" or "nextWhenNoRotationScheduled" => new EnrForkId(NextDigest, FuluVersion, Presets.FarFutureEpoch).Encode(),
            "foreign" => new EnrForkId(ForeignDigest, FuluVersion, Presets.FarFutureEpoch).Encode(),
            _ => null,
        };
        byte[]? nextDigest = eth2 == "nextWhenNoRotationScheduled" ? null : NextDigest;
        NodeRecord record = ParsedEnr(TestItem.PrivateKeyA, ssz, includeTcp);

        return BeaconDiscovery.TryCreateCandidate(record, CurrentDigest, nextDigest, out _);
    }

    [Test]
    public void Derives_peer_id_and_multiaddr_matching_libp2p_identity_and_carries_the_source_enr()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        // The libp2p library itself is the reference for the expected peer id.
        string expectedPeerId = new Identity(key.KeyBytes, KeyType.Secp256K1).PeerId.ToString();
        NodeRecord record = ParsedEnr(key, TestForkId.Encode(), includeTcp: true);

        Assert.That(BeaconDiscovery.TryCreateCandidate(record, CurrentDigest, null, out BeaconPeerCandidate? candidate), Is.True);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(candidate!.PeerId, Is.EqualTo(expectedPeerId));
        Assert.That(candidate.Multiaddress, Is.EqualTo($"/ip4/8.8.8.8/tcp/9000/p2p/{expectedPeerId}"));
        Assert.That(candidate.ForkDigest, Is.EqualTo(CurrentDigest));
        Assert.That(candidate.EnrSequence, Is.EqualTo(1ul));
        // The Beacon API's node/peers endpoint reads this straight from the admitted peer; a
        // candidate that silently drops it forces that endpoint back to reporting null.
        Assert.That(candidate.Enr, Is.EqualTo(record.ToString()));
    }

    // next_fork_version tracks the next hard fork (or stays at the current one), while next_fork_epoch also
    // rotates on EIP-7892 BPO forks from Fulu onward, mirroring Lighthouse's enr_fork_id.
    [TestCase(364031ul, "0x05000000", 364032ul)] // deneb: next hard fork is electra
    [TestCase(364032ul, "0x06000000", 411392ul)] // electra: next is fulu; BPO schedule not yet in effect
    [TestCase(411392ul, "0x06000000", 412672ul)] // fulu: next digest change is BPO1, version stays fulu
    [TestCase(412672ul, "0x06000000", 419072ul)] // BPO1: next digest change is BPO2
    [TestCase(419072ul, "0x06000000", ulong.MaxValue)] // beyond BPO2 nothing is scheduled
    public void Computes_mainnet_enr_fork_id(ulong epoch, string expectedNextVersion, ulong expectedNextEpoch)
    {
        EnrForkId forkId = EnrForkId.Compute(BeaconChainSpec.Mainnet, epoch);

        Assert.That(EnrForkId.TryDecode(forkId.Encode(), out EnrForkId? decoded), Is.True);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(forkId.ForkDigest, Is.EqualTo(ForkDigest.Compute(BeaconChainSpec.Mainnet, epoch)));
        Assert.That(forkId.NextForkVersion, Is.EqualTo(Bytes.FromHexString(expectedNextVersion)));
        Assert.That(forkId.NextForkEpoch, Is.EqualTo(expectedNextEpoch));
        Assert.That(decoded, Is.EqualTo(forkId));
    }

    [Test]
    public void Built_in_mainnet_bootnodes_decode_with_discovery_endpoints()
    {
        Assert.That(MainnetBootnodes.Enrs, Is.Not.Empty);
        foreach (string enr in MainnetBootnodes.Enrs)
        {
            NodeRecord record = NodeRecord.FromEnrString(enr);
            Assert.That(record.TryGetDiscoveryEndpoint(out IPEndPoint? discoveryEndpoint), Is.True, enr);
            Assert.That(discoveryEndpoint!.Port, Is.GreaterThan(0), enr);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Discv5_service_graph_resolves_without_binding_a_socket()
    {
        BeaconChainConfig config = new() { Discv5Port = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = new(config, BeaconChainSpec.Mainnet, store, new RangeSyncTests.FixedIPResolver(PublicIp), Timestamper.Default, LimboLogs.Instance);

        // Resolving the private container is what regressed; Start would mask it behind a bind and live traffic.
        NettyDiscoveryV5Handler handler = discovery.CreateDiscv5Services(PublicIp);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(handler, Is.Not.Null);
        Assert.That(discovery.LocalNodeRecord, Is.Not.Null);
    }

    [Test]
    [Explicit("live mainnet discovery")]
    [CancelAfter(180_000)]
    public async Task Discovers_live_mainnet_peers_with_current_fork_digest(CancellationToken token)
    {
        const int targetCandidates = 15;
        BeaconChainConfig config = new() { Discv5Port = 0 }; // ephemeral UDP port
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = new(config, BeaconChainSpec.Mainnet, store, new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        await discovery.Start(cts.Token);
        TestContext.Progress.WriteLine($"Local ENR: {discovery.LocalNodeRecord}");

        ulong currentEpoch = BeaconChainSpec.Mainnet.GetEpoch(BeaconChainSpec.Mainnet.GetSlotAtTime(Timestamper.Default.UnixTime.Seconds));
        byte[] currentDigest = ForkDigest.Compute(BeaconChainSpec.Mainnet, currentEpoch);
        List<BeaconPeerCandidate> candidates = [];
        try
        {
            await foreach (BeaconPeerCandidate candidate in discovery.DiscoverPeers(cts.Token))
            {
                candidates.Add(candidate);
                TestContext.Progress.WriteLine($"{candidate.Multiaddress} eth2 digest: {candidate.ForkDigest.ToHexString()}, seq: {candidate.EnrSequence}");
                if (candidates.Count >= targetCandidates)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        int matching = 0;
        foreach (BeaconPeerCandidate candidate in candidates)
        {
            if (Bytes.AreEqual(candidate.ForkDigest, currentDigest))
            {
                matching++;
            }
        }

        TestContext.Progress.WriteLine($"Discovered {candidates.Count} candidates, {matching} with the current fork digest {currentDigest.ToHexString()}");
        Assert.That(matching, Is.GreaterThanOrEqualTo(5));
    }

    private static NodeRecord ParsedEnr(PrivateKey key, byte[]? eth2Ssz, bool includeTcp)
    {
        NodeRecord record = new();
        record.SetEntry(new IpEntry(PublicIp));
        if (includeTcp)
        {
            record.SetEntry(new TcpEntry(9000));
        }

        record.SetEntry(new UdpEntry(9001));
        record.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
        if (eth2Ssz is not null)
        {
            record.SetEntry(new Eth2Entry(eth2Ssz));
        }

        record.EnrSequence = 1;
        new NodeRecordSigner(new Ecdsa(), key).Sign(record);
        // Parse back from the string form so entries take the same unknown-entry path as wire records.
        return NodeRecord.FromEnrString(record.ToString());
    }
}
