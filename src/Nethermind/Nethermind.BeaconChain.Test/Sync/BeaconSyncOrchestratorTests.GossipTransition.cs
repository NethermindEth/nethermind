// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.P2P.Gossip;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

// altair/p2p-interface.md "Transitioning the gossip": both digests are live from one epoch before a digest change until two epochs after it.
public partial class BeaconSyncOrchestratorTests
{
    private const ulong MainnetBpo2Epoch = 419_072;

    [Test]
    public async Task Slot_ticks_join_the_next_digest_one_epoch_early_and_drop_the_previous_one_two_epochs_after([Range(-2, 2)] int startEpochFromBoundary)
    {
        ulong startEpoch = (ulong)((long)MainnetBpo2Epoch + startEpochFromBoundary);
        ulong startSlot = startEpoch * Spec.SlotsPerEpoch + 2;
        Harness harness = CreateHarness(anchorSlot: startSlot - 10, wallSlot: startSlot);
        byte[] bpo1Digest = ForkDigest.Compute(Spec, MainnetBpo2Epoch - 1);
        byte[] bpo2Digest = ForkDigest.Compute(Spec, MainnetBpo2Epoch);
        Dictionary<string, FakeTopic> topics = [];
        List<string> requested = [];

        harness.Orchestrator.StartGossip(id => { requested.Add(id); return topics[id] = new FakeTopic(); });
        for (ulong epoch = startEpoch; epoch <= MainnetBpo2Epoch + 3; epoch++)
        {
            if (epoch > startEpoch)
            {
                await harness.Orchestrator.ProcessSlotAsync(epoch * Spec.SlotsPerEpoch, CancellationToken.None);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(IsSubscribed(topics, bpo1Digest, GossipTopics.BeaconBlock), Is.EqualTo(epoch < MainnetBpo2Epoch + 2), $"epoch {epoch}: previous digest");
                Assert.That(IsSubscribed(topics, bpo2Digest, GossipTopics.BeaconBlock), Is.EqualTo(epoch + 1 >= MainnetBpo2Epoch), $"epoch {epoch}: next digest");
                Assert.That(harness.Orchestrator.CurrentGossipDigest, Is.EqualTo(epoch >= MainnetBpo2Epoch ? bpo2Digest : bpo1Digest), $"epoch {epoch}: status digest switches at the boundary");
            }
        }

        Assert.That(requested, Is.Unique, "a live digest is subscribed once, not again on every slot");
    }

    [TestCaseSource(typeof(GossipDigestWindowTests), nameof(GossipDigestWindowTests.Boundaries))]
    public async Task Production_wiring_subscribes_both_routers_across_the_transition_window(ulong chainId, ulong boundary)
    {
        BeaconChainSpec spec = BeaconChainSpec.ForChainId(chainId);
        await using IContainer container = BuildGossipContainer(chainId, spec, boundary - 1, out BeaconSyncOrchestrator orchestrator, out BeaconDiscovery discovery);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        string[] sampledSubnets = SampledSubnetNames(discovery);
        byte[] previous = ForkDigest.Compute(spec, boundary - 1);
        byte[] next = ForkDigest.Compute(spec, boundary);
        bool gloas = boundary == spec.GloasForkEpoch;
        Dictionary<string, FakeTopic> topics = [];

        orchestrator.StartGossip(id => topics[id] = new FakeTopic());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sampledSubnets, Has.Length.EqualTo(8), "a base-custody node samples 8 columns on 8 subnets");
            foreach (byte[] digest in (byte[][])[previous, next])
            {
                Assert.That(IsSubscribed(topics, digest, GossipTopics.BeaconBlock), "both digests are live one epoch before the boundary");
                Assert.That(SubscribedColumnSubnets(topics, digest), Is.EquivalentTo(sampledSubnets), "the column router subscribes every sampled subnet on each digest");
            }

