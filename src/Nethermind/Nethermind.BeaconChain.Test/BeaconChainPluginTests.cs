// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Google.Protobuf;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
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

        // Without the validator the library accepts and forwards every message, including a signed one StrictNoSign forbids.
        Message signed = new() { Topic = "/eth2/00000000/beacon_block/ssz_snappy", Signature = ByteString.CopyFrom([1]) };
        Assert.That(p2p.VerifyMessageForTest?.Invoke(p2p.LocalPeerId!, signed), Is.EqualTo(MessageValidity.Rejected));
        ITopic topic = p2p.GetTopic(signed.Topic);
        topic.Unsubscribe();
        Assert.That(p2p.VerifyMessageForTest?.Invoke(p2p.LocalPeerId!, new Message { Topic = signed.Topic }), Is.EqualTo(MessageValidity.Throttled));
    }

    /// <summary>p2p-interface.md gossipsub parameters: seen_ttl is SLOT_DURATION_MS * SLOTS_PER_EPOCH * 2 // 1000 seconds.</summary>
    [Test]
    public async Task Gossipsub_seen_ttl_covers_two_epochs()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();

        Assert.That(p2p.PubsubSettingsForTest.MessageCacheTtl, Is.EqualTo(768_000));
    }

    /// <summary>p2p-interface.md "Gossipsub size limits": an encoded RPC may reach max_message_size(), max_compressed_len(10 MiB) + 1024 bytes.</summary>
    /// <remarks>The library's own 1 MiB RPC and 512 KiB IWANT bounds would drop a legal block, and its 10,000 seen ids would forget most ids before seen_ttl.</remarks>
    [Test]
    public async Task Gossipsub_bounds_admit_a_max_size_rpc_and_keep_every_id_for_seen_ttl()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        PubsubSettings settings = p2p.PubsubSettingsForTest;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.MaxRpcBytes, Is.EqualTo(12_234_442));
            Assert.That(settings.MaxIwantResponseBytes, Is.EqualTo(12_234_442));
            // (64 committees * 16 aggregators + 512 PTC votes + 128 column subnets + 5 single-message topics) per slot, over 64 slots.
            Assert.That(settings.MaxSeenMessageIds, Is.EqualTo(106_816));
        }
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
