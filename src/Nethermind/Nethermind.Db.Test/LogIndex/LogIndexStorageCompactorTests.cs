// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Db.LogIndex;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Db.LogIndex.LogIndexStorage;

namespace Nethermind.Db.Test.LogIndex;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class LogIndexStorageCompactorTests
{
    private const int RaceConditionTestRepeat = 3;

    private static ILogIndexStorage MockStorage(int? min = null, int? max = null)
    {
        ILogIndexStorage storage = Substitute.For<ILogIndexStorage>();
        storage.MinBlockNumber.Returns(min);
        storage.MaxBlockNumber.Returns(max);
        return storage;
    }

    private static Compactor CreateCompactor(ILogIndexStorage storage, IDbMeta? db = null, int compactionDistance = 100) =>
        new(storage, db ?? new FakeDb(), LimboLogs.Instance.GetClassLogger(), compactionDistance);

    private static Compactor CreateCompactor(ILogIndexStorage storage, int compactionDistance = 100) =>
        CreateCompactor(storage, db: null, compactionDistance: compactionDistance);

    [TestCase(0, 50, 0, 50, 100, ExpectedResult = false, Description = "No change from baseline")]
    [TestCase(0, 0, 0, 99, 100, ExpectedResult = false, Description = "99 blocks forward, threshold 100")]
    [TestCase(0, 0, 0, 100, 100, ExpectedResult = true, Description = "Exactly at threshold")]
    [TestCase(100, 200, 50, 250, 100, ExpectedResult = true, Description = "Both directions sum to threshold")]
    public async Task<bool> TryEnqueue_Respects_CompactionDistance(
        int initMin, int initMax, int newMin, int newMax, int compactionDistance
    )
    {
        ILogIndexStorage storage = MockStorage(min: initMin, max: initMax);
        using Compactor compactor = CreateCompactor(storage, compactionDistance);

        storage.MinBlockNumber.Returns(newMin);
        storage.MaxBlockNumber.Returns(newMax);

        bool result = compactor.TryEnqueue();

        await compactor.StopAsync();
        return result;
    }

    [Test]
    [Repeat(RaceConditionTestRepeat)]
    public async Task TryEnqueue_During_Compact_Does_Not_Run_Compact_Concurrently()
    {
        const int compactionDistance = 10;
        var compactionDelay = TimeSpan.FromMilliseconds(200);

        ILogIndexStorage storage = MockStorage(min: 0, max: 0);
        FakeDb db = new(compactionDelay);
        using Compactor compactor = CreateCompactor(storage, db, compactionDistance);

        // Trigger first compaction
        storage.MaxBlockNumber.Returns(compactionDistance);
        Assert.That(compactor.TryEnqueue(), Is.True);

        await Task.Delay(compactionDelay / 4);

        // Try to cause a second compaction
        storage.MaxBlockNumber.Returns(storage.MaxBlockNumber + compactionDistance);
        compactor.TryEnqueue();

        await compactor.ForceAsync();
        await compactor.StopAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    [Repeat(RaceConditionTestRepeat)]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
    public async Task ForceAsync_Does_Not_Run_Compact_Concurrently(bool duringCompact)
    {
        const int compactionDistance = 10;
        var compactionDelay = TimeSpan.FromMilliseconds(200);

        ILogIndexStorage storage = MockStorage(min: 0, max: 0);
        FakeDb db = new(compactionDelay);
        using Compactor compactor = CreateCompactor(storage, db, compactionDistance);

        if (duringCompact)
        {
            storage.MaxBlockNumber.Returns(compactionDistance);
            compactor.TryEnqueue();

            await Task.Delay(compactionDelay / 4);
        }

        const int concurrentCalls = 5;
        await Task.WhenAll(Enumerable.Range(0, concurrentCalls).Select(_ => Task.Run(compactor.ForceAsync)).ToArray());

        await compactor.StopAsync();
    }

    [Test]
    public async Task TryEnqueue_Resets_Baseline_After_Enqueue()
    {
        const int compactionDistance = 10;

        ILogIndexStorage storage = MockStorage(min: 0, max: 0);
        using Compactor compactor = CreateCompactor(storage, compactionDistance);

        storage.MaxBlockNumber.Returns(compactionDistance);
        Assert.That(compactor.TryEnqueue(), Is.True);

        await Task.Delay(100);

        storage.MaxBlockNumber.Returns(storage.MaxBlockNumber + compactionDistance / 2);
        Assert.That(compactor.TryEnqueue(), Is.False);

        await Task.Delay(100);

        storage.MaxBlockNumber.Returns(storage.MaxBlockNumber + compactionDistance / 2);
        Assert.That(compactor.TryEnqueue(), Is.True);

        await compactor.StopAsync();
    }

    [Test]
    public async Task TryEnqueue_Returns_False_After_Stop()
    {
        const int compactionDistance = 10;

        ILogIndexStorage storage = MockStorage(min: 0, max: 0);
        using Compactor compactor = CreateCompactor(storage, new NonCompactableDb(), compactionDistance);

        await compactor.StopAsync();

        storage.MaxBlockNumber.Returns(compactionDistance);
        Assert.That(compactor.TryEnqueue(), Is.False);
    }

    // Fails on compaction attempt
    private class NonCompactableDb : IDbMeta
    {
        private class CompactionException : Exception;

        public void Compact() => throw new CompactionException();
        public void Flush(bool onlyWal = false) { }
    }

    // Simulates compaction with Thread.Sleep and fail on concurrent calls
    private class FakeDb(TimeSpan? compactDelay = null) : IDbMeta
    {
        private class ConcurrentCompactionException : Exception;

        private readonly TimeSpan _compactDelay = compactDelay ?? TimeSpan.Zero;

        private int _compacting;

        public void Compact()
        {
            if (Interlocked.CompareExchange(ref _compacting, 1, 0) != 0)
                throw new ConcurrentCompactionException();

            try
            {
                if (_compactDelay > TimeSpan.Zero)
                    Thread.Sleep(_compactDelay);
            }
            finally
            {
                Interlocked.Exchange(ref _compacting, 0);
            }
        }

        public void Flush(bool onlyWal = false) { }
    }
}
