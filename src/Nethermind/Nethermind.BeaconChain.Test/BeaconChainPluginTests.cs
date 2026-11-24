// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
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
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.BeaconChain.ValidatorClient;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using NSubstitute;

namespace Nethermind.BeaconChain.Test;

public class BeaconChainPluginTests
{
    [Test]
    public void Plugin_wiring_resolves_service_and_database_and_is_gated_by_config()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(container.Resolve<BeaconChainService>(), Is.Not.Null);
        Assert.That(container.Resolve<IColumnsDb<BeaconChainDbColumns>>(), Is.Not.Null);
        Assert.That(container.Resolve<BeaconSyncOrchestrator>(), Is.Not.Null);
        BeaconChainSpec spec = container.Resolve<BeaconChainSpec>();
        Assert.That(spec.GenesisValidatorsRoot, Is.EqualTo(BeaconChainSpec.Mainnet.GenesisValidatorsRoot));
        Assert.That(spec.Forks.Select(static fork => fork.Epoch), Is.EqualTo(BeaconChainSpec.Mainnet.Forks.Select(static fork => fork.Epoch)));
        Assert.That(new BeaconChainPlugin(new BeaconChainConfig()).Enabled, Is.False);
        Assert.That(new BeaconChainPlugin(new BeaconChainConfig { Enabled = true }).Enabled, Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Started_libp2p_host_runs_the_gossip_validator_on_every_pubsub_message(CancellationToken token)
    {
        using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig { P2PPort = 0 }).Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();

        await p2p.StartAsync(token);

        Assert.That(p2p.VerifyMessageForTest?.Invoke(p2p.LocalPeerId!, new Message { Topic = "/eth2/beacon_block" }), Is.EqualTo(MessageValidity.Rejected));
        Message unknown = new() { Topic = "/eth2/00000000/beacon_blocks/ssz_snappy" };
        Assert.That(p2p.VerifyMessageForTest?.Invoke(p2p.LocalPeerId!, unknown), Is.EqualTo(MessageValidity.Rejected));
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

    [Test]
    public async Task Gossipsub_runs_the_StrictNoSign_policy()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();

        Assert.That(p2p.PubsubSettingsForTest.DefaultSignaturePolicy, Is.EqualTo(PubsubSettings.SignaturePolicy.StrictNoSign));
    }

    [Test]
    public async Task Gossipsub_seen_ttl_covers_two_epochs()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();

