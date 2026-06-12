// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Autofac;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Config;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Bloom = Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter;

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
        UInt256 stored = BaseFlatPersistence.DecodeSlotValue([0x12, 0x34]);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c => c.Storages[new HashedKey<(Address, UInt256)>((address, index))] = stored)),
            recordDetailedMetrics: true);

        bundle.GetSlot(address, index, selfDestructStateIdx: -1, out UInt256? value);
        Assert.That(value, Is.EqualTo(stored));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void GetSlot_OverlayPresenceControlsFallback(bool hasEntry, bool explicitZero)
    {
        Address address = TestItem.AddressA;
        UInt256 index = 42;
        UInt256? overlayValue = explicitZero ? UInt256.Zero : null;
        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c => c.Storages[(address, index)] = new UInt256(7)),
            MakeSnapshot(c =>
            {
                if (hasEntry) c.Storages[(address, index)] = overlayValue;
            })));

        bundle.GetSlot(address, in index, selfDestructStateIdx: -1, out UInt256? value);
        Assert.That(value, Is.EqualTo(hasEntry ? overlayValue : new UInt256(7)));
    }

    [Test]
    public void GetSlot_StopsAtSelfDestructIndex_AndReturnsNull()
    {
        // Two snapshots, neither holds the slot. Iteration goes 1 -> 0.
        // selfDestructStateIdx=1 forces the loop to bail at i==1 instead of falling through to persistence.
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot(), MakeSnapshot()), reader);

        bundle.GetSlot(TestItem.AddressA, (UInt256)42, selfDestructStateIdx: 1, out UInt256? value);
        Assert.That(value, Is.Null);
        reader.DidNotReceive().TryGetSlot(Arg.Any<Address>(), Arg.Any<UInt256>(), ref Arg.Any<UInt256>());
    }

    [Test]
    public void GetSlot_FallsBackToPersistence_PreservesPresence([Values] bool detailedMetrics, [Values] bool found)
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryGetSlot(Arg.Any<Address>(), Arg.Any<UInt256>(), ref Arg.Any<UInt256>()).Returns(found);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(MakeSnapshot()), reader, detailedMetrics);

        bundle.GetSlot(TestItem.AddressA, (UInt256)1, selfDestructStateIdx: -1, out UInt256? value);
        Assert.That(value, Is.EqualTo(found ? UInt256.Zero : (UInt256?)null));
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
    public void ReverseDiffStack_ReadsForwardThenNearestReverseDiffThenPersistence()
    {
        // Historical stack: ascending list [R_top (nearest persisted), R_boundary, F]. The newest-first
        // read loop must check F, then R_boundary, then R_top, then persistence.
        Address inForward = TestItem.AddressA;
        Address inBothDiffs = TestItem.AddressB;
        Address nullMarker = TestItem.AddressC;
        Address onlyPersisted = TestItem.AddressD;

        Account forwardValue = TestItem.GenerateIndexedAccount(1);
        Account boundaryValue = TestItem.GenerateIndexedAccount(2);
        Account topValue = TestItem.GenerateIndexedAccount(3);
        Account persistedValue = TestItem.GenerateIndexedAccount(4);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.GetAccount(onlyPersisted).Returns(persistedValue);

        using ReadOnlySnapshotBundle bundle = Bundle(FlatTestHelpers.SnapshotList(
            MakeSnapshot(c =>
            {
                c.Accounts[new HashedKey<Address>(inBothDiffs)] = topValue;
                c.Accounts[new HashedKey<Address>(nullMarker)] = topValue;
            }),
            MakeSnapshot(c =>
            {
                c.Accounts[new HashedKey<Address>(inBothDiffs)] = boundaryValue;
                c.Accounts[new HashedKey<Address>(nullMarker)] = null;
            }),
            MakeSnapshot(c => c.Accounts[new HashedKey<Address>(inForward)] = forwardValue)), reader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetAccount(inForward), Is.EqualTo(forwardValue));
            Assert.That(bundle.GetAccount(inBothDiffs), Is.EqualTo(boundaryValue), "diff nearest the served state wins");
            Assert.That(bundle.GetAccount(nullMarker), Is.Null, "null marker shadows the newer persisted value");
            Assert.That(bundle.GetAccount(onlyPersisted), Is.EqualTo(persistedValue));
        }
        reader.DidNotReceive().GetAccount(nullMarker);
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

/// <summary>
/// The negative slot filter of <see cref="ReadOnlySnapshotBundle.GetSlotFiltered"/> must never change a read:
/// every read is compared with <see cref="ReadOnlySnapshotBundle.GetSlot(int, HashedKey{ValueTuple{Address, UInt256}}, out UInt256?)"/>
/// and with an oracle that scans a plain model of the layers newest-first.
/// </summary>
[TestFixture]
public class ReadOnlySnapshotBundleFilterTests
{
    private const int AddressCount = 6;
    private const int SlotsPerAddress = 16;
    private const double RealBitsPerKey = 14;

    // One bit per key saturates a small filter: a large share of absent keys answer "maybe", so reads take both the
    // plain loop and the definite-miss branch.
    private const double DegradedBitsPerKey = 1;

    private static readonly Address[] Addresses = TestItem.Addresses.Take(AddressCount).ToArray();
    private static readonly Address[] AbsentAddresses = TestItem.Addresses.Skip(AddressCount).Take(3).ToArray();

    private FlatDbConfig _config = null!;
    private ResourcePool _pool = null!;
    private ISnapshotCompactor _compactor = null!;
    private IArenaManager Arena => _tier.Resolve<IArenaManager>();
    private BlobArenaManager _blobs = null!;
    private FlatTestContainer _tier = null!;

    public enum FilterMode
    {
        /// <summary>A filter built by the first filtered read at the default bits per key.</summary>
        Built,

