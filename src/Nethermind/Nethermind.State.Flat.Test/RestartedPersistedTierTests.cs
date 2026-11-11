// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// What a node reads through after a clean restart: the in-memory tail that the shutdown converted into one-block
/// persisted snapshots, loaded again by the next start.
/// </summary>
/// <remarks>
/// Both runs go through <see cref="FlatDbManager"/> as a node does: blocks are added with <see cref="FlatDbManager.AddSnapshot"/>
/// (which compacts them in memory), <see cref="FlatDbManager.DisposeAsync"/> converts the tail, and the second manager's
/// constructor loads it. RocksDB is a substitute whose state stays at <see cref="Persisted"/>.
/// </remarks>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class RestartedPersistedTierTests
{
    // A node following the head with the default config (CompactSize 32, MinReorgDepth 128) keeps 128 to 159 blocks in
    // memory above the RocksDB state: the finalized persist moves RocksDB one CompactSize forward once the head is
    // MinReorgDepth past the next boundary. A clean shutdown converts all of them; the measured mainnet restart reloaded 128.
    private const int TailLength = 128;
    private const int SlotsPerBlock = 64;

    // A persist always ends on a CompactSize boundary (offset 0 here), as the finalized persist leaves it.
    private static readonly StateId Persisted = State(1024);

    private static StateId State(ulong number) => new(number, Keccak.Compute($"{number}"));

    private static StateId Block(int indexInTail) => State(Persisted.BlockNumber + (ulong)indexInTail);

    private static FlatDbConfig Config() => new() { CompactionOffset = 0, InlineCompaction = true };

    // 128 ends on a boundary; 150 leaves a partial chunk of 22 blocks on top of four full ones.
    [TestCase(TailLength)]
    [TestCase(150)]
    public async Task Restart_reads_the_reloaded_tail_through_a_logarithmic_persisted_stack(int tailLength)
    {
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        FlatDbConfig config = Config();
        IPersistence persistence = PersistenceAt(Persisted);
        StateId head = Block(tailLength);

        // Enough keys per block that a filter sized for one block cannot hold a window of several: a mainnet block writes
        // thousands of keys.
        await RunAndShutDown(config, dir.Path, catalog, persistence, tailLength, static (i, content) =>
        {
            content.Accounts[TestItem.AddressA] = new Account((ulong)i, (UInt256)i);
            for (int slot = 0; slot < SlotsPerBlock; slot++)
            {
                content.Storages[(TestItem.AddressA, (UInt256)(i * SlotsPerBlock + slot))] = (UInt256)i;
            }
        });

        using FlatTestContainer restarted = Open(config, dir.Path, catalog, persistence);
        // The start: the manager's constructor loads the persisted tier before any read.
        await using FlatDbManager manager = (FlatDbManager)restarted.Resolve<IFlatDbManager>();
        Assert.That(manager.HasStateForBlock(head), Is.True, "the head's state is available after the restart");

        // A logarithmically compacted tail starting on a boundary is tiled by at most floor(N / CompactSize)
        // CompactSize-wide snapshots plus one narrower power-of-two snapshot per set bit of N mod CompactSize.
        int compactSize = (int)config.CompactSize;
        int bound = (tailLength + compactSize - 1) / compactSize + BitOperations.Log2((uint)compactSize);

        // The read stack GatherReadOnlySnapshotBundle assembles for the head, recomputed on every poll so that a compaction
        // running in the background is seen without waiting out the manager's bundle cache. A slot or account read that
        // misses probes one bloom in every snapshot of it (PersistedSnapshotStack.TryGetSlot), so this is also the number
        // of snapshots a miss visits.
        int PersistedReadDepth()
        {
            using AssembledSnapshotResult assembled = restarted.Repository.AssembleSnapshots(head, Persisted, estimatedSize: tailLength);
            return assembled.Persisted.Count;
        }

        Assert.That(PersistedReadDepth, Is.LessThanOrEqualTo(bound).After(1000, 10),
            $"persisted snapshots read through for the head after a restart that reloaded {tailLength} blocks");

        // The depth is worth only as much as each snapshot's filter: one that lets every key through turns each probe into
        // a lookup in the snapshot's table.
        using AssembledSnapshotResult stack = restarted.Repository.AssembleSnapshots(head, Persisted, estimatedSize: tailLength);
        using ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(head);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WorstFalsePositiveRate(stack.Persisted), Is.LessThan(0.05), "share of absent keys the weakest filter of the stack lets through");
            Assert.That(bundle.SnapshotCount, Is.LessThanOrEqualTo(bound), "snapshots in the bundle the manager serves for the head");
        }
    }

    [Test]
    public async Task Reads_after_a_restart_return_what_each_reloaded_block_wrote()
    {
        Address hot = TestItem.AddressA;        // written by every block, like a busy contract
        Address destructed = TestItem.AddressB; // self-destructed in block 70, created again in block 71
        Address deleted = TestItem.AddressC;    // created in block 10, deleted in block 90
        Address untouched = TestItem.AddressD;  // written by no block of the tail

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
        FlatDbConfig config = Config();
        await RunAndShutDown(config, dir.Path, catalog, persistence, TailLength, (i, content) =>
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

        using FlatTestContainer restarted = Open(config, dir.Path, catalog, persistence);
        await using FlatDbManager manager = (FlatDbManager)restarted.Resolve<IFlatDbManager>();

        StateId head = Block(TailLength);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadAccount(head, hot)?.Balance, Is.EqualTo((UInt256)TailLength));
            Assert.That(ReadSlot(head, hot, 1), Is.EqualTo((UInt256?)TailLength), "the newest of 128 writes");
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
        }

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

    /// <summary>Adds the tail to a node whose RocksDB state is <see cref="Persisted"/>, then shuts it down cleanly.</summary>
    private static async Task RunAndShutDown(FlatDbConfig config, string path, MemDb catalog, IPersistence persistence, int tailLength, Action<int, SnapshotContent> write)
    {
        using FlatTestContainer node = Open(config, path, catalog, persistence);
        FlatDbManager manager = (FlatDbManager)node.Resolve<IFlatDbManager>();
        StateId parent = Persisted;
        for (int i = 1; i <= tailLength; i++)
        {
            StateId to = Block(i);
            Snapshot snapshot = node.ResourcePool.CreateSnapshot(parent, to, ResourcePool.Usage.MainBlockProcessing);
            write(i, snapshot.Content);
            manager.AddSnapshot(snapshot, node.ResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing));
            parent = to;
        }

        Assert.That(node.Repository.PersistedSnapshotCount, Is.Zero, "below MaxInMemoryBaseSnapshotCount the tail stays in memory");
        await manager.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Repository.HasBasePersistedSnapshot(Block(1)), Is.True, "the shutdown converts the whole tail");
            Assert.That(node.Repository.HasBasePersistedSnapshot(Block(tailLength)), Is.True);
        }
    }

    // A filter sized for its keys lets about 0.2% of absent keys through at the default 14 bits per key; the always-true
    // placeholder, or a filter far too small for its keys, lets nearly all of them through.
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

    private static FlatTestContainer Open(FlatDbConfig config, string path, MemDb catalog, IPersistence persistence) =>
        new(config, arenaFileSizeBytes: 1024 * 1024, baseDbPath: path, catalogDb: catalog, loadPersistedTier: false,
            configure: builder => builder.AddSingleton(persistence));

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
