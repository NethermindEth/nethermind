// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.P2P.Gossip.GossipDigestWindowTests;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class ColumnGossipRouterSubscriptionTests
{
    // The first slot of the mainnet BPO2 epoch, inside the window where the BPO1 digest is still subscribed.
    private const ulong Bpo2Slot = 13_410_304;
    private const ulong Bpo1Epoch = 412_672;
    private const ulong Bpo2Epoch = 419_072;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly byte[] Bpo1Digest = ForkDigest.Compute(Spec, Bpo1Epoch);
    private static readonly byte[] Bpo2Digest = ForkDigest.Compute(Spec, Bpo2Epoch);

    [Test]
    public void Each_digest_subscribes_the_started_subnets_once_and_unsubscribes_only_its_own()
    {
        using IContainer container = BuildContainer();
        ColumnGossipRouter router = container.Resolve<ColumnGossipRouter>();
        Dictionary<string, RecordingTopic> topics = [];
        List<string> requested = [];
        ulong[] subnets = [3, 5, 9];

        Assert.That(() => router.SubscribeDigest(Bpo2Digest), Throws.InvalidOperationException, "subscribing requires Start");

        router.Start(id => { requested.Add(id); return topics[id] = new RecordingTopic(); }, Bpo1Digest, subnets);
        router.SubscribeDigest(Bpo2Digest);
        router.SubscribeDigest(Bpo2Digest);
        router.UnsubscribeDigest(Bpo1Digest);
        router.UnsubscribeDigest(Bpo1Digest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requested, Is.EquivalentTo(subnets.SelectMany(s => new[] { SubnetTopic(Bpo1Digest, s), SubnetTopic(Bpo2Digest, s) })),
                "every digest subscribes exactly the started subnets, each once");
            Assert.That(subnets.Select(s => topics[SubnetTopic(Bpo1Digest, s)].IsSubscribed), Is.All.False, "the unsubscribed digest drops all its subnets");
            Assert.That(subnets.Select(s => topics[SubnetTopic(Bpo2Digest, s)].IsSubscribed), Is.All.True, "the other digest keeps its subnets");
        }
    }

    [Test]
    public void Reconstructed_column_is_published_on_no_digest()
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        using IContainer container = BuildContainer();
        ColumnGossipRouter router = container.Resolve<ColumnGossipRouter>();
        Dictionary<string, RecordingTopic> topics = [];
        router.Start(id => topics[id] = new RecordingTopic(), Bpo1Digest, [.. Enumerable.Range(0, required + 1).Select(static i => (ulong)i)]);
        router.SubscribeDigest(Bpo2Digest);

        // An imported block's header needs no key cache or lookahead, so its columns reach reconstruction.
        BeaconBlockHeader header = DataColumnSidecarTestFixture.BuildValidSidecar(0, Bpo2Slot).SignedBlockHeader!.Message!;
        Hash256 blockRoot = SszRoots.HashTreeRoot(header);
        SignedBeaconBlock block = SignedBeaconBlockBuilders.CreateMinimalBlock(Bpo2Slot);
        block.Signature = DataColumnSidecarTestFixture.BuildValidSidecar(0, Bpo2Slot).SignedBlockHeader!.Signature;
        container.Resolve<BeaconChainStore>().PutBlock(blockRoot, block);

        for (ulong column = 0; column < required; column++)
        {
            router.Handle(column, gloasTopic: false, Snappy.CompressToArray(DataColumnSidecar.Encode(DataColumnSidecarTestFixture.BuildValidSidecar(column, Bpo2Slot))));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<DataColumnSidecarPool>().TryGet(blockRoot, required, out _), Is.True, "the missing column was reconstructed");
            Assert.That(topics[SubnetTopic(Bpo2Digest, required)].Published, Is.Empty, "the pinned library signs every publish, which StrictNoSign peers drop");
            Assert.That(topics[SubnetTopic(Bpo1Digest, required)].Published, Is.Empty, "never re-broadcast on the other fork's topic");
        }
    }

    private static string SubnetTopic(byte[] digest, ulong subnet) => GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(subnet));

    private static IContainer BuildContainer() =>
        BeaconChainTestContainer.Builder()
            .AddSingleton<ITimestamper>(new ManualTimestamper(EpochStart(Spec, Bpo2Epoch).AddSeconds(6)))
            .Build();
}
