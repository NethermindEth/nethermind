// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class ColumnGossipRouterTests
{
    // A Fulu/BPO2-era mainnet slot, matching GossipRouterTests, so the current digest is well defined.
    private const ulong CurrentSlot = 13_410_304;
    private const ulong SubnetId = 5; // matches ColumnIndex below under the mainnet column==subnet coincidence.
    private const ulong ColumnIndex = SubnetId;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    private static ColumnGossipRouter CreateRouter(DataColumnSidecarPool? pool = null, double secondsIntoSlot = 6.0)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot).AddSeconds(secondsIntoSlot);
        return new ColumnGossipRouter(Spec, new SlotClock(Spec, new ManualTimestamper(now)), LimboLogs.Instance, pool);
    }

    private static byte[] Message(DataColumnSidecar sidecar) => Snappy.CompressToArray(DataColumnSidecar.Encode(sidecar));

    [Test]
    public void Start_subscribes_exactly_the_given_subnets_and_rotation_moves_them()
    {
        byte[] bpo1Digest = ForkDigest.Compute(Spec, 412_672);
        byte[] bpo2Digest = ForkDigest.Compute(Spec, 419_072);
        Dictionary<string, FakeTopic> topics = [];
        ColumnGossipRouter router = CreateRouter();
        int received = 0;
        router.DataColumnSidecarReceived += _ => received++;

        Assert.That(() => router.RotateDigest(bpo2Digest), Throws.InvalidOperationException, "rotation requires Start");

        ulong[] subnets = [3, 5, 9];
        router.Start(id => topics[id] = new FakeTopic(), bpo1Digest, subnets);

        List<string> expectedTopics = [];
        foreach (ulong subnet in subnets)
        {
            expectedTopics.Add(GossipTopics.Topic(bpo1Digest, GossipTopics.DataColumnSidecarTopicName(subnet)));
        }

        Assert.That(topics.Keys, Is.EquivalentTo(expectedTopics), "exactly the given subnets are subscribed, not all 128");

        FakeTopic subnet5Bpo1 = topics[GossipTopics.Topic(bpo1Digest, GossipTopics.DataColumnSidecarTopicName(5))];
        subnet5Bpo1.Deliver(Message(DataColumnSidecarTestFixture.BuildValidSidecar(5, CurrentSlot)));
        Assert.That(received, Is.EqualTo(1));

        router.RotateDigest(bpo2Digest);
        FakeTopic subnet5Bpo2 = topics[GossipTopics.Topic(bpo2Digest, GossipTopics.DataColumnSidecarTopicName(5))];
        subnet5Bpo1.Deliver(Message(DataColumnSidecarTestFixture.BuildValidSidecar(5, CurrentSlot - 1, seed: 0x20)));
        subnet5Bpo2.Deliver(Message(DataColumnSidecarTestFixture.BuildValidSidecar(5, CurrentSlot - 2, seed: 0x30)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(subnet5Bpo1.IsSubscribed, Is.False, "old subnet topics are unsubscribed on rotation");
            Assert.That(subnet5Bpo2.IsSubscribed, "new subnet topics are subscribed on rotation");
            Assert.That(received, Is.EqualTo(2), "only the new digest topic delivers after rotation");
        }
    }

    [Test]
    public void A_correctly_formed_sidecar_on_its_own_subnet_raises_the_event_and_populates_the_pool()
    {
        DataColumnSidecarPool pool = new();
        ColumnGossipRouter router = CreateRouter(pool);
        Dictionary<string, FakeTopic> topics = [];
        router.Start(id => topics[id] = new FakeTopic(), ForkDigest.Compute(Spec, 419_072), [SubnetId]);
        DataColumnSidecar received = null!;
        router.DataColumnSidecarReceived += s => received = s;

        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot);
        topics[GossipTopics.Topic(ForkDigest.Compute(Spec, 419_072), GossipTopics.DataColumnSidecarTopicName(SubnetId))].Deliver(Message(sidecar));

        Assert.That(received, Is.Not.Null);
        Hash256 blockRoot = SszRoots.HashTreeRoot(sidecar.SignedBlockHeader!.Message!);
        Assert.That(pool.TryGet(blockRoot, ColumnIndex, out DataColumnSidecar? pooled), Is.True, "an accepted sidecar is added to the serving pool");
        Assert.That(pooled!.Index, Is.EqualTo(ColumnIndex));
    }

    private static IEnumerable<TestCaseData> DroppedCases()
    {
        yield return new TestCaseData(new Func<DataColumnSidecar>(() =>
        {
            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot);
            sidecar.KzgCommitments = [];
            sidecar.Column = [];
            sidecar.KzgProofs = [];
            return sidecar;
        }), ColumnGossipDropReason.FailedStructure).SetName("empty commitments fails structural check");

        yield return new TestCaseData(
            new Func<DataColumnSidecar>(() => DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex + 1, CurrentSlot)),
            ColumnGossipDropReason.WrongSubnet).SetName("column whose subnet does not match the subscribed subnet");

        yield return new TestCaseData(
            new Func<DataColumnSidecar>(() => DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot + 2)),
            ColumnGossipDropReason.FutureSlot).SetName("slot two ahead of the wall clock");

        yield return new TestCaseData(
            new Func<DataColumnSidecar>(() => DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot - Spec.SlotsPerEpoch - 1)),
            ColumnGossipDropReason.StaleSlot).SetName("sidecar older than one epoch");

        yield return new TestCaseData(new Func<DataColumnSidecar>(() =>
        {
            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot);
            byte[] tamperedSibling = sidecar.KzgCommitmentsInclusionProof![0].Bytes.ToArray();
            tamperedSibling[0] ^= 0xFF;
            sidecar.KzgCommitmentsInclusionProof[0] = new Hash256(tamperedSibling);
            return sidecar;
        }), ColumnGossipDropReason.FailedInclusionProof).SetName("tampered inclusion proof");

        yield return new TestCaseData(new Func<DataColumnSidecar>(() =>
        {
            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot);
            byte[] tampered = sidecar.KzgProofs![0].AsSpan().ToArray();
            tampered[0] ^= 0xFF;
            sidecar.KzgProofs[0] = Nethermind.Merge.Plugin.SszRest.SszKzgCommitment.FromSpan(tampered);
            return sidecar;
        }), ColumnGossipDropReason.FailedKzgProofs).SetName("tampered KZG proof");
    }

    [TestCaseSource(nameof(DroppedCases))]
    public void Invalid_sidecars_are_dropped_for_the_expected_reason_and_never_raise_the_event(Func<DataColumnSidecar> build, ColumnGossipDropReason reason)
    {
        ColumnGossipRouter router = CreateRouter();
        Dictionary<string, FakeTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new FakeTopic(), digest, [SubnetId]);
        int received = 0;
        router.DataColumnSidecarReceived += _ => received++;

        topics[GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(SubnetId))].Deliver(Message(build()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Is.Zero, "no event for a dropped sidecar");
            Assert.That(router.GetDropCount(reason), Is.EqualTo(1), "the drop is counted under its reason");
        }
    }

    [Test]
    public void Duplicate_sidecar_for_the_same_slot_proposer_and_index_is_dropped_and_counted()
    {
        ColumnGossipRouter router = CreateRouter();
        Dictionary<string, FakeTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new FakeTopic(), digest, [SubnetId]);
        int received = 0;
        router.DataColumnSidecarReceived += _ => received++;
        byte[] message = Message(DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot));

        FakeTopic topic = topics[GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(SubnetId))];
        topic.Deliver(message);
        topic.Deliver(message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Is.EqualTo(1), "only the first copy raises the event");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(1));
        }
    }

    private sealed class FakeTopic : ITopic
    {
        public event Action<byte[]>? OnMessage;

        public bool IsSubscribed { get; private set; }

        public void Subscribe() => IsSubscribed = true;

        public void Unsubscribe() => IsSubscribed = false;

        public void Publish(byte[] value) { }

        public void Publish(IMessage value) { }

        public void Deliver(byte[] message) => OnMessage?.Invoke(message);
    }
}
