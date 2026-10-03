// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Google.Protobuf;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

// altair/p2p-interface.md "Transitioning the gossip": post-fork topics are joined in advance, and pre-fork topics
// are dropped two epochs after the fork; every fork digest change counts, EIP-7892 BPO boundaries included.
public class GossipDigestWindowTests
{
    private const ulong MainnetBpo2Epoch = 419_072;

    public static IEnumerable<TestCaseData> Boundaries()
    {
        yield return new TestCaseData(BlockchainIds.Sepolia, BeaconChainSpec.Sepolia.GloasForkEpoch).SetArgDisplayNames("Sepolia Gloas fork");
        yield return new TestCaseData(BlockchainIds.Mainnet, MainnetBpo2Epoch).SetArgDisplayNames("mainnet BPO2");
    }

    [TestCaseSource(nameof(Boundaries))]
    public void Next_digest_is_joined_one_epoch_early_and_the_previous_one_kept_until_two_epochs_after(ulong chainId, ulong boundary)
    {
        BeaconChainSpec spec = BeaconChainSpec.ForChainId(chainId);
        (byte[] Digest, bool Gloas) previous = (ForkDigest.Compute(spec, boundary - 1), boundary - 1 >= spec.GloasForkEpoch);
        (byte[] Digest, bool Gloas) next = (ForkDigest.Compute(spec, boundary), boundary >= spec.GloasForkEpoch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GossipTopics.DigestsAround(spec, boundary - 2), Is.EqualTo(new[] { previous }), "two epochs before, only the current digest");
            Assert.That(GossipTopics.DigestsAround(spec, boundary - 1), Is.EqualTo(new[] { previous, next }), "one epoch before, the next digest is joined");
            Assert.That(GossipTopics.DigestsAround(spec, boundary), Is.EqualTo(new[] { previous, next }), "at the boundary");
            Assert.That(GossipTopics.DigestsAround(spec, boundary + 1), Is.EqualTo(new[] { previous, next }), "one epoch after, the previous digest is kept");
            Assert.That(GossipTopics.DigestsAround(spec, boundary + 2), Is.EqualTo(new[] { next }), "two epochs after, the previous digest is dropped");
        }
    }

    [Test]
    public void Window_ends_at_the_far_future_epoch_instead_of_wrapping([Values(ulong.MaxValue - 1, ulong.MaxValue)] ulong epoch) =>
        Assert.That(GossipTopics.DigestsAround(BeaconChainSpec.Mainnet, epoch)[^1].Digest, Is.EqualTo(ForkDigest.Compute(BeaconChainSpec.Mainnet, ulong.MaxValue)));

    [TestCaseSource(nameof(Boundaries))]
    public void Validator_accepts_exactly_the_digests_the_orchestrator_subscribes(ulong chainId, ulong boundary)
    {
        BeaconChainSpec spec = BeaconChainSpec.ForChainId(chainId);
        ManualTimestamper timestamper = new(EpochStart(spec, boundary - 3));
        using IContainer container = BeaconChainTestContainer.Builder(chainId).AddSingleton<ITimestamper>(timestamper).Build();
        GossipRouter router = container.Resolve<GossipRouter>();
        GossipMessageValidator validator = container.Resolve<GossipMessageValidator>();
        BeaconSyncOrchestrator orchestrator = container.Resolve<BeaconSyncOrchestrator>();
        Dictionary<string, RecordingTopic> topics = [];
        router.Start(id => topics[id] = new RecordingTopic(), ForkDigest.Compute(spec, boundary - 3));
        byte[][] candidates = [.. Enumerable.Range(-4, 9).Select(offset => ForkDigest.Compute(spec, (ulong)((long)boundary + offset))).DistinctBy(static d => d.ToHexString())];

        for (ulong epoch = boundary - 3; epoch <= boundary + 3; epoch++)
        {
            timestamper.Set(EpochStart(spec, epoch));
            orchestrator.ReconcileGossipDigests(epoch);

            string[] subscribed = [.. candidates.Where(d => topics.TryGetValue(GossipTopics.Topic(d, GossipTopics.BeaconBlock), out RecordingTopic? t) && t.IsSubscribed).Select(static d => d.ToHexString())];
            string[] accepted = [.. candidates.Where(d => IsAccepted(validator, router, d)).Select(static d => d.ToHexString())];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accepted, Is.EqualTo(subscribed), $"epoch {epoch}: acceptance and subscription share one window");
                Assert.That(subscribed, Has.Length.EqualTo(epoch + 1 >= boundary && epoch < boundary + 2 ? 2 : 1), $"epoch {epoch}: digests live");
            }
        }
    }

    internal static DateTime EpochStart(BeaconChainSpec spec, ulong epoch) =>
        DateTime.UnixEpoch.AddSeconds(spec.GenesisTime + epoch * spec.SlotsPerEpoch * spec.SecondsPerSlot);

    // An undecodable payload is dropped as unknown only when its digest is outside the window.
    private static bool IsAccepted(GossipMessageValidator validator, GossipRouter router, byte[] digest)
    {
        long unknownBefore = router.GetDropCount(GossipDropReason.UnknownTopic);
        validator.Validate(new Message { Topic = GossipTopics.Topic(digest, GossipTopics.BeaconBlock), Data = ByteString.CopyFrom(0xff, 0xff, 0xff, 0xff) }, GossipVerdict.None);
        return router.GetDropCount(GossipDropReason.UnknownTopic) == unknownBefore;
    }

    /// <summary>A pubsub topic that records its subscription state and delivers nothing.</summary>
    internal sealed class RecordingTopic : ITopic
    {
        public event Action<PeerId, byte[]>? OnMessage { add { } remove { } }

        public bool IsSubscribed { get; private set; }

        public List<byte[]> Published { get; } = [];

        public void Subscribe() => IsSubscribed = true;

        public void Unsubscribe() => IsSubscribed = false;

        public void Publish(byte[] value) => Published.Add(value);

        public void Publish(IMessage value) { }
    }
}
