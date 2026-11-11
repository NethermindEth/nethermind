// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// What a node reads through after a clean restart: the in-memory tail that the shutdown converted into one-block
/// persisted snapshots, which the next start loads and compacts.
/// </summary>
/// <remarks>
/// Blocks go through <see cref="FlatDbManager.AddSnapshot"/>, <see cref="FlatDbManager.DisposeAsync"/> converts the tail,
/// and the next manager's constructor loads it. RocksDB is a substitute whose state stays at <see cref="Persisted"/>.
/// </remarks>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class RestartedPersistedTierTests
{
    private const int CompactSize = 32;
    // A mainnet block writes thousands of keys; 64 slots are enough that a one-line filter cannot hold several blocks.
    private const int SlotsPerBlock = 64;

    // On a CompactSize boundary (offset 0 here), as the finalized persist leaves it.
    private static readonly StateId Persisted = State(1024);

    private static StateId State(ulong number) => new(number, Keccak.Compute($"{number}"));

    private static StateId Block(int indexInTail) => State(Persisted.BlockNumber + (ulong)indexInTail);

    // The default config keeps 128 to 159 blocks above the RocksDB state, and a clean shutdown converts all of them. 128
    // ends on a boundary; 150 and 159 leave 22 and 31 blocks above the last one.
    [TestCase(128)]
    [TestCase(150)]
    [TestCase(159)]
    public async Task A_restart_compacts_the_reloaded_tail_before_the_first_read(int tailLength)
    {
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        IPersistence persistence = PersistenceAt(Persisted);
        StateId head = Block(tailLength);
        await RunAndShutDown(dir.Path, catalog, persistence, tailLength, WriteBlock);

        // One CompactSize-wide snapshot per chunk, then one narrower one per set bit of the rest: 4, 7 and 9.
        int depth = tailLength / CompactSize + BitOperations.PopCount((uint)(tailLength % CompactSize));
        CatalogEntry[]? afterFirstStart = null;
        for (int start = 0; start < 2; start++) // the second start finds the work done
        {
            using FlatTestContainer restarted = Open(dir.Path, catalog, persistence);
            await using FlatDbManager manager = (FlatDbManager)restarted.Resolve<IFlatDbManager>();
            // Asserted at once: the first reads after the constructor returns already see the compacted stack.
            using AssembledSnapshotResult stack = restarted.Repository.AssembleSnapshots(head, Persisted, estimatedSize: tailLength);
            using ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(head);
            CatalogEntry[] rows = [.. new SnapshotCatalog(catalog).Load()];
            afterFirstStart ??= rows;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stack.Persisted.Count, Is.EqualTo(depth), "persisted snapshots a miss for the head walks");
                Assert.That(bundle.SnapshotCount, Is.EqualTo(depth), "snapshots in the bundle the manager serves for the head");
                Assert.That(rows.Count(static r => r.Tier == SnapshotTier.PersistedCompactSized), Is.EqualTo(tailLength / CompactSize));
                Assert.That(rows.Count(static r => r.Tier == SnapshotTier.PersistedLargeCompacted), Is.Zero, "no wider merge at a start");
                Assert.That(rows, Has.Length.EqualTo(restarted.Repository.PersistedSnapshotCount), "a catalog row for every snapshot");
                Assert.That(rows, Is.EquivalentTo(afterFirstStart), "the second start rewrites nothing");
                // A filter has to reject absent keys and admit present ones: every block writes AddressA.
                Assert.That(WorstFalsePositiveRate(stack.Persisted), Is.LessThan(0.01), "absent keys the weakest filter lets through");
                Assert.That(stack.Persisted.Select(static s => s.Bloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(TestItem.AddressA))), Is.All.True);
                Assert.That(stack.Persisted.Where(static s => s.Tier == SnapshotTier.PersistedSmallCompacted).Select(static s => s.Bloom.Capacity),
                    Is.All.GreaterThan(1), "the windows above the last boundary get a sized filter");
            }

            if (start == 0) // until the next start rebuilds the filters
            {
                Assert.That(restarted.Repository.TryLeasePersistedState(Block(16), SnapshotTier.PersistedSmallCompacted, out PersistedSnapshot? covered), Is.True);
                using (covered) Assert.That(covered!.Bloom.Capacity, Is.EqualTo(1), "a window under a CompactSize-wide one keeps the runtime filter");
            }
        }
    }

    // At 150 the head reads through the sub-CompactSize windows above the last boundary.
    [TestCase(128)]
    [TestCase(150)]
    public async Task Reads_after_a_restart_return_what_each_reloaded_block_wrote(int tailLength)
    {
        Address hot = TestItem.AddressA;        // written by every block, like a busy contract
        Address destructed = TestItem.AddressB; // self-destructed in block 70, created again in block 71
        Address deleted = TestItem.AddressC;    // created in block 10, deleted in block 90
        Address untouched = TestItem.AddressD;  // written by no block of the tail
        Address cleared = TestItem.AddressE;    // slots written in block 34, self-destructed in 40 below the large boundary 1088

        // RocksDB holds values the tail overwrote, deleted or cleared; none of them may show through.
        IPersistence persistence = PersistenceAt(Persisted, reader =>
        {
            reader.GetAccount(deleted).Returns(new Account(7, 777));
            reader.GetAccount(untouched).Returns(new Account(4, 444));
            StoredSlot(reader, hot, 2, 0xDEAD);
            StoredSlot(reader, hot, 3, 0x33);
            StoredSlot(reader, destructed, 1, 0xBAD1);
            StoredSlot(reader, destructed, 2, 0xBAD2);
        });

        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        await RunAndShutDown(dir.Path, catalog, persistence, tailLength, (i, content) =>
        {
            content.Accounts[hot] = new Account((ulong)i, (UInt256)i);
            content.Storages[(hot, (UInt256)1)] = (UInt256)i;
            switch (i)
            {
                case 3:
                    content.Storages[(hot, (UInt256)2)] = (UInt256)0x22;
                    break;
                case 5:
                    content.Accounts[destructed] = new Account(1, 5);
                    content.Storages[(destructed, (UInt256)1)] = (UInt256)0xB1;
                    content.Storages[(destructed, (UInt256)2)] = (UInt256)0xB2;
                    break;
                case 10:
                    content.Accounts[deleted] = new Account(1, 10);
                    break;
                case 34:
                    content.Accounts[cleared] = new Account(1, 34);
                    content.Storages[(cleared, (UInt256)0)] = (UInt256)0x34;
                    content.Storages[(cleared, (UInt256)1)] = (UInt256)0x35;
                    break;
                case 40:
                    content.Accounts[cleared] = null;
                    content.SelfDestructedStorageAddresses[cleared] = false;
                    break;
                case 41:
                    content.Accounts[cleared] = new Account(0, 41);
                    content.SelfDestructedStorageAddresses[cleared] = true;
                    content.Storages[(cleared, (UInt256)2)] = (UInt256)0x41;
                    break;
                case 60:
                    content.Storages[(hot, (UInt256)2)] = null;
                    break;
                case 70:
                    content.Accounts[destructed] = null;
                    content.SelfDestructedStorageAddresses[destructed] = false;
                    break;
                case 71:
                    content.Accounts[destructed] = new Account(0, 71);
                    content.SelfDestructedStorageAddresses[destructed] = true;
                    content.Storages[(destructed, (UInt256)2)] = (UInt256)0xB22;
                    break;
                case 90:
                    content.Accounts[deleted] = null;
                    break;
            }
        });

        for (int start = 0; start < 2; start++) // the second start rebuilds the filters of what the first compacted
        {
            using FlatTestContainer restarted = Open(dir.Path, catalog, persistence);
            await using FlatDbManager manager = (FlatDbManager)restarted.Resolve<IFlatDbManager>();

            StateId head = Block(tailLength);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ReadAccount(head, hot)?.Balance, Is.EqualTo((UInt256)tailLength));
                Assert.That(ReadSlot(head, hot, 1), Is.EqualTo((UInt256?)tailLength), "the newest write");
                Assert.That(ReadSlot(head, hot, 2), Is.Null, "deleted in block 60");
                Assert.That(ReadSlot(head, hot, 3), Is.EqualTo((UInt256?)0x33), "written by no block of the tail: RocksDB");
                Assert.That(ReadAccount(head, destructed)?.Balance, Is.EqualTo((UInt256)71));
                Assert.That(ReadSlot(head, destructed, 1), Is.Null, "cleared by the self-destruct in block 70");
                Assert.That(ReadSlot(head, destructed, 2), Is.EqualTo((UInt256?)0xB22), "written after the self-destruct");
                Assert.That(ReadAccount(head, deleted), Is.Null, "deleted in block 90");
                Assert.That(ReadAccount(head, untouched)?.Balance, Is.EqualTo((UInt256)444));
            }

            // States below the head stay readable: a reorg within MinReorgDepth starts from one of them.
            StateId beforeDeletions = Block(50);
            StateId atSelfDestruct = Block(70);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ReadSlot(beforeDeletions, hot, 1), Is.EqualTo((UInt256?)50));
                Assert.That(ReadSlot(beforeDeletions, hot, 2), Is.EqualTo((UInt256?)0x22));
                Assert.That(ReadSlot(beforeDeletions, destructed, 1), Is.EqualTo((UInt256?)0xB1));
                Assert.That(ReadAccount(beforeDeletions, deleted)?.Balance, Is.EqualTo((UInt256)10));
                Assert.That(ReadAccount(atSelfDestruct, destructed), Is.Null);
                Assert.That(ReadSlot(atSelfDestruct, destructed, 2), Is.Null, "cleared in this very block");
                Assert.That(ReadSlot(atSelfDestruct, hot, 2), Is.Null);
                Assert.That(ReadSlot(Block(71), hot, 1), Is.EqualTo((UInt256?)71), "through the base of block 71");
            }

            // Every retained state: a merge over block 40 drops the slots written before the self-destruct, so its filter
            // must not stand in for the snapshots below it, neither at this start nor when the next one rebuilds the filters.
            string[] wrong = [.. Enumerable.Range(1, tailLength).SelectMany(i => Enumerable.Range(0, 3)
                .Where(slot => ReadSlot(Block(i), cleared, slot) != ClearedSlot(i, slot)).Select(slot => $"block {i} slot {slot}"))];
            Assert.That(wrong, Is.Empty, "reads of the slots of the contract self-destructed in block 40");

            Account? ReadAccount(StateId at, Address address)
            {
                using ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(at);
                return bundle.GetAccount(address);
            }

            // As FlatStateReader reads a slot.
            UInt256? ReadSlot(StateId at, Address address, int index)
            {
                using ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(at);
                bundle.GetSlot(address, (UInt256)index, bundle.DetermineSelfDestructSnapshotIdx(address), out UInt256? value);
                return value;
            }
        }

        static UInt256? ClearedSlot(int i, int slot) =>
            i is >= 34 and < 40 && slot < 2 ? (UInt256)(0x34 + slot) : i >= 41 && slot == 2 ? (UInt256)0x41 : null;
    }

    // 33: the next start finds RocksDB in the middle of a chunk.
    [TestCase(32)]
    [TestCase(33)]
    public async Task The_first_persist_after_a_restart_writes_a_whole_chunk(int persistedAtNextStart)
    {
        const int TailLength = 159;
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        IPersistence persistence = PersistenceAt(Persisted);
        await RunAndShutDown(dir.Path, catalog, persistence, TailLength, WriteBlock);

        // Block 32 is finalized and the block after the tail leaves MinReorgDepth above it, so it is persisted.
        IStateHeaderProvider finality = Substitute.For<IStateHeaderProvider>();
        finality.FinalizedBlockNumber.Returns(Block(32).BlockNumber);
        finality.GetFinalizedHeader(Block(32).BlockNumber).Returns(Build.A.BlockHeader.WithNumber(Block(32).BlockNumber)
            .WithStateRoot(Keccak.Compute($"{Block(32).BlockNumber}")).TestObject);
        using (FlatTestContainer restarted = Open(dir.Path, catalog, persistence, finality))
        {
            FlatDbManager manager = (FlatDbManager)restarted.Resolve<IFlatDbManager>();
            AddBlocks(restarted, manager, TailLength + 1, TailLength + 1, WriteBlock);
            await manager.DisposeAsync(); // drains the persistence queue
        }

        (StateId From, StateId To)[] batches = [.. persistence.ReceivedCalls()
            .Where(static call => call.GetMethodInfo().Name == nameof(IPersistence.CreateWriteBatch))
            .Select(static call => ((StateId)call.GetArguments()[0]!, (StateId)call.GetArguments()[1]!))];
        Assert.That(batches, Is.EqualTo(new[] { (Persisted, Block(32)) }), "RocksDB write batches");

        using FlatTestContainer next = Open(dir.Path, catalog, PersistenceAt(Block(persistedAtNextStart)));
        await using FlatDbManager nextManager = (FlatDbManager)next.Resolve<IFlatDbManager>();
        using ReadOnlySnapshotBundle bundle = nextManager.GatherReadOnlySnapshotBundle(Block(TailLength + 1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.SnapshotCount, Is.EqualTo(4), "four CompactSize-wide snapshots above the persisted block");
            Assert.That(bundle.GetAccount(TestItem.AddressA)?.Balance, Is.EqualTo((UInt256)(TailLength + 1)));
        }
    }

    [Test]
    public async Task A_start_without_a_persisted_tier_compacts_nothing()
    {
        IPersistedSnapshotCompactor compactor = Substitute.For<IPersistedSnapshotCompactor>();
        using TempPath dir = TempPath.GetTempDirectory();
        using FlatTestContainer node = Open(dir.Path, new MemDb(), PersistenceAt(Persisted), compactor: compactor);
        await using FlatDbManager manager = (FlatDbManager)node.Resolve<IFlatDbManager>();
        compactor.DidNotReceiveWithAnyArgs().CompactReloaded(default, default);
    }

    [Test]
    public void A_start_compacts_what_one_run_can_leave_uncompacted_and_no_more()
    {
        // MaxInMemoryBaseSnapshotCount + (16 queued + 2) batches of CompactSize = 4 + 18 * 4 blocks below the newest.
        const ulong Bound = 100 - 76;
        using FlatTestContainer tier = SmallTier();
        ConvertBases(tier, 1, 100);

        Assert.That(tier.Compactor.CompactReloaded(0, CancellationToken.None), Is.Positive);
        ulong[] compactedTo = [.. tier.Repository.PersistedSnapshots.Where(static s => s.Tier != SnapshotTier.PersistedBase).Select(static s => s.To.BlockNumber)];
        Assert.That(compactedTo, Is.Not.Empty.And.All.GreaterThan(Bound), "nothing ends at or below the bound");
        Assert.That(compactedTo.Min(), Is.LessThanOrEqualTo(Bound + 4), "the lowest window ends within one CompactSize above it");
    }

    [Test]
    public void A_start_compacts_above_a_database_that_holds_no_state()
    {
        // RocksDB at PreGenesis, whose ulong.MaxValue must sort below genesis.
        using FlatTestContainer tier = SmallTier();
        ConvertBases(tier, 0, 20);
        Assert.That(tier.Compactor.CompactReloaded(StateId.PreGenesis.BlockNumber, CancellationToken.None), Is.Positive);
    }

    [Test]
    public async Task Runtime_compaction_keeps_the_one_line_filter_of_small_windows()
    {
        using FlatTestContainer tier = SmallTier();
        ConvertBases(tier, 1, 2);
        await tier.Compactor.EnqueueAsync(new ArrayPoolList<StateId>(2) { State(1), State(2) }, 0, CancellationToken.None);
        await tier.Compactor.DisposeAsync(); // drains the queue

        Assert.That(tier.Repository.TryLeasePersistedState(State(2), SnapshotTier.PersistedSmallCompacted, out PersistedSnapshot? window), Is.True);
        using (window) Assert.That(window!.Bloom.Capacity, Is.EqualTo(1));
    }

    private static void WriteBlock(int i, SnapshotContent content)
    {
        content.Accounts[TestItem.AddressA] = new Account((ulong)i, (UInt256)i);
        for (int slot = 0; slot < SlotsPerBlock; slot++)
            content.Storages[(TestItem.AddressA, (UInt256)(i * SlotsPerBlock + slot))] = (UInt256)i;
    }

    /// <summary>Adds the tail to a node whose RocksDB state is <see cref="Persisted"/>, then shuts it down cleanly.</summary>
    private static async Task RunAndShutDown(string path, MemDb catalog, IPersistence persistence, int tailLength, Action<int, SnapshotContent> write)
    {
        using FlatTestContainer node = Open(path, catalog, persistence);
        FlatDbManager manager = (FlatDbManager)node.Resolve<IFlatDbManager>();
        AddBlocks(node, manager, 1, tailLength, write);
        Assert.That(node.Repository.PersistedSnapshotCount, Is.Zero, "below MaxInMemoryBaseSnapshotCount the tail stays in memory");
        await manager.DisposeAsync();
        Assert.That(node.Repository.HasBasePersistedSnapshot(Block(1)) && node.Repository.HasBasePersistedSnapshot(Block(tailLength)), Is.True,
            "the shutdown converts the whole tail");
    }

    private static void AddBlocks(FlatTestContainer node, FlatDbManager manager, int first, int last, Action<int, SnapshotContent> write)
    {
        for (int i = first; i <= last; i++)
        {
            Snapshot snapshot = node.ResourcePool.CreateSnapshot(Block(i - 1), Block(i), ResourcePool.Usage.MainBlockProcessing);
            write(i, snapshot.Content);
            manager.AddSnapshot(snapshot, node.ResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing));
        }
    }

    private static FlatTestContainer SmallTier() =>
        new(new FlatDbConfig { CompactSize = 4, MaxInMemoryBaseSnapshotCount = 4, CompactionOffset = 0 }, arenaFileSizeBytes: 1024 * 1024);

    private static void ConvertBases(FlatTestContainer tier, ulong first, ulong last)
    {
        for (ulong i = first; i <= last; i++)
        {
            using Snapshot snapshot = tier.ResourcePool.CreateSnapshot(i == 0 ? StateId.PreGenesis : State(i - 1), State(i), ResourcePool.Usage.MainBlockProcessing);
            snapshot.Content.Accounts[TestItem.AddressA] = new Account(i, i);
            tier.ConvertToPersistedBase(snapshot).Dispose();
        }
    }

    // A filter sized for its keys lets about 0.2% of absent keys through at the default 14 bits per key.
    private static double WorstFalsePositiveRate(PersistedSnapshotList snapshots)
    {
        const int Probes = 1024;
        Random random = new(42);
        double worst = 0;
        foreach (PersistedSnapshot snapshot in snapshots)
        {
            int passed = 0;
            for (int i = 0; i < Probes; i++)
            {
                if (snapshot.Bloom.MightContain((ulong)random.NextInt64())) passed++;
            }

            worst = Math.Max(worst, (double)passed / Probes);
        }

        return worst;
    }

    private static FlatTestContainer Open(string path, MemDb catalog, IPersistence persistence, IStateHeaderProvider? finality = null,
        IPersistedSnapshotCompactor? compactor = null) =>
        new(new FlatDbConfig { CompactionOffset = 0, InlineCompaction = true }, arenaFileSizeBytes: 1024 * 1024, baseDbPath: path,
            catalogDb: catalog, finalizedStateProvider: finality, loadPersistedTier: false, configure: builder =>
            {
                builder.AddSingleton(persistence);
                if (compactor is not null) builder.AddSingleton(compactor);
            });

    private static IPersistence PersistenceAt(StateId state, Action<IPersistence.IPersistenceReader>? configure = null)
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(state);
        configure?.Invoke(reader);
        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader(Arg.Any<ReaderFlags>()).Returns(reader);
        return persistence;
    }

    private static void StoredSlot(IPersistence.IPersistenceReader reader, Address address, int index, int value) =>
        reader.TryGetSlot(address, (UInt256)index, ref Arg.Any<UInt256>()).Returns(call =>
        {
            call[2] = (UInt256)value;
            return true;
        });
}
