// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ReadOnlySnapshotBundleTests
{
    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp() => _pool = new ResourcePool(new FlatDbConfig { CompactSize = 2 });

    private Snapshot MakeSnapshot(Action<SnapshotContent>? populate = null) =>
        FlatTestHelpers.MakeSnapshot(_pool, populate);

    private static ReadOnlySnapshotBundle Bundle(SnapshotPooledList snapshots, IPersistence.IPersistenceReader? reader = null, bool recordDetailedMetrics = false) =>
        new(snapshots, reader ?? Substitute.For<IPersistence.IPersistenceReader>(), recordDetailedMetrics,
            PersistedSnapshotStack.Empty(recordDetailedMetrics));

    [TestCase(true)]
    [TestCase(false)]
    public void GetAccount_FoundInSnapshot_ReturnsIt(bool detailedMetrics)
    {
        Address address = TestItem.AddressA;
        Account account = TestItem.GenerateIndexedAccount(1);
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();

        using ReadOnlySnapshotBundle bundle = Bundle(
            FlatTestHelpers.SnapshotList(MakeSnapshot(c => c.Accounts[new HashedKey<Address>(address)] = account)),
            reader, detailedMetrics);

        Assert.That(bundle.GetAccount(address), Is.EqualTo(account));
        reader.DidNotReceive().GetAccount(Arg.Any<Address>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetAccount_FallsBackToPersistence(bool detailedMetrics)
    {
        Address address = TestItem.AddressA;
        Account account = TestItem.GenerateIndexedAccount(1);
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.GetAccount(address).Returns(account);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()), reader, detailedMetrics);

        Assert.That(bundle.GetAccount(address), Is.EqualTo(account));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetAccount_PersistenceMiss_ReturnsNull_AndRecordsMetric(bool detailedMetrics)
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.GetAccount(Arg.Any<Address>()).Returns((Account?)null);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()), reader, detailedMetrics);

        Assert.That(bundle.GetAccount(TestItem.AddressA), Is.Null);
    }

    [Test]
    public void DetermineSelfDestructSnapshotIdx_ReturnsHighestIndexWhenSelfDestructed()
    {
        Address address = TestItem.AddressA;

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(),
            MakeSnapshot(c => c.SelfDestructedStorageAddresses[new HashedKey<Address>(address)] = true),
            MakeSnapshot()));

        Assert.That(bundle.DetermineSelfDestructSnapshotIdx(address), Is.EqualTo(1));
        Assert.That(bundle.DetermineSelfDestructSnapshotIdx(TestItem.AddressB), Is.EqualTo(-1));
    }

    [Test]
    public void GetSlot_FoundInSnapshot_ShortCircuits()
    {
        Address address = TestItem.AddressA;
        UInt256 index = 42;
        SlotValue stored = SlotValue.FromSpanWithoutLeadingZero([0x12, 0x34]);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c => c.Storages[new HashedKey<(Address, UInt256)>((address, index))] = stored)),
            recordDetailedMetrics: true);

        Assert.That(bundle.GetSlot(address, index, selfDestructStateIdx: -1), Is.EqualTo(new byte[] { 0x12, 0x34 }));
    }

    [Test]
    public void GetSlot_StopsAtSelfDestructIndex_AndReturnsNull()
    {
        // Two snapshots, neither holds the slot. Iteration goes 1 -> 0.
        // selfDestructStateIdx=1 forces the loop to bail at i==1 instead of falling through to persistence.
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot(), MakeSnapshot()), reader);

        Assert.That(bundle.GetSlot(TestItem.AddressA, (UInt256)42, selfDestructStateIdx: 1), Is.Null);
        reader.DidNotReceive().TryGetSlot(Arg.Any<Address>(), Arg.Any<UInt256>(), ref Arg.Any<SlotValue>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetSlot_FallsBackToPersistence_WithMetricBranches(bool detailedMetrics)
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        // Returning false leaves the SlotValue at default (zero) -> exercises the "value is zero" metric branch.
        reader.TryGetSlot(Arg.Any<Address>(), Arg.Any<UInt256>(), ref Arg.Any<SlotValue>()).Returns(false);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()), reader, detailedMetrics);

        // Default SlotValue.ToEvmBytes() is the canonical zero (single 0x00 byte).
        Assert.That(bundle.GetSlot(TestItem.AddressA, (UInt256)1, selfDestructStateIdx: -1), Is.EqualTo(new byte[] { 0 }));
    }

    [Test]
    public void GetSlots_UsesSnapshotsAndBatchesOnlyPersistenceMisses()
    {
        StorageCell snapshotCell = new(TestItem.AddressA, (UInt256)1);
        StorageCell persistenceCell = new(TestItem.AddressB, (UInt256)2);
        SlotValue snapshotValue = SlotValue.FromSpanWithoutLeadingZero([0x12]);
        SlotValue persistenceValue = SlotValue.FromSpanWithoutLeadingZero([0x34]);
        TrackingSlotReader reader = new(persistenceCell, persistenceValue);

        using ReadOnlySnapshotBundle bundle = Bundle(
            FlatTestHelpers.SnapshotList(MakeSnapshot(c =>
                c.Storages[new HashedKey<(Address, UInt256)>((snapshotCell.Address, snapshotCell.Index))] = snapshotValue)),
            reader);
        SlotRead[] reads = [new(snapshotCell, -1), new(persistenceCell, -1)];
        SlotValue?[] values = new SlotValue?[2];

        bundle.GetSlots(reads, values);

        Assert.That(values, Is.EqualTo(new SlotValue?[] { snapshotValue, persistenceValue }));
        Assert.That(reader.GetSlotsCalls, Is.EqualTo(1));
        Assert.That(reader.RequestedCells, Is.EqualTo(new[] { persistenceCell }));
    }

    [Test]
    public void TryFindStateNodes_ReturnsTrueWhenPresentInSnapshot()
    {
        TreePath path = TreePath.FromHexString("12");
        TrieNode node = new(NodeType.Leaf, [0xc1, 0x01]);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c => c.StateNodes[new HashedKey<TreePath>(path)] = node)),
            recordDetailedMetrics: true);

        Assert.That(bundle.TryFindStateNodes(path, Keccak.Zero, out TrieNode? found), Is.True);
        Assert.That(found, Is.SameAs(node));
    }

    [Test]
    public void TryFindStateNodes_FalseWhenAbsent()
    {
        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()));

        Assert.That(bundle.TryFindStateNodes(TreePath.FromHexString("ab"), Keccak.Zero, out TrieNode? node), Is.False);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void TryFindStorageNodes_ReturnsTrueWhenPresent()
    {
        Hash256 address = TestItem.KeccakA;
        TreePath path = TreePath.FromHexString("ab");
        TrieNode node = new(NodeType.Leaf, [0xc1, 0x02]);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c => c.StorageNodes[new HashedKey<(Hash256, TreePath)>((address, path))] = node)),
            recordDetailedMetrics: true);

        Assert.That(bundle.TryFindStorageNodes(address, path, Keccak.Zero, out TrieNode? found), Is.True);
        Assert.That(found, Is.SameAs(node));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TryLoadStateRlp_DelegatesToReader(bool detailedMetrics)
    {
        TreePath path = TreePath.FromHexString("12");
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(path, ReadFlags.None).Returns([0xc1, 0xff]);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()), reader, detailedMetrics);

        Assert.That(bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None), Is.EqualTo(new byte[] { 0xc1, 0xff }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TryLoadStorageRlp_DelegatesToReader(bool detailedMetrics)
    {
        TreePath path = TreePath.FromHexString("ab");
        Hash256 address = TestItem.KeccakA;
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStorageRlp(address, path, ReadFlags.None).Returns([0xc1, 0xee]);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()), reader, detailedMetrics);

        Assert.That(bundle.TryLoadStorageRlp(address, path, Keccak.Zero, ReadFlags.None), Is.EqualTo(new byte[] { 0xc1, 0xee }));
    }

    [Test]
    public void TryLease_ReturnsTrueWhileAlive_ThrowsAfterDispose()
    {
        ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()));

        Assert.That(bundle.TryLease(), Is.True);
        bundle.Dispose(); // releases the lease taken above
        bundle.Dispose(); // tears down for real

        Assert.That(() => bundle.GetAccount(TestItem.AddressA), Throws.TypeOf<ObjectDisposedException>());
    }

    private sealed class TrackingSlotReader(StorageCell cell, SlotValue value) : IPersistence.IPersistenceReader
    {
        public int GetSlotsCalls { get; private set; }
        public StorageCell[]? RequestedCells { get; private set; }

        public Account? GetAccount(Address address) => null;
        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) => false;

        public void GetSlots(ReadOnlySpan<StorageCell> cells, Span<SlotValue?> values)
        {
            GetSlotsCalls++;
            RequestedCells = cells.ToArray();
            for (int i = 0; i < cells.Length; i++)
                values[i] = cells[i].Equals(cell) ? value : null;
        }

        public StateId CurrentState => default;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => false;
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
        public void Dispose() { }
    }
}
