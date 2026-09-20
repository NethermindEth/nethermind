// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
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

    [Test]
    [Repeat(20)]
    public void Concurrent_gossip_arrivals_across_subnets_do_not_race_the_held_column_accumulator()
    {
        const int required = 64;
        ulong[] subscribedSubnets = [.. Enumerable.Range(0, 128).Select(i => (ulong)i)];
        DataColumnSidecarPool pool = new();
        ColumnGossipRouter router = CreateRouter(pool);
        Dictionary<string, FakeTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new FakeTopic(), digest, subscribedSubnets);

        System.Collections.Concurrent.ConcurrentBag<DataColumnSidecar> receivedEvents = [];
        router.DataColumnSidecarReceived += s => receivedEvents.Add(s);

        Task[] tasks = new Task[required];
        for (ulong column = 0; column < (ulong)required; column++)
        {
            ulong c = column;
            tasks[c] = Task.Run(() =>
            {
                DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(c, CurrentSlot);
                topics[GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(c))].Deliver(Message(sidecar));
            });
        }

        Task.WaitAll(tasks);

        Assert.That(receivedEvents.Select(s => s.Index).Distinct().Count(), Is.EqualTo(Eip7594DasConstants.NumberOfColumns),
            $"expected all 128 columns raised, got {receivedEvents.Select(s => s.Index).Distinct().Count()} distinct, {receivedEvents.Count} total events");
    }

    [Test]
    public void Crossing_the_reconstruction_threshold_reconstructs_and_publishes_the_missing_columns_exactly_once_with_the_cache_marked_first()
    {
        // Held directly over gossip: columns 0..63 (crosses the 64-column threshold on the last one).
        // Subscribed but held: subnets 64..70, so publishing-only-when-subscribed is actually exercised
        // rather than vacuously true. Subnets 71..127 are neither held nor subscribed.
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        ulong[] subscribedSubnets = [.. Enumerable.Range(0, required + 7).Select(i => (ulong)i)];
        DataColumnSidecarPool pool = new();
        ColumnGossipRouter router = CreateRouter(pool);
        Dictionary<string, FakeTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new FakeTopic(), digest, subscribedSubnets);

        List<DataColumnSidecar> receivedEvents = [];
        router.DataColumnSidecarReceived += receivedEvents.Add;

        for (ulong column = 0; column < (ulong)required; column++)
        {
            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot);
            topics[GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(column))].Deliver(Message(sidecar));
        }

        // Never called TryReconstruct/SelectNewlyReconstructed directly: everything below is only
        // observable if ColumnGossipRouter itself drives reconstruction from real gossip arrivals.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receivedEvents, Has.Count.EqualTo(Eip7594DasConstants.NumberOfColumns),
                "64 directly-received plus 64 reconstructed columns, each raised exactly once");
            Assert.That(receivedEvents.Select(s => s.Index), Is.Unique, "no column is ever raised twice");
            Assert.That(receivedEvents.Select(s => s.Index), Is.EquivalentTo(Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(i => (ulong)i)));
        }

        // Published exactly on the reconstructed columns whose own subnet is subscribed (64..70), and
        // nowhere else: not on the 0..63 already held directly, not on the unsubscribed 71..127.
        List<(string Topic, DataColumnSidecar Sidecar)> published = [.. topics
            .SelectMany(kv => kv.Value.Published.Select(m => (kv.Key, Decode(m))))];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(published, Has.Count.EqualTo(7), "only the subscribed-but-missing subnets 64..70 are published to");
            Assert.That(published.Select(p => p.Sidecar.Index), Is.EquivalentTo(Enumerable.Range(required, 7).Select(i => (ulong)i)));
            foreach ((string topic, DataColumnSidecar sidecar) in published)
            {
                Assert.That(topic, Is.EqualTo(GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(sidecar.Index))),
                    "published on the reconstructed sidecar's own subnet, not some other one");
            }
        }

        // The anti-equivocation cache was marked for the reconstructed columns: a genuine gossip
        // arrival for one of them afterward is rejected as a duplicate, not re-accepted or re-raised.
        int receivedBeforeReplay = receivedEvents.Count;
        DataColumnSidecar replay = DataColumnSidecarTestFixture.BuildValidSidecar((ulong)required, CurrentSlot);
        topics[GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName((ulong)required))].Deliver(Message(replay));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receivedEvents, Has.Count.EqualTo(receivedBeforeReplay), "the reconstructed column's cache entry rejects the concurrent gossip copy");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(1));
        }

        // Exposed exactly as if received over the network: also present in the serving pool.
        Hash256 blockRoot = SszRoots.HashTreeRoot(replay.SignedBlockHeader!.Message!);
        Assert.That(pool.TryGet(blockRoot, (ulong)required, out DataColumnSidecar? pooled), Is.True);
        Assert.That(pooled!.Index, Is.EqualTo((ulong)required));
    }

    private static DataColumnSidecar Decode(byte[] wireMessage)
    {
        Eth2MessageId.TryDecompress(wireMessage, Eth2MessageId.MaxGossipSize, out byte[]? payload);
        DataColumnSidecar.Decode(payload!, out DataColumnSidecar sidecar);
        return sidecar;
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

        // One over the 21 that mainnet's BPO2 allows at this slot's epoch. Padded rather than
        // built from 22 real blobs: the count is checked before any KZG work, which is the point.
        yield return new TestCaseData(new Func<DataColumnSidecar>(() =>
        {
            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot);
            const int overBpo2 = 22;
            sidecar.KzgCommitments = [.. Enumerable.Repeat(sidecar.KzgCommitments![0], overBpo2)];
            sidecar.KzgProofs = [.. Enumerable.Repeat(sidecar.KzgProofs![0], overBpo2)];
            sidecar.Column = [.. Enumerable.Repeat(sidecar.Column![0], overBpo2)];
            return sidecar;
        }), ColumnGossipDropReason.FailedBlobCount).SetName("more commitments than the epoch's max_blobs_per_block");

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

        public List<byte[]> Published { get; } = [];

        public void Subscribe() => IsSubscribed = true;

        public void Unsubscribe() => IsSubscribed = false;

        public void Publish(byte[] value) => Published.Add(value);

        public void Publish(IMessage value) { }

        public void Deliver(byte[] message) => OnMessage?.Invoke(message);
    }
}
