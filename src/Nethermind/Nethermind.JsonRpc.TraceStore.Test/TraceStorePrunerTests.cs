// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Test;

[Parallelizable(ParallelScope.All)]
public class TraceStorePrunerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestCase(3UL, 5)]
    [TestCase(7UL, 1)]
    public async Task prunes_old_blocks(ulong blocksToKeep, int removed)
    {
        static IEnumerable<Hash256> GenerateTraces(MemDb db, BlockTree tree)
        {
            ParityLikeBlockTracer parityTracer = new(ParityTraceTypes.Trace);
            ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
            DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer> dbPersistingTracer =
                new(parityTracer, db, serializer, LimboLogs.Instance);

            Block? current = tree.Head;
            while (current is not null)
            {
                dbPersistingTracer.StartNewBlockTrace(current);
                dbPersistingTracer.StartNewTxTrace(Build.A.Transaction.TestObject);
                dbPersistingTracer.EndTxTrace();
                dbPersistingTracer.EndBlockTrace();
                yield return current.Hash!;
                current = tree.FindParent(current, BlockTreeLookupOptions.None);
            }
        }

        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(5);
        BlockTree blockTree = builder.TestObject;
        CheckpointObservingDb memDb = new();
        List<Hash256> keys = GenerateTraces(memDb, blockTree).ToList();
        Assert.That(keys.Select(k => memDb.Get(k)), Does.Not.Contain((byte[]?)null));
        using TraceStorePruner tracePruner = CreatePruner(builder, memDb, blocksToKeep);
        Block headPlus1 = Build.A.Block.WithParent(blockTree.Head!).TestObject;
        Block headPlus2 = Build.A.Block.WithParent(headPlus1).TestObject;
        AddToMain(blockTree, headPlus1, headPlus2, Build.A.Block.WithParent(headPlus2).TestObject);
        bool drained = await memDb.WaitForCheckpoint(next => next >= 8 - blocksToKeep);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint past the window");
            Assert.That(keys.SkipLast(removed).Select(k => memDb.Get(k)), Does.Not.Contain((byte[]?)null), "within the window");
            Assert.That(keys.TakeLast(removed).Select(k => memDb.Get(k)), Is.All.Null, "at or below head minus the window, including genesis");
        }
    }

    [Test]
    public async Task Lowered_window_removes_preexisting_traces_outside_it([Values] bool advanceHead)
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(11);
        BlockTree tree = builder.TestObject;
        Block fork = Build.A.Block.WithParent(tree.FindBlock(6UL, BlockTreeLookupOptions.RequireCanonical)!).WithExtraData([1]).TestObject;
        tree.SuggestBlock(fork);
        CheckpointObservingDb db = new();
        List<Block> recorded = [.. Seed(db, [.. Canonical(tree, 11).Skip(6), fork])];

        using TraceStorePruner pruner = CreatePruner(builder, db, 3);
        if (advanceHead)
        {
            Block next = Seed(db, Build.A.Block.WithParent(tree.Head!).TestObject)[0];
            recorded.Add(next);
            AddToMain(tree, next);
        }

        ulong cutoff = advanceHead ? 8UL : 7UL;
        bool drained = await db.WaitForCheckpoint(next => next > cutoff);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint past the window");
            AssertWindow(db, recorded, cutoff, "fork included");
        }
    }

    [Test]
    public async Task Store_without_checkpoint_is_checked_by_its_keys_and_reads_no_block_levels()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(600);
        Block[] chain = Canonical(builder.TestObject, 600);
        CheckpointObservingDb db = new();
        Block[] recorded = Seed(db, [chain[5], .. chain[300..]]);
        db[TestItem.KeccakA.Bytes] = [1];
        ControlledLevelRepository levels = new(builder.ChainLevelInfoRepository);

        using TraceStorePruner pruner = new(builder.TestObject, levels, db, 3, LimboLogs.Instance);
        bool drained = await db.WaitForCheckpoint(next => next == 597);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint at the window");
            AssertWindow(db, recorded, 596);
            Assert.That(db.Get(TestItem.KeccakA), Is.Not.Null, "trace of a block the node does not have");
            Assert.That(levels.Reads, Is.Zero, "block levels read for 301 traces under head 599");
        }
    }

    [Test]
    public async Task Empty_store_reads_no_block_levels_when_the_head_jumps_past_the_window()
    {
        Block head = Build.A.Block.WithNumber(1).TestObject;
        TaskCompletionSource belowWindow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockTree tree = Substitute.For<IBlockTree>();
        tree.Head.Returns(_ =>
        {
            Block current = Volatile.Read(ref head);
            if (current.Number == 1) belowWindow.TrySetResult();
            return current;
        });
        ControlledLevelRepository levels = new(new ChainLevelInfoRepository(new MemDb()));
        CheckpointObservingDb db = new();

        using TraceStorePruner pruner = new(tree, levels, db, 3, LimboLogs.Instance);
        await belowWindow.Task.WaitAsync(Timeout);
        // As at the end of a snap sync: the first block added is far above genesis.
        Volatile.Write(ref head, Build.A.Block.WithNumber(100_000).TestObject);
        tree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(head));
        bool started = await db.WaitForCheckpoint(next => next == 99_998);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(started, Is.True, "checkpoint at the window");
            Assert.That(db.CheckpointWrites, Is.EqualTo(1), "no checkpoint while the head is below the window");
            Assert.That(levels.Reads, Is.Zero, "block levels read");
        }
    }

    [Test]
    public async Task Reorganization_deeper_than_window_removes_replaced_levels()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(11);
        BlockTree tree = builder.TestObject;
        CheckpointObservingDb db = new();
        using TraceStorePruner pruner = CreatePruner(builder, db, 3);
        bool started = await db.WaitForCheckpoint(next => next == 8);

        Block[] fork = BuildFork(tree, db, 5, 7);
        AddToMain(tree, fork);
        bool drained = await db.WaitForCheckpoint(next => next == 6) && await db.WaitForCheckpoint(next => next >= 10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(started && drained, Is.True, "checkpoint moved back to the fork, then past the window");
            AssertWindow(db, fork, 9);
        }
    }

    [Test]
    public async Task Reorganization_during_a_batch_rechecks_the_replaced_levels()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(11);
        BlockTree tree = builder.TestObject;
        CheckpointObservingDb db = FromGenesis();
        Block[] original = Seed(db, Canonical(tree, 11));
        ControlledLevelRepository levels = new(builder.ChainLevelInfoRepository, holdFirstRead: true);
        using TraceStorePruner pruner = new(tree, levels, db, 3, LimboLogs.Instance);
        Block[] fork;
        try
        {
            await levels.Held.Task.WaitAsync(Timeout);
            // The batch has read levels 0 to 7 of the original chain; the replacing branch is processed before it commits.
            fork = BuildFork(tree, db, 5, 6);
            AddToMain(tree, fork);
        }
        finally
        {
            levels.Release();
        }

        bool drained = await db.WaitForCheckpoint(next => next >= 9);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint past the window");
            AssertWindow(db, fork, 8, "replacing branch");
            AssertWindow(db, original, 8, "replaced chain");
        }
    }

    [Test]
    public async Task Reorganization_just_before_stop_is_rechecked_after_restart()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(11);
        BlockTree tree = builder.TestObject;
        CheckpointObservingDb db = FromGenesis(holdAt: 8);
        Block[] fork = BuildFork(tree, db, 5, 7);
        using (TraceStorePruner stopped = CreatePruner(builder, db, 3))
        {
            try
            {
                await db.Held.Task.WaitAsync(Timeout);
                // The batch that passed levels 6 and 7 is committing; the worker sees the stop before the replacing branch.
                AddToMain(tree, fork);
                Task stopping = stopped.StopAsync();
                db.Release();
                await stopping.WaitAsync(Timeout);
            }
            finally
            {
                db.Release();
            }
        }

        Assert.That(db.Checkpoint, Is.EqualTo(6UL), "checkpoint moved back to the branch before the worker exits");
        using TraceStorePruner restarted = CreatePruner(builder, db, 3);
        bool drained = await db.WaitForCheckpoint(next => next >= 10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint past the window");
            AssertWindow(db, fork, 9);
        }
    }

    [Test]
    public async Task Unreadable_level_is_checked_by_the_keys_of_the_store()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(12);
        BlockTree tree = builder.TestObject;
        CheckpointObservingDb db = FromGenesis();
        Block[] chain = Seed(db, Canonical(tree, 12));
        using TraceStorePruner pruner = new(tree, new ControlledLevelRepository(builder.ChainLevelInfoRepository, unreadable: 2), db, 3, LimboLogs.Instance);
        bool drained = await db.WaitForCheckpoint(next => next == 9);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint past the window");
            AssertWindow(db, chain, 8, "unreadable level 2 included");
        }
    }

    [Test]
    public async Task Storage_failure_stays_in_the_worker_is_reported_once_and_a_later_block_retries()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(11);
        BlockTree tree = builder.TestObject;
        CheckpointObservingDb db = new();
        TestErrorLogManager logs = new();
        using TraceStorePruner pruner = new(tree, builder.ChainLevelInfoRepository, db, 3, logs);
        bool started = await db.WaitForCheckpoint(next => next == 8);

        db.FailCheckpointWrites(2);
        List<Block> added = [.. BuildFork(tree, db, 5, 7)];
        Assert.That(() => AddToMain(tree, [.. added]), Throws.Nothing, "block processing is not affected by the trace store");
        bool failed = true;
        for (int i = 0; i < 2; i++)
        {
            failed &= await db.CheckpointWriteFailed.WaitAsync(Timeout);
            added.Add(Seed(db, Build.A.Block.WithParent(added[^1]).TestObject)[0]);
            AddToMain(tree, added[^1]);
        }

        bool drained = await db.WaitForCheckpoint(checkpoint => checkpoint == 6) && await db.WaitForCheckpoint(checkpoint => checkpoint >= 12);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(started && failed && drained, Is.True, "checkpoint moved back to the fork after two failures, then past the window");
            AssertWindow(db, added, 11);
            Assert.That(logs.Errors, Has.Count.EqualTo(1), "the second failure in a row is not reported again");
        }
    }

    [Test]
    public async Task Block_event_delivered_after_stop_writes_nothing()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(11);
        BlockTree tree = builder.TestObject;
        CheckpointObservingDb db = new();
        TaskCompletionSource dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void HoldPublisher(object? sender, BlockReplacementEventArgs e)
        {
            dispatched.TrySetResult();
            release.Task.Wait();
        }

        // Subscribed first, so the publisher has already taken the pruner's handler when it is held here.
        tree.BlockAddedToMain += HoldPublisher;
        using TraceStorePruner pruner = CreatePruner(builder, db, 3);
        bool started = await db.WaitForCheckpoint(next => next == 8);
        Task reorganization = Task.Run(() => AddToMain(tree, BuildFork(tree, db, 5, 7)));
        int writes;
        try
        {
            await dispatched.Task.WaitAsync(Timeout);
            await pruner.StopAsync().WaitAsync(Timeout);
            writes = db.CheckpointWrites;
        }
        finally
        {
            release.TrySetResult();
        }

        await reorganization.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(started, Is.True);
            Assert.That(db.CheckpointWrites, Is.EqualTo(writes), "checkpoint writes after stop");
            Assert.That(db.Checkpoint, Is.EqualTo(8UL));
        }
    }

    [Test]
    public async Task Stopping_keeps_progress_and_restart_resumes_from_checkpoint([Values] bool keptAllInBetween)
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(300);
        CheckpointObservingDb db = FromGenesis(holdAt: 256);
        Block[] chain = Seed(db, Canonical(builder.TestObject, 300));

        using (TraceStorePruner interrupted = CreatePruner(builder, db, 1))
        {
            try
            {
                await db.Held.Task.WaitAsync(Timeout);
                Task stopping = interrupted.StopAsync();
                Assert.That(stopping.IsCompleted, Is.False, "stopping waits for the batch in progress");
                db.Release();
                await stopping.WaitAsync(Timeout);
            }
            finally
            {
                db.Release();
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Checkpoint, Is.EqualTo(256UL));
            AssertWindow(db, chain, 255);
        }

        // Below the checkpoint, so only a check of the whole store removes it.
        Seed(db, chain[0]);
        if (keptAllInBetween)
            CreatePruner(builder, db, 0).Dispose();

        using TraceStorePruner resumed = CreatePruner(builder, db, 1);
        bool drained = await db.WaitForCheckpoint(next => next >= 299);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint past the window");
            Assert.That(db.Get(chain[0].Hash!), keptAllInBetween ? Is.Null : Is.Not.Null);
            AssertWindow(db, chain.Skip(1), 298);
        }
    }

    private static TraceStorePruner CreatePruner(BlockTreeBuilder builder, IDb db, ulong blocksToKeep) =>
        new(builder.TestObject, builder.ChainLevelInfoRepository, db, blocksToKeep, LimboLogs.Instance);

    /// <summary>A store whose checkpoint is at genesis, so its traces are found through the block levels and not through its keys.</summary>
    private static CheckpointObservingDb FromGenesis(ulong holdAt = ulong.MaxValue)
    {
        CheckpointObservingDb db = new(holdAt);
        db[TraceStorePruner.CheckpointKey] = new byte[sizeof(ulong)];
        return db;
    }

    private static void AddToMain(BlockTree tree, params Block[] blocks)
    {
        foreach (Block block in blocks)
            tree.SuggestBlock(block);
        tree.TryUpdateMainChain(blocks[^1].Header, true, true, blocks);
    }

    private static Block[] Canonical(BlockTree tree, int count) =>
        [.. Enumerable.Range(0, count).Select(number => tree.FindBlock((ulong)number, BlockTreeLookupOptions.RequireCanonical)!)];

    private static Block[] Seed(IDb db, params Block[] blocks)
    {
        foreach (Block block in blocks)
            db[block.Hash!.Bytes] = [1];
        return blocks;
    }

    private static Block[] BuildFork(BlockTree tree, IDb db, ulong parentNumber, int length)
    {
        Block[] fork = new Block[length];
        Block parent = tree.FindBlock(parentNumber, BlockTreeLookupOptions.RequireCanonical)!;
        for (int i = 0; i < fork.Length; i++)
            parent = fork[i] = Build.A.Block.WithParent(parent).WithExtraData([1]).TestObject;
        return Seed(db, fork);
    }

    /// <summary>Asserts that the traces of the blocks at or below <paramref name="cutoff"/> are removed and the others kept.</summary>
    private static void AssertWindow(IDb db, IEnumerable<Block> blocks, ulong cutoff, string label = "")
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Where(block => block.Number <= cutoff && db.Get(block.Hash!) is not null).Select(block => block.Number), Is.Empty, $"{label} retained at or below {cutoff}");
            Assert.That(blocks.Where(block => block.Number > cutoff && db.Get(block.Hash!) is null).Select(block => block.Number), Is.Empty, $"{label} removed above {cutoff}");
        }
    }

    /// <summary>Returns copies of the levels, as a database read does, and counts them; can hold the first read until released or fail one level.</summary>
    private sealed class ControlledLevelRepository(IChainLevelInfoRepository inner, bool holdFirstRead = false, ulong unreadable = ulong.MaxValue) : IChainLevelInfoRepository
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads => Volatile.Read(ref _reads);

        public void Release() => _release.TrySetResult();

        public IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers)
        {
            Interlocked.Add(ref _reads, blockNumbers.Count);
            for (int i = 0; i < blockNumbers.Count; i++)
            {
                if (blockNumbers[i] == unreadable) throw new InvalidDataException("Injected unreadable block level.");
            }

            using IOwnedReadOnlyList<ChainLevelInfo?> levels = inner.MultiLoadLevel(blockNumbers);
            ArrayPoolList<ChainLevelInfo?> copies = new(levels.Count, levels.Count);
            for (int i = 0; i < levels.Count; i++)
                copies[i] = Copy(levels[i]);

            if (holdFirstRead && Held.TrySetResult()) _release.Task.Wait();
            return copies;
        }

        public ChainLevelInfo? LoadLevel(ulong number) =>
            number == unreadable ? throw new InvalidDataException("Injected unreadable block level.") : Copy(inner.LoadLevel(number));

        public void Delete(ulong number, BatchWrite? batch = null) => inner.Delete(number, batch);
        public void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null) => inner.PersistLevel(number, level, batch);
        public BatchWrite StartBatch() => inner.StartBatch();

        private static ChainLevelInfo? Copy(ChainLevelInfo? level) => level is null ? null : new ChainLevelInfo(level.HasBlockOnMainChain, [.. level.BlockInfos]);
    }

    /// <summary>Records every checkpoint the pruner writes; can hold the writer of one checkpoint until released.</summary>
    private sealed class CheckpointObservingDb(ulong holdAt = ulong.MaxValue) : MemDb
    {
        private readonly Channel<ulong> _checkpoints = Channel.CreateUnbounded<ulong>();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _failures;
        private int _writes;

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SemaphoreSlim CheckpointWriteFailed { get; } = new(0);

        public ulong? Checkpoint => Get(TraceStorePruner.CheckpointKey) is { } value ? BinaryPrimitives.ReadUInt64BigEndian(value) : null;

        public int CheckpointWrites => Volatile.Read(ref _writes);

        public void Release() => _release.TrySetResult();

        public void FailCheckpointWrites(int count) => Volatile.Write(ref _failures, count);

        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            bool isCheckpoint = value is not null && key.SequenceEqual(TraceStorePruner.CheckpointKey);
            if (isCheckpoint && Volatile.Read(ref _failures) > 0)
            {
                Interlocked.Decrement(ref _failures);
                CheckpointWriteFailed.Release();
                throw new IOException("Injected trace store write failure.");
            }

            base.Set(key, value, flags);
            if (!isCheckpoint) return;
            Interlocked.Increment(ref _writes);
            ulong next = BinaryPrimitives.ReadUInt64BigEndian(value!);
            _checkpoints.Writer.TryWrite(next);
            if (next == holdAt && Held.TrySetResult()) _release.Task.Wait();
        }

        /// <summary>Waits for the first checkpoint, in write order after those already consumed, that satisfies <paramref name="reached"/>.</summary>
        public async Task<bool> WaitForCheckpoint(Func<ulong, bool> reached)
        {
            using CancellationTokenSource timeout = new(Timeout);
            try
            {
                while (!reached(await _checkpoints.Reader.ReadAsync(timeout.Token))) { }
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
