// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
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

public class ColumnGossipRouterTests
{
    private const ulong CurrentSlot = 13_410_304;
    private const ulong SubnetId = 5; // matches ColumnIndex below under the mainnet column==subnet coincidence.
    private const ulong ColumnIndex = SubnetId;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    private static ColumnGossipRouter CreateRouter(DataColumnSidecarPool? pool = null, double secondsIntoSlot = 6.0, BeaconChainStore? store = null)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot).AddSeconds(secondsIntoSlot);
        return new ColumnGossipRouter(Spec, new SlotClock(Spec, new ManualTimestamper(now)), LimboLogs.Instance, pool, store);
    }

    private static byte[] Message(DataColumnSidecar sidecar) => Snappy.CompressToArray(DataColumnSidecar.Encode(sidecar));

    [Test]
    public void A_correctly_formed_sidecar_on_its_own_subnet_raises_the_event_and_populates_the_pool()
    {
        DataColumnSidecarPool pool = new();
        ColumnGossipRouter router = CreateRouter(pool);
        Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        router.Start(id => topics[id] = new GossipDigestWindowTests.RecordingTopic(), ForkDigest.Compute(Spec, 419_072), [SubnetId]);
        DataColumnSidecar received = null!;
        router.DataColumnSidecarReceived += s => received = s;

        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot);
        router.Handle(SubnetId, gloasTopic: false, Message(sidecar));

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
        Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new GossipDigestWindowTests.RecordingTopic(), digest, subscribedSubnets);

        System.Collections.Concurrent.ConcurrentBag<DataColumnSidecar> receivedEvents = [];
        router.DataColumnSidecarReceived += s => receivedEvents.Add(s);

        Task[] tasks = new Task[required];
        for (ulong column = 0; column < (ulong)required; column++)
        {
            ulong c = column;
            tasks[c] = Task.Run(() =>
            {
                DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(c, CurrentSlot);
                router.Handle(c, gloasTopic: false, Message(sidecar));
            });
        }

        Task.WaitAll(tasks);

        Assert.That(receivedEvents.Select(s => s.Index).Distinct().Count(), Is.EqualTo(Eip7594DasConstants.NumberOfColumns),
            $"expected all 128 columns raised, got {receivedEvents.Select(s => s.Index).Distinct().Count()} distinct, {receivedEvents.Count} total events");
    }

    [Test]
    public void Crossing_the_reconstruction_threshold_reconstructs_the_missing_columns_exactly_once_and_publishes_those_of_subscribed_subnets([Values] bool headerVerified)
    {
        // Held directly over gossip: columns 0..63 (crosses the 64-column threshold on the last one).
        // Subscribed but not held: subnets 64..70, the ones a reconstructed column could be published to.
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        ulong[] subscribedSubnets = [.. Enumerable.Range(0, required + 7).Select(i => (ulong)i)];
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        if (headerVerified)
        {
            DataColumnSidecarTestFixture.StoreAsImported(store, DataColumnSidecarTestFixture.BuildValidSidecar(0, CurrentSlot));
        }

        ColumnGossipRouter router = CreateRouter(pool, store: store);
        Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new GossipDigestWindowTests.RecordingTopic(), digest, subscribedSubnets);

        List<DataColumnSidecar> receivedEvents = [];
        router.DataColumnSidecarReceived += receivedEvents.Add;

        for (ulong column = 0; column < (ulong)required; column++)
        {
            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot);
            router.Handle(column, gloasTopic: false, Message(sidecar));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receivedEvents, Has.Count.EqualTo(Eip7594DasConstants.NumberOfColumns),
                "64 directly-received plus 64 reconstructed columns, each raised exactly once");
            Assert.That(receivedEvents.Select(s => s.Index), Is.Unique, "no column is ever raised twice");
            Assert.That(receivedEvents.Select(s => s.Index), Is.EquivalentTo(Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(i => (ulong)i)));
        }

        // fulu/das-core.md "Reconstruction and cross-seeding": a reconstructed column of a subscribed subnet goes to that subnet's topic, and no other column is sent.
        // An unverified header may be forged, so its reconstructed columns are never sent.
        ulong[] publishedColumns = [.. Enumerable.Range(required, headerVerified ? 7 : 0).Select(static column => (ulong)column)];
        string[] publishedTopics = [.. publishedColumns.Select(column => GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(column)))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topics.Where(static t => t.Value.Published.Count > 0).Select(static t => t.Key), Is.EquivalentTo(publishedTopics), "only subscribed subnets are published to");
            Assert.That(publishedTopics.Select(topic => topics[topic].Published), Is.EqualTo(publishedColumns.Select(column => new[] { Message(DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot)) })),
                "each reconstructed column is published once, as its snappy SSZ");
        }

        int receivedBeforeReplay = receivedEvents.Count;
        DataColumnSidecar replay = DataColumnSidecarTestFixture.BuildValidSidecar((ulong)required, CurrentSlot);
        router.Handle((ulong)required, gloasTopic: false, Message(replay));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receivedEvents, Has.Count.EqualTo(receivedBeforeReplay), "the reconstructed column's cache entry rejects the concurrent gossip copy");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(1));
        }

        Hash256 blockRoot = SszRoots.HashTreeRoot(replay.SignedBlockHeader!.Message!);
        Assert.That(pool.TryGet(blockRoot, (ulong)required, out DataColumnSidecar? pooled), Is.True);
        Assert.That(pooled!.Index, Is.EqualTo((ulong)required));
    }

    // fulu/p2p-interface.md data_column_sidecar_{subnet_id}: the header proposer signature must be valid.
    [Test]
    public void Reconstructed_columns_under_a_signature_other_than_the_verified_one_are_not_published()
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        DataColumnSidecarTestFixture.StoreAsImported(store, DataColumnSidecarTestFixture.BuildValidSidecar(0, CurrentSlot));
        ColumnGossipRouter router = CreateRouter(pool, store: store);
        Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        router.Start(id => topics[id] = new GossipDigestWindowTests.RecordingTopic(), ForkDigest.Compute(Spec, 419_072), [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static i => (ulong)i)]);
        DataColumnSidecar forged = DataColumnSidecarTestFixture.BuildValidSidecar(0, CurrentSlot);
        forged.SignedBlockHeader!.Signature = new BlsSignature(Enumerable.Repeat((byte)0xAB, BlsSignature.Length).ToArray());
        pool.Add(SszRoots.HashTreeRoot(forged.SignedBlockHeader.Message!), CurrentSlot, forged);

        MessageValidity[] received = [.. Enumerable.Range(1, required).Select(column =>
            router.Handle((ulong)column, gloasTopic: false, Message(DataColumnSidecarTestFixture.BuildValidSidecar((ulong)column, CurrentSlot))))];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(received, Is.All.EqualTo(MessageValidity.Accepted), "the gossip columns pass every check under the imported header");
        Assert.That(pool.TryGet(SszRoots.HashTreeRoot(forged.SignedBlockHeader.Message!), (ulong)required + 1, out _), Is.True, "fixture: the block was reconstructed");
        Assert.That(topics.Values.SelectMany(static t => t.Published), Is.Empty);
    }

    // fulu/p2p-interface.md: first sidecar per tuple; das-core.md exposes reconstructed columns as received.
    [Test]
    public void Reconstructed_column_of_an_equivocating_block_is_not_published_for_a_tuple_already_forwarded()
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        ColumnGossipRouter router = CreateRouter(new DataColumnSidecarPool(), store: store);
        Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new GossipDigestWindowTests.RecordingTopic(), digest, [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static i => (ulong)i)]);
        DataColumnSidecar first = DataColumnSidecarTestFixture.BuildValidSidecar(required, CurrentSlot);
        DataColumnSidecarTestFixture.StoreAsImported(store, first);
        DataColumnSidecarTestFixture.StoreAsImported(store, Equivocation(0));

        MessageValidity forwarded = router.Handle(required, gloasTopic: false, Message(first));
        MessageValidity[] equivocations = [.. Enumerable.Range(0, required).Select(column => router.Handle((ulong)column, gloasTopic: false, Message(Equivocation((ulong)column))))];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forwarded, Is.EqualTo(MessageValidity.Accepted));
            Assert.That(equivocations, Is.All.EqualTo(MessageValidity.Accepted), "fixture: the equivocating block's columns pass every check and reconstruct it");
            Assert.That(topics[GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(required))].Published, Is.Empty, "the forwarded tuple is not published again");
            Assert.That(topics[GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(required + 1))].Published, Has.Count.EqualTo(1), "the other reconstructed columns are");
        }

        static DataColumnSidecar Equivocation(ulong column)
        {
            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot);
            sidecar.SignedBlockHeader!.Message!.StateRoot = Keccak.OfAnEmptyString;
            return sidecar;
        }
    }

    // Validation and topic subscription can both hold the pubsub monitor, so waiting here could deadlock.
    [Test]
    [CancelAfter(60_000)]
    public async Task Publishing_a_reconstructed_column_does_not_wait_for_a_digest_subscription(CancellationToken token)
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        DataColumnSidecarTestFixture.StoreAsImported(store, DataColumnSidecarTestFixture.BuildValidSidecar(0, CurrentSlot));
        ColumnGossipRouter router = CreateRouter(new DataColumnSidecarPool(), store: store);
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        string otherDigest = Convert.ToHexStringLower(ForkDigest.Compute(Spec, 412_672));
        TaskCompletionSource subscribing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        System.Collections.Concurrent.ConcurrentDictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        router.Start(id => id.Contains(otherDigest) ? new GatedTopic(subscribing, release) : topics[id] = new GossipDigestWindowTests.RecordingTopic(), digest, [.. Enumerable.Range(0, required + 1).Select(static i => (ulong)i)]);

        Task subscribe = Task.Run(() => router.SubscribeDigest(ForkDigest.Compute(Spec, 412_672)), token);
        await subscribing.Task.WaitAsync(token);
        Task reconstruct = Task.Run(() =>
        {
            for (ulong column = 0; column < required; column++)
            {
                router.Handle(column, gloasTopic: false, Message(DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot)));
            }
        }, token);
        bool finished = await Task.WhenAny(reconstruct, Task.Delay(TimeSpan.FromSeconds(30), token)) == reconstruct;
        release.Set();
        await subscribe;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(finished, Is.True, "reconstruction and its publication finished while a subscription held the router's subscription lock");
        Assert.That(topics[GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(required))].Published, Has.Count.EqualTo(1));
    }

    // das-core.md: reconstruction works from every column the node holds, however it got them; range sync adds to the pool without gossip.
    [Test]
    public void Columns_added_to_the_pool_by_sync_count_toward_reconstruction()
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        DataColumnSidecarPool pool = new();
        ColumnGossipRouter router = CreateRouter(pool);
        router.Start(id => new GossipDigestWindowTests.RecordingTopic(), ForkDigest.Compute(Spec, 419_072), [.. Enumerable.Range(0, required).Select(i => (ulong)i)]);
        List<DataColumnSidecar> received = [];
        router.DataColumnSidecarReceived += received.Add;

        DataColumnSidecar last = DataColumnSidecarTestFixture.BuildValidSidecar(required - 1, CurrentSlot);
        Hash256 blockRoot = SszRoots.HashTreeRoot(last.SignedBlockHeader!.Message!);
        for (ulong column = 0; column < required - 1; column++)
        {
            pool.Add(blockRoot, CurrentSlot, DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot));
        }

        router.Handle(required - 1, gloasTopic: false, Message(last));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(pool.TryGet(blockRoot, Eip7594DasConstants.NumberOfColumns - 1, out _), Is.True, "the 64th held column, the only one gossip delivered, completes the matrix");
        Assert.That(received.Select(s => s.Index), Is.EquivalentTo(Enumerable.Range(required - 1, required + 1).Select(i => (ulong)i)), "the gossip column plus the 64 reconstructed ones");
    }

    // das-core.md: a node SHOULD reconstruct once it holds half the columns, including ones an earlier run verified and stored.
    [Test]
    public void Columns_stored_before_a_restart_count_toward_reconstruction()
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        Nethermind.Db.MemColumnsDb<Nethermind.BeaconChain.Storage.BeaconChainDbColumns> db = new();
        Nethermind.BeaconChain.Storage.BeaconChainStore store = new(db, Spec);
        DataColumnSidecar last = DataColumnSidecarTestFixture.BuildValidSidecar(required - 1, CurrentSlot);
        Hash256 blockRoot = SszRoots.HashTreeRoot(last.SignedBlockHeader!.Message!);
        DataColumnSidecarPool earlierRun = new(store: store);
        for (ulong column = 0; column < required - 1; column++)
        {
            earlierRun.Add(blockRoot, CurrentSlot, DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot));
        }

        DataColumnSidecarPool pool = new(store: store);
        ColumnGossipRouter router = CreateRouter(pool);
        router.Start(id => new GossipDigestWindowTests.RecordingTopic(), ForkDigest.Compute(Spec, 419_072), [.. Enumerable.Range(0, required).Select(i => (ulong)i)]);

        router.Handle(required - 1, gloasTopic: false, Message(last));

        Assert.That(pool.TryGet(blockRoot, Eip7594DasConstants.NumberOfColumns - 1, out _), Is.True, "the stored columns and the one gossip delivered complete the matrix");
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
        Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new GossipDigestWindowTests.RecordingTopic(), digest, [SubnetId]);
        int received = 0;
        router.DataColumnSidecarReceived += _ => received++;

        router.Handle(SubnetId, gloasTopic: false, Message(build()));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(received, Is.Zero, "no event for a dropped sidecar");
        Assert.That(router.GetDropCount(reason), Is.EqualTo(1), "the drop is counted under its reason");
    }

    [Test]
    public void Duplicate_sidecar_for_the_same_slot_proposer_and_index_is_dropped_and_counted()
    {
        ColumnGossipRouter router = CreateRouter();
        Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        byte[] digest = ForkDigest.Compute(Spec, 419_072);
        router.Start(id => topics[id] = new GossipDigestWindowTests.RecordingTopic(), digest, [SubnetId]);
        int received = 0;
        router.DataColumnSidecarReceived += _ => received++;
        byte[] message = Message(DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, CurrentSlot));

        router.Handle(SubnetId, gloasTopic: false, message);
        router.Handle(SubnetId, gloasTopic: false, message);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(received, Is.EqualTo(1), "only the first copy raises the event");
        Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(1));
    }

    private sealed class GatedTopic(TaskCompletionSource entered, ManualResetEventSlim release) : ITopic
    {
        public event Action<PeerId, byte[]>? OnMessage { add { } remove { } }

        public bool IsSubscribed => false;

        public void Subscribe()
        {
            entered.TrySetResult();
            release.Wait();
        }

        public void Unsubscribe() { }

        public void Publish(byte[] value) { }

        public void Publish(IMessage value) { }
    }
}
