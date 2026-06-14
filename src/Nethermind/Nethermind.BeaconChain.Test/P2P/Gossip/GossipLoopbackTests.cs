// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class GossipLoopbackTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    // The mesh itself forms in-process (subscriptions exchange, the topic is grafted both ways and
    // the RPC reaches the peer router — verified with trace logging), but dotnet-libp2p
    // 1.0.0-preview.45 `PubsubRouter.Publish` always signs messages (attaching from/seqno/signature)
    // while `StrictNoSign` reception requires an empty signature, so the receiver rejects every
    // self-published message. Run explicitly once the library can publish StrictNoSign-compliant
    // messages; the GossipRouter handler path is covered by GossipRouterTests meanwhile.
    [Explicit("dotnet-libp2p preview.45 cannot publish StrictNoSign-compliant messages, so loopback delivery is rejected by the receiving router")]
    [Test]
    [CancelAfter(120_000)]
    public async Task Block_published_on_one_host_reaches_the_gossip_router_on_the_other(CancellationToken token)
    {
        SlotClock slotClock = new(Spec, Timestamper.Default);
        byte[] digest = ForkDigest.Compute(Spec, slotClock.CurrentEpoch);
        string blockTopic = GossipTopics.Topic(digest, GossipTopics.BeaconBlock);

        await using BeaconP2P publisher = CreateHost();
        await using BeaconP2P subscriber = CreateHost();
        await publisher.StartAsync(token);
        await subscriber.StartAsync(token);

        ITopic publisherTopic = publisher.GetTopic(blockTopic);
        GossipRouter router = new(Spec, slotClock, LimboLogs.Instance);
        router.Start(subscriber.GetTopic, digest);
        TaskCompletionSource<SignedBeaconBlock> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

        SignedBeaconBlock receivedBlock = await received.Task;
        Assert.That(receivedBlock.Message!.Slot, Is.EqualTo(slotClock.CurrentSlot).Within(1), "the published block round-trips the mesh");
    }

    private static BeaconP2P CreateHost()
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        return new BeaconP2P(config, Spec, store, new BeaconChainStatusHolder(Spec, Timestamper.Default), new LocalMetadataSource(), LimboLogs.Instance);
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
