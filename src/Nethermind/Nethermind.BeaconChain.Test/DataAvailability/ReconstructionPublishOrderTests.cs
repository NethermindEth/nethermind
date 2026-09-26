// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>
/// Reconstructed columns are marked in the anti-equivocation cache BEFORE they are published. The
/// order is observed, not assumed: the publish itself echoes the sidecar straight back into the
/// router as a gossip arrival, exactly the race a real mesh produces, and only a cache marked first
/// rejects that echo as a duplicate. Marking after publishing would accept the echo as a fresh
/// column and this test would see no duplicate drop at all.
/// </summary>
public class ReconstructionPublishOrderTests
{
    // A Fulu/BPO2-era mainnet slot, so the fork digest the router subscribes under is well defined.
    private const ulong CurrentSlot = 13_410_304;
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    public void A_gossip_echo_of_a_reconstructed_column_arriving_during_its_publish_is_dropped_as_a_duplicate()
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        // Held over gossip: columns 0..63. Subscribed but missing: subnet 64, the one that gets published to.
        ulong[] subscribedSubnets = [.. Enumerable.Range(0, required + 1).Select(i => (ulong)i)];
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot).AddSeconds(6);
        DataColumnSidecarPool pool = new();
        ColumnGossipRouter router = new(Spec, new SlotClock(Spec, new ManualTimestamper(now)), LimboLogs.Instance, pool);
        Dictionary<string, EchoingTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new EchoingTopic(message => router.Handle(SubnetOf(id), gloasTopic: false, message)), digest, subscribedSubnets);
        List<DataColumnSidecar> raised = [];
        router.DataColumnSidecarReceived += raised.Add;

        for (ulong column = 0; column < (ulong)required; column++)
        {
            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot);
            router.Handle(column, gloasTopic: false, Snappy.CompressToArray(DataColumnSidecar.Encode(sidecar)));
        }

        EchoingTopic publishedTo = topics[GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName((ulong)required))];
        Assert.Multiple(() =>
        {
            Assert.That(publishedTo.Published, Has.Count.EqualTo(1), "the one reconstructed column with a subscribed subnet was published, and echoed back once");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(1),
                "the echo found the cache already marked; had it been marked after publishing, the echo would have been accepted as new");
            Assert.That(raised.Select(s => s.Index), Is.Unique, "no column is raised twice");
            Assert.That(raised, Has.Count.EqualTo(Eip7594DasConstants.NumberOfColumns));
        });
    }

    private static ulong SubnetOf(string topic)
    {
        GossipTopics.TryParse(topic, out _, out string? name);
        GossipTopics.TryParseDataColumnSidecarTopicName(name!, out ulong subnet);
        return subnet;
    }

    /// <summary>A topic that hands everything published on it straight back to the router's validator entry, as a mesh peer relaying it would.</summary>
    private sealed class EchoingTopic(Action<byte[]> receive) : ITopic
    {
        public event Action<byte[]>? OnMessage { add { } remove { } }

        public List<byte[]> Published { get; } = [];

        public bool IsSubscribed { get; private set; }

        public void Subscribe() => IsSubscribed = true;

        public void Unsubscribe() => IsSubscribed = false;

        public void Publish(byte[] value)
        {
            Published.Add(value);
            receive(value);
        }

        public void Publish(IMessage value) { }
    }
}
