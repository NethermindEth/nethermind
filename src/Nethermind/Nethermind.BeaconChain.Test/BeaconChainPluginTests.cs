// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.Config;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

public class BeaconChainPluginTests
{
    [Test]
    public void Plugin_wiring_resolves_service_and_database_and_is_gated_by_config()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();

        Assert.Multiple(() =>
        {
            // Pulls the whole driver graph: orchestrator -> importer factory/engine driver and
            // P2P (peer pool -> peer manager -> libp2p host -> status/metadata sources, discv5).
            Assert.That(container.Resolve<BeaconChainService>(), Is.Not.Null);
            Assert.That(container.Resolve<IColumnsDb<BeaconChainDbColumns>>(), Is.Not.Null);
            Assert.That(container.Resolve<BeaconSyncOrchestrator>(), Is.Not.Null);
            // The spec is derived from the execution layer's chain id, not a separate config knob.
            Assert.That(container.Resolve<BeaconChainSpec>(), Is.SameAs(BeaconChainSpec.Mainnet));
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig()).Enabled, Is.False);
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig { Enabled = true }).Enabled, Is.True);
        });
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Started_libp2p_host_runs_the_gossip_validator_on_every_pubsub_message(CancellationToken token)
    {
        using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig { P2PPort = 0 }).Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();

        await p2p.StartAsync(token);

        // Without the validator the library accepts and forwards every message, including one on a topic the spec requires rejecting.
        Assert.That(p2p.VerifyMessageForTest?.Invoke(p2p.LocalPeerId!, new Message { Topic = "/eth2/beacon_block" }), Is.EqualTo(MessageValidity.Rejected));
        Message unknown = new() { Topic = "/eth2/00000000/beacon_blocks/ssz_snappy" };
        Assert.That(p2p.VerifyMessageForTest?.Invoke(p2p.LocalPeerId!, unknown), Is.EqualTo(MessageValidity.Rejected));
        // A message on a handled topic is checked off the router's monitor, and forwarded only once its verdict is given.
        string blockTopic = GossipTopics.Topic(ForkDigest.Compute(BeaconChainSpec.Mainnet, container.Resolve<SlotClock>().CurrentEpoch), GossipTopics.BeaconBlock);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(p2p.VerifyMessageForTest?.Invoke(p2p.LocalPeerId!, new Message { Topic = blockTopic }), Is.EqualTo(MessageValidity.Deferred));
            Assert.That(((PubsubRouter)p2p.RoutingStateForTest!).OnDeferredMessage, Is.Not.Null, "a deferred message the router cannot dispatch is dropped");
        }

        ITopic topic = p2p.GetTopic(unknown.Topic);
        topic.Unsubscribe();
        Assert.That(p2p.VerifyMessageForTest?.Invoke(p2p.LocalPeerId!, new Message { Topic = unknown.Topic }), Is.EqualTo(MessageValidity.Throttled));
        p2p.GetTopic(blockTopic).Unsubscribe();
        Assert.That(p2p.VerifyMessageForTest?.Invoke(p2p.LocalPeerId!, new Message { Topic = blockTopic }), Is.EqualTo(MessageValidity.Throttled));
    }

    /// <summary>p2p-interface.md "Topics and messages": the router drops a message carrying from, seqno, signature or key before the validator runs.</summary>
    /// <remarks>The validator does not check these fields itself, so the host must run the library's <c>StrictNoSign</c> policy.</remarks>
    [Test]
    public async Task Gossipsub_runs_the_StrictNoSign_policy()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();

        Assert.That(p2p.PubsubSettingsForTest.DefaultSignaturePolicy, Is.EqualTo(PubsubSettings.SignaturePolicy.StrictNoSign));
    }

    /// <summary>p2p-interface.md gossipsub parameters: seen_ttl is SLOT_DURATION_MS * SLOTS_PER_EPOCH * 2 // 1000 seconds.</summary>
    [Test]
    public async Task Gossipsub_seen_ttl_covers_two_epochs()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();

        Assert.That(p2p.PubsubSettingsForTest.MessageCacheTtl, Is.EqualTo(768_000));
    }

    /// <summary>Every topic of every scheduled fork digest, each column subnet included, scores only invalid deliveries before the router starts.</summary>
    /// <remarks>A topic missing from the table gets the library's default delivery score, which prunes honest peers of a sparse topic.</remarks>
    [Test]
    public async Task Gossipsub_scores_only_invalid_deliveries_on_every_topic_of_any_scheduled_fork_digest()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        Dictionary<string, TopicScoreParams> scores = p2p.PubsubSettingsForTest.TopicScoreParams;
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        List<string> missing = [];
        foreach (ulong epoch in GossipTopics.DigestRotationEpochs(spec, 0).Prepend(0UL))
        {
            byte[] digest = ForkDigest.Compute(spec, epoch);
            string[] names = [.. GossipTopics.SubscribedTopicNames, .. GossipTopics.GloasTopicNames];
            for (ulong subnet = 0; subnet < Eip7594DasConstants.DataColumnSidecarSubnetCount; subnet++)
            {
                names = [.. names, GossipTopics.DataColumnSidecarTopicName(subnet)];
            }

            missing.AddRange(names.Select(name => GossipTopics.Topic(digest, name)).Where(topic => scores.GetValueOrDefault(topic) is not
            {
                TopicWeight: 1, TimeInMeshWeight: 0, FirstMessageDeliveriesWeight: 0, MeshMessageDeliveriesWeight: 0, MeshFailurePenaltyWeight: 0,
                InvalidMessageDeliveriesWeight: GossipScoring.InvalidMessageDeliveriesWeight,
            }));
        }

        PubsubSettings settings = p2p.PubsubSettingsForTest;
        double decay = scores[GossipTopics.Topic(ForkDigest.Compute(spec, 0), GossipTopics.BeaconBlock)].InvalidMessageDeliveriesDecay;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(missing, Is.Empty);
        Assert.That((settings.BehaviorPenaltyWeight, settings.IPColocationFactorWeight, settings.AppSpecificWeight), Is.EqualTo((0d, 0d, 0d)));
        // One invalid delivery prunes the peer and stops gossip and publication to it, a second graylists it.
        Assert.That(settings.PublishThreshold, Is.GreaterThan(GossipScoring.InvalidMessageDeliveriesWeight));
        Assert.That(settings.GraylistThreshold, Is.LessThanOrEqualTo(GossipScoring.InvalidMessageDeliveriesWeight).And.GreaterThan(4 * GossipScoring.InvalidMessageDeliveriesWeight));
        // The count decays to a hundredth over two epochs: 768 decays of one second on mainnet.
        Assert.That(Math.Pow(decay, 768), Is.EqualTo(0.01).Within(1e-9));
    }

    /// <summary>The pending validation bounds come from the config; the router's own bounds are the node's plus the vote queue, so it never drops a deferred message unseen.</summary>
    [Test]
    public async Task Gossipsub_pending_validation_bounds_follow_the_config()
    {
        using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig { GossipMaxPendingValidations = 7, GossipMaxPendingValidationBytes = 1000 }).Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        PubsubSettings settings = p2p.PubsubSettingsForTest;

        // The router dispatches every message the node reserved room for: its bounds are twice the node's own plus a full vote queue of the largest votes.
        Assert.That((settings.MaxPendingValidationMessages, settings.MaxPendingValidationBytes), Is.EqualTo((2 * (7 + 1024), 2 * (1000 + 1024 * 32 * 1024))));
    }

    /// <summary>fulu/das-core.md "Reconstruction and cross-seeding": a reconstructed column goes to the topic mesh neighbors, not to every peer subscribed to the topic.</summary>
    [Test]
    public async Task Gossipsub_publishes_to_the_topic_mesh_only()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();

        Assert.That(p2p.PubsubSettingsForTest.FloodPublish, Is.False);
    }

    /// <summary>p2p-interface.md "Gossipsub size limits": an encoded RPC may reach max_message_size(), max_compressed_len(10 MiB) + 1024 bytes.</summary>
    /// <remarks>The library's own 1 MiB RPC and 512 KiB IWANT bounds would drop a legal block, and its 10,000 seen ids would forget most honest ids before seen_ttl.</remarks>
    [Test]
    public async Task Gossipsub_bounds_admit_a_max_size_rpc_and_size_the_seen_cache_for_seen_ttl_traffic()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        PubsubSettings settings = p2p.PubsubSettingsForTest;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(settings.MaxRpcBytes, Is.EqualTo(12_234_442));
        Assert.That(settings.MaxIwantResponseBytes, Is.EqualTo(12_234_442));
        // (64 committees * 16 target aggregators + 512 PTC votes + 128 column subnets + 5 single-message topics) per slot, over 64 slots.
        Assert.That(settings.MaxSeenMessageIds, Is.EqualTo(106_816));
    }

    /// <summary>Without discovery the peer manager knows no sampled column, so every custodian search and keep rule would be inert.</summary>
    [Test]
    public async Task Plugin_wiring_gives_the_peer_manager_the_discovery_that_knows_this_nodes_sampled_columns()
    {
        await using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig { Discv5Port = 0 }).Build();
        BeaconDiscovery discovery = container.Resolve<BeaconDiscovery>();
        PeerManager peerManager = container.Resolve<PeerManager>();

        discovery.CreateDiscv5Services(IPAddress.Loopback);

        Assert.That(peerManager.UncustodiedSampledColumns(), Is.EqualTo(new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns).And.Not.Empty,
            "with no peer connected, every sampled column lacks a custodian");
    }

    [Test]
    public void Plugin_wiring_resolves_the_beacon_api_host()
    {
        using IContainer container = BeaconChainTestContainer.Builder()
            .AddSingleton<IBeaconApiConfig>(new BeaconApiConfig())
            .AddSingleton(Substitute.For<IProcessExitSource>()) // registered by the runner in production
            .Build();

        // The host's own tests build it by hand, which cannot see a missing registration: the
        // discovery container threw at startup once for exactly that reason behind a green suite.
        Assert.That(container.Resolve<BeaconApiHost>(), Is.Not.Null);
    }

    [Test]
    public void Plugin_wiring_fails_loudly_for_an_unsupported_chain_id()
    {
        // A synthetic id, not a real network: naming one here makes the test fail the day we model it.
        const ulong unmodelledChainId = 0xDEADBEEF;
        using IContainer container = BeaconChainTestContainer.Builder(unmodelledChainId).Build();

        // Autofac wraps the factory's exception; the failure must still surface loudly with the chain id.
        DependencyResolutionException wrapped = Assert.Throws<DependencyResolutionException>(
            () => container.Resolve<BeaconChainSpec>())!;
        UnsupportedBeaconNetworkException ex = (UnsupportedBeaconNetworkException)wrapped.GetBaseException();
        Assert.Multiple(() =>
        {
            Assert.That(ex.ChainId, Is.EqualTo(unmodelledChainId));
            Assert.That(ex.Message, Does.Contain(unmodelledChainId.ToString()));
        });
    }
}