        /// <summary>A filter built at one bit per key, answering "maybe" for many absent keys.</summary>
        Degraded,

        /// <summary>No filter: bits per key 0.</summary>
        Disabled,
    }

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { CompactSize = 16, CompactionOffset = 0 };
        _tier = new FlatTestContainer(_config, arenaFileSizeBytes: 1024 * 1024, blobFileSizeBytes: 4L * 1024 * 1024);
        _pool = _tier.ResourcePool;
        _compactor = _tier.Resolve<ISnapshotCompactor>();
        _blobs = _tier.Blobs;
    }

    [TearDown]
    public void TearDown() => _tier.Dispose();

    [Test]
    [NonParallelizable]
    public void Persisted_snapshot_helper_releases_reservation_and_blob_leases()
    {
        using Snapshot snapshot = MakeSnapshot(0, content =>
            content.StateNodes[new TreePath(Keccak.Compute("p"), 8)] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]));
        long blobBytes = Metrics.BlobAllocatedBytes;
        byte[] table = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        long reservations = Metrics.ArenaReservationCount;
        Assert.That(Metrics.BlobAllocatedBytes, Is.GreaterThan(blobBytes));

        using (PersistedSnapshot persisted = TestFixtureHelpers.CreatePersistedSnapshot(Arena, _blobs, snapshot.From, snapshot.To, table))
        {
            Assert.That(Metrics.ArenaReservationCount, Is.EqualTo(reservations + 1));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.ArenaReservationCount, Is.EqualTo(reservations));
            Assert.That(Metrics.BlobAllocatedBytes, Is.EqualTo(blobBytes));
        }
    }

    [Test]
    public void Random_stacks_read_the_same_through_the_filter([Range(0, 499)] int seed) => RunRandomCase(seed);

    [Test]
    [Explicit("Long run of the randomized equivalence; run on demand.")]
    [Category("LongRunning")]
    public void Random_stacks_read_the_same_through_the_filter_long_run()
    {
        for (int seed = 500; seed < 20_000; seed++)
        {
            RunRandomCase(seed);
            TearDown();
            SetUp();
        }
    }

    [Test]
    public void Definite_miss_with_a_clear_in_memory_is_null_and_skips_persistence(
        [Values(FilterMode.Built, FilterMode.Degraded)] FilterMode mode)
    {
        Address address = Addresses[0];
        using LayerStack stack = new();
        stack.PersistenceSlots[(address, 1)] = 11;
        stack.AddInMemory(MakeSnapshot(0, c => c.Storages[(Addresses[1], 1)] = 5));
        stack.AddInMemory(MakeSnapshot(1, c => c.SelfDestructedStorageAddresses[address] = false));
        stack.AddInMemory(MakeSnapshot(2, c => c.Storages[(Addresses[2], 1)] = 6));

        using ReadOnlySnapshotBundle bundle = stack.CreateBundle(BitsFor(mode), detailedMetrics: false, out DictionaryReader reader);
        int clearIdx = bundle.DetermineSelfDestructSnapshotIdx(address);
        Assume.That(clearIdx, Is.EqualTo(1));

        bundle.GetSlotFiltered(clearIdx, (address, (UInt256)1), out UInt256? value);
        Assume.That(bundle.SlotFilter!.MightContain(Snapshot.StorageFilterKey((address, (UInt256)1))), Is.False,
            "the key must be a definite miss for this test to exercise its branch");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value, Is.Null);
            Assert.That(reader.SlotReads, Is.Zero);
        }

        // Without the clear the same definite miss reaches persistence.
        bundle.GetSlotFiltered(-1, (address, (UInt256)1), out value);
        Assert.That(value, Is.EqualTo((UInt256?)11));
    }

    [Test]
    public void Definite_miss_passes_the_clear_index_on_to_the_persisted_tier()
    {
        Address address = Addresses[0];
        using LayerStack stack = new();
        stack.PersistenceSlots[(address, 1)] = 11;
        stack.AddPersisted(_blobs, Arena, MakeSnapshot(0, c => c.Storages[(address, 1)] = 7));
        stack.AddPersisted(_blobs, Arena, MakeSnapshot(1, c => c.SelfDestructedStorageAddresses[address] = false));
        stack.AddInMemory(MakeSnapshot(2, c => c.Storages[(Addresses[1], 1)] = 5));
        stack.AddInMemory(MakeSnapshot(3, c => c.Storages[(Addresses[2], 1)] = 6));

        using ReadOnlySnapshotBundle bundle = stack.CreateBundle(RealBitsPerKey, detailedMetrics: true, out DictionaryReader reader);
        int clearIdx = bundle.DetermineSelfDestructSnapshotIdx(address);
        Assume.That(clearIdx, Is.EqualTo(1));

        bundle.GetSlotFiltered(clearIdx, (address, (UInt256)1), out UInt256? cleared);
        bundle.GetSlotFiltered(0, (address, (UInt256)1), out UInt256? belowClear);
        bundle.GetSlotFiltered(-1, (address, (UInt256)1), out UInt256? noClear);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.Null, "the clear in persisted snapshot 1 hides the older write");
            Assert.That(belowClear, Is.EqualTo((UInt256?)7), "persisted snapshot 0 is at the clear index and is still probed");
            Assert.That(noClear, Is.EqualTo((UInt256?)7));
            Assert.That(reader.SlotReads, Is.Zero);
        }
    }

    [Test]
    public void Filter_holds_every_key_of_mutable_and_compacted_snapshots([Values] bool sorted)
    {
        using LayerStack stack = new();
        Random rng = new(sorted ? 1 : 2);
        List<(Address, UInt256)> written = [];
        for (int layer = 0; layer < 4; layer++)
        {
            int salt = layer;
            Snapshot snapshot = MakeSnapshot(layer, c =>
            {
                for (int i = 0; i < 2_000; i++)
                {
                    (Address, UInt256) key = (TestItem.Addresses[rng.Next(40)], (UInt256)(ulong)rng.Next(1_000_000));
                    c.Storages[key] = rng.Next(5) == 0 ? null : (UInt256)(ulong)(salt * 1_000_000 + i + 1);
                    written.Add(key);
                }
            });
            stack.AddInMemory(sorted ? Compact(snapshot) : snapshot);
        }

        using ReadOnlySnapshotBundle bundle = stack.CreateBundle(RealBitsPerKey, detailedMetrics: false, out _);
        bundle.GetSlotFiltered(-1, (Addresses[0], (UInt256)1), out _);
        Bloom filter = bundle.SlotFilter!;

        Assert.That(written.Where(key => !filter.MightContain(Snapshot.StorageFilterKey(key))), Is.Empty);
    }

    [Test]
    public void Filter_is_built_only_with_bits_per_key_and_two_snapshots(
        [Values(0, 1, 2, 5)] int snapshots,
        [Values(0.0, RealBitsPerKey)] double bitsPerKey)
    {
        using LayerStack stack = new();
        for (int i = 0; i < snapshots; i++)
        {
            int salt = i;
            stack.AddInMemory(MakeSnapshot(i, c => c.Storages[(Addresses[0], (UInt256)(ulong)salt)] = (UInt256)(ulong)(salt + 1)));
        }

        using ReadOnlySnapshotBundle bundle = stack.CreateBundle(bitsPerKey, detailedMetrics: false, out _);
        Assert.That(bundle.SlotFilter, Is.Null, "nothing is built before the first filtered read");

        bundle.GetSlot(-1, (Addresses[0], (UInt256)0), out _);
        Assert.That(bundle.SlotFilter, Is.Null, "the plain read never builds");

        bundle.GetSlotFiltered(-1, (Addresses[0], (UInt256)0), out UInt256? value);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(value, Is.EqualTo(snapshots > 0 ? (UInt256?)1 : null));
            Assert.That(bundle.SlotFilter is not null, Is.EqualTo(snapshots >= 2 && bitsPerKey > 0));
        }
    }

    [Test]
    public void Snapshots_without_slots_get_a_filter_that_answers_every_read()
    {
        Address address = Addresses[0];
        using LayerStack stack = new();
        stack.PersistenceSlots[(address, 3)] = 33;
        stack.AddInMemory(MakeSnapshot(0, c => c.Accounts[address] = new Account(1, 1)));
        stack.AddInMemory(MakeSnapshot(1));

        using ReadOnlySnapshotBundle bundle = stack.CreateBundle(RealBitsPerKey, detailedMetrics: false, out _);
        bundle.GetSlotFiltered(-1, (address, (UInt256)3), out UInt256? value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value, Is.EqualTo((UInt256?)33));
            Assert.That(bundle.SlotFilter, Is.Not.Null);
        }
    }

    [Test]
    [NonParallelizable]
    public void A_filter_that_cannot_be_built_leaves_the_plain_loop_in_charge()
    {
        Address address = Addresses[0];
        using LayerStack stack = new();
        stack.PersistenceSlots[(address, 2)] = 22;
        stack.AddInMemory(MakeSnapshot(0, c => c.Storages[(address, 1)] = 1));
        stack.AddInMemory(MakeSnapshot(1, c => c.Storages[(address, 1)] = 2));
        long builds = Metrics.InMemorySlotFilterBuilds;
        long failures = Metrics.InMemorySlotFilterBuildFailures;

        // BloomFilter rejects a non-finite bits-per-key value, so the build throws for real.
        using ReadOnlySnapshotBundle bundle = stack.CreateBundle(double.PositiveInfinity, detailedMetrics: false, out _);
        bundle.GetSlotFiltered(-1, (address, (UInt256)1), out UInt256? hit);
        bundle.GetSlotFiltered(-1, (address, (UInt256)2), out UInt256? miss);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hit, Is.EqualTo((UInt256?)2));
            Assert.That(miss, Is.EqualTo((UInt256?)22));
            Assert.That(bundle.SlotFilter, Is.Null);
            Assert.That(Metrics.InMemorySlotFilterBuilds, Is.EqualTo(builds));
            Assert.That(Metrics.InMemorySlotFilterBuildFailures - failures, Is.EqualTo(1), "one failed build, then no retry");
        }
    }

    [Test]
    [NonParallelizable]
    public void Cleanup_frees_the_filter_and_its_memory()
    {
        using LayerStack stack = new();
        stack.AddInMemory(MakeSnapshot(0, c => c.Storages[(Addresses[0], 1)] = 1));
        stack.AddInMemory(MakeSnapshot(1, c => c.Storages[(Addresses[1], 1)] = 2));
        long memoryBefore = Metrics.InMemorySlotFilterMemory;
        long buildsBefore = Metrics.InMemorySlotFilterBuilds;

        ReadOnlySnapshotBundle bundle = stack.CreateBundle(RealBitsPerKey, detailedMetrics: false, out _);
        Assert.That(bundle.TryLease(), Is.True);
        bundle.GetSlotFiltered(-1, (Addresses[0], (UInt256)1), out _);
        Bloom filter = bundle.SlotFilter!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.InMemorySlotFilterBuilds - buildsBefore, Is.EqualTo(1));
            Assert.That(Metrics.InMemorySlotFilterMemory - memoryBefore, Is.EqualTo(filter.DataBytes));
        }

        bundle.Dispose();
        Assert.DoesNotThrow(() => filter.MightContain(1), "a bundle still leased keeps its filter");

        bundle.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.SlotFilter, Is.Null);
            Assert.That(() => filter.MightContain(1), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(Metrics.InMemorySlotFilterMemory, Is.EqualTo(memoryBefore));
        }
    }

    [Test]
    [NonParallelizable]
    public void Concurrent_reads_racing_the_build_match_the_plain_read()
    {
        const int threads = 8;
        using LayerStack stack = new();
        Random rng = new(7);
        List<(Address, UInt256)> keys = [];
        for (int layer = 0; layer < 6; layer++)
        {
            int salt = layer;
            Snapshot snapshot = MakeSnapshot(layer, c =>
            {
                for (int i = 0; i < 20_000; i++)
                {
                    (Address, UInt256) key = (TestItem.Addresses[rng.Next(40)], (UInt256)(ulong)rng.Next(10_000_000));
                    c.Storages[key] = (UInt256)(ulong)(salt * 100_000 + i + 1);
                    if (i % 20 == 0) keys.Add(key);
                }
                if (salt == 3) c.SelfDestructedStorageAddresses[TestItem.Addresses[0]] = false;
            });
            stack.AddInMemory(layer % 2 == 0 ? Compact(snapshot) : snapshot);
        }

        int hits = keys.Count;
        for (int i = 0; i < hits; i++) keys.Add((TestItem.Addresses[rng.Next(40)], (UInt256)(ulong)(20_000_000 + i)));
        foreach ((Address, UInt256) key in keys.Where((_, i) => i % 3 == 0)) stack.PersistenceSlots[key] = 1;

        using ReadOnlySnapshotBundle plain = stack.CreateBundle(0, detailedMetrics: false, out _);
        using ReadOnlySnapshotBundle filtered = stack.CreateBundle(RealBitsPerKey, detailedMetrics: false, out _);
        UInt256?[] expected = new UInt256?[keys.Count];
        int[] clearIdx = new int[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            clearIdx[i] = plain.DetermineSelfDestructSnapshotIdx(keys[i].Item1);
            plain.GetSlot(clearIdx[i], keys[i], out expected[i]);
        }

        long builds = Metrics.InMemorySlotFilterBuilds;
        using Barrier barrier = new(threads);
        int mismatches = 0;
        Task[] readers = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int pass = 0; pass < 3; pass++)
            {
                // Each thread walks the keys from its own offset, so the builder and the racers read different keys.
                for (int n = 0; n < keys.Count; n++)
                {
                    int i = (n + t * keys.Count / threads) % keys.Count;
                    filtered.GetSlotFiltered(clearIdx[i], keys[i], out UInt256? value);
                    if (value != expected[i]) Interlocked.Increment(ref mismatches);
                }
            }
        })).ToArray();
        Task.WaitAll(readers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Zero);
            Assert.That(filtered.SlotFilter, Is.Not.Null);
            Assert.That(Metrics.InMemorySlotFilterBuilds - builds, Is.EqualTo(1), "exactly one reader builds");
        }
    }

    [Test]
    [NonParallelizable]
    public void First_reads_released_together_build_one_filter()
    {
        const int rounds = 300;
        const int threads = 8;
        using LayerStack stack = new();
        stack.AddInMemory(MakeSnapshot(0, c => c.Storages[(Addresses[0], 1)] = 1));
        stack.AddInMemory(MakeSnapshot(1, c => c.Storages[(Addresses[1], 1)] = 2));
        long builds = Metrics.InMemorySlotFilterBuilds;
        long memory = Metrics.InMemorySlotFilterMemory;

        for (int round = 0; round < rounds; round++)
        {
            using ReadOnlySnapshotBundle bundle = stack.CreateBundle(RealBitsPerKey, detailedMetrics: false, out _);
            int ready = 0;
            int go = 0;
            Thread[] readers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
            {
                Interlocked.Increment(ref ready);
                while (Volatile.Read(ref go) == 0) Thread.SpinWait(1);
                bundle.GetSlotFiltered(-1, (Addresses[0], (UInt256)1), out _);
            })).ToArray();
            foreach (Thread reader in readers) reader.Start();
            while (Volatile.Read(ref ready) < threads) Thread.SpinWait(1);
            Volatile.Write(ref go, 1);
            foreach (Thread reader in readers) reader.Join();
        }

        // A second build of the same bundle would publish over the first and leak it.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.InMemorySlotFilterBuilds - builds, Is.EqualTo(rounds));
            Assert.That(Metrics.InMemorySlotFilterMemory, Is.EqualTo(memory));
        }
    }

    [Test]
    public void Snapshot_bundle_uses_the_filter_only_when_asked([Values] bool filterInMemorySlotReads)
    {
        Address address = Addresses[0];
        using LayerStack stack = new();
        stack.AddInMemory(MakeSnapshot(0, c => c.Storages[(address, 1)] = 1));
        stack.AddInMemory(MakeSnapshot(1, c => c.Storages[(address, 2)] = 2));

        ReadOnlySnapshotBundle readOnly = stack.CreateBundle(RealBitsPerKey, detailedMetrics: false, out _);
        Assert.That(readOnly.TryLease(), Is.True);
        try
        {
            using (SnapshotBundle bundle = new(readOnly, Substitute.For<ITrieNodeCache>(), _pool,
                       ResourcePool.Usage.ReadOnlyProcessingEnv, filterInMemorySlotReads: filterInMemorySlotReads))
            {
                bundle.GetSlot(address, 1, bundle.DetermineSelfDestructSnapshotIdx(address), out UInt256? value);
                Assert.That(value, Is.EqualTo((UInt256?)1));
            }

            Assert.That(readOnly.SlotFilter is not null, Is.EqualTo(filterInMemorySlotReads));
        }
        finally
        {
            readOnly.Dispose();
        }
    }

    [Test]
    public void Overridable_world_scope_reads_slots_through_the_filter([Values] bool throughScope)
    {
        Address address = Addresses[0];
        using LayerStack stack = new();
        stack.PersistenceSlots[(address, 9)] = 99;
        stack.AddInMemory(MakeSnapshot(0, c => c.Storages[(address, 1)] = 1));
        stack.AddInMemory(MakeSnapshot(1, c => c.Storages[(address, 2)] = 2));

        ReadOnlySnapshotBundle readOnly = stack.CreateBundle(RealBitsPerKey, detailedMetrics: false, out _);
        IFlatDbManager flatDbManager = Substitute.For<IFlatDbManager>();
        flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns(_ =>
        {
            readOnly.TryLease();
            return readOnly;
        });

        using ILifetimeScope scopeContainer = _tier.Resolve<ILifetimeScope>().BeginLifetimeScope(builder => builder
            .AddSingleton<IFlatDbManager>(flatDbManager)
            .AddKeyedSingleton<IDb>(DbNames.Code, _ => new TestMemDb()));
        FlatOverridableWorldScope overridable = scopeContainer.Resolve<FlatOverridableWorldScope>();
        BlockHeader header = Build.A.BlockHeader.WithNumber(2).WithStateRoot(Keccak.Compute("root")).TestObject;
        try
        {
            UInt256 hit;
            UInt256 miss;
            if (throughScope)
            {
                using IWorldStateScopeProvider.IScope scope = overridable.WorldState.BeginScope(header);
                IWorldStateScopeProvider.IStorageTree tree = scope.CreateStorageTree(address);
                tree.Get(1, out hit);
                tree.Get(9, out miss);
            }
            else
            {
                overridable.GlobalStateReader.GetStorage(header, address, 1, out hit);
                overridable.GlobalStateReader.GetStorage(header, address, 9, out miss);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(hit, Is.EqualTo((UInt256)1));
                Assert.That(miss, Is.EqualTo((UInt256)99));
                Assert.That(readOnly.SlotFilter, Is.Not.Null);
            }
        }
        finally
        {
            readOnly.Dispose();
        }
    }

    [Test]
    public async Task Flat_db_manager_enables_the_filter_only_on_shared_bundles([Values(0.0, RealBitsPerKey)] double bitsPerKey)
    {
        Address address = Addresses[0];
        StateId head = CreateStateId(10);
        StateId persisted = CreateStateId(5);
        FlatDbConfig config = new() { CompactSize = 16, MaxInFlightCompactJob = 4, InlineCompaction = true, InMemorySnapshotBloomBitsPerKey = bitsPerKey };

        using LayerStack stack = new();
        stack.AddInMemory(MakeSnapshot(6, c => c.Storages[(address, 1)] = 1));
        stack.AddInMemory(MakeSnapshot(7, c => c.Storages[(address, 2)] = 2));

        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        persistenceManager.GetCurrentPersistedStateId().Returns(persisted);
        persistenceManager.LeaseReader(Arg.Any<ReaderFlags>()).Returns(_ => new DictionaryReader(stack.PersistenceSlots, stack.PersistenceAccounts, persisted));
        ISnapshotRepository repository = Substitute.For<ISnapshotRepository>();
        repository.AssembleSnapshots(head, persisted, Arg.Any<int>()).Returns(_ => new AssembledSnapshotResult(stack.LeaseInMemory(), PersistedSnapshotList.Empty()));

        using CancellationTokenSource cts = new();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(cts.Token);
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.SecondsPerSlot.Returns(12UL);

        await using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .AddSingleton<IProcessExitSource>(processExitSource)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IBlocksConfig>(blocksConfig)
            .AddSingleton<ISyncConfig>(new SyncConfig())
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<ISnapshotRepository>(repository)
            .AddSingleton<IPersistenceManager>(persistenceManager)
            .AddSingleton<IPersistedSnapshotLoader>(Substitute.For<IPersistedSnapshotLoader>())
            .AddSingleton<ISnapshotCompactor>(Substitute.For<ISnapshotCompactor>())
            .Build();
        IFlatDbManager manager = container.Resolve<IFlatDbManager>();

        // Block processing reads the shared bundle without the filter...
        using (SnapshotBundle processing = manager.GatherSnapshotBundle(head, ResourcePool.Usage.MainBlockProcessing))
        {
            processing.GetSlot(address, 1, processing.DetermineSelfDestructSnapshotIdx(address), out UInt256? value);
            Assert.That(value, Is.EqualTo((UInt256?)1));
        }

        using ReadOnlySnapshotBundle shared = manager.GatherReadOnlySnapshotBundle(head);
        Assert.That(shared.SlotFilter, Is.Null, "block processing must not build the filter");

        // ...while a filtered read of the same bundle builds it, and a flagged bundle never does.
        shared.GetSlotFiltered(-1, (address, (UInt256)1), out UInt256? sharedValue);
        using ReadOnlySnapshotBundle fullScan = manager.GatherReadOnlySnapshotBundle(head, ReaderFlags.FullScan);
        fullScan.GetSlotFiltered(-1, (address, (UInt256)2), out UInt256? fullScanValue);
        using ReadOnlySnapshotBundle preGenesis = manager.GatherReadOnlySnapshotBundle(StateId.PreGenesis);
        preGenesis.GetSlotFiltered(-1, (address, (UInt256)2), out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sharedValue, Is.EqualTo((UInt256?)1));
            Assert.That(fullScanValue, Is.EqualTo((UInt256?)2));
            Assert.That(shared.SlotFilter is not null, Is.EqualTo(bitsPerKey > 0));
            Assert.That(fullScan.SlotFilter, Is.Null);
            Assert.That(preGenesis.SlotFilter, Is.Null);
        }
    }

    private void RunRandomCase(int seed)
    {
        Random rng = new(seed);
        bool detailedMetrics = seed % 2 == 0;
        using LayerStack stack = new();

        FillPersistence(rng, stack);

        ulong block = 0;
        int persistedCount = seed % 3 == 0 ? 0 : rng.Next(1, 4);
        for (int i = 0; i < persistedCount; i++)
        {
            Layer layer = new();
            ulong salt = ++block;
            stack.AddPersisted(_blobs, Arena, MakeSnapshot(salt, c => FillRandom(rng, c, layer, salt)), layer);
        }

        int inMemoryCount = rng.Next(0, 9);
        for (int i = 0; i < inMemoryCount; i++)
        {
            // A third of the layers are compacted runs, the sorted form most layers of a mainnet bundle have.
            int run = rng.Next(3) == 0 ? rng.Next(1, 5) : 0;
            if (run == 0)
            {
                Layer layer = new();
                ulong salt = ++block;
                stack.AddInMemory(MakeSnapshot(salt, c => FillRandom(rng, c, layer, salt)), layer);
            }
            else
            {
                using SnapshotPooledList sources = new(run);
                for (int j = 0; j < run; j++)
                {
                    ulong salt = ++block;
                    sources.Add(MakeSnapshot(salt, c => FillRandom(rng, c, new Layer(), salt)));
                }

                Snapshot compacted = _compactor.CompactSnapshotBundle(sources);
                stack.AddInMemory(compacted, Layer.Of(compacted));
            }
        }

        List<string> errors = [];
        foreach (FilterMode mode in Enum.GetValues<FilterMode>())
        {
            using ReadOnlySnapshotBundle bundle = stack.CreateBundle(BitsFor(mode), detailedMetrics, out _);

            CheckReadOnlyBundle(stack, bundle, mode, errors);

            bool expectBuilt = (mode is FilterMode.Built or FilterMode.Degraded) && inMemoryCount >= 2;
            if ((bundle.SlotFilter is not null) != expectBuilt) errors.Add($"{mode}: filter built = {bundle.SlotFilter is not null}");

            if (bundle.SlotFilter is { } filter)
            {
                foreach (Layer layer in stack.Layers.Skip(stack.PersistedCount))
                {
                    foreach ((Address, UInt256) key in layer.Slots.Keys)
                    {
                        if (!filter.MightContain(Snapshot.StorageFilterKey(key))) errors.Add($"{mode}: false negative for {key}");
                    }
                }
            }

            CheckSnapshotBundle(rng, stack, bundle, mode, errors);

            // The filter has no false negatives only while no snapshot gains a slot key after the build, so after all
            // the reads above each in-memory snapshot must still hold exactly the slot keys of its model.
            for (int i = 0; i < stack.InMemory.Count; i++)
            {
                HashSet<(Address, UInt256)> now = stack.InMemory[i].Storages.Select(kv => kv.Key.Key).ToHashSet();
                if (!now.SetEquals(stack.Layers[stack.PersistedCount + i].Slots.Keys)) errors.Add($"{mode}: in-memory snapshot {i} changed its slot keys");
            }
        }

        Assert.That(errors, Is.Empty,
            $"seed {seed}: {persistedCount} persisted, {inMemoryCount} in-memory; first: {string.Join("; ", errors.Take(10))}");
    }

    private static void CheckReadOnlyBundle(LayerStack stack, ReadOnlySnapshotBundle bundle, FilterMode mode, List<string> errors)
    {
        foreach (Address address in Addresses.Concat(AbsentAddresses))
        {
            int expectedIdx = Oracle.SelfDestructIdx(stack.Layers, address);
            int actualIdx = bundle.DetermineSelfDestructSnapshotIdx(address);
            if (actualIdx != expectedIdx) errors.Add($"{mode}: self-destruct index of {address} {actualIdx} != {expectedIdx}");
        }

        foreach ((Address, UInt256) key in ProbeKeys())
        {
            for (int d = -1; d <= bundle.SnapshotCount; d++)
            {
                UInt256? expected = Oracle.Slot(stack.Layers, stack.PersistenceSlots, key, d);
                bundle.GetSlot(d, key, out UInt256? plain);
                bundle.GetSlotFiltered(d, key, out UInt256? filtered);
                if (plain != expected) errors.Add($"{mode}: GetSlot({d}, {key}) = {plain}, oracle {expected}");
                if (filtered != expected) errors.Add($"{mode}: GetSlotFiltered({d}, {key}) = {filtered}, oracle {expected}");
            }
        }
    }

    // The same stack behind a SnapshotBundle with its own snapshots and a written current content, read with the
    // filter on and off.
    private void CheckSnapshotBundle(Random rng, LayerStack stack, ReadOnlySnapshotBundle readOnly, FilterMode mode, List<string> errors)
    {
        List<Layer> layers = [.. stack.Layers];
        List<Snapshot> own = [];
        int ownCount = rng.Next(0, 3);
        for (int i = 0; i < ownCount; i++)
        {
            Layer layer = new();
            ulong salt = (ulong)(1000 + i);
            own.Add(MakeSnapshot(salt, c => FillRandom(rng, c, layer, salt)));
            layers.Add(layer);
        }

        Layer current = new();
        layers.Add(current);
        List<(Address Address, UInt256 Index, UInt256 Value, bool Clear)> ops = [];
        int opCount = rng.Next(0, 12);
        for (int i = 0; i < opCount; i++)
        {
            Address address = Addresses[rng.Next(AddressCount)];
            bool clear = rng.Next(6) == 0;
            UInt256 index = (UInt256)(ulong)rng.Next(1, SlotsPerAddress + 1);
            UInt256 value = rng.Next(4) == 0 ? UInt256.Zero : (UInt256)(ulong)rng.Next(1, 1000);
            ops.Add((address, index, value, clear));
            if (clear)
            {
                foreach ((Address, UInt256) key in current.Slots.Keys.Where(k => k.Item1 == address).ToArray()) current.Slots.Remove(key);
                current.SelfDestructs.Add(address);
            }
            else
            {
                current.Slots[(address, index)] = value.IsZero ? null : value;
            }
        }

        SnapshotBundle[] bundles = new SnapshotBundle[2];
        try
        {
            for (int b = 0; b < bundles.Length; b++)
            {
                SnapshotPooledList ownList = new(Math.Max(1, own.Count));
                foreach (Snapshot snapshot in own)
                {
                    snapshot.AcquireLease();
                    ownList.Add(snapshot);
                }

                readOnly.TryLease();
                bundles[b] = new SnapshotBundle(readOnly, Substitute.For<ITrieNodeCache>(), _pool,
                    ResourcePool.Usage.ReadOnlyProcessingEnv, ownList, filterInMemorySlotReads: b == 1);
                foreach ((Address address, UInt256 index, UInt256 value, bool clear) in ops)
                {
                    if (clear) bundles[b].ClearStorage(address, Keccak.Compute(address.Bytes));
                    else bundles[b].SetChangedSlot(address, index, value);
                }
            }

            int total = readOnly.SnapshotCount + ownCount;
            foreach (Address address in Addresses.Concat(AbsentAddresses))
            {
                int expectedIdx = Oracle.SelfDestructIdx(layers, address);
                string expectedAccount = Oracle.Describe(Oracle.Account(layers, stack.PersistenceAccounts, address));
                foreach (SnapshotBundle bundle in bundles)
                {
                    int actualIdx = bundle.DetermineSelfDestructSnapshotIdx(address);
                    if (actualIdx != expectedIdx) errors.Add($"{mode} bundle: self-destruct index of {address} {actualIdx} != {expectedIdx}");
                    string actualAccount = Oracle.Describe(bundle.GetAccount(address));
                    if (actualAccount != expectedAccount) errors.Add($"{mode} bundle: account {address} {actualAccount} != {expectedAccount}");
                }
            }

            foreach ((Address, UInt256) key in ProbeKeys())
            {
                for (int d = -1; d <= total; d++)
                {
                    UInt256? expected = Oracle.Slot(layers, stack.PersistenceSlots, key, d);
                    for (int b = 0; b < bundles.Length; b++)
                    {
                        bundles[b].GetSlot(key.Item1, key.Item2, d, out UInt256? actual);
                        if (actual != expected) errors.Add($"{mode} bundle (filter {b == 1}): GetSlot({d}, {key}) = {actual}, oracle {expected}");
                    }
                }
            }
        }
        finally
        {
            foreach (SnapshotBundle? bundle in bundles) bundle?.Dispose();
            foreach (Snapshot snapshot in own) snapshot.Dispose();
        }
    }

    private static IEnumerable<(Address, UInt256)> ProbeKeys()
    {
        foreach (Address address in Addresses)
        {
            for (int slot = 1; slot <= SlotsPerAddress + 2; slot++) yield return (address, (UInt256)(ulong)slot);
        }

        foreach (Address address in AbsentAddresses)
        {
            for (int slot = 1; slot <= 4; slot++) yield return (address, (UInt256)(ulong)slot);
        }
    }

    private static void FillPersistence(Random rng, LayerStack stack)
    {
        foreach ((Address, UInt256) key in ProbeKeys())
        {
            if (rng.Next(5) < 2) stack.PersistenceSlots[key] = (UInt256)(ulong)rng.Next(1, 1_000_000);
        }

        foreach (Address address in Addresses.Concat(AbsentAddresses))
        {
            if (rng.Next(2) == 0) stack.PersistenceAccounts[address] = new Account(0, (UInt256)(ulong)rng.Next(1, 1000));
        }
    }

    private static void FillRandom(Random rng, SnapshotContent content, Layer layer, ulong salt)
    {
        if (rng.Next(4) == 0)
        {
            int clears = rng.Next(1, 3);
            for (int i = 0; i < clears; i++)
            {
                Address address = Addresses[rng.Next(AddressCount)];
                content.SelfDestructedStorageAddresses[address] = rng.Next(2) == 0;
                layer.SelfDestructs.Add(address);
            }
        }

        int writes = rng.Next(0, 25);
        for (int i = 0; i < writes; i++)
        {
            (Address, UInt256) key = (Addresses[rng.Next(AddressCount)], (UInt256)(ulong)rng.Next(1, SlotsPerAddress + 1));
            // Zero writes are stored as null, as SnapshotBundle.SetChangedSlot does.
            UInt256? value = rng.Next(5) == 0 ? null : (UInt256)(salt * 10_000 + (ulong)rng.Next(1, 10_000));
            content.Storages[key] = value;
            layer.Slots[key] = value;
        }

        int accounts = rng.Next(0, 4);
        for (int i = 0; i < accounts; i++)
        {
            Address address = Addresses[rng.Next(AddressCount)];
            Account? account = rng.Next(4) == 0 ? null : new Account((ulong)rng.Next(1, 100), (UInt256)(salt * 10_000 + (ulong)rng.Next(1, 10_000)));
            content.Accounts[address] = account;
            layer.Accounts[address] = account;
        }
    }

    private static double BitsFor(FilterMode mode) => mode switch
    {
        FilterMode.Degraded => DegradedBitsPerKey,
        FilterMode.Disabled => 0,
        _ => RealBitsPerKey,
    };

    private static StateId CreateStateId(ulong blockNumber)
    {
        byte[] bytes = new byte[32];
        bytes[31] = (byte)blockNumber;
        bytes[30] = (byte)(blockNumber >> 8);
        return new StateId(blockNumber, new ValueHash256(bytes));
    }

    private Snapshot MakeSnapshot(ulong block, Action<SnapshotContent>? populate = null)
    {
        SnapshotContent content = _pool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        populate?.Invoke(content);
        return new Snapshot(CreateStateId(block), CreateStateId(block + 1), content, _pool, ResourcePool.Usage.MainBlockProcessing);
    }

    private Snapshot MakeSnapshot(int block, Action<SnapshotContent>? populate = null) => MakeSnapshot((ulong)block, populate);

    private Snapshot Compact(Snapshot snapshot)
    {
        using SnapshotPooledList sources = new(1) { snapshot };
        return _compactor.CompactSnapshotBundle(sources);
    }

    /// <summary>A plain model of one layer: what it wrote and which addresses it cleared.</summary>
    private sealed class Layer
    {
        public readonly Dictionary<(Address, UInt256), UInt256?> Slots = [];
        public readonly Dictionary<Address, Account?> Accounts = [];
        public readonly HashSet<Address> SelfDestructs = [];

        // A compacted snapshot's model is read off its content: the compactor drops writes cleared later in its run,
        // which is its own behaviour and not what these tests check.
        public static Layer Of(Snapshot snapshot)
        {
            Layer layer = new();
            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> kv in snapshot.Storages) layer.Slots[kv.Key.Key] = kv.Value;
            foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts) layer.Accounts[kv.Key.Key] = kv.Value;
            foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses) layer.SelfDestructs.Add(kv.Key.Key);
            return layer;
        }
    }

    /// <summary>Reads a list of layers (oldest first, by combined index) newest-first, honouring clears.</summary>
    private static class Oracle
    {
        public static UInt256? Slot(IReadOnlyList<Layer> layers, Dictionary<(Address, UInt256), UInt256> persistence, (Address, UInt256) key, int selfDestructIdx)
        {
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                if (layers[i].Slots.TryGetValue(key, out UInt256? value)) return value;
                if (i <= selfDestructIdx) return null;
            }

            return persistence.TryGetValue(key, out UInt256 persisted) ? persisted : null;
        }

        public static int SelfDestructIdx(IReadOnlyList<Layer> layers, Address address)
        {
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                if (layers[i].SelfDestructs.Contains(address)) return i;
            }

            return -1;
        }

        public static Account? Account(IReadOnlyList<Layer> layers, Dictionary<Address, Account> persistence, Address address)
        {
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                if (layers[i].Accounts.TryGetValue(address, out Account? account)) return account;
            }

            return persistence.GetValueOrDefault(address);
        }

        public static string Describe(Account? account) => account is null ? "none" : $"{account.Nonce}/{account.Balance}";
    }

    /// <summary>The layers of one test stack, owned here, and bundles over leases of them.</summary>
    private sealed class LayerStack : IDisposable
    {
        private readonly List<Snapshot> _inMemory = [];
        private readonly List<PersistedSnapshot> _persisted = [];

        /// <summary>Models of the persisted layers, then of the in-memory ones, oldest first.</summary>
        public List<Layer> Layers { get; } = [];
        public int PersistedCount => _persisted.Count;
        public IReadOnlyList<Snapshot> InMemory => _inMemory;
        public Dictionary<(Address, UInt256), UInt256> PersistenceSlots { get; } = [];
        public Dictionary<Address, Account> PersistenceAccounts { get; } = [];

        public void AddInMemory(Snapshot snapshot, Layer? layer = null)
        {
            _inMemory.Add(snapshot);
            Layers.Add(layer ?? Layer.Of(snapshot));
        }

        public void AddPersisted(BlobArenaManager blobs, IArenaManager arena, Snapshot snapshot, Layer? layer = null)
        {
            Assert.That(_inMemory, Is.Empty, "persisted layers are older than in-memory ones");
            try
            {
                byte[] table = PersistedSnapshotBuilderTestExtensions.Build(snapshot, blobs);
                _persisted.Add(TestFixtureHelpers.CreatePersistedSnapshot(arena, blobs, snapshot.From, snapshot.To, table));
                Layers.Add(layer ?? Layer.Of(snapshot));
            }
            finally
            {
                snapshot.Dispose();
            }
        }

        public SnapshotPooledList LeaseInMemory()
        {
            SnapshotPooledList list = new(Math.Max(1, _inMemory.Count));
            foreach (Snapshot snapshot in _inMemory)
            {
                snapshot.AcquireLease();
                list.Add(snapshot);
            }

            return list;
        }

        public ReadOnlySnapshotBundle CreateBundle(double bitsPerKey, bool detailedMetrics, out DictionaryReader reader)
        {
            PersistedSnapshotList persisted = new(Math.Max(1, _persisted.Count));
            foreach (PersistedSnapshot snapshot in _persisted)
            {
                Assert.That(snapshot.TryAcquire(), Is.True);
                persisted.Add(snapshot);
            }

            reader = new DictionaryReader(PersistenceSlots, PersistenceAccounts, StateId.PreGenesis);
            return new ReadOnlySnapshotBundle(LeaseInMemory(), reader, detailedMetrics,
                new PersistedSnapshotStack(persisted, detailedMetrics), slotFilterBitsPerKey: bitsPerKey);
        }

        public void Dispose()
        {
            foreach (Snapshot snapshot in _inMemory) snapshot.Dispose();
            foreach (PersistedSnapshot snapshot in _persisted) snapshot.Dispose();
        }
    }

    /// <summary>Persistence over dictionaries; counts slot reads.</summary>
    private sealed class DictionaryReader(
        Dictionary<(Address, UInt256), UInt256> slots,
        Dictionary<Address, Account> accounts,
        StateId currentState) : IPersistence.IPersistenceReader
    {
        private int _slotReads;
        public int SlotReads => Volatile.Read(ref _slotReads);

        public Account? GetAccount(Address address) => accounts.GetValueOrDefault(address);

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            Interlocked.Increment(ref _slotReads);
            if (!slots.TryGetValue((address, slot), out UInt256 value)) return false;
            outValue = value;
            return true;
        }

        public StateId CurrentState => currentState;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => false;
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
        public void Dispose() { }
    }
}