            Assert.That(IsSubscribed(topics, next, GossipTopics.ExecutionPayload), Is.EqualTo(gloas), "execution_payload exists only on a Gloas digest");
            Assert.That(topics.Keys, Has.None.EqualTo(GossipTopics.Topic(previous, GossipTopics.ExecutionPayload)), "never on the pre-Gloas digest");
            Assert.That(IsSubscribed(topics, next, GossipTopics.PayloadAttestationMessage), Is.EqualTo(gloas), "fork choice consumes PTC votes on a Gloas digest");
            Assert.That(topics.Keys, Has.None.EqualTo(GossipTopics.Topic(previous, GossipTopics.PayloadAttestationMessage)), "never on the pre-Gloas digest");
        }

        orchestrator.ReconcileGossipDigests(boundary + 1);
        Assert.That((IsSubscribed(topics, previous, GossipTopics.BeaconBlock), SubscribedColumnSubnets(topics, previous).Length), Is.EqualTo((true, sampledSubnets.Length)),
            "the previous digest is kept one epoch after the boundary");

        orchestrator.ReconcileGossipDigests(boundary + 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsSubscribed(topics, previous, GossipTopics.BeaconBlock), Is.False, "two epochs after the boundary the previous digest is dropped");
            Assert.That(SubscribedColumnSubnets(topics, previous), Is.Empty, "and so are its column subnets");
            Assert.That(SubscribedColumnSubnets(topics, next), Is.EquivalentTo(sampledSubnets), "the next digest keeps its column subnets");
            Assert.That(IsSubscribed(topics, next, GossipTopics.ExecutionPayload), Is.EqualTo(gloas));
        }
    }

    /// <summary>A slot tick queued behind imports can carry an epoch older than the one gossip started at; it must not drop the next digest.</summary>
    [Test]
    public async Task A_stale_tick_does_not_undo_the_window_gossip_started_at()
    {
        const ulong bpo1Epoch = 412_672;
        await using IContainer container = BuildGossipContainer(BlockchainIds.Mainnet, Spec, bpo1Epoch - 2, out BeaconSyncOrchestrator orchestrator, out BeaconDiscovery discovery);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        byte[] next = ForkDigest.Compute(Spec, bpo1Epoch);
        Dictionary<string, FakeTopic> topics = [];
        orchestrator.StartGossip(id => topics[id] = new FakeTopic());
        orchestrator.ReconcileGossipDigests(bpo1Epoch - 1);

        orchestrator.ReconcileGossipDigests(bpo1Epoch - 2);

        Assert.That(IsSubscribed(topics, next, GossipTopics.BeaconBlock), Is.True, "the next digest joined at the later epoch stays subscribed");
    }

    [Test]
    public async Task Successive_digest_changes_each_drop_the_digest_before_them()
    {
        const ulong fuluEpoch = 411_392;
        const ulong bpo1Epoch = 412_672;
        await using IContainer container = BuildGossipContainer(BlockchainIds.Mainnet, Spec, bpo1Epoch - 2, out BeaconSyncOrchestrator orchestrator, out BeaconDiscovery discovery);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        string[] sampledSubnets = SampledSubnetNames(discovery);
        byte[][] digests = [ForkDigest.Compute(Spec, fuluEpoch), ForkDigest.Compute(Spec, bpo1Epoch), ForkDigest.Compute(Spec, MainnetBpo2Epoch)];
        Dictionary<string, FakeTopic> topics = [];

        orchestrator.StartGossip(id => topics[id] = new FakeTopic());
        foreach ((ulong epoch, bool[] live) in new (ulong, bool[])[]
        {
            (bpo1Epoch - 2, [true, false, false]),
            (bpo1Epoch + 2, [false, true, false]),
            (MainnetBpo2Epoch - 2, [false, true, false]),
            (MainnetBpo2Epoch + 2, [false, false, true]),
        })
        {
            orchestrator.ReconcileGossipDigests(epoch);
            using (Assert.EnterMultipleScope())
            {
                for (int i = 0; i < digests.Length; i++)
                {
                    Assert.That(IsSubscribed(topics, digests[i], GossipTopics.BeaconBlock), Is.EqualTo(live[i]), $"epoch {epoch}: gossip digest {i}");
                    Assert.That(SubscribedColumnSubnets(topics, digests[i]), live[i] ? Is.EquivalentTo(sampledSubnets) : Is.Empty, $"epoch {epoch}: column digest {i}");
                }
            }
        }
    }

    [Test]
    public async Task Column_subnets_are_subscribed_on_the_first_slot_tick_after_discovery_has_a_custody([Values] bool retryAtBoundary)
    {
        ulong startEpoch = retryAtBoundary ? MainnetBpo2Epoch - 1 : MainnetBpo2Epoch + 3;
        ulong retrySlot = retryAtBoundary ? MainnetBpo2Epoch * Spec.SlotsPerEpoch : startEpoch * Spec.SlotsPerEpoch + 1;
        await using IContainer container = BuildGossipContainer(BlockchainIds.Mainnet, Spec, startEpoch, out BeaconSyncOrchestrator orchestrator, out BeaconDiscovery discovery);
        byte[][] live = retryAtBoundary ? [ForkDigest.Compute(Spec, MainnetBpo2Epoch - 1), ForkDigest.Compute(Spec, MainnetBpo2Epoch)] : [ForkDigest.Compute(Spec, MainnetBpo2Epoch)];
        Dictionary<string, FakeTopic> topics = [];

        orchestrator.StartGossip(id => topics[id] = new FakeTopic());
        Assert.That(live.Select(d => (IsSubscribed(topics, d, GossipTopics.BeaconBlock), SubscribedColumnSubnets(topics, d).Length)), Is.All.EqualTo((true, 0)),
            "without a custody only the beacon gossip starts");

        discovery.CreateDiscv5Services(IPAddress.Loopback);
        await orchestrator.ProcessSlotAsync(retrySlot, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            foreach (byte[] digest in live)
            {
                Assert.That(SubscribedColumnSubnets(topics, digest), Is.EquivalentTo(SampledSubnetNames(discovery)), "the next slot tick starts the column subnets on every live digest");
            }
        }
    }

    private static IContainer BuildGossipContainer(ulong chainId, BeaconChainSpec spec, ulong startEpoch, out BeaconSyncOrchestrator orchestrator, out BeaconDiscovery discovery)
    {
        ManualTimestamper timestamper = new(GossipDigestWindowTests.EpochStart(spec, startEpoch).AddSeconds(6));
        IContainer container = BeaconChainTestContainer.Builder(chainId).AddSingleton<ITimestamper>(timestamper).AddSingleton<IEngineDriver>(new ScriptedEngine()).Build();
        orchestrator = container.Resolve<BeaconSyncOrchestrator>();
        discovery = container.Resolve<BeaconDiscovery>();
        ulong anchorSlot = startEpoch * spec.SlotsPerEpoch;
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(anchorSlot);
        orchestrator.Initialize(new ScriptedImporter { Head = CreateHead(TestItem.KeccakA, anchorSlot, finalizedEpoch: startEpoch - 1) }, new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot);
        return container;
    }

    // fulu/das-core.md: a node samples SAMPLES_PER_SLOT columns of the custody derived from its advertised node id.
    private static string[] SampledSubnetNames(BeaconDiscovery discovery) =>
        [.. new NodeColumnCustody(discovery.LocalCustody.NodeId, discovery.LocalCustody.CustodyGroupCount).SampledColumns
            .Select(CustodyGroups.ComputeSubnetForDataColumnSidecar).Distinct().Select(GossipTopics.DataColumnSidecarTopicName)];

    private static bool IsSubscribed(Dictionary<string, FakeTopic> topics, byte[] digest, string name) =>
        topics.TryGetValue(GossipTopics.Topic(digest, name), out FakeTopic? topic) && topic.IsSubscribed;

    private static string[] SubscribedColumnSubnets(Dictionary<string, FakeTopic> topics, byte[] digest)
    {
        List<string> names = [];
        foreach ((string id, FakeTopic topic) in topics)
        {
            if (topic.IsSubscribed && GossipTopics.TryParse(id, out byte[]? topicDigest, out string? name)
                && topicDigest.AsSpan().SequenceEqual(digest) && GossipTopics.TryParseDataColumnSidecarTopicName(name, out _))
            {
                names.Add(name);
            }
        }

        return [.. names];
    }
}
