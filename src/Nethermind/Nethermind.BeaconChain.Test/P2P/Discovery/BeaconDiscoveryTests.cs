// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
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
using NUnit.Framework;
using KeyType = Nethermind.Libp2p.Core.Dto.KeyType;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

public class BeaconDiscoveryTests
{
    private static readonly IPAddress PublicIp = IPAddress.Parse("8.8.8.8");
    private static readonly byte[] CurrentDigest = Bytes.FromHexString("0x8c9f62fe"); // mainnet BPO2 digest
    private static readonly byte[] NextDigest = Bytes.FromHexString("0xcb0d1acc");
    private static readonly byte[] ForeignDigest = Bytes.FromHexString("0xdeadbeef");
    private static readonly byte[] FuluVersion = Bytes.FromHexString("0x06000000");

    private static readonly EnrForkId TestForkId = new(CurrentDigest, FuluVersion, Presets.FarFutureEpoch);

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.EnrSequence, Is.EqualTo(2ul));
            Assert.That(updatedForkId, Is.EqualTo(rotated));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidate!.PeerId, Is.EqualTo(expectedPeerId));
            Assert.That(candidate.Multiaddress, Is.EqualTo($"/ip4/8.8.8.8/tcp/9000/p2p/{expectedPeerId}"));
            Assert.That(candidate.ForkDigest, Is.EqualTo(CurrentDigest));
            Assert.That(candidate.EnrSequence, Is.EqualTo(1ul));
            // The Beacon API's node/peers endpoint reads this straight from the admitted peer; a
            // candidate that silently drops it forces that endpoint back to reporting null.
            Assert.That(candidate.Enr, Is.EqualTo(record.ToString()));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkId.ForkDigest, Is.EqualTo(ForkDigest.Compute(BeaconChainSpec.Mainnet, epoch)));
            Assert.That(forkId.NextForkVersion, Is.EqualTo(Bytes.FromHexString(expectedNextVersion)));
            Assert.That(forkId.NextForkEpoch, Is.EqualTo(expectedNextEpoch));
            Assert.That(decoded, Is.EqualTo(forkId));
        }
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
        await using BeaconDiscovery discovery = new(config, BeaconChainSpec.Mainnet, store, new FixedIPResolver(PublicIp), Timestamper.Default, LimboLogs.Instance);

        // Resolving the private container is what regressed; Start would mask it behind a bind and live traffic.
        NettyDiscoveryV5Handler handler = discovery.CreateDiscv5Services(PublicIp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler, Is.Not.Null);
            Assert.That(discovery.LocalNodeRecord, Is.Not.Null);
        }
    }

    [Test]
    [Explicit("live mainnet discovery")]
    [CancelAfter(180_000)]
    public async Task Discovers_live_mainnet_peers_with_current_fork_digest(CancellationToken token)
    {
        const int targetCandidates = 15;
        BeaconChainConfig config = new() { Discv5Port = 0 }; // ephemeral UDP port
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = new(config, BeaconChainSpec.Mainnet, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);

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

    private sealed class FixedIPResolver(IPAddress ip) : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default)
            => new(new IIPResolver.NethermindIp(ip, ip));
    }
}
