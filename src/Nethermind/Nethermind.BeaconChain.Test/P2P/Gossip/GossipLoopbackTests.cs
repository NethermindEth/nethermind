// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class GossipLoopbackTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    /// <summary>A block this host publishes passes the other host's <c>StrictNoSign</c> check and validator and reaches its gossip router.</summary>
    /// <remarks>p2p-interface.md "Topics and messages": a published message omits from, seqno, signature and key, which the receiver enforces.</remarks>
    [Test]
    [CancelAfter(120_000)]
    public async Task Block_published_on_one_host_reaches_the_gossip_router_on_the_other(CancellationToken token)
    {
        SlotClock slotClock = new(Spec, Timestamper.Default);
        byte[] digest = ForkDigest.Compute(Spec, slotClock.CurrentEpoch);
        string blockTopic = GossipTopics.Topic(digest, GossipTopics.BeaconBlock);
        GossipRouter router = new(Spec, slotClock, LimboLogs.Instance);

        await using BeaconP2P publisher = CreateHost();
        await using BeaconP2P subscriber = CreateHost(new GossipMessageValidator(router, new ColumnGossipRouter(Spec, slotClock, LimboLogs.Instance), Spec, slotClock));
        await publisher.StartAsync(token);
        await subscriber.StartAsync(token);

        ITopic publisherTopic = publisher.GetTopic(blockTopic);
        router.Start(subscriber.GetTopic, digest);
        TaskCompletionSource<ForkedSignedBeaconBlock> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeaconBlockReceived += block => received.TrySetResult(block);

        subscriber.Discover([LoopbackAddress(publisher)]);

        // Republish (with distinct payloads, so dedup cannot hide a delivery) until the mesh has
        // formed and a message makes it across.
        for (ulong attempt = 0; !received.Task.IsCompleted; attempt++)
        {
            SignedBeaconBlock block = TestChain.CreateBlock(slotClock.CurrentSlot, new Hash256(ValueKeccak.Compute([(byte)attempt]).Bytes));
            publisherTopic.Publish(Snappy.CompressToArray(SignedBeaconBlock.Encode(block)));
            await Task.WhenAny(received.Task, Task.Delay(500, token));
            token.ThrowIfCancellationRequested();
        }

        ForkedSignedBeaconBlock receivedBlock = await received.Task;
        Assert.That(receivedBlock.Slot, Is.EqualTo(slotClock.CurrentSlot).Within(1), "the published block round-trips the mesh");
    }

    /// <summary>A column this host reconstructs reaches a mesh neighbor on that column's subnet, which enforces <c>StrictNoSign</c>.</summary>
    /// <remarks>fulu/das-core.md "Reconstruction and cross-seeding": a reconstructed column of a subscribed subnet MUST be sent to the topic mesh neighbors.</remarks>
    [Test]
    [CancelAfter(120_000)]
    public async Task Reconstructed_column_reaches_the_mesh_neighbor_on_its_subnet(CancellationToken token)
    {
        const ulong slot = 13_410_304;
        const ulong reconstructed = Eip7594DasConstants.RequiredColumnsForReconstruction;
        SlotClock slotClock = new(Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + slot * Spec.SecondsPerSlot + 6)));
        byte[] digest = ForkDigest.Compute(Spec, Spec.GetEpoch(slot));
        string topicId = GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(reconstructed));
        // An imported block's header, so its sidecars pass every check and its reconstructed columns may be published.
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        DataColumnSidecarTestFixture.StoreAsImported(store, DataColumnSidecarTestFixture.BuildValidSidecar(0, slot));
        ColumnGossipRouter columns = new(Spec, slotClock, LimboLogs.Instance, new DataColumnSidecarPool(), store);

        await using BeaconP2P reconstructing = CreateHost(new GossipMessageValidator(new GossipRouter(Spec, slotClock, LimboLogs.Instance), columns, Spec, slotClock));
        await using BeaconP2P neighbor = CreateHost();
        await reconstructing.StartAsync(token);
        await neighbor.StartAsync(token);
        columns.Start(reconstructing.GetTopic, digest, [.. Enumerable.Range(0, (int)reconstructed + 1).Select(static subnet => (ulong)subnet)]);
        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        neighbor.GetTopic(topicId).OnMessage += (_, data) => received.TrySetResult(data);
        neighbor.Discover([LoopbackAddress(reconstructing)]);
        while (!IsMeshNeighbor(reconstructing, topicId, neighbor.LocalPeerId!))
        {
            await Task.Delay(100, token);
        }

        for (ulong column = 0; column < reconstructed; column++)
        {
            columns.Handle(column, gloasTopic: false, Snappy.CompressToArray(DataColumnSidecar.Encode(DataColumnSidecarTestFixture.BuildValidSidecar(column, slot))));
        }

        Assert.That(await received.Task, Is.EqualTo(Snappy.CompressToArray(DataColumnSidecar.Encode(DataColumnSidecarTestFixture.BuildValidSidecar(reconstructed, slot)))));
    }

    private static bool IsMeshNeighbor(BeaconP2P node, string topicId, PeerId peer)
    {
        IRoutingStateContainer router = node.RoutingStateForTest!;
        lock (router)
        {
            return router.Mesh.TryGetValue(topicId, out HashSet<PeerId>? mesh) && mesh.Contains(peer);
        }
    }

    /// <summary>A gossip message of any legal size crosses a real session whole, so its sender is not disconnected for a truncated RPC.</summary>
    /// <remarks>p2p-interface.md "Gossipsub size limits" allow a compressed payload up to max_compressed_len(10 MiB); a message over one yamux window
    /// spans several frames, and Nethermind.Libp2p 1.0.0 truncated such an RPC in the yamux channel (see ContiguousChunkProtocol).</remarks>
    [TestCase(64)]
    [TestCase(300 * 1024)]
    [TestCase(1200 * 1024)]
    [TestCase(6 * 1024 * 1024)]
    [CancelAfter(60_000)]
    public async Task A_gossip_message_of_any_legal_size_reaches_the_other_host(int size, CancellationToken token)
    {
        string topicId = GossipTopics.Topic(ForkDigest.Compute(Spec, 0), GossipTopics.DataColumnSidecarTopicName(0));
        await using BeaconP2P publisher = CreateHost();
        await using BeaconP2P subscriber = CreateHost();
        await publisher.StartAsync(token);
        await subscriber.StartAsync(token);
        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.GetTopic(topicId).OnMessage += (_, data) => received.TrySetResult(data);
        ITopic topic = publisher.GetTopic(topicId);
        subscriber.Discover([LoopbackAddress(publisher)]);

        byte[] message = new byte[size];
        Random.Shared.NextBytes(message);
        while (!received.Task.IsCompleted)
        {
            // The publisher sends only once it has learnt the subscription, so it repeats until the subscriber has the message.
            token.ThrowIfCancellationRequested();
            topic.Publish(message);
            await Task.WhenAny(received.Task, Task.Delay(500, token));
        }

        Assert.That(await received.Task, Is.EqualTo(message));
    }

    private static BeaconP2P CreateHost(GossipMessageValidator? validator = null)
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        return new BeaconP2P(config, Spec, store, new BeaconChainStatusHolder(Spec, Timestamper.Default), new LocalMetadataSource(), new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance,
            validator);
    }

    private static Multiaddress LoopbackAddress(BeaconP2P node)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        if (!address.Contains("/p2p/"))
        {
            address += $"/p2p/{node.LocalPeerId}";
        }

        return Multiaddress.Decode(address);
    }
}
