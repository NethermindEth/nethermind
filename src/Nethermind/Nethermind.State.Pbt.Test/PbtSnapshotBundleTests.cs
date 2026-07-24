// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotBundleTests
{
    private static readonly Stem StemA = new(new byte[31]);
    private static readonly TrieNodeKey NodeA = new(0, StemA);
    private static readonly Address Address = TestItem.AddressA;

    /// <summary>Below <see cref="PbtKeyDerivation.HeaderStorageOffset"/>, so it shares <see cref="HeaderStem"/> with the account's own leaves.</summary>
    private static readonly UInt256 Slot = 7;

    private static readonly Stem HeaderStem = PbtKeyDerivation.AccountHeaderStem(Address);

    private PbtResourcePool _pool = null!;
    private FakeReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _pool = new PbtResourcePool(new PbtConfig());
        _reader = new FakeReader();
    }

    [TearDown]
    public void TearDown() => _reader.Dispose();

    /// <summary>
    /// Each tier shadows the ones below it: the write buffer over this branch's own sealed layers,
    /// those over the shared view's layers, and those over the disk.
    /// </summary>
    [Test]
    public void Reads_TakeTheHighestTierHoldingTheKey()
    {
        Seed(_reader, 0x44);
        PbtSnapshotContent shared = Content(0x33);
        PbtSnapshotContent local = Content(0x22);

        using PbtSnapshotBundle bundle = Bundle(sharedLayers: [shared], localLayers: [local]);
        Seed(bundle, 0x11);

        AssertReadsAre(bundle, 0x11);
    }

    [TestCase(0, ExpectedResult = 0x44, TestName = "Reads_FallThrough_ToTheReader")]
    [TestCase(1, ExpectedResult = 0x33, TestName = "Reads_FallThrough_ToTheSharedBundlesLayers")]
    [TestCase(2, ExpectedResult = 0x22, TestName = "Reads_FallThrough_ToThisBranchsLayers")]
    public byte Reads_FallThroughToTheFirstTierHoldingTheKey(int tiersAboveTheReader)
    {
        Seed(_reader, 0x44);
        List<PbtSnapshotContent> shared = tiersAboveTheReader >= 1 ? [Content(0x33)] : [];
        List<PbtSnapshotContent> local = tiersAboveTheReader >= 2 ? [Content(0x22)] : [];

        using PbtSnapshotBundle bundle = Bundle(shared, local);

        byte fromAccount = (byte)bundle.GetAccount(Address)!.Nonce;
        AssertReadsAre(bundle, fromAccount);
        return fromAccount;
    }

    /// <summary>
    /// A stem deleted in a layer of the shared view must read as absent, even though the disk below
    /// still holds it. Letting the empty-blob marker fall through would resurrect a deleted stem and
    /// silently produce the wrong state root.
    /// </summary>
    [Test]
    public void DeletedStem_InTheSharedBundle_DoesNotResurrectFromDisk()
    {
        _reader.LeafBlobs[StemA] = [0x44];
        _reader.TrieNodes[NodeA] = [0x44];

        PbtSnapshotContent shared = new();
        shared.SetLeafBlob(StemA, null);
        shared.SetTrieNode(NodeA, null);

        using PbtSnapshotBundle bundle = Bundle(sharedLayers: [shared], localLayers: []);

        Assert.That(bundle.GetLeafBlob(StemA), Is.Null);
        Assert.That(bundle.GetTrieNode(NodeA), Is.Null);
    }

    [Test]
    public void TransferredValues_AreReleased_WhenWrittenAfterBundleDisposal()
    {
        PbtSnapshotBundle bundle = Bundle(sharedLayers: [], localLayers: []);
        RefCountingMemory blob = Memory(0x11);
        RefCountingMemory node = Memory(0x22);
        bundle.Dispose();

        Assert.That(() => bundle.SetOwnedLeafBlob(StemA, blob), Throws.TypeOf<System.ObjectDisposedException>());
        Assert.That(() => bundle.SetOwnedTrieNode(NodeA, node), Throws.TypeOf<System.ObjectDisposedException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blob.AcquireLease, Throws.InvalidOperationException, "the rejected blob transfer releases its lease");
            Assert.That(node.AcquireLease, Throws.InvalidOperationException, "the rejected node transfer releases its lease");
        }
    }

    [Test]
    public void TransferredValues_AreReleased_WhenWritesRaceBundleDisposal()
    {
        const int writeCount = 256;
        PbtSnapshotBundle bundle = Bundle(sharedLayers: [], localLayers: []);
        RefCountingMemory[] values = new RefCountingMemory[writeCount];
        for (int i = 0; i < values.Length; i++) values[i] = Memory((byte)i);

        using System.Threading.Barrier start = new(2);
        System.Threading.Tasks.Task writer = System.Threading.Tasks.Task.Run(() =>
        {
            start.SignalAndWait();
            foreach (RefCountingMemory value in values)
            {
                try
                {
                    bundle.SetOwnedLeafBlob(StemA, value);
                }
                catch (System.ObjectDisposedException)
                {
                }
            }
        });

        start.SignalAndWait();
        bundle.Dispose();
        writer.GetAwaiter().GetResult();

        foreach (RefCountingMemory value in values)
        {
            Assert.That(value.AcquireLease, Throws.InvalidOperationException, "every transferred lease is released whichever side wins the race");
        }
    }

    [Test]
    public void LayerValueLease_RemainsValidAfterBundleDisposal()
    {
        PbtSnapshotBundle bundle = Bundle(sharedLayers: [Content(0x33)], localLayers: []);
        using RefCountingMemory? blob = bundle.GetLeafBlob(StemA);

        bundle.Dispose();

        Assert.That(blob!.AcquireLease, Throws.Nothing, "the returned lease owns the value independently");
        ((System.IDisposable)blob).Dispose();
        Assert.That(blob.GetSpan().ToArray(), Is.EqualTo((byte[])[0x33]));
    }

    [Test]
    public void SelfDestruct_InThisBranch_ShadowsASharedBundleSlot()
    {
        using PbtSnapshotBundle bundle = Bundle(sharedLayers: [Content(0x33)], localLayers: []);
        bundle.SelfDestruct(Address);

        Assert.That(EvmWordSlot.IsZero(bundle.GetSlot(Address, Slot)));
    }

    /// <summary>
    /// A slot written after the clear is a post-clear write and must survive it — that is the order
    /// <c>ProcessStorageChanges</c> issues them in for every newly created account.
    /// </summary>
    [Test]
    public void SlotWrittenAfterASelfDestruct_SurvivesIt()
    {
        using PbtSnapshotBundle bundle = Bundle(sharedLayers: [Content(0x33)], localLayers: []);
        bundle.SelfDestruct(Address);
        bundle.SetSlot(Address, Slot, Word(0x11));

        Assert.That(bundle.GetSlot(Address, Slot), Is.EqualTo(Word(0x11)));
    }

    /// <summary>
    /// The shared view outlives any one bundle: each holds its own lease, and only the last release
    /// frees the reader. Over-releasing it would read through a freed native RocksDB snapshot.
    /// </summary>
    [Test]
    public void SharedBundle_IsReleased_OnlyByTheLastBundleHoldingIt()
    {
        PbtReadOnlySnapshotBundle shared = new(new PbtSnapshotPooledList(0), _reader, recordDetailedMetrics: false);
        shared.TryLease();
        PbtSnapshotBundle first = new(new PbtSnapshotPooledList(0), shared, _pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtSnapshotBundle second = new(new PbtSnapshotPooledList(0), shared, _pool, PbtResourcePool.Usage.MainBlockProcessing);

        first.Dispose();
        first.Dispose();
        Assert.That(_reader.Disposed, Is.False, "a second bundle still reads through it");
        Assert.That(second.GetAccount(Address), Is.Null, "and its reads still work");

        second.Dispose();
        Assert.That(_reader.Disposed, "the last release frees the reader");
    }

    [Test]
    public void CollectSnapshot_StacksOnThisBranchAndRentsAFreshBuffer()
    {
        using PbtSnapshotBundle bundle = Bundle(sharedLayers: [], localLayers: []);
        Seed(bundle, 0x11);

        using PbtSnapshot sealed_ = bundle.CollectSnapshot(default, new StateId(1, default), TestItem.KeccakA.ValueHash256);

        Assert.That(sealed_.Content.LeafBlobs[HeaderStem]!.GetSpan().ToArray(), Is.EqualTo(HeaderBlob(0x11)), "the sealed layer took the buffer's writes");
        Assert.That((byte)bundle.GetAccount(Address)!.Nonce, Is.EqualTo(0x11), "which the bundle still reads through its own chain, now decoded from the leaf");
        Assert.That(bundle.TreeRoot, Is.EqualTo(TestItem.KeccakA.ValueHash256), "and the sealed layer's tree root becomes the bundle's");
        Assert.That(bundle.CollectSnapshot(new StateId(1, default), new StateId(2, default), default).Content, Is.Not.SameAs(sealed_.Content), "a fresh buffer backs the next block");
    }

    /// <summary>
    /// Timing a read must not change what it answers, at either tier and for either outcome — the
    /// hit, miss and self-destruct branches each carry their own observation.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void Reads_AreUnchanged_WhenDetailedMetricsAreRecorded(bool recordDetailedMetrics)
    {
        using PbtSnapshotBundle empty = Bundle(sharedLayers: [], localLayers: [], recordDetailedMetrics);
        Assert.That(empty.GetAccount(Address), Is.Null);
        Assert.That(EvmWordSlot.IsZero(empty.GetSlot(Address, Slot)));
        Assert.That(empty.GetLeafBlob(StemA), Is.Null);
        Assert.That(empty.GetTrieNode(NodeA), Is.Null);

        Seed(_reader, 0x44);
        using PbtSnapshotBundle fromReader = Bundle(sharedLayers: [], localLayers: [], recordDetailedMetrics);
        AssertReadsAre(fromReader, 0x44);

        using PbtSnapshotBundle fromSharedLayer = Bundle(sharedLayers: [Content(0x33)], localLayers: [], recordDetailedMetrics);
        AssertReadsAre(fromSharedLayer, 0x33);

        using PbtSnapshotBundle overSelfDestruct = Bundle(sharedLayers: [Content(0x33)], localLayers: [], recordDetailedMetrics);
        overSelfDestruct.SelfDestruct(Address);
        Assert.That(EvmWordSlot.IsZero(overSelfDestruct.GetSlot(Address, Slot)));
    }

    /// <summary>
    /// A trie node read that reaches disk is timed under the zone partition it is keyed into, so the
    /// account, code and storage columns can be told apart — and a miss stays distinct from a hit.
    /// </summary>
    [Test]
    public void TrieNodeReadsReachingPersistence_AreLabelledByPartition()
    {
        TrieNodeKey accountNode = new(0, ZoneStem(0x00));
        TrieNodeKey codeNode = new(0, ZoneStem(0x10));
        TrieNodeKey storageNode = new(0, ZoneStem(0x80));

        _reader.TrieNodes[accountNode] = [1];
        _reader.TrieNodes[storageNode] = [2];

        IMetricObserver original = Metrics.PbtReadOnlySnapshotBundleTimes;
        LabelRecordingObserver recorder = new();
        Metrics.PbtReadOnlySnapshotBundleTimes = recorder;
        try
        {
            using PbtSnapshotBundle bundle = Bundle(sharedLayers: [], localLayers: [], recordDetailedMetrics: true);
            using (RefCountingMemory? account = bundle.GetTrieNode(accountNode)) { }
            using (RefCountingMemory? storage = bundle.GetTrieNode(storageNode)) { }

            // seeded by neither, so it is the miss shape
            Assert.That(bundle.GetTrieNode(codeNode), Is.Null);
        }
        finally
        {
            Metrics.PbtReadOnlySnapshotBundleTimes = original;
        }

        Assert.That(recorder.Labels, Does.Contain("account_trie_node_persistence"));
        Assert.That(recorder.Labels, Does.Contain("storage_trie_node_persistence"));
        Assert.That(recorder.Labels, Does.Contain("code_trie_node_persistence_null"));
    }

    private static Stem ZoneStem(byte firstByte)
    {
        byte[] path = new byte[31];
        path[0] = firstByte;
        return new Stem(path);
    }

    private sealed class LabelRecordingObserver : IMetricObserver
    {
        public List<string> Labels { get; } = [];

        public void Observe(double value, IMetricLabels? labels = null)
        {
            if (labels is not null) Labels.AddRange(labels.Labels);
        }
    }

    private PbtSnapshotBundle Bundle(IReadOnlyList<PbtSnapshotContent> sharedLayers, IReadOnlyList<PbtSnapshotContent> localLayers, bool recordDetailedMetrics = false)
    {
        PbtSnapshotPooledList sharedChain = new(1);
        foreach (PbtSnapshotContent content in sharedLayers) sharedChain.Add(Layer(content));

        PbtSnapshotPooledList localChain = new(1);
        foreach (PbtSnapshotContent content in localLayers) localChain.Add(Layer(content));

        return new PbtSnapshotBundle(localChain, new PbtReadOnlySnapshotBundle(sharedChain, _reader, recordDetailedMetrics), _pool, PbtResourcePool.Usage.MainBlockProcessing);
    }

    private PbtSnapshot Layer(PbtSnapshotContent content) =>
        new(default, default, default, content, _pool, PbtResourcePool.Usage.MainBlockProcessing);

    private static PbtSnapshotContent Content(byte marker)
    {
        PbtSnapshotContent content = new();
        content.SetLeafBlob(HeaderStem, Memory(HeaderBlob(marker)));
        content.SetLeafBlob(StemA, Memory(marker));
        content.SetTrieNode(NodeA, Memory(marker));
        return content;
    }

    /// <summary>
    /// Seeds the branch's write buffer as a block would leave it: the account and slot eagerly, and the
    /// header blob the root fold would have produced from them.
    /// </summary>
    private static void Seed(PbtSnapshotBundle bundle, byte marker)
    {
        bundle.SetAccount(Address, Build.An.Account.WithNonce(marker).TestObject);
        bundle.SetSlot(Address, Slot, Word(marker));
        bundle.SetOwnedLeafBlob(HeaderStem, Memory(HeaderBlob(marker)));
        bundle.SetOwnedLeafBlob(StemA, Memory(marker));
        bundle.SetOwnedTrieNode(NodeA, Memory(marker));
    }

    /// <summary>The account header stem's blob carrying <paramref name="marker"/> as both the nonce and the value of <see cref="Slot"/>.</summary>
    private static byte[] HeaderBlob(byte marker)
    {
        byte[] basicData = new byte[StemLeafBlob.ValueLength];
        PbtKeyDerivation.PackBasicData(basicData, 0, marker, UInt256.Zero);

        return PbtTestLeaves.Blob(
            (PbtKeyDerivation.BasicDataLeafKey, basicData),
            (PbtKeyDerivation.HeaderSlotSubIndex(Slot), [marker]));
    }

    private static void Seed(FakeReader reader, byte marker)
    {
        reader.LeafBlobs[HeaderStem] = HeaderBlob(marker);
        reader.LeafBlobs[StemA] = [marker];
        reader.TrieNodes[NodeA] = [marker];
    }

    private static void AssertReadsAre(PbtSnapshotBundle bundle, byte marker)
    {
        using RefCountingMemory? blob = bundle.GetLeafBlob(StemA);
        using RefCountingMemory? node = bundle.GetTrieNode(NodeA);

        Assert.That((byte)bundle.GetAccount(Address)!.Nonce, Is.EqualTo(marker));
        Assert.That(bundle.GetSlot(Address, Slot), Is.EqualTo(Word(marker)));
        Assert.That(blob!.GetSpan().ToArray(), Is.EqualTo((byte[])[marker]));
        Assert.That(node!.GetSpan().ToArray(), Is.EqualTo((byte[])[marker]));
    }

    private static RefCountingMemory Memory(params byte[] value) => RefCountingMemory.Wrapping(value);

    private static EvmWord Word(byte marker) => EvmWordSlot.FromStripped([marker]);

    private sealed class FakeReader : IPbtPersistence.IReader
    {
        public Dictionary<Stem, byte[]> LeafBlobs { get; } = [];
        public Dictionary<TrieNodeKey, byte[]> TrieNodes { get; } = [];
        public bool Disposed { get; private set; }

        public StateId CurrentState => StateId.PreGenesis;

        public ValueHash256 CurrentTreeRoot { get; set; }

        public RefCountingMemory? GetLeafBlob(in Stem stem) => LeafBlobs.TryGetValue(stem, out byte[]? blob) ? RefCountingMemory.Wrapping(blob) : null;

        public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => TrieNodes.TryGetValue(key, out byte[]? node) ? RefCountingMemory.Wrapping(node) : null;

        public void Dispose() => Disposed = true;
    }
}
