// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
    private static readonly UInt256 Slot = 7;

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
        shared.LeafBlobs[StemA] = [];      // the "stem deleted" marker
        shared.TrieNodes[NodeA] = null;    // the "node removed" marker

        using PbtSnapshotBundle bundle = Bundle(sharedLayers: [shared], localLayers: []);

        Assert.That(bundle.GetLeafBlob(StemA), Is.Null);
        Assert.That(bundle.GetTrieNode(NodeA), Is.Null);
    }

    [Test]
    public void SelfDestruct_InThisBranch_ShadowsASharedBundleSlot()
    {
        PbtSnapshotContent shared = new();
        shared.Slots[(Address, Slot)] = Word(0x33);

        using PbtSnapshotBundle bundle = Bundle(sharedLayers: [shared], localLayers: []);
        bundle.SelfDestruct(Address);

        Assert.That(EvmWordSlot.IsZero(bundle.GetSlot(Address, Slot)));
    }

    /// <summary>
    /// The shared view outlives any one bundle: each holds its own lease, and only the last release
    /// frees the reader. Over-releasing it would read through a freed native RocksDB snapshot.
    /// </summary>
    [Test]
    public void SharedBundle_IsReleased_OnlyByTheLastBundleHoldingIt()
    {
        PbtReadOnlySnapshotBundle shared = new(new PbtSnapshotPooledList(0), _reader);
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

        using PbtSnapshot sealed_ = bundle.CollectSnapshot(default, new StateId(1, default));

        Assert.That((byte)sealed_.Content.Accounts[Address]!.Nonce, Is.EqualTo(0x11), "the sealed layer took the buffer's writes");
        Assert.That((byte)bundle.GetAccount(Address)!.Nonce, Is.EqualTo(0x11), "which the bundle still reads through its own chain");
        Assert.That(bundle.CollectSnapshot(new StateId(1, default), new StateId(2, default)).Content, Is.Not.SameAs(sealed_.Content), "a fresh buffer backs the next block");
    }

    private PbtSnapshotBundle Bundle(IReadOnlyList<PbtSnapshotContent> sharedLayers, IReadOnlyList<PbtSnapshotContent> localLayers)
    {
        PbtSnapshotPooledList sharedChain = new(1);
        foreach (PbtSnapshotContent content in sharedLayers) sharedChain.Add(Layer(content));

        PbtSnapshotPooledList localChain = new(1);
        foreach (PbtSnapshotContent content in localLayers) localChain.Add(Layer(content));

        return new PbtSnapshotBundle(localChain, new PbtReadOnlySnapshotBundle(sharedChain, _reader), _pool, PbtResourcePool.Usage.MainBlockProcessing);
    }

    private PbtSnapshot Layer(PbtSnapshotContent content) =>
        new(default, default, content, _pool, PbtResourcePool.Usage.MainBlockProcessing);

    private static PbtSnapshotContent Content(byte marker)
    {
        PbtSnapshotContent content = new();
        content.Accounts[Address] = Build.An.Account.WithNonce(marker).TestObject;
        content.Slots[(Address, Slot)] = Word(marker);
        content.LeafBlobs[StemA] = [marker];
        content.TrieNodes[NodeA] = [marker];
        return content;
    }

    private static void Seed(PbtSnapshotBundle bundle, byte marker)
    {
        bundle.SetAccount(Address, Build.An.Account.WithNonce(marker).TestObject);
        bundle.SetSlot(Address, Slot, Word(marker));
        bundle.SetLeafBlob(StemA, [marker]);
        bundle.SetTrieNode(NodeA, [marker]);
    }

    private static void Seed(FakeReader reader, byte marker)
    {
        reader.Accounts[Address] = Build.An.Account.WithNonce(marker).TestObject;
        reader.Slots[(Address, Slot)] = Word(marker);
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

    private static EvmWord Word(byte marker) => EvmWordSlot.FromStripped([marker]);

    private sealed class FakeReader : IPbtPersistence.IReader
    {
        public Dictionary<AddressAsKey, Account?> Accounts { get; } = [];
        public Dictionary<(AddressAsKey, UInt256), EvmWord> Slots { get; } = [];
        public Dictionary<Stem, byte[]> LeafBlobs { get; } = [];
        public Dictionary<TrieNodeKey, byte[]> TrieNodes { get; } = [];
        public bool Disposed { get; private set; }

        public StateId CurrentState => StateId.PreGenesis;

        public Account? GetAccount(Address address) => Accounts.GetValueOrDefault(address);

        public EvmWord GetSlot(Address address, in UInt256 slot) => Slots.GetValueOrDefault((address, slot));

        public RefCountingMemory? GetLeafBlob(in Stem stem) => LeafBlobs.TryGetValue(stem, out byte[]? blob) ? RefCountingMemory.Wrapping(blob) : null;

        public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => TrieNodes.TryGetValue(key, out byte[]? node) ? RefCountingMemory.Wrapping(node) : null;

        public void Dispose() => Disposed = true;
    }
}
