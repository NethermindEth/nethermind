// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Find;
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
        private int? _minBlockNumber;
        private int? _maxBlockNumber;

        public bool Enabled => true;

        public event EventHandler<int>? NewMaxBlockNumber;
        public event EventHandler<int>? NewMinBlockNumber;

        public int? GetMaxBlockNumber() => _maxBlockNumber;
        public int? GetMinBlockNumber() => _minBlockNumber;

        public int? MinBlockNumber
        {
            get => _minBlockNumber;
            init => _minBlockNumber = value;
        }

        public int? MaxBlockNumber
        {
            get => _maxBlockNumber;
            init => _maxBlockNumber = value;
        }

        public List<int> GetBlockNumbersFor(Address address, int from, int to) =>
            throw new NotImplementedException();

        public List<int> GetBlockNumbersFor(int index, Hash256 topic, int from, int to) =>
            throw new NotImplementedException();

        public string GetDbSize() => 0L.SizeToString();

        public LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null) =>
            new(batch);

        public Task SetReceiptsAsync(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null)
        {
            return SetReceiptsAsync(Aggregate(batch, isBackwardSync, stats), stats);
        }

        public virtual Task SetReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats? stats = null)
        {
            var min = Math.Min(aggregate.FirstBlockNum, aggregate.LastBlockNum);
            var max = Math.Max(aggregate.FirstBlockNum, aggregate.LastBlockNum);

            if (_minBlockNumber is null || min < _minBlockNumber)
            {
                if (_minBlockNumber is not null && max != _minBlockNumber - 1)
                    throw new InvalidOperationException("Invalid receipts order.");

                _minBlockNumber = min;
                NewMinBlockNumber?.Invoke(this, min);
            }

            if (_maxBlockNumber is null || max > _maxBlockNumber)
            {
                if (_maxBlockNumber is not null && min != _maxBlockNumber + 1)
                    throw new InvalidOperationException("Invalid receipts order.");

                _maxBlockNumber = max;
                NewMaxBlockNumber?.Invoke(this, max);
            }

            return Task.CompletedTask;
        }

        public Task ReorgFrom(BlockReceipts block) => Task.CompletedTask;

        public Task CompactAsync(bool flush = false, int mergeIterations = 0, LogIndexUpdateStats? stats = null) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class FailingLogIndexStorage(int failAfter, Exception exception) : TestLogIndexStorage
    {
        private int _callCount;

        public override Task SetReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats? stats = null)
        {
            return Interlocked.Increment(ref _callCount) <= failAfter
                ? base.SetReceiptsAsync(aggregate, stats)
                : throw exception;
        }
    }

    private static readonly TimeSpan WaitTime = TimeSpan.FromSeconds(60);

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
        _syncConfig.PivotNumber = _blockTree.SyncPivot.BlockNumber.ToString();

        _receiptStorage
            .Get(Arg.Any<Block>())
            .Returns(c => []);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        foreach (var disposable in _testDisposables)
        {
            if (disposable is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (disposable is IDisposable disposable1)
                disposable1.Dispose();
        }
    }

    private LogIndexBuilder GetService(ILogIndexStorage logIndexStorage)
    {
        return new LogIndexBuilder(
            logIndexStorage, _config, _blockTree, _syncConfig, _receiptStorage, _logManager
        ).AddTo(_testDisposables);
    }

    [Test]
    [Combinatorial]
    public async Task Should_SyncToBarrier(
        [Values(1, 10)] int minBarrier,
        [Values(1, 16, MaxBlock)] int batchSize,
        [Values(
            new[] { -1, -1 },
            new[] { 0, MaxSyncBlock / 2 },
            new[] { MaxSyncBlock / 2, MaxSyncBlock / 2 },
            new[] { MaxSyncBlock / 2, MaxSyncBlock },
            new[] { 5, MaxSyncBlock - 5 }
        )]
        int[] synced
    )
    {
        _config.MaxBatchSize = batchSize;
        _syncConfig.AncientReceiptsBarrier = minBarrier;
        Assert.That(_syncConfig.AncientReceiptsBarrierCalc, Is.EqualTo(minBarrier));

        var expectedMin = minBarrier <= 1 ? 0 : synced[0] < 0 ? minBarrier : Math.Min(synced[0], minBarrier);
        var storage = new TestLogIndexStorage
        {
            MinBlockNumber = synced[0] < 0 ? null : synced[0],
            MaxBlockNumber = synced[1] < 0 ? null : synced[1]
        };

        LogIndexBuilder builder = GetService(storage);

        Task completion = WaitBlocksAsync(storage, expectedMin, MaxSyncBlock);
        await builder.StartAsync();
        await completion;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.LastError, Is.Null);

            Assert.That(storage.GetMinBlockNumber(), Is.EqualTo(expectedMin));
            Assert.That(storage.GetMaxBlockNumber(), Is.EqualTo(MaxSyncBlock));
        }
    }

    [Test]
    public async Task Should_ForwardError(
        [Values(0, 1, 4)] int failAfter
    )
    {
        var exception = new Exception(nameof(Should_ForwardError));
        LogIndexBuilder builder = GetService(new FailingLogIndexStorage(failAfter, exception));

        await builder.StartAsync();

        using (Assert.EnterMultipleScope())
        {
            Exception thrown = Assert.ThrowsAsync<Exception>(() => builder.BackwardSyncCompletion.WaitAsync(TimeSpan.FromSeconds(999)));
            Assert.That(thrown, Is.EqualTo(exception));
            Assert.That(builder.LastError, Is.EqualTo(exception));
        }
    }

    [Test]
    [Sequential]
    public async Task Should_CompleteImmediately_IfAlreadySynced(
        [Values(1, 10, 10, 10)] int minBarrier,
        [Values(0, 00, 05, 10)] int minBlock
    )
    {
        Assert.That(minBlock, Is.LessThanOrEqualTo(minBarrier));

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

    private static Task WaitMaxBlockAsync(TestLogIndexStorage storage, int blockNumber)
    {
        if (storage.GetMaxBlockNumber() >= blockNumber)
            return Task.CompletedTask;

        return Wait.ForEventCondition<int>(
            WaitTime,
            e => storage.NewMaxBlockNumber += e,
            e => storage.NewMaxBlockNumber -= e,
            e => e >= blockNumber
        );
    }

    private static Task WaitMinBlockAsync(TestLogIndexStorage storage, int blockNumber)
    {
        if (storage.GetMinBlockNumber() <= blockNumber)
            return Task.CompletedTask;

        return Wait.ForEventCondition<int>(
            WaitTime,
            e => storage.NewMinBlockNumber += e,
            e => storage.NewMinBlockNumber -= e,
            e => e <= blockNumber
        );
    }

    private static Task WaitBlocksAsync(TestLogIndexStorage storage, int minBlock, int maxBlock) => Task.WhenAll(
        WaitMinBlockAsync(storage, minBlock), WaitMaxBlockAsync(storage, maxBlock)
    );
}
