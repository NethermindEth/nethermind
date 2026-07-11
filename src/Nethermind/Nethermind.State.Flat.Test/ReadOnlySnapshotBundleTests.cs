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
    public void TryFindStateNodes_ReturnsTrueWhenPresentInSnapshot()
    {
        TreePath path = TreePath.FromHexString("12");
        byte[] rlp = [0xc1, 0x01];
        Hash256 hash = Keccak.Compute(rlp);
        TrieNode node = new(NodeType.Leaf, hash, rlp);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c => c.StateNodes[new HashedKey<TreePath>(path)] = node)),
            recordDetailedMetrics: true);

        Assert.That(bundle.TryFindStateNodes(path, hash, out TrieNode? found), Is.True);
        Assert.That(found, Is.SameAs(node));
    }

    [Test]
    public void TryFindStateNodes_SkipsNewerNodeWithDifferentHash()
    {
        TreePath path = TreePath.FromHexString("12");
        byte[] requestedRlp = [0xc1, 0x01];
        Hash256 requestedHash = Keccak.Compute(requestedRlp);
        TrieNode requested = new(NodeType.Leaf, requestedHash, requestedRlp);
        byte[] newerRlp = [0xc1, 0x02];
        TrieNode newer = new(NodeType.Leaf, Keccak.Compute(newerRlp), newerRlp);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c => c.StateNodes[new HashedKey<TreePath>(path)] = requested),
            MakeSnapshot(c => c.StateNodes[new HashedKey<TreePath>(path)] = newer)));

        Assert.That(bundle.TryFindStateNodes(path, requestedHash, out TrieNode? found), Is.True);
        Assert.That(found, Is.SameAs(requested));
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
        byte[] rlp = [0xc1, 0x02];
        Hash256 hash = Keccak.Compute(rlp);
        TrieNode node = new(NodeType.Leaf, hash, rlp);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c => c.StorageNodes[new HashedKey<(Hash256, TreePath)>((address, path))] = node)),
            recordDetailedMetrics: true);

        Assert.That(bundle.TryFindStorageNodes(address, path, hash, out TrieNode? found), Is.True);
        Assert.That(found, Is.SameAs(node));
    }

    [Test]
    public void TryFindStorageNodes_SkipsNewerNodeWithDifferentHash()
    {
        Hash256 address = TestItem.KeccakA;
        TreePath path = TreePath.FromHexString("ab");
        byte[] requestedRlp = [0xc1, 0x01];
        Hash256 requestedHash = Keccak.Compute(requestedRlp);
        TrieNode requested = new(NodeType.Leaf, requestedHash, requestedRlp);
        byte[] newerRlp = [0xc1, 0x02];
        TrieNode newer = new(NodeType.Leaf, Keccak.Compute(newerRlp), newerRlp);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c => c.StorageNodes[new HashedKey<(Hash256, TreePath)>((address, path))] = requested),
            MakeSnapshot(c => c.StorageNodes[new HashedKey<(Hash256, TreePath)>((address, path))] = newer)));

        Assert.That(bundle.TryFindStorageNodes(address, path, requestedHash, out TrieNode? found), Is.True);
        Assert.That(found, Is.SameAs(requested));
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
}
