// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Find;
using Nethermind.History;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class LogIndexBuilderTests
{
    private class TestLogIndexStorage : ILogIndexStorage
    {
        private readonly Lock _gate = new();
        private int? _minBlockNumber;
        private int? _maxBlockNumber;

        public bool Enabled => true;

        public event EventHandler<int>? NewMaxBlockNumber;
        public event EventHandler<int>? NewMinBlockNumber;

        public int? MinBlockNumber
        {
            get { lock (_gate) return _minBlockNumber; }
            init => _minBlockNumber = value;
        }

        public int? MaxBlockNumber
        {
            get { lock (_gate) return _maxBlockNumber; }
            init => _maxBlockNumber = value;
        }

        public IEnumerator<int> GetEnumerator(Address address, int from, int to) =>
            throw new NotImplementedException();

        public IEnumerator<int> GetEnumerator(int topicIndex, Hash256 topic, int from, int to) =>
            throw new NotImplementedException();

        public string GetDbSize() => 0L.SizeToString();

        public virtual LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null) =>
            new(batch);

        public virtual Task AddReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats? stats = null)
        {
            int min = Math.Min(aggregate.FirstBlockNum, aggregate.LastBlockNum);
            int max = Math.Max(aggregate.FirstBlockNum, aggregate.LastBlockNum);

            bool fireMin = false;
            bool fireMax = false;
            lock (_gate)
            {
                if (_minBlockNumber is null || min < _minBlockNumber)
                {
                    if (_minBlockNumber is not null && max != _minBlockNumber - 1)
                        throw new InvalidOperationException("Invalid receipts order.");

                    _minBlockNumber = min;
                    fireMin = true;
                }

                if (_maxBlockNumber is null || max > _maxBlockNumber)
                {
                    if (_maxBlockNumber is not null && min != _maxBlockNumber + 1)
                        throw new InvalidOperationException("Invalid receipts order.");

                    _maxBlockNumber = max;
                    fireMax = true;
                }
            }

            // Fire events outside the lock to avoid running subscriber code
            // under the gate (subscribers in tests can re-enter via Wait
            // helpers that subscribe/unsubscribe).
            if (fireMin) NewMinBlockNumber?.Invoke(this, min);
            if (fireMax) NewMaxBlockNumber?.Invoke(this, max);

            return Task.CompletedTask;
        }

        public Task RemoveReorgedAsync(BlockReceipts block) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class RecordingLogIndexStorage : TestLogIndexStorage
    {
        private readonly ConcurrentDictionary<int, int> _receiptCounts = new();

        public int ReceiptCountAt(int blockNumber) => _receiptCounts.TryGetValue(blockNumber, out int count) ? count : -1;

        public override LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null)
        {
            foreach (BlockReceipts blockReceipts in batch)
                _receiptCounts[blockReceipts.BlockNumber] = blockReceipts.Receipts.Length;
            return base.Aggregate(batch, isBackwardSync, stats);
        }
    }

    private class FailingLogIndexStorage(int failAfter, Exception exception) : TestLogIndexStorage
    {
        private int _callCount;

        public override Task AddReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats? stats = null) => Interlocked.Increment(ref _callCount) <= failAfter
                ? base.AddReceiptsAsync(aggregate, stats)
                : throw exception;
    }

    private const int MaxReorgDepth = 8;
    private const int MaxBlock = 100;
    private const int MaxSyncBlock = MaxBlock - MaxReorgDepth;
    private const int BatchSize = 10;

    private ILogIndexConfig _config = null!;
    private IBlockTree _blockTree = null!;
    private ISyncConfig _syncConfig = null!;
    private IReceiptStorage _receiptStorage = null!;
    private ILogManager _logManager = null!;
    private List<object> _testDisposables = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new LogIndexConfig { Enabled = true, MaxReorgDepth = MaxReorgDepth, MaxBatchSize = BatchSize };
        _blockTree = Build.A.BlockTree().OfChainLength(MaxBlock + 1).BlockTree;
        _syncConfig = new SyncConfig { FastSync = true, SnapSync = true };
        _receiptStorage = Substitute.For<IReceiptStorage>();
        _logManager = new TestLogManager();
        _testDisposables = [];

        Block head = _blockTree.Head!;
        _blockTree.SyncPivot = (head.Number, head.Hash);
        _syncConfig.PivotNumber = _blockTree.SyncPivot.BlockNumber;

        _receiptStorage
            .Get(Arg.Any<Block>())
            .Returns(c => []);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        foreach (object disposable in _testDisposables)
        {
            if (disposable is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (disposable is IDisposable disposable1)
                disposable1.Dispose();
        }
    }

    private LogIndexBuilder GetService(ILogIndexStorage logIndexStorage, IBlockTree? blockTree = null, IFlatDbConfig? flatDbConfig = null, IPrunedLogsRetention? prunedLogsRetention = null, ISyncPointers? syncPointers = null, int stallTicksBeforeGivingUp = 1440) => new LogIndexBuilder(
            logIndexStorage, _config, blockTree ?? _blockTree, _syncConfig, _receiptStorage, _logManager, flatDbConfig, prunedLogsRetention, syncPointers
        )
    { StallTicksBeforeGivingUp = stallTicksBeforeGivingUp }.AddTo(_testDisposables);

    [TestCase(0UL, 1440)]
    [TestCase(8UL, 1842)]
    [TestCase(40UL, 9216)]
    public void Stall_deadline_scales_with_the_pruning_interval(ulong pruningInterval, int expectedTicks)
    {
        LogIndexBuilder builder = new(
            Substitute.For<ILogIndexStorage>(), _config, _blockTree, _syncConfig, _receiptStorage, _logManager,
            historyConfig: new HistoryConfig { PruningInterval = pruningInterval });
        builder.AddTo(_testDisposables);

        Assert.That(builder.StallTicksBeforeGivingUp, Is.EqualTo(expectedTicks));
    }

    private static ISyncPointers DownloadedToTheBarrier()
    {
        ISyncPointers pointers = Substitute.For<ISyncPointers>();
        pointers.LowestInsertedBodyNumber.Returns(1UL);
        return pointers;
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Should_SyncToBarrier(
        [Values(1UL, 10UL)] ulong minBarrier,
        [Values(1, 16, MaxBlock)] int batchSize,
        [Values(
            new[] { -1, -1 }, // -1 is treated as null
            new[] { 0, MaxSyncBlock / 2 },
            new[] { MaxSyncBlock / 2, MaxSyncBlock / 2 },
            new[] { MaxSyncBlock / 2, MaxSyncBlock },
            new[] { 5, MaxSyncBlock - 5 }
        )]
        int[] synced,
        CancellationToken cancellation
    )
    {
        _config.MaxBatchSize = batchSize;
        _syncConfig.AncientReceiptsBarrier = minBarrier;
        Assert.That(_syncConfig.AncientReceiptsBarrierCalc, Is.EqualTo(minBarrier));

        int expectedMin = minBarrier <= 1 ? 0 : synced[0] < 0 ? (int)minBarrier : Math.Min(synced[0], (int)minBarrier);
        TestLogIndexStorage storage = new()
        {
            MinBlockNumber = synced[0] < 0 ? null : synced[0],
            MaxBlockNumber = synced[1] < 0 ? null : synced[1]
        };

        LogIndexBuilder builder = GetService(storage);

        Task completion = WaitBlocksAsync(storage, expectedMin, MaxSyncBlock, cancellation);
        await builder.StartAsync();
        await completion;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.LastError, Is.Null);

            Assert.That(storage.MinBlockNumber, Is.EqualTo(expectedMin));
            Assert.That(storage.MaxBlockNumber, Is.EqualTo(MaxSyncBlock));
        }
    }

    [TestCase(-1, TestName = "Should_ForwardError_FromQueueingLoop")]
    [TestCase(0, TestName = "Should_ForwardError_FromStorage_Immediately")]
    [TestCase(1, TestName = "Should_ForwardError_FromStorage_AfterOneBatch")]
    [TestCase(4, TestName = "Should_ForwardError_FromStorage_AfterFourBatches")]
    [CancelAfter(60_000)]
    public async Task Should_ForwardErrorAndStopWithoutDeadlock(int failAfter)
    {
        Exception exception = new(nameof(Should_ForwardErrorAndStopWithoutDeadlock));

        LogIndexBuilder builder = failAfter < 0
            ? GetService(new TestLogIndexStorage(), CreateFailingBlockTree(exception))
            : GetService(new FailingLogIndexStorage(failAfter, exception));

        await builder.StartAsync();

        using (Assert.EnterMultipleScope())
        {
            Exception thrown = Assert.ThrowsAsync<Exception>(() => builder.BackwardSyncCompletion.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.That(thrown, Is.EqualTo(exception));
            Assert.That(builder.LastError, Is.EqualTo(exception));
        }

        await builder.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    [Sequential]
    public async Task Should_CompleteImmediately_IfAlreadySynced(
        [Values(1UL, 10UL, 10UL, 10UL)] ulong minBarrier,
        [Values(0, 00, 05, 10)] int minBlock
    )
    {
        Assert.That((ulong)minBlock, Is.LessThanOrEqualTo(minBarrier));

        _syncConfig.AncientReceiptsBarrier = minBarrier;
        LogIndexBuilder builder = GetService(new FailingLogIndexStorage(0, new("Should not set new receipts."))
        {
            MinBlockNumber = minBlock,
            MaxBlockNumber = MaxSyncBlock
        });

        await builder.StartAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.BackwardSyncCompletion.IsCompleted);
            Assert.That(builder.LastError, Is.Null);
            Assert.That(builder.LastUpdate, Is.Null);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Should_StopBackwardSync_AtTheOldestStoredBlock_InsteadOfWaitingForPrunedReceipts(CancellationToken cancellation)
    {
        const int oldestStored = 50;
        _syncConfig.AncientReceiptsBarrier = 1;

        IBlockTree realTree = _blockTree;
        IBlockTree prunedTree = Substitute.For<IBlockTree>();
        prunedTree.SyncPivot.Returns(realTree.SyncPivot);
        prunedTree.BestKnownNumber.Returns(realTree.BestKnownNumber);
        prunedTree.GetLowestBlock().Returns((ulong)oldestStored);
        prunedTree
            .FindBlock(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(ci => ci.ArgAt<ulong>(0) < oldestStored
                ? null
                : realTree.FindBlock(ci.ArgAt<ulong>(0), ci.ArgAt<BlockTreeLookupOptions>(1)));

        TestLogIndexStorage storage = new();
        LogIndexBuilder builder = GetService(storage, prunedTree);

        Task completion = WaitMinBlockAsync(storage, oldestStored, cancellation);
        await builder.StartAsync();
        await completion;
        await builder.BackwardSyncCompletion.WaitAsync(cancellation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.LastError, Is.Null);
            Assert.That(storage.MinBlockNumber, Is.EqualTo(oldestStored),
                "receipts below the oldest stored block are pruned, not late - the backward sync must complete there rather than poll forever");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Should_ContinueBackwardSyncThroughRetainedIslands_BelowTheOldestStoredBlock_WhenSlicesAreConfigured(CancellationToken cancellation)
    {
        const int oldestStored = 50;
        const int islandLow = 20;
        const int islandHigh = 21;
        _syncConfig.AncientReceiptsBarrier = 1;

        IBlockTree realTree = _blockTree;
        IBlockTree prunedTree = Substitute.For<IBlockTree>();
        prunedTree.SyncPivot.Returns(realTree.SyncPivot);
        prunedTree.BestKnownNumber.Returns(realTree.BestKnownNumber);
        prunedTree.GetLowestBlock().Returns((ulong)oldestStored);
        prunedTree
            .FindBlock(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(ci =>
            {
                ulong number = ci.ArgAt<ulong>(0);
                if (number is islandLow or islandHigh)
                    return Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.TestObject).TestObject;

                bool pruned = number < oldestStored;
                return pruned ? null : realTree.FindBlock(number, ci.ArgAt<BlockTreeLookupOptions>(1));
            });
        _receiptStorage
            .Get(Arg.Is<Block>(b => b.Number == islandLow || b.Number == islandHigh))
            .Returns([new TxReceipt()]);

        RecordingLogIndexStorage storage = new();
        LogIndexBuilder builder = GetService(
            storage,
            prunedTree,
            new FlatDbConfig { HistorySliceAddresses = "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2" },
            Substitute.For<IPrunedLogsRetention>(),
            DownloadedToTheBarrier());

        Task completion = WaitMinBlockAsync(storage, 0, cancellation);
        await builder.StartAsync();
        await completion;
        await builder.BackwardSyncCompletion.WaitAsync(cancellation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.LastError, Is.Null);
            Assert.That(storage.MinBlockNumber, Is.EqualTo(0),
                "a sliced node keeps receipt islands below the pruned boundary, so the backward sync must descend past the boundary and index them instead of completing there");
            Assert.That(storage.ReceiptCountAt(islandLow), Is.EqualTo(1),
                "the island's real receipts must reach the index - fabricating an empty entry for a retained height serves a lie at match cost");
            Assert.That(storage.ReceiptCountAt(islandHigh), Is.EqualTo(1));
            Assert.That(storage.ReceiptCountAt(islandLow - 1), Is.Zero);
            Assert.That(storage.ReceiptCountAt(islandHigh + 1), Is.Zero);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Should_WaitInsteadOfCompletingOrFabricating_WhenABelowBoundaryHeightHasABodyButNoReceipts(CancellationToken cancellation)
    {
        const int oldestStored = 50;
        const int stalledHeight = 30;
        _syncConfig.AncientReceiptsBarrier = 1;

        IBlockTree realTree = _blockTree;
        IBlockTree prunedTree = Substitute.For<IBlockTree>();
        prunedTree.SyncPivot.Returns(realTree.SyncPivot);
        prunedTree.BestKnownNumber.Returns(realTree.BestKnownNumber);
        prunedTree.GetLowestBlock().Returns((ulong)oldestStored);
        prunedTree
            .FindBlock(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(ci =>
            {
                ulong number = ci.ArgAt<ulong>(0);
                if (number is stalledHeight)
                    return Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.TestObject).TestObject;

                bool pruned = number < oldestStored;
                return pruned ? null : realTree.FindBlock(number, ci.ArgAt<BlockTreeLookupOptions>(1));
            });

        bool reclaimed = false;
        _receiptStorage
            .Get(Arg.Is<Block>(b => b.Number == stalledHeight))
            .Returns(_ => Volatile.Read(ref reclaimed) ? [new TxReceipt()] : []);

        RecordingLogIndexStorage storage = new();
        LogIndexBuilder builder = GetService(
            storage,
            prunedTree,
            new FlatDbConfig { HistorySliceAddresses = "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2" },
            Substitute.For<IPrunedLogsRetention>(),
            DownloadedToTheBarrier());

        Task stalled = WaitMinBlockAsync(storage, stalledHeight + 1, cancellation);
        await builder.StartAsync();
        await stalled;

        Assert.That(builder.BackwardSyncCompletion.IsCompleted, Is.False,
            "a below-boundary height with a body but no readable receipts is either mid-reclaim or a retained height that lost its data - the descent must wait, not complete and not fabricate an empty entry");

        Volatile.Write(ref reclaimed, true);

        Task completion = WaitMinBlockAsync(storage, 0, cancellation);
        await completion;
        await builder.BackwardSyncCompletion.WaitAsync(cancellation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.LastError, Is.Null);
            Assert.That(storage.MinBlockNumber, Is.EqualTo(0));
            Assert.That(storage.ReceiptCountAt(stalledHeight), Is.EqualTo(1));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Should_StopTheSlicedDescentAtTheLowestDownloadedBody_InsteadOfFabricatingToTheBarrier(CancellationToken cancellation)
    {
        const int oldestStored = 50;
        const int lowestDownloadedBody = 30;
        _syncConfig.AncientReceiptsBarrier = 1;

        IBlockTree realTree = _blockTree;
        IBlockTree prunedTree = Substitute.For<IBlockTree>();
        prunedTree.SyncPivot.Returns(realTree.SyncPivot);
        prunedTree.BestKnownNumber.Returns(realTree.BestKnownNumber);
        prunedTree.GetLowestBlock().Returns((ulong)oldestStored);
        prunedTree
            .FindBlock(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(ci =>
            {
                ulong number = ci.ArgAt<ulong>(0);
                return number < oldestStored ? null : realTree.FindBlock(number, ci.ArgAt<BlockTreeLookupOptions>(1));
            });
        ISyncPointers pointers = Substitute.For<ISyncPointers>();
        pointers.LowestInsertedBodyNumber.Returns((ulong)lowestDownloadedBody);

        RecordingLogIndexStorage storage = new();
        LogIndexBuilder builder = GetService(
            storage,
            prunedTree,
            new FlatDbConfig { HistorySliceAddresses = "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2" },
            Substitute.For<IPrunedLogsRetention>(),
            pointers);

        Task completion = WaitMinBlockAsync(storage, lowestDownloadedBody, cancellation);
        await builder.StartAsync();
        await completion;
        await builder.BackwardSyncCompletion.WaitAsync(cancellation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.LastError, Is.Null);
            Assert.That(storage.MinBlockNumber, Is.EqualTo(lowestDownloadedBody),
                "nothing below the lowest downloaded body can be a retained island, so the descent must stop there instead of fabricating to the barrier");
            Assert.That(storage.ReceiptCountAt(lowestDownloadedBody - 1), Is.EqualTo(-1));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Should_NotCompleteAtTheMovingDownloadFrontier_WhileThePrunerHoldsTheBoundary(CancellationToken cancellation)
    {
        const int firstFrontier = 40;
        const int secondFrontier = 30;
        _syncConfig.AncientReceiptsBarrier = 1;

        int frontier = firstFrontier;
        int publishedBoundary = 1;
        IBlockTree realTree = _blockTree;
        IBlockTree downloadingTree = Substitute.For<IBlockTree>();
        downloadingTree.SyncPivot.Returns(realTree.SyncPivot);
        downloadingTree.BestKnownNumber.Returns(realTree.BestKnownNumber);
        downloadingTree.GetLowestBlock().Returns(_ => (ulong)Volatile.Read(ref publishedBoundary));
        downloadingTree
            .FindBlock(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(ci =>
            {
                ulong number = ci.ArgAt<ulong>(0);
                return number >= (ulong)Volatile.Read(ref frontier)
                    ? realTree.FindBlock(number, ci.ArgAt<BlockTreeLookupOptions>(1))
                    : null;
            });
        ISyncPointers pointers = Substitute.For<ISyncPointers>();
        pointers.LowestInsertedBodyNumber.Returns(_ => (ulong)Volatile.Read(ref frontier));

        RecordingLogIndexStorage storage = new();
        LogIndexBuilder builder = GetService(
            storage,
            downloadingTree,
            new FlatDbConfig { HistorySliceAddresses = "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2" },
            Substitute.For<IPrunedLogsRetention>(),
            pointers);

        Task firstStop = WaitMinBlockAsync(storage, firstFrontier, cancellation);
        await builder.StartAsync();
        await firstStop;

        Assert.That(builder.BackwardSyncCompletion.IsCompleted, Is.False,
            "the body pointer is a moving frontier while the pruner still holds the boundary at its barrier - the descent must wait there, not declare itself complete");

        Volatile.Write(ref frontier, secondFrontier);

        await WaitMinBlockAsync(storage, secondFrontier, cancellation);

        Assert.That(builder.BackwardSyncCompletion.IsCompleted, Is.False);

        Volatile.Write(ref publishedBoundary, secondFrontier);

        await builder.BackwardSyncCompletion.WaitAsync(cancellation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.LastError, Is.Null);
            Assert.That(storage.MinBlockNumber, Is.EqualTo(secondFrontier),
                "once the pruner publishes the boundary at the parked frontier, the risen floor must complete the descent instead of leaving it polling forever");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Should_NotFabricateOverNeverDownloadedHeights_WhenTheBodyPointerWasNeverWritten(CancellationToken cancellation)
    {
        const int oldestStored = 50;
        _syncConfig.AncientReceiptsBarrier = 1;

        IBlockTree realTree = _blockTree;
        IBlockTree prunedTree = Substitute.For<IBlockTree>();
        prunedTree.SyncPivot.Returns(realTree.SyncPivot);
        prunedTree.BestKnownNumber.Returns(realTree.BestKnownNumber);
        prunedTree.GetLowestBlock().Returns((ulong)oldestStored);
        prunedTree
            .FindBlock(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(ci =>
            {
                ulong number = ci.ArgAt<ulong>(0);
                return number < oldestStored ? null : realTree.FindBlock(number, ci.ArgAt<BlockTreeLookupOptions>(1));
            });

        RecordingLogIndexStorage storage = new();
        LogIndexBuilder builder = GetService(
            storage,
            prunedTree,
            new FlatDbConfig { HistorySliceAddresses = "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2" },
            Substitute.For<IPrunedLogsRetention>(),
            Substitute.For<ISyncPointers>());

        Task completion = WaitMinBlockAsync(storage, oldestStored, cancellation);
        await builder.StartAsync();
        await completion;
        await builder.BackwardSyncCompletion.WaitAsync(cancellation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.LastError, Is.Null);
            Assert.That(storage.MinBlockNumber, Is.EqualTo(oldestStored),
                "an unwritten body pointer on a fast-synced node means nothing below the pivot was ever downloaded - heights below the boundary are undownloaded, not reclaimed, and must not be fabricated");
            Assert.That(storage.ReceiptCountAt(oldestStored - 1), Is.EqualTo(-1));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Should_GiveUpTheDescentAlone_WhenABelowBoundaryHeightNeverYieldsItsReceipts(CancellationToken cancellation)
    {
        const int oldestStored = 50;
        const int stalledHeight = 30;
        _syncConfig.AncientReceiptsBarrier = 1;

        IBlockTree realTree = _blockTree;
        IBlockTree prunedTree = Substitute.For<IBlockTree>();
        prunedTree.SyncPivot.Returns(realTree.SyncPivot);
        prunedTree.BestKnownNumber.Returns(realTree.BestKnownNumber);
        prunedTree.GetLowestBlock().Returns((ulong)oldestStored);
        prunedTree
            .FindBlock(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(ci =>
            {
                ulong number = ci.ArgAt<ulong>(0);
                if (number is stalledHeight)
                    return Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.TestObject).TestObject;

                bool pruned = number < oldestStored;
                return pruned ? null : realTree.FindBlock(number, ci.ArgAt<BlockTreeLookupOptions>(1));
            });

        RecordingLogIndexStorage storage = new();
        LogIndexBuilder builder = GetService(
            storage,
            prunedTree,
            new FlatDbConfig { HistorySliceAddresses = "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2" },
            Substitute.For<IPrunedLogsRetention>(),
            DownloadedToTheBarrier(),
            stallTicksBeforeGivingUp: 2);

        await builder.StartAsync();

        Assert.ThrowsAsync<InvalidOperationException>(() => builder.BackwardSyncCompletion.WaitAsync(cancellation),
            "a height that never yields its receipts must end the descent in a hard error, not an invisible infinite poll");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.LastError, Is.InstanceOf<InvalidOperationException>());
            Assert.That(builder.IsRunning, Is.True,
                "the give-up is scoped to the backward direction - the live forward index must keep running");
        }
    }

    // FindBlock must succeed for the single pivot-setup lookup in StartAsync, then throw on
    // the later lookups issued by DoQueueBlocks — that is the self-await deadlock path.
    private IBlockTree CreateFailingBlockTree(Exception exception)
    {
        IBlockTree realTree = _blockTree;
        int findCalls = 0;

        IBlockTree throwingTree = Substitute.For<IBlockTree>();
        throwingTree.SyncPivot.Returns(realTree.SyncPivot);
        throwingTree.BestKnownNumber.Returns(realTree.BestKnownNumber);
        throwingTree
            .FindBlock(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(ci => Interlocked.Increment(ref findCalls) == 1
                ? realTree.FindBlock(ci.ArgAt<ulong>(0), ci.ArgAt<BlockTreeLookupOptions>(1))
                : throw exception);

        return throwingTree;
    }

    private static Task WaitMaxBlockAsync(TestLogIndexStorage storage, int blockNumber, CancellationToken cancellation)
    {
        if (storage.MaxBlockNumber >= blockNumber)
            return Task.CompletedTask;

        return Wait.ForEventCondition<int>(
            cancellation,
            e => storage.NewMaxBlockNumber += e,
            e => storage.NewMaxBlockNumber -= e,
            e => e >= blockNumber
        );
    }

    private static Task WaitMinBlockAsync(TestLogIndexStorage storage, int blockNumber, CancellationToken cancellation)
    {
        if (storage.MinBlockNumber <= blockNumber)
            return Task.CompletedTask;

        return Wait.ForEventCondition<int>(
            cancellation,
            e => storage.NewMinBlockNumber += e,
            e => storage.NewMinBlockNumber -= e,
            e => e <= blockNumber
        );
    }

    private static Task WaitBlocksAsync(TestLogIndexStorage storage, int minBlock, int maxBlock, CancellationToken cancellation) => Task.WhenAll(
        WaitMinBlockAsync(storage, minBlock, cancellation),
        WaitMaxBlockAsync(storage, maxBlock, cancellation)
    );
}
