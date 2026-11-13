// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Visitors;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PersistInMemoryTailForShutdownTests
{
    private static readonly StateId Block0 = new(0, Keccak.EmptyTreeHash);

    private static StateId State(ulong number, byte fork = 0) => new(number, Keccak.Compute($"{number}-{fork}"));

    private static FlatDbConfig Config(bool longFinality = true) => new()
    {
        CompactSize = 16,
        CompactionOffset = 0,
        MinReorgDepth = 64,
        MaxInMemoryBaseSnapshotCount = 160,
        EnableLongFinality = longFinality,
    };

    [Test]
    public void Converts_the_committed_chain_and_it_survives_a_restart()
    {
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        StateId head = State(3);
        using (Session session = new(Config(), dir.Path, catalog))
        {
            session.AddChain(Block0, 3);
            // A fork off the committed chain stays behind: only the head's ancestry is needed to resume from it.
            session.Add(State(1), State(2, fork: 1));
            session.Repository.SetLastCommittedStateId(head);

            int converted = session.Manager.PersistInMemoryTailForShutdown(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(converted, Is.EqualTo(3));
                Assert.That(session.Repository.HasBasePersistedSnapshot(State(1)), Is.True);
                Assert.That(session.Repository.HasBasePersistedSnapshot(head), Is.True);
                Assert.That(session.Repository.HasBasePersistedSnapshot(State(2, fork: 1)), Is.False, "fork");
                Assert.That(session.Barrier.ReceivedCalls(), Is.Not.Empty, "deferred block data is made durable before the state");
            }
        }

        using Session restarted = new(Config(), dir.Path, catalog);
        using AssembledSnapshotResult assembled = restarted.Repository.AssembleSnapshots(head, Block0, estimatedSize: 4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restarted.Repository.HasState(head), Is.True, "the head's state is available after the restart");
            Assert.That(assembled.SnapshotCount, Is.EqualTo(3), "and reachable from the persisted state");
            Assert.That(restarted.Repository.HasState(State(2, fork: 1)), Is.False);
        }
    }

    [Test]
    public void Converts_only_what_is_missing_above_the_persisted_tier()
    {
        using TempPath dir = TempPath.GetTempDirectory();
        using Session session = new(Config(), dir.Path, new MemDb());
        session.AddChain(Block0, 4);
        // As after a normal conversion: the oldest blocks of the tail are already in the persisted tier.
        using (Snapshot? first = session.Lease(State(1))) session.Loader.ConvertAndRegister(first!);
        using (Snapshot? second = session.Lease(State(2))) session.Loader.ConvertAndRegister(second!);
        session.Repository.SetLastCommittedStateId(State(4));

        int converted = session.Manager.PersistInMemoryTailForShutdown(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(converted, Is.EqualTo(2));
            Assert.That(session.Repository.HasBasePersistedSnapshot(State(4)), Is.True);
        }
    }

    [Test]
    public void Nothing_is_converted_when_cancelled_without_long_finality_or_across_a_gap(
        [Values("cancelled", "no long finality", "gap", "no committed head")] string reason)
    {
        using TempPath dir = TempPath.GetTempDirectory();
        using Session session = new(Config(longFinality: reason != "no long finality"), dir.Path, new MemDb());
        session.AddChain(Block0, 3);
        if (reason == "gap") session.Add(State(5), State(6));
        StateId head = reason == "gap" ? State(6) : State(3);
        if (reason != "no committed head") session.Repository.SetLastCommittedStateId(head);
        using CancellationTokenSource cancelled = new();
        if (reason == "cancelled") cancelled.Cancel();

        int converted = session.Manager.PersistInMemoryTailForShutdown(cancelled.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(converted, Is.Zero);
            Assert.That(session.Repository.HasBasePersistedSnapshot(State(1)), Is.False);
            Assert.That(session.Barrier.ReceivedCalls(), Is.Empty, "nothing to persist, nothing to flush");
        }
    }

    // The whole shutdown path: blocks committed through the manager, a clean dispose, and a restart on the same disk.
    [Test]
    public async Task A_clean_shutdown_keeps_the_head_state_for_the_next_start([Values] bool longFinality)
    {
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        FlatDbConfig config = Config(longFinality);
        StateId head = State(3);
        using (FlatTestContainer node = new(config, baseDbPath: dir.Path, catalogDb: catalog))
        {
            FlatDbManager manager = (FlatDbManager)node.Resolve<IFlatDbManager>();
            StateId parent = StateId.PreGenesis;
            for (ulong n = 0; n <= head.BlockNumber; n++)
            {
                StateId to = n == 0 ? Block0 : State(n);
                Snapshot snapshot = node.ResourcePool.CreateSnapshot(parent, to, ResourcePool.Usage.MainBlockProcessing);
                snapshot.Content.Accounts[TestItem.AddressA] = new Account(n, 100);
                manager.AddSnapshot(snapshot, node.ResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing));
                parent = to;
            }

            Assert.That(manager.HasStateForBlock(head), Is.True);
            await manager.DisposeAsync();
        }

        // The restarted container loads the persisted tier itself, as the manager would on construction.
        using FlatTestContainer restarted = new(config, baseDbPath: dir.Path, catalogDb: catalog);
        Assert.That(restarted.Repository.HasState(head), Is.EqualTo(longFinality),
            "with long finality the in-memory tail is kept; without it there is no persisted-snapshot tier to keep it in");
    }

    [Test]
    public async Task Rocksdb_restart_resumes_the_durable_prefix_and_can_reorg([Values] bool interruptConversion)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        CatalogFault fault = new();
        Block[] blocks = new Block[4];
        await using (IContainer node = OpenDiskNode(directory.Path, fault))
        {
            Assert.That(node.ResolveKeyed<IDb>(DbNames.PersistedSnapshotCatalog), Is.InstanceOf<DbOnTheRocks>());
            MainProcessingContext processing = node.Resolve<MainProcessingContext>();
            IWorldState state = processing.WorldState;
            using (state.BeginScope(IWorldState.PreGenesis))
            {
                state.CreateAccount(TestItem.AddressA, 1_000_000);
                state.Commit(Berlin.Instance);
                state.RecalculateStateRoot();
                blocks[0] = Build.A.Block.Genesis.WithStateRoot(state.StateRoot).TestObject;
                state.CommitTree(0);
            }
            IBlockTree tree = node.Resolve<IBlockTree>();
            tree.SuggestBlock(blocks[0]);
            Assert.That(tree.TryUpdateMainChain(blocks[0].Header, true, preloadedBlocks: [blocks[0]]), Is.True);
            node.Resolve<IWorldStateManager>().FlushCache(CancellationToken.None);

            for (int i = 1; i < blocks.Length; i++)
                blocks[i] = ExecuteTransfer(node, blocks[i - 1], TestItem.AddressB, 10);

            Assert.That(node.Resolve<IPersistenceManager>().GetCurrentPersistedStateId().BlockNumber, Is.Zero);
            fault.Enabled = interruptConversion;
        }

        Assert.That(fault.Triggered, Is.EqualTo(interruptConversion));
        if (interruptConversion) Assert.That(fault.Attempts, Is.EqualTo(2));
        Block reorgParent;
        Block reorgHead;
        await using (IContainer restarted = OpenDiskNode(directory.Path))
        {
            IBlockTree tree = restarted.Resolve<IBlockTree>();
            IStateReader reader = restarted.Resolve<IStateReader>();
            ulong kept = interruptConversion ? 1UL : 3UL;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tree.Head!.Number, Is.Zero, "RocksDB's base must not move to the unfinalized head");
                Assert.That(KeptStateResumer.Resume(tree, reader, LimboLogs.Instance), Is.EqualTo(kept));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tree.Head!.Hash, Is.EqualTo(blocks[kept].Hash));
                Assert.That(reader.GetBalance(blocks[kept].Header, TestItem.AddressB), Is.EqualTo((UInt256)(kept * 10)));
                Assert.That(reader.HasStateForBlock(blocks[2].Header), Is.EqualTo(!interruptConversion));
                Assert.That(reader.HasStateForBlock(blocks[3].Header), Is.EqualTo(!interruptConversion));
                Assert.That(restarted.Resolve<IPersistenceManager>().GetCurrentPersistedStateId().BlockNumber, Is.Zero);
            }

            using (Assert.EnterMultipleScope())
            {
                for (int i = 2; i < blocks.Length; i++)
                {
                    Assert.That(tree.FindBlock(blocks[i].Hash!, BlockTreeLookupOptions.None)?.Hash, Is.EqualTo(blocks[i].Hash));
                    Assert.That(tree.WasProcessed(blocks[i].Number, blocks[i].Hash!), Is.True);
                }
            }

            for (ulong i = kept + 1; i < (ulong)blocks.Length; i++)
            {
                Block original = tree.FindBlock(blocks[i].Hash!, BlockTreeLookupOptions.None)!;
                Block replayed = Execute(restarted, blocks[i - 1], original);
                Assert.That(replayed.Hash, Is.EqualTo(blocks[i].Hash), "replay must reproduce the original state and block");
            }

            reorgParent = ExecuteTransfer(restarted, blocks[1], TestItem.AddressC, 20);
            reorgHead = ExecuteTransfer(restarted, reorgParent, TestItem.AddressC, 20);
            AssertRecoveredFork(restarted, blocks[1], reorgParent, reorgHead);
        }

        await using IContainer reopened = OpenDiskNode(directory.Path);
        Assert.That(KeptStateResumer.Resume(reopened.Resolve<IBlockTree>(), reopened.Resolve<IStateReader>(), LimboLogs.Instance), Is.EqualTo(3));
        AssertRecoveredFork(reopened, blocks[1], reorgParent, reorgHead);
    }

    // ---- The next start: it loads the tail this shutdown converted and compacts it before the first read ----
    // Blocks go through FlatDbManager.AddSnapshot, DisposeAsync converts the tail, and the next manager's constructor loads
    // and compacts it. RocksDB is a substitute whose state stays at Persisted.

    private const int DefaultCompactSize = 32;
    // A mainnet block writes thousands of keys; 64 slots are enough that a one-line filter cannot hold several blocks.
    private const int SlotsPerBlock = 64;

    // The RocksDB state of the restart tests: on a CompactSize boundary (offset 0 here), as the finalized persist leaves it.
    private static readonly StateId Persisted = State(1024);

    private static StateId TailBlock(int indexInTail) => State(Persisted.BlockNumber + (ulong)indexInTail);

    // The default config keeps 128 to 159 blocks above the RocksDB state, and a clean shutdown converts all of them. 128
    // ends on a boundary; 150 and 159 leave 22 and 31 blocks above the last one.
    [Test]
    public async Task A_restart_compacts_the_reloaded_tail_before_the_first_read([Values(128, 150, 159)] int tailLength)
    {
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        IPersistence persistence = PersistenceAt(Persisted);
        StateId head = TailBlock(tailLength);
        await RunAndShutDown(dir.Path, catalog, persistence, tailLength, WriteBlock);

        // One CompactSize-wide snapshot per chunk, then one narrower one per set bit of the rest: 4, 7 and 9.
        int depth = tailLength / DefaultCompactSize + BitOperations.PopCount((uint)(tailLength % DefaultCompactSize));
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
                Assert.That(rows.Count(static r => r.Tier == SnapshotTier.PersistedCompactSized), Is.EqualTo(tailLength / DefaultCompactSize));
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
                Assert.That(restarted.Repository.TryLeasePersistedState(TailBlock(80), SnapshotTier.PersistedSmallCompacted, out PersistedSnapshot? covered), Is.True);
                using (covered) Assert.That(covered!.Bloom.Capacity, Is.EqualTo(1), "a window under a CompactSize-wide one keeps the runtime filter");
            }
        }
    }

    // At 150 the head reads through the sub-CompactSize windows above the last boundary.
    [Test]
    public async Task Reads_after_a_restart_return_what_each_reloaded_block_wrote([Values(128, 150)] int tailLength)
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

            StateId head = TailBlock(tailLength);
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
            StateId beforeDeletions = TailBlock(50);
            StateId atSelfDestruct = TailBlock(70);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ReadSlot(beforeDeletions, hot, 1), Is.EqualTo((UInt256?)50));
                Assert.That(ReadSlot(beforeDeletions, hot, 2), Is.EqualTo((UInt256?)0x22));
                Assert.That(ReadSlot(beforeDeletions, destructed, 1), Is.EqualTo((UInt256?)0xB1));
                Assert.That(ReadAccount(beforeDeletions, deleted)?.Balance, Is.EqualTo((UInt256)10));
                Assert.That(ReadAccount(atSelfDestruct, destructed), Is.Null);
                Assert.That(ReadSlot(atSelfDestruct, destructed, 2), Is.Null, "cleared in this very block");
                Assert.That(ReadSlot(atSelfDestruct, hot, 2), Is.Null);
                Assert.That(ReadSlot(TailBlock(71), hot, 1), Is.EqualTo((UInt256?)71), "through the base of block 71");
            }

            // Every retained state: a merge over block 40 drops the slots written before the self-destruct, so its filter
            // must not stand in for the snapshots below it, neither at this start nor when the next one rebuilds the filters.
            string[] wrong = [.. Enumerable.Range(1, tailLength).SelectMany(i => Enumerable.Range(0, 3)
                .Where(slot => ReadSlot(TailBlock(i), cleared, slot) != ClearedSlot(i, slot)).Select(slot => $"block {i} slot {slot}"))];
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
    [Test]
    public async Task The_first_persist_after_a_restart_writes_a_whole_chunk([Values(32, 33)] int persistedAtNextStart)
    {
        const int TailLength = 159;
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        IPersistence persistence = PersistenceAt(Persisted);
        await RunAndShutDown(dir.Path, catalog, persistence, TailLength, WriteBlock);

        // Block 32 is finalized and the block after the tail leaves MinReorgDepth above it, so it is persisted.
        IStateHeaderProvider finality = Substitute.For<IStateHeaderProvider>();
        finality.FinalizedBlockNumber.Returns(TailBlock(32).BlockNumber);
        finality.GetFinalizedHeader(TailBlock(32).BlockNumber).Returns(Build.A.BlockHeader.WithNumber(TailBlock(32).BlockNumber)
            .WithStateRoot(TailBlock(32).StateRoot.ToCommitment()).TestObject);
        using (FlatTestContainer restarted = Open(dir.Path, catalog, persistence, finality))
        {
            FlatDbManager manager = (FlatDbManager)restarted.Resolve<IFlatDbManager>();
            AddBlocks(restarted, manager, TailLength + 1, TailLength + 1, WriteBlock);
            await manager.DisposeAsync(); // drains the persistence queue
        }

        (StateId From, StateId To)[] batches = [.. persistence.ReceivedCalls()
            .Where(static call => call.GetMethodInfo().Name == nameof(IPersistence.CreateWriteBatch))
            .Select(static call => ((StateId)call.GetArguments()[0]!, (StateId)call.GetArguments()[1]!))];
        Assert.That(batches, Is.EqualTo(new[] { (Persisted, TailBlock(32)) }), "RocksDB write batches");

        using FlatTestContainer next = Open(dir.Path, catalog, PersistenceAt(TailBlock(persistedAtNextStart)));
        await using FlatDbManager nextManager = (FlatDbManager)next.Resolve<IFlatDbManager>();
        using ReadOnlySnapshotBundle bundle = nextManager.GatherReadOnlySnapshotBundle(TailBlock(TailLength + 1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.SnapshotCount, Is.EqualTo(4), "four CompactSize-wide snapshots above the persisted block");
            Assert.That(bundle.GetAccount(TestItem.AddressA)?.Balance, Is.EqualTo((UInt256)(TailLength + 1)));
        }

        // The pass clamps the windows over the persisted block to it, so the persist drains that chunk through them.
        Assert.That(next.Repository.TryLeasePersistedState(TailBlock(36), SnapshotTier.PersistedSmallCompacted, out PersistedSnapshot? window), Is.True);
        using (window) Assert.That(window!.From, Is.EqualTo(TailBlock(persistedAtNextStart)), "the window ending at block 36 starts at the persisted block");
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

    // Each small window's first page enters the residency tracker when its catalog row is written; the pass then demotes
    // the windows under a boundary, as at runtime, and keeps the pages of those above the last boundary. The covered window
    // is checked when the CompactSize-wide row is written: the merge that reads it drops its pages once it ends.
    [Test]
    public void A_start_keeps_the_pages_of_the_windows_above_the_last_boundary_only()
    {
        ConcurrentDictionary<StateId, SnapshotLocation> published = [];
        bool? coveredKept = null;
        IArenaManager? arena = null;
        using FlatTestContainer tier = SmallTier(arenaPageCacheBytes: 4 * 1024 * 1024, configure: builder =>
            builder.AddDecorator<ISnapshotCatalog>((context, inner) => new ObservedCatalog(inner, after: entry =>
            {
                if (entry.Tier == SnapshotTier.PersistedCompactSized) coveredKept = IsTracked(State(2));
                if (entry.Tier != SnapshotTier.PersistedSmallCompacted) return;
                published[entry.To] = entry.Location;
                arena!.PageTracker.TryTouch(entry.Location.ArenaId, FirstPage(entry.Location), out _, out _);
            })));
        arena = tier.Resolve<IArenaManager>();
        ConvertBases(tier, 1, 6);
        tier.Compactor.CompactReloaded(0, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsTracked(State(6)), Is.True, "(4, 6] lies above the last boundary: kept");
            Assert.That(coveredKept, Is.False, "(0, 2] lies under the CompactSize-wide (0, 4]: demoted");
        }

        bool IsTracked(StateId to) => arena!.PageTracker.ContainsPage(published[to].ArenaId, FirstPage(published[to]));
        static uint FirstPage(SnapshotLocation location) => (uint)(location.Offset / Environment.SystemPageSize);
    }

    // The pass stops at the second CompactSize-wide snapshot, whose catalog row fails to be written, or when the process
    // stops after the fifth small one.
    [Test]
    public async Task An_interrupted_start_pass_leaves_a_consistent_catalog_and_the_next_start_finishes([Values] bool processExit)
    {
        const int TailLength = 150;
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        IPersistence persistence = PersistenceAt(Persisted);
        await RunAndShutDown(dir.Path, catalog, persistence, TailLength, WriteBlock);
        using CancellationTokenSource exit = new();
        int published = 0;
        ObservedCatalog Interrupt(ISnapshotCatalog inner) => processExit
            ? new(inner, after: entry => { if (entry.Tier == SnapshotTier.PersistedSmallCompacted && Interlocked.Increment(ref published) == 5) exit.Cancel(); })
            : new(inner, before: entry => { if (entry.Tier == SnapshotTier.PersistedCompactSized && Interlocked.Increment(ref published) == 2) throw new IOException("injected"); });

        for (int start = 0; start < 2; start++)
        {
            using FlatTestContainer restarted = Open(dir.Path, catalog, persistence, configure: start == 0
                ? builder => builder.AddSingleton<IProcessExitSource>(new ProcessExitSource(exit.Token)).AddDecorator<ISnapshotCatalog>((_, inner) => Interrupt(inner))
                : null);
            await using FlatDbManager manager = (FlatDbManager)restarted.Resolve<IFlatDbManager>();
            CatalogEntry[] rows = [.. new SnapshotCatalog(catalog).Load()];
            using ReadOnlySnapshotBundle head = manager.GatherReadOnlySnapshotBundle(TailBlock(TailLength));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(published, Is.GreaterThanOrEqualTo(processExit ? 5 : 2), "the pass was interrupted");
                Assert.That(rows.Where(row => !IsLive(restarted, row)), Is.Empty, "a catalog row points at a released snapshot");
                Assert.That(rows, Has.Length.EqualTo(restarted.Repository.PersistedSnapshotCount), "a catalog row for every snapshot");
                Assert.That(Enumerable.Range(1, TailLength).Where(i => ReadBalance(manager, TailBlock(i)) != (UInt256)i), Is.Empty, "states read wrong");
                if (start == 0) Assert.That(head.SnapshotCount, Is.GreaterThan(7), "the interrupted start left work");
                else Assert.That(head.SnapshotCount, Is.EqualTo(7), "the next start finishes it");
            }
        }

        static bool IsLive(FlatTestContainer node, CatalogEntry row)
        {
            if (!node.Repository.TryLeasePersistedState(row.To, row.Tier, out PersistedSnapshot? snapshot)) return false;
            using (snapshot) return snapshot.From == row.From;
        }

        static UInt256? ReadBalance(FlatDbManager manager, StateId at)
        {
            using ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(at);
            return bundle.GetAccount(TestItem.AddressA)?.Balance;
        }
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
        Assert.That(node.Repository.HasBasePersistedSnapshot(TailBlock(1)) && node.Repository.HasBasePersistedSnapshot(TailBlock(tailLength)), Is.True,
            "the shutdown converts the whole tail");
    }

    private static void AddBlocks(FlatTestContainer node, FlatDbManager manager, int first, int last, Action<int, SnapshotContent> write)
    {
        for (int i = first; i <= last; i++)
        {
            Snapshot snapshot = node.ResourcePool.CreateSnapshot(TailBlock(i - 1), TailBlock(i), ResourcePool.Usage.MainBlockProcessing);
            write(i, snapshot.Content);
            manager.AddSnapshot(snapshot, node.ResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing));
        }
    }

    private static FlatTestContainer SmallTier(long arenaPageCacheBytes = 0, Action<ContainerBuilder>? configure = null) =>
        new(new FlatDbConfig { CompactSize = 4, MaxInMemoryBaseSnapshotCount = 4, CompactionOffset = 0 }, arenaFileSizeBytes: 1024 * 1024,
            arenaPageCacheBytes: arenaPageCacheBytes, configure: configure);

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
        IPersistedSnapshotCompactor? compactor = null, Action<ContainerBuilder>? configure = null) =>
        new(new FlatDbConfig { CompactionOffset = 0, InlineCompaction = true }, arenaFileSizeBytes: 1024 * 1024, baseDbPath: path,
            catalogDb: catalog, finalizedStateProvider: finality, loadPersistedTier: false, configure: builder =>
            {
                builder.AddSingleton(persistence);
                if (compactor is not null) builder.AddSingleton(compactor);
                configure?.Invoke(builder);
            });

    /// <summary>A catalog that calls back around every row it writes.</summary>
    private sealed class ObservedCatalog(ISnapshotCatalog inner, Action<CatalogEntry>? before = null, Action<CatalogEntry>? after = null) : ISnapshotCatalog
    {
        public void Add(CatalogEntry entry)
        {
            before?.Invoke(entry);
            inner.Add(entry);
            after?.Invoke(entry);
        }

        public bool Remove(in StateId to, SnapshotTier tier) => inner.Remove(to, tier);
        public IEnumerable<CatalogEntry> Load() => inner.Load();
    }

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

    private static IContainer OpenDiskNode(string path, CatalogFault? fault = null)
    {
        FlatDbConfig flat = Config();
        flat.Enabled = true;
        flat.TrieWarmerWorkerCount = 0;
        flat.ArenaFileSizeBytes = 1024 * 1024;
        flat.PersistedSnapshotArenaPageCacheBytes = 0;
        ConfigProvider config = new(flat, new InitConfig { BaseDbPath = path });
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new NethermindModule(new ChainSpec { Parameters = new ChainParameters(), Allocations = [] }, config, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, null))
            .AddModule(new TestBlockProcessingModule())
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<IJsonSerializer, EthereumJsonSerializer>()
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance));
        if (fault is not null)
            builder.AddDecorator<ISnapshotCatalog>((_, catalog) => new InterruptingCatalog(catalog, fault));
        return builder.Build();
    }

    private static Block ExecuteTransfer(IContainer node, Block parent, Address recipient, ulong value)
    {
        Transaction transaction = Build.A.Transaction.WithNonce(parent.Number).WithTo(recipient)
            .WithValue(value).WithGasPrice(1).WithGasLimit(21_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block suggested = Build.A.Block.WithParent(parent).WithTimestamp(parent.Timestamp + 12)
            .WithTransactions(transaction).TestObject;
        return Execute(node, parent, suggested);
    }

    private static Block Execute(IContainer node, Block parent, Block suggested)
    {
        Block processed = node.Resolve<MainProcessingContext>().BranchProcessor.Process(
            parent.Header, [suggested], ProcessingOptions.NoValidation, NullBlockTracer.Instance)[0];
        processed.Header.Hash = processed.Header.CalculateHash();
        IBlockTree tree = node.Resolve<IBlockTree>();
        tree.SuggestBlock(processed);
        Assert.That(tree.TryUpdateMainChain(processed.Header, true, forceUpdateHeadBlock: true, preloadedBlocks: [processed]), Is.True);
        return processed;
    }

    private static void AssertRecoveredFork(IContainer node, Block forkParent, Block fork, Block head)
    {
        IStateReader reader = node.Resolve<IStateReader>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Resolve<IBlockTree>().Head!.Hash, Is.EqualTo(head.Hash));
            Assert.That(node.Resolve<IBlockTree>().FindHeader(fork.Number, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(fork.Hash));
            Assert.That(reader.GetBalance(head.Header, TestItem.AddressB), Is.EqualTo((UInt256)10));
            Assert.That(reader.GetBalance(head.Header, TestItem.AddressC), Is.EqualTo((UInt256)40));
            Assert.That(reader.GetBalance(forkParent.Header, TestItem.AddressB), Is.EqualTo((UInt256)10));
            Assert.That(node.Resolve<IPersistenceManager>().GetCurrentPersistedStateId().BlockNumber, Is.Zero);
        }
    }

    private sealed class CatalogFault
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; set; }
        public int Attempts { get; set; }
    }

    private sealed class InterruptingCatalog(ISnapshotCatalog inner, CatalogFault fault) : ISnapshotCatalog
    {
        public void Add(CatalogEntry entry)
        {
            // Fail after the second snapshot's files are fsynced, before publishing its catalog row.
            if (fault.Enabled && ++fault.Attempts == 2)
            {
                fault.Triggered = true;
                throw new IOException("Injected catalog publication failure");
            }
            inner.Add(entry);
        }

        public bool Remove(in StateId to, SnapshotTier tier) => inner.Remove(to, tier);
        public IEnumerable<CatalogEntry> Load() => inner.Load();
    }

    /// <summary>A persistence manager over a real persisted tier whose RocksDB state is at <see cref="Block0"/>.</summary>
    private sealed class Session : IDisposable
    {
        private readonly FlatTestContainer _tier;
        private readonly IResourcePool _pool;
        public IPersistenceManager Manager { get; }
        public SnapshotRepository Repository => _tier.Repository;
        public IPersistedSnapshotLoader Loader => _tier.Loader;
        public IStatePersistenceBarrier Barrier { get; } = Substitute.For<IStatePersistenceBarrier>();

        public Session(FlatDbConfig config, string path, IDb catalog)
        {
            IPersistence persistence = Substitute.For<IPersistence>();
            IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
            reader.CurrentState.Returns(Block0);
            persistence.CreateReader().Returns(reader);
            _tier = new FlatTestContainer(config, baseDbPath: path, catalogDb: catalog, configure: builder => builder
                .AddSingleton(persistence)
                .AddSingleton(Barrier)
                .AddSingleton(Substitute.For<IPersistedSnapshotCompactor>()));
            _pool = _tier.Resolve<IResourcePool>();
            Manager = _tier.Resolve<IPersistenceManager>();
        }

        public void AddChain(StateId from, ulong count)
        {
            ulong last = from.BlockNumber + count;
            for (ulong n = from.BlockNumber + 1; n <= last; n++)
            {
                StateId to = State(n);
                Add(from, to);
                from = to;
            }
        }

        public void Add(StateId from, StateId to)
        {
            Snapshot snapshot = _pool.CreateSnapshot(from, to, ResourcePool.Usage.MainBlockProcessing);
            snapshot.Content.Accounts[TestItem.AddressA] = new Account(to.BlockNumber, 100);
            Repository.TryAdd(snapshot, SnapshotTier.InMemoryBase);
            Repository.AddStateId(to);
        }

        public Snapshot? Lease(StateId state) => Repository.TryLeaseInMemoryState(state, SnapshotTier.InMemoryBase, out Snapshot? snapshot) ? snapshot : null;

        public void Dispose() => _tier.Dispose();
    }
}
