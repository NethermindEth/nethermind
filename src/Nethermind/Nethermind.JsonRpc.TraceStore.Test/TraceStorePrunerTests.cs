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
        List<Block> recorded = [.. Enumerable.Range(6, 5).Select(number => tree.FindBlock((ulong)number, BlockTreeLookupOptions.RequireCanonical)!), fork];
        CheckpointObservingDb db = new();
        foreach (Block block in recorded)
            db[block.Hash!.Bytes] = [1];

        using TraceStorePruner pruner = CreatePruner(builder, db, 3);
        if (advanceHead)
        {
            Block next = Build.A.Block.WithParent(tree.Head!).TestObject;
            db[next.Hash!.Bytes] = [1];
            recorded.Add(next);
            AddToMain(tree, next);
        }

        ulong cutoff = advanceHead ? 8UL : 7UL;
        bool drained = await db.WaitForCheckpoint(next => next > cutoff);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint past the window");
            Assert.That(recorded.Where(block => block.Number <= cutoff && db.Get(block.Hash!) is not null).Select(block => block.Number), Is.Empty,
                "levels outside the new retention window, fork included");
            Assert.That(recorded.Where(block => block.Number > cutoff).Select(block => db.Get(block.Hash!)), Is.All.Not.Null);
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
            Assert.That(fork.Where(block => block.Number <= 9 && db.Get(block.Hash!) is not null).Select(block => block.Number), Is.Empty);
            Assert.That(fork.Where(block => block.Number > 9).Select(block => db.Get(block.Hash!)), Is.All.Not.Null);
        }
    }

    [Test]
    public async Task Reorganization_during_a_batch_rechecks_the_replaced_levels()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(11);
        BlockTree tree = builder.TestObject;
        CheckpointObservingDb db = new();
        Block[] original = [.. Enumerable.Range(0, 11).Select(number => tree.FindBlock((ulong)number, BlockTreeLookupOptions.RequireCanonical)!)];
        foreach (Block block in original)
            db[block.Hash!.Bytes] = [1];

        GatedLevelRepository levels = new(builder.ChainLevelInfoRepository);
        using TraceStorePruner pruner = new(tree, levels, db, 3, LimboLogs.Instance);
        await levels.Held.Task.WaitAsync(Timeout);
        // The batch has read levels 0 to 7 of the original chain; the replacing branch is processed before it commits.
        Block[] fork = BuildFork(tree, db, 5, 6);
        AddToMain(tree, fork);
        levels.Release();

        bool drained = await db.WaitForCheckpoint(next => next >= 9);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint past the window");
            Assert.That(fork.Where(block => block.Number <= 8 && db.Get(block.Hash!) is not null).Select(block => block.Number), Is.Empty, "replacing branch");
            Assert.That(fork.Where(block => block.Number > 8).Select(block => db.Get(block.Hash!)), Is.All.Not.Null, "replacing branch");
            Assert.That(original.Where(block => block.Number <= 8 && db.Get(block.Hash!) is not null).Select(block => block.Number), Is.Empty, "replaced chain");
            Assert.That(original.Where(block => block.Number > 8).Select(block => db.Get(block.Hash!)), Is.All.Not.Null, "replaced chain");
        }
    }

    [Test]
    public async Task Storage_failure_stays_in_the_worker_and_the_next_block_retries()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(11);
        BlockTree tree = builder.TestObject;
        CheckpointObservingDb db = new();
        using TraceStorePruner pruner = CreatePruner(builder, db, 3);
        bool started = await db.WaitForCheckpoint(next => next == 8);

        db.FailNextCheckpointWrite();
        Block[] fork = BuildFork(tree, db, 5, 7);
        Assert.That(() => AddToMain(tree, fork), Throws.Nothing, "block processing is not affected by the trace store");
        await db.CheckpointWriteFailed.Task.WaitAsync(Timeout);
        Block next = Build.A.Block.WithParent(fork[^1]).TestObject;
        db[next.Hash!.Bytes] = [1];
        AddToMain(tree, next);

        bool drained = await db.WaitForCheckpoint(checkpoint => checkpoint == 6) && await db.WaitForCheckpoint(checkpoint => checkpoint >= 11);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(started && drained, Is.True, "checkpoint moved back to the fork after the failure, then past the window");
            Assert.That(fork.Where(block => block.Number <= 10 && db.Get(block.Hash!) is not null).Select(block => block.Number), Is.Empty);
            Assert.That(fork.Where(block => block.Number > 10).Append(next).Select(block => db.Get(block.Hash!)), Is.All.Not.Null);
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
            release.Task.Wait(Timeout);
        }

        // Subscribed first, so the publisher has already taken the pruner's handler when it is held here.
        tree.BlockAddedToMain += HoldPublisher;
        using TraceStorePruner pruner = CreatePruner(builder, db, 3);
        bool started = await db.WaitForCheckpoint(next => next == 8);
        Block[] fork = BuildFork(tree, db, 5, 7);
        Task reorganization = Task.Run(() => AddToMain(tree, fork));
        await dispatched.Task.WaitAsync(Timeout);
        await pruner.StopAsync().WaitAsync(Timeout);
        int writes = db.CheckpointWrites;
        release.TrySetResult();
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
        Hash256[] hashes = [.. Enumerable.Range(0, 300).Select(number => builder.TestObject.FindBlock((ulong)number, BlockTreeLookupOptions.RequireCanonical)!.Hash!)];
        CheckpointObservingDb db = new(holdAt: 256);
        foreach (Hash256 hash in hashes)
            db[hash.Bytes] = [1];

        using (TraceStorePruner interrupted = CreatePruner(builder, db, 1))
        {
            await db.Held.Task.WaitAsync(Timeout);
            Task stopping = interrupted.StopAsync();
            Assert.That(stopping.IsCompleted, Is.False, "stopping waits for the batch in progress");
            db.Release();
            await stopping.WaitAsync(Timeout);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Checkpoint, Is.EqualTo(256UL));
            Assert.That(hashes.Take(256).Where(hash => db.Get(hash) is not null), Is.Empty);
            Assert.That(hashes.Skip(256).Select(hash => db.Get(hash)), Is.All.Not.Null);
        }

        // Below the checkpoint, so only a recheck from genesis removes it.
        db[hashes[0].Bytes] = [1];
        if (keptAllInBetween)
            CreatePruner(builder, db, 0).Dispose();

        using TraceStorePruner resumed = CreatePruner(builder, db, 1);
        bool drained = await db.WaitForCheckpoint(next => next >= 299);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True, "checkpoint past the window");
            Assert.That(db.Get(hashes[0]), keptAllInBetween ? Is.Null : Is.Not.Null);
            Assert.That(hashes[1..299].Where(hash => db.Get(hash) is not null), Is.Empty);
            Assert.That(db.Get(hashes[299]), Is.Not.Null);
        }
    }

    private static TraceStorePruner CreatePruner(BlockTreeBuilder builder, IDb db, ulong blocksToKeep) =>
        new(builder.TestObject, builder.ChainLevelInfoRepository, db, blocksToKeep, LimboLogs.Instance);

    private static void AddToMain(BlockTree tree, params Block[] blocks)
    {
        foreach (Block block in blocks)
            tree.SuggestBlock(block);
        tree.TryUpdateMainChain(blocks[^1].Header, true, true, blocks);
    }

    private static Block[] BuildFork(BlockTree tree, IDb db, ulong parentNumber, int length)
    {
        Block[] fork = new Block[length];
        Block parent = tree.FindBlock(parentNumber, BlockTreeLookupOptions.RequireCanonical)!;
        for (int i = 0; i < fork.Length; i++)
        {
            parent = fork[i] = Build.A.Block.WithParent(parent).WithExtraData([1]).TestObject;
            db[parent.Hash!.Bytes] = [1];
        }

        return fork;
    }

    /// <summary>Holds the first level read after it returns, until released; returns copies, as a database read does.</summary>
    private sealed class GatedLevelRepository(IChainLevelInfoRepository inner) : IChainLevelInfoRepository
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers)
        {
            using IOwnedReadOnlyList<ChainLevelInfo?> levels = inner.MultiLoadLevel(blockNumbers);
            ArrayPoolList<ChainLevelInfo?> copies = new(levels.Count, levels.Count);
            for (int i = 0; i < levels.Count; i++)
                copies[i] = levels[i] is { } level ? new ChainLevelInfo(level.HasBlockOnMainChain, [.. level.BlockInfos]) : null;

            if (Held.TrySetResult()) _release.Task.Wait(Timeout);
            return copies;
        }

        public void Delete(ulong number, BatchWrite? batch = null) => inner.Delete(number, batch);
        public void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null) => inner.PersistLevel(number, level, batch);
        public BatchWrite StartBatch() => inner.StartBatch();
        public ChainLevelInfo? LoadLevel(ulong number) => inner.LoadLevel(number);
    }

    /// <summary>Records every checkpoint the pruner writes; can hold the writer of one checkpoint until released.</summary>
    private sealed class CheckpointObservingDb(ulong holdAt = ulong.MaxValue) : MemDb
    {
        private readonly Channel<ulong> _checkpoints = Channel.CreateUnbounded<ulong>();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _failNext;
        private int _writes;

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CheckpointWriteFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ulong? Checkpoint => Get(TraceStorePruner.CheckpointKey) is { } value ? BinaryPrimitives.ReadUInt64BigEndian(value) : null;

        public int CheckpointWrites => Volatile.Read(ref _writes);

        public void Release() => _release.TrySetResult();

        public void FailNextCheckpointWrite() => Volatile.Write(ref _failNext, 1);

        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            bool isCheckpoint = value is not null && key.SequenceEqual(TraceStorePruner.CheckpointKey);
            if (isCheckpoint && Interlocked.Exchange(ref _failNext, 0) == 1)
            {
                CheckpointWriteFailed.TrySetResult();
                throw new IOException("Injected trace store write failure.");
            }

            base.Set(key, value, flags);
            if (!isCheckpoint) return;
            Interlocked.Increment(ref _writes);
            ulong next = BinaryPrimitives.ReadUInt64BigEndian(value!);
            _checkpoints.Writer.TryWrite(next);
            if (next == holdAt && Held.TrySetResult()) _release.Task.Wait(Timeout);
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