        Assert.That(p2p.PubsubSettingsForTest.MessageCacheTtl, Is.EqualTo(768_000));
    }

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
            string[] names = [.. GossipTopics.SubscribedTopicNames, .. GossipTopics.GloasTopicNames, .. GossipTopics.BeaconAttestationTopicNames];
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
        Assert.That(settings.PublishThreshold, Is.GreaterThan(GossipScoring.InvalidMessageDeliveriesWeight));
        Assert.That(settings.GraylistThreshold, Is.LessThanOrEqualTo(GossipScoring.InvalidMessageDeliveriesWeight).And.GreaterThan(4 * GossipScoring.InvalidMessageDeliveriesWeight));
        Assert.That(settings.DecayInterval, Is.EqualTo(settings.HeartbeatInterval));
        Assert.That(Math.Pow(decay, 768_000d / settings.DecayInterval), Is.EqualTo(0.01).Within(1e-9));
    }

    [Test]
    public async Task Gossipsub_pending_validation_bounds_follow_the_config()
    {
        using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig { GossipMaxPendingValidations = 7, GossipMaxPendingValidationBytes = 1000 }).Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        PubsubSettings settings = p2p.PubsubSettingsForTest;
        // Twice: the node's bound for messages and for columns, the vote queue of the largest votes, and a full set of parked Gloas sidecars.
        int parkedSidecarBytes = DataColumnSidecarPool.MaxPendingGloasSidecars * (int)DataColumnSidecarGloasSize.ComputeMax(container.Resolve<BeaconChainSpec>());

        Assert.That((settings.MaxPendingValidationMessages, settings.MaxPendingValidationBytes), Is.EqualTo((2 * (2 * 7 + 1024), 2 * (2 * 1000 + 1024 * 32 * 1024 + parkedSidecarBytes))));
    }

    [Test]
    public async Task Gossipsub_publishes_to_the_topic_mesh_only()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();

        Assert.That(p2p.PubsubSettingsForTest.FloodPublish, Is.False);
    }

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
    public void Plugin_wiring_shares_one_validator_and_subnet_set_of_attestations_and_of_sync_committees_between_gossip_the_orchestrator_and_the_validator_api()
    {
        Type[] shared = [typeof(SingleAttestationValidator), typeof(AttestationSubnets), typeof(SyncCommitteeValidator), typeof(SyncCommitteeSubnets)];
        List<(Type Consumer, Type Dependency, object? Instance)> resolved = [];
        using IContainer container = BeaconChainTestContainer.BuildTrackingDependencies(BeaconChainTestContainer.Builder(), resolved, shared);

        container.Resolve<IBeaconNodeValidatorApi>();
        Dictionary<Type, object> instances = shared.ToDictionary(static type => type, container.Resolve);

        Assert.That(resolved.Select(r => (r.Consumer, r.Dependency, ReferenceEquals(r.Instance, instances[r.Dependency]))), Is.EquivalentTo(new[]
        {
            (typeof(GossipRouter), typeof(SingleAttestationValidator), true),
            (typeof(GossipRouter), typeof(SyncCommitteeValidator), true),
            (typeof(BeaconSyncOrchestrator), typeof(AttestationSubnets), true),
            (typeof(BeaconSyncOrchestrator), typeof(SyncCommitteeSubnets), true),
            (typeof(BeaconNodeValidatorApi), typeof(SingleAttestationValidator), true),
            (typeof(BeaconNodeValidatorApi), typeof(AttestationSubnets), true),
            (typeof(BeaconNodeValidatorApi), typeof(SyncCommitteeValidator), true),
            (typeof(BeaconNodeValidatorApi), typeof(SyncCommitteeSubnets), true),
        }));
    }

    [Test]
    public void Plugin_wiring_resolves_the_beacon_api_host()
    {
        using IContainer container = BeaconChainTestContainer.Builder()
            .AddSingleton<IBeaconApiConfig>(new BeaconApiConfig())
            .AddSingleton(Substitute.For<IProcessExitSource>()) // registered by the runner in production
            .Build();

        Assert.That(container.Resolve<BeaconApiHost>(), Is.Not.Null);
    }

    private const string ValidatorKey = "0x989d34725a2bfc3f15105f3f5fc8741f436c25ee1ee4f948e425d6bcb8c56bce6e06c269635b7e985a7ffa639e2409bf";
    private const string FeeRecipient = "0x00000000000000000000000000000000000000fe";

    [TestCase(true, "http://127.0.0.1:9000", ValidatorKey, FeeRecipient, null)]
    [TestCase(true, "ftp://127.0.0.1:9000", ValidatorKey, FeeRecipient, "RemoteSignerUrl")]
    [TestCase(true, null, ValidatorKey, FeeRecipient, "RemoteSignerUrl")]
    [TestCase(true, "http://127.0.0.1:9000", "0x1234", FeeRecipient, "PublicKeys")]
    [TestCase(true, "http://127.0.0.1:9000", null, FeeRecipient, "PublicKeys")]
    [TestCase(true, "http://127.0.0.1:9000", ValidatorKey, null, "FeeRecipient")]
    [TestCase(true, "http://127.0.0.1:9000", ValidatorKey, "0x00fe", "FeeRecipient")]
    [TestCase(false, null, null, null, null)]
    public async Task Plugin_wiring_starts_the_validator_client_only_when_enabled_and_refuses_a_bad_config(bool enabled, string? remoteSignerUrl, string? publicKey, string? feeRecipient,
        string? refusedItem)
    {
        ValidatorClientConfig config = new() { Enabled = enabled, RemoteSignerUrl = remoteSignerUrl, PublicKeys = publicKey is null ? [] : [publicKey], FeeRecipient = feeRecipient };
        await using IContainer container = BeaconChainTestContainer.Builder().AddSingleton<IValidatorClientConfig>(config).Build();
        StartValidatorClient step = container.Resolve<StartValidatorClient>();

        if (refusedItem is not null)
        {
            Assert.That(Assert.Catch(() => step.Execute(CancellationToken.None))?.GetBaseException(),
                Is.TypeOf<InvalidConfigurationException>().With.Message.Contains($"ValidatorClient.{refusedItem}"));
            return;
        }

        await step.Execute(CancellationToken.None);
        if (!enabled) return;
        await container.Resolve<AttesterDutyRunner>().StopAsync();
        await container.Resolve<SyncCommitteeDutyRunner>().StopAsync();
        await container.Resolve<ProposerDutyRunner>().StopAsync();
    }

    [Test]
    public void Plugin_wiring_fails_loudly_for_an_unsupported_chain_id()
    {
        // A synthetic id, not a real network: naming one here makes the test fail the day we model it.
        const ulong unmodelledChainId = 0xDEADBEEF;
        using IContainer container = BeaconChainTestContainer.Builder(unmodelledChainId).Build();

        DependencyResolutionException wrapped = Assert.Throws<DependencyResolutionException>(
            () => container.Resolve<BeaconChainSpec>())!;
        UnsupportedBeaconNetworkException ex = (UnsupportedBeaconNetworkException)wrapped.GetBaseException();
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ex.ChainId, Is.EqualTo(unmodelledChainId));
        Assert.That(ex.Message, Does.Contain(unmodelledChainId.ToString()));
    }
}
