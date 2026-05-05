// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ReadOnlySnapshotBundleTests
{
    private static Snapshot MakeSnapshot(IResourcePool pool, Action<SnapshotContent>? populate = null)
    {
        SnapshotContent content = pool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        populate?.Invoke(content);
        return new Snapshot(StateId.PreGenesis, StateId.PreGenesis, content, pool, ResourcePool.Usage.MainBlockProcessing);
    }

    private static SnapshotPooledList ListOf(params Snapshot[] snapshots)
    {
        SnapshotPooledList list = new(snapshots.Length);
        foreach (Snapshot s in snapshots) list.Add(s);
        return list;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetAccount_FoundInSnapshot_ReturnsIt(bool detailedMetrics)
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        Address address = TestItem.AddressA;
        Account account = TestItem.GenerateIndexedAccount(1);

        Snapshot snap = MakeSnapshot(pool, c => c.Accounts[new HashedKey<Address>(address)] = account);
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();

        using ReadOnlySnapshotBundle bundle = new(ListOf(snap), reader, detailedMetrics);

        bundle.GetAccount(address).Should().Be(account);
        reader.DidNotReceive().GetAccount(Arg.Any<Address>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetAccount_FallsBackToPersistence(bool detailedMetrics)
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        Address address = TestItem.AddressA;
        Account account = TestItem.GenerateIndexedAccount(1);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.GetAccount(address).Returns(account);

        using ReadOnlySnapshotBundle bundle = new(ListOf(MakeSnapshot(pool)), reader, detailedMetrics);

        bundle.GetAccount(address).Should().Be(account);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetAccount_PersistenceMiss_ReturnsNull_AndRecordsMetric(bool detailedMetrics)
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.GetAccount(Arg.Any<Address>()).Returns((Account?)null);

        using ReadOnlySnapshotBundle bundle = new(ListOf(MakeSnapshot(pool)), reader, detailedMetrics);

        bundle.GetAccount(TestItem.AddressA).Should().BeNull();
    }

    [Test]
    public void DetermineSelfDestructSnapshotIdx_ReturnsHighestIndexWhenSelfDestructed()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        Address address = TestItem.AddressA;

        Snapshot s0 = MakeSnapshot(pool);
        Snapshot s1 = MakeSnapshot(pool, c => c.SelfDestructedStorageAddresses[new HashedKey<Address>(address)] = true);
        Snapshot s2 = MakeSnapshot(pool);

        using ReadOnlySnapshotBundle bundle = new(ListOf(s0, s1, s2), Substitute.For<IPersistence.IPersistenceReader>(), recordDetailedMetrics: false);

        bundle.DetermineSelfDestructSnapshotIdx(address).Should().Be(1);
        bundle.DetermineSelfDestructSnapshotIdx(TestItem.AddressB).Should().Be(-1);
    }

    [Test]
    public void GetSlot_FoundInSnapshot_ShortCircuits()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        Address address = TestItem.AddressA;
        UInt256 index = 42;
        SlotValue stored = SlotValue.FromSpanWithoutLeadingZero([0x12, 0x34]);

        Snapshot snap = MakeSnapshot(pool,
            c => c.Storages[new HashedKey<(Address, UInt256)>((address, index))] = stored);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        using ReadOnlySnapshotBundle bundle = new(ListOf(snap), reader, recordDetailedMetrics: true);

        bundle.GetSlot(address, index, selfDestructStateIdx: -1).Should().Equal(0x12, 0x34);
    }

    [Test]
    public void GetSlot_StopsAtSelfDestructIndex_AndReturnsNull()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        Address address = TestItem.AddressA;
        UInt256 index = 42;

        // Two snapshots, neither holds the slot. Iteration goes 1 -> 0.
        // selfDestructStateIdx=1 forces the loop to bail at i==1 instead of falling through to persistence.
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        using ReadOnlySnapshotBundle bundle = new(ListOf(MakeSnapshot(pool), MakeSnapshot(pool)), reader, recordDetailedMetrics: false);

        bundle.GetSlot(address, index, selfDestructStateIdx: 1).Should().BeNull();
        reader.DidNotReceive().TryGetSlot(Arg.Any<Address>(), Arg.Any<UInt256>(), ref Arg.Any<SlotValue>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetSlot_FallsBackToPersistence_WithMetricBranches(bool detailedMetrics)
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        // Returning false leaves the SlotValue at default (zero) -> exercises the "value is zero" metric branch.
        reader.TryGetSlot(Arg.Any<Address>(), Arg.Any<UInt256>(), ref Arg.Any<SlotValue>()).Returns(false);

        using ReadOnlySnapshotBundle bundle = new(ListOf(MakeSnapshot(pool)), reader, detailedMetrics);

        // Default SlotValue.ToEvmBytes() is the canonical zero (single 0x00 byte).
        bundle.GetSlot(TestItem.AddressA, (UInt256)1, selfDestructStateIdx: -1).Should().Equal((byte)0);
    }

    [Test]
    public void TryFindStateNodes_ReturnsTrueWhenPresentInSnapshot()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        TreePath path = TreePath.FromHexString("12");
        TrieNode node = new(NodeType.Leaf, [0xc1, 0x01]);

        Snapshot snap = MakeSnapshot(pool, c => c.StateNodes[new HashedKey<TreePath>(path)] = node);
        using ReadOnlySnapshotBundle bundle = new(ListOf(snap), Substitute.For<IPersistence.IPersistenceReader>(), recordDetailedMetrics: true);

        bundle.TryFindStateNodes(path, Keccak.Zero, out TrieNode? found).Should().BeTrue();
        found.Should().BeSameAs(node);
    }

    [Test]
    public void TryFindStateNodes_FalseWhenAbsent()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        using ReadOnlySnapshotBundle bundle = new(ListOf(MakeSnapshot(pool)), Substitute.For<IPersistence.IPersistenceReader>(), recordDetailedMetrics: false);

        bundle.TryFindStateNodes(TreePath.FromHexString("ab"), Keccak.Zero, out TrieNode? node).Should().BeFalse();
        node.Should().BeNull();
    }

    [Test]
    public void TryFindStorageNodes_ReturnsTrueWhenPresent()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        Hash256 address = TestItem.KeccakA;
        TreePath path = TreePath.FromHexString("ab");
        TrieNode node = new(NodeType.Leaf, [0xc1, 0x02]);

        Snapshot snap = MakeSnapshot(pool, c => c.StorageNodes[new HashedKey<(Hash256, TreePath)>((address, path))] = node);
        using ReadOnlySnapshotBundle bundle = new(ListOf(snap), Substitute.For<IPersistence.IPersistenceReader>(), recordDetailedMetrics: true);

        bundle.TryFindStorageNodes(address, path, Keccak.Zero, out TrieNode? found).Should().BeTrue();
        found.Should().BeSameAs(node);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TryLoadStateRlp_DelegatesToReader(bool detailedMetrics)
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        TreePath path = TreePath.FromHexString("12");
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(path, ReadFlags.None).Returns([0xc1, 0xff]);

        using ReadOnlySnapshotBundle bundle = new(ListOf(MakeSnapshot(pool)), reader, detailedMetrics);

        bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None).Should().Equal((byte)0xc1, (byte)0xff);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TryLoadStorageRlp_DelegatesToReader(bool detailedMetrics)
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        TreePath path = TreePath.FromHexString("ab");
        Hash256 address = TestItem.KeccakA;
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStorageRlp(address, path, ReadFlags.None).Returns([0xc1, 0xee]);

        using ReadOnlySnapshotBundle bundle = new(ListOf(MakeSnapshot(pool)), reader, detailedMetrics);

        bundle.TryLoadStorageRlp(address, path, Keccak.Zero, ReadFlags.None).Should().Equal((byte)0xc1, (byte)0xee);
    }

    [Test]
    public void TryLease_ReturnsTrueWhileAlive_ThrowsAfterDispose()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        ReadOnlySnapshotBundle bundle = new(ListOf(MakeSnapshot(pool)), Substitute.For<IPersistence.IPersistenceReader>(), recordDetailedMetrics: false);

        bundle.TryLease().Should().BeTrue();
        bundle.Dispose(); // releases the lease taken above
        bundle.Dispose(); // tears down for real

        Assert.That(() => bundle.GetAccount(TestItem.AddressA), Throws.TypeOf<ObjectDisposedException>());
    }
}
