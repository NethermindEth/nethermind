// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
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

    [Test]
    public async Task SiblingPruning_PreservesLeasedReads_AndBackstopResumesAfterRelease([Values] bool persistedFork)
    {
        using FlatTestContainer tier = new();
        SnapshotRepository repository = tier.Repository;
        SnapshotRetention retention = tier.Resolve<SnapshotRetention>();
        StateId start = new(0, TestItem.KeccakA);
        StateId canonicalParent = new(1, TestItem.KeccakA);
        StateId canonicalHead = new(2, TestItem.KeccakA);
        StateId forkParent = new(1, TestItem.KeccakB);
        StateId readerHead = new(2, TestItem.KeccakB);
        StateId unrelated = new(2, TestItem.KeccakC);
        Account account = new(1, 100);
        foreach ((StateId from, StateId to) in new[]
                 { (start, canonicalParent), (canonicalParent, canonicalHead), (start, forkParent), (forkParent, readerHead), (canonicalParent, unrelated) })
        {
            using Snapshot snapshot = tier.ResourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
            snapshot.Content.Accounts[TestItem.AddressA] = account;
            if (persistedFork && (to == forkParent || to == readerHead))
            {
                tier.Loader.ConvertAndRegister(snapshot);
            }
            else
            {
                snapshot.AcquireLease();
                if (!repository.TryAdd(snapshot, SnapshotTier.InMemoryBase))
                {
                    snapshot.Dispose();
                    Assert.Fail($"Duplicate snapshot {to}");
                }
                repository.AddStateId(to);
            }
        }
        repository.SetLastCommittedStateId(canonicalHead);

        retention.Register(readerHead);
        ReadOnlySnapshotBundle bundle;
        try
        {
            AssembledSnapshotResult assembled = repository.AssembleSnapshots(readerHead, start, 2);
            bundle = new ReadOnlySnapshotBundle(assembled.InMemory, Substitute.For<IPersistence.IPersistenceReader>(),
                false, new PersistedSnapshotStack(assembled.Persisted, false), retention, readerHead);
        }
        catch
        {
            retention.Release(readerHead);
            throw;
        }

        using (bundle)
        {
            await Task.Run(() => repository.RemoveSiblingAndDescendents(canonicalParent));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(repository.HasState(readerHead), Is.False);
                Assert.That(bundle.SnapshotCount, Is.EqualTo(2));
                Assert.That(bundle.GetAccount(TestItem.AddressA), Is.EqualTo(account));
                Assert.That(repository.HasState(unrelated), Is.True);
            }

            using AssembledSnapshotResult retry = repository.AssembleSnapshots(readerHead, start, 2);
            Assert.That(retry.SnapshotCount, Is.Zero, "a later assembly cannot use the removed branch");
            Assert.That(repository.RemoveOrphanedStates(canonicalHead, canonicalHead, 1), Is.Zero,
                "the missing registered head makes ancestry classification conservative");
        }

        Assert.That(repository.RemoveOrphanedStates(canonicalHead, canonicalHead, 1), Is.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasState(forkParent), Is.False);
            Assert.That(repository.HasState(unrelated), Is.False);
            Assert.That(repository.HasState(canonicalHead), Is.True);
        }
    }

    [Test]
    public void GetAccount_FoundInSnapshot_ReturnsIt([Values] bool detailedMetrics)
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

    [Test]
    public void GetAccount_FallsBackToPersistence([Values] bool detailedMetrics)
    {
        Address address = TestItem.AddressA;
        Account account = TestItem.GenerateIndexedAccount(1);
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.GetAccount(address).Returns(account);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()), reader, detailedMetrics);

        Assert.That(bundle.GetAccount(address), Is.EqualTo(account));
    }

    [Test]
    public void GetAccount_PersistenceMiss_ReturnsNull_AndRecordsMetric([Values] bool detailedMetrics)
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

    [Test]
    public void GetSlot_FallsBackToPersistence_WithMetricBranches([Values] bool detailedMetrics)
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

    [Test]
    public void TryLoadStateRlp_DelegatesToReader([Values] bool detailedMetrics)
    {
        TreePath path = TreePath.FromHexString("12");
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(path, ReadFlags.None).Returns([0xc1, 0xff]);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()), reader, detailedMetrics);

        Assert.That(bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None), Is.EqualTo(new byte[] { 0xc1, 0xff }));
    }

    [Test]
    public void TryLoadStorageRlp_DelegatesToReader([Values] bool detailedMetrics)
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
