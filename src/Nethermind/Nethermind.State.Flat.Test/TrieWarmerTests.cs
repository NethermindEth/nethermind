// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.ScopeProvider;
using NSubstitute;
using NSubstitute.Exceptions;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
// Scheduling-sensitive: worker fan-out/boost assertions starve under a thread pool shared with
// other fixtures' blocking tests.
[NonParallelizable]
public class TrieWarmerTests
{
    private static readonly TestCaseData[] SlotJobCases =
    [
        new TestCaseData(SlotPushMode.Spmc).SetName("PushSlotJob_CallsWarmUpStorageTrie"),
        new TestCaseData(SlotPushMode.Mpmc).SetName("PushSlotJobMpmc_CallsWarmUpStorageTrie")
    ];

    private ILogManager _logManager = null!;
    private FlatDbConfig _config = null!;

    [SetUp]
    public void SetUp()
    {
        _logManager = LimboLogs.Instance;
        _config = new FlatDbConfig { TrieWarmerWorkerCount = 2 };
    }


    [Test]
    public async Task PushAddressJob_CallsWarmUpStateTrie()
    {
        TrieWarmer warmer = new(_logManager, _config);

        ITrieWarmer.IAddressWarmer addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        Address address = new("0x1234567890123456789012345678901234567890");

        warmer.PushAddressJob(addressWarmer, address, sequenceId: 1);

        await Eventually.AssertAsync<ReceivedCallsException>(() => addressWarmer.Received().WarmUpStateTrie(address, 1));

        await warmer.DisposeAsync();
    }

    [TestCaseSource(nameof(SlotJobCases))]
    public async Task PushSlotJob_CallsWarmUpStorageTrie(SlotPushMode slotPushMode)
    {
        TrieWarmer warmer = new(_logManager, _config);

        ITrieWarmer.IStorageWarmer storageWarmer = Substitute.For<ITrieWarmer.IStorageWarmer>();
        UInt256 index = 42;

        bool enqueued = slotPushMode == SlotPushMode.Spmc
            ? warmer.PushSlotJob(storageWarmer, index, sequenceId: 5)
            : warmer.PushSlotJobMpmc(storageWarmer, index, sequenceId: 5);

        Assert.That(enqueued, Is.True);
        await Eventually.AssertAsync<ReceivedCallsException>(() => storageWarmer.Received().WarmUpStorageTrie(index, 5));

        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushAddressJob_PassesCorrectSequenceId()
    {
        TrieWarmer warmer = new(_logManager, _config);

        ITrieWarmer.IAddressWarmer addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        Address address = new("0x1111111111111111111111111111111111111111");

        warmer.PushAddressJob(addressWarmer, address, sequenceId: 999);

        await Eventually.AssertAsync<ReceivedCallsException>(() => addressWarmer.Received().WarmUpStateTrie(address, 999));

        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushAddressJob_AfterProcessorIdle_ProcessesNextJob()
    {
        TrieWarmer warmer = new(_logManager, _config);
        CountingAddressWarmer addressWarmer = new();
        Address address = new("0x2222222222222222222222222222222222222222");

        Assert.That(warmer.PushAddressJob(addressWarmer, address, sequenceId: 1), Is.True);
        await WaitForConditionAsync(() => addressWarmer.Calls == 1, "addressWarmer.Calls should be 1");

        await Task.Delay(50);

        Assert.That(warmer.PushAddressJob(addressWarmer, address, sequenceId: 2), Is.True);
        await WaitForConditionAsync(() => addressWarmer.Calls == 2, "addressWarmer.Calls should be 2");

        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushSlotJobMpmc_Bursts_FanOutWithoutExceedingWorkerCount()
    {
        const int WorkerCount = 4;
        const int JobCount = 16;

        _config.TrieWarmerWorkerCount = WorkerCount;
        TrieWarmer warmer = new(_logManager, _config);
        using BlockingStorageWarmer storageWarmer = new(parallelismTarget: 2);

        try
        {
            for (int i = 0; i < JobCount; i++)
            {
                UInt256 index = (uint)i;
                Assert.That(warmer.PushSlotJobMpmc(storageWarmer, index, sequenceId: i), Is.True);
            }

            Assert.That(storageWarmer.WaitForParallelism(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(storageWarmer.MaxConcurrency, Is.GreaterThan(1));
            Assert.That(storageWarmer.MaxConcurrency, Is.LessThanOrEqualTo(WorkerCount));

            storageWarmer.Release();
            await WaitForConditionAsync(() => storageWarmer.Calls == JobCount, $"storageWarmer.Calls should be {JobCount}");
        }
        finally
        {
            storageWarmer.Release();
            await warmer.DisposeAsync();
        }
    }

    [Test]
    public async Task PushSlotJobMpmc_DeepBacklog_BoostsWorkersBeyondConfiguredCount()
    {
        if (Environment.ProcessorCount - 2 <= 2) Assert.Ignore("Needs more than 4 logical processors to observe the boost");

        const int ConfiguredWorkerCount = 2;
        const int JobCount = 600; // > BoostQueueDepth

        _config.TrieWarmerWorkerCount = ConfiguredWorkerCount;
        TrieWarmer warmer = new(_logManager, _config);
        using BlockingStorageWarmer storageWarmer = new(parallelismTarget: ConfiguredWorkerCount + 1);

        try
        {
            for (int i = 0; i < JobCount; i++)
            {
                UInt256 index = (uint)i;
                Assert.That(warmer.PushSlotJobMpmc(storageWarmer, index, sequenceId: i), Is.True);
            }

            Assert.That(storageWarmer.WaitForParallelism(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(storageWarmer.MaxConcurrency, Is.GreaterThan(ConfiguredWorkerCount));
        }
        finally
        {
            storageWarmer.Release();
            await warmer.DisposeAsync();
        }
    }

    [Test]
    public async Task PushSlotJobMpmc_WithOneBusyProcessor_WakesIdleProcessorForSinglePendingJob()
    {
        _config.TrieWarmerWorkerCount = 2;
        TrieWarmer warmer = new(_logManager, _config);
        using BlockingStorageWarmer storageWarmer = new(parallelismTarget: 2);
        UInt256 firstIndex = 1;
        UInt256 secondIndex = 2;

        try
        {
            Assert.That(warmer.PushSlotJobMpmc(storageWarmer, firstIndex, sequenceId: 1), Is.True);
            Assert.That(storageWarmer.WaitForFirstCall(TimeSpan.FromSeconds(5)), Is.True);

            Assert.That(warmer.PushSlotJobMpmc(storageWarmer, secondIndex, sequenceId: 2), Is.True);
            Assert.That(storageWarmer.WaitForParallelism(TimeSpan.FromSeconds(5)), Is.True);
        }
        finally
        {
            storageWarmer.Release();
            await warmer.DisposeAsync();
        }
    }

    [Test]
    public async Task DisposeAsync_RejectsNewJobs()
    {
        TrieWarmer warmer = new(_logManager, _config);
        await warmer.DisposeAsync();

        ITrieWarmer.IAddressWarmer addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        ITrieWarmer.IStorageWarmer storageWarmer = Substitute.For<ITrieWarmer.IStorageWarmer>();
        Address address = new("0x3333333333333333333333333333333333333333");
        UInt256 index = 44;

        Assert.That(warmer.PushAddressJob(addressWarmer, address, sequenceId: 1), Is.False);
        Assert.That(warmer.PushSlotJob(storageWarmer, index, sequenceId: 2), Is.False);
        Assert.That(warmer.PushSlotJobMpmc(storageWarmer, index, sequenceId: 3), Is.False);

        addressWarmer.DidNotReceive().WarmUpStateTrie(Arg.Any<Address>(), Arg.Any<int>());
        storageWarmer.DidNotReceive().WarmUpStorageTrie(Arg.Any<UInt256>(), Arg.Any<int>());
    }

    [Test]
    public async Task DisposeAsync_DrainsAcceptedJobs()
    {
        TrieWarmer warmer = new(_logManager, _config);
        using BlockingStorageWarmer storageWarmer = new(parallelismTarget: 1);
        UInt256 index = 45;

        Assert.That(warmer.PushSlotJobMpmc(storageWarmer, index, sequenceId: 1), Is.True);

        Task disposeTask = warmer.DisposeAsync().AsTask();

        try
        {
            Assert.That(storageWarmer.WaitForFirstCall(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(disposeTask.IsCompleted, Is.False);
            storageWarmer.Release();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(storageWarmer.Calls, Is.EqualTo(1));
        }
        finally
        {
            storageWarmer.Release();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task PushSlotJobs_SameTarget_AllWarmedExactlyOnceAcrossSingleAndBatchDispatch()
    {
        const int JobCount = 150; // > BatchDrainQueueDepth so the deep-backlog batch path engages

        TrieWarmer warmer = new(_logManager, _config);
        using BlockingStorageWarmer blockingWarmer = new(parallelismTarget: 2);
        BatchCountingStorageWarmer countingWarmer = new();

        try
        {
            // Occupy both base workers so the counting jobs accumulate and drain as one batch pass.
            Assert.That(warmer.PushSlotJobMpmc(blockingWarmer, 1, sequenceId: 1), Is.True);
            Assert.That(warmer.PushSlotJobMpmc(blockingWarmer, 2, sequenceId: 2), Is.True);
            Assert.That(blockingWarmer.WaitForParallelism(TimeSpan.FromSeconds(5)), Is.True);

            for (int i = 0; i < JobCount; i++)
            {
                Assert.That(warmer.PushSlotJobMpmc(countingWarmer, (UInt256)(uint)i, sequenceId: 7), Is.True);
            }

            blockingWarmer.Release();
            await WaitForConditionAsync(() => countingWarmer.TotalWarmedSlots == JobCount, $"all {JobCount} slots should be warmed exactly once");
            Assert.That(countingWarmer.MaxBatchSize, Is.GreaterThan(1), "queued same-target jobs should dispatch as a batch");
        }
        finally
        {
            blockingWarmer.Release();
            await warmer.DisposeAsync();
        }
    }

    private sealed class BatchCountingStorageWarmer : ITrieWarmer.IStorageWarmer
    {
        private int _totalWarmedSlots;
        private int _maxBatchSize;

        public int TotalWarmedSlots => Volatile.Read(ref _totalWarmedSlots);

        public int MaxBatchSize => Volatile.Read(ref _maxBatchSize);

        public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
        {
            Interlocked.Increment(ref _totalWarmedSlots);
            return true;
        }

        public bool WarmUpStorageTrieBatch(ReadOnlySpan<UInt256> indices, int sequenceId)
        {
            Interlocked.Add(ref _totalWarmedSlots, indices.Length);
            int max;
            while (indices.Length > (max = Volatile.Read(ref _maxBatchSize)))
            {
                if (Interlocked.CompareExchange(ref _maxBatchSize, indices.Length, max) == max) break;
            }
            return true;
        }
    }

    private sealed class CountingAddressWarmer : ITrieWarmer.IAddressWarmer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool WarmUpStateTrie(Address address, int sequenceId)
        {
            Interlocked.Increment(ref _calls);
            return true;
        }
    }

    private sealed class BlockingStorageWarmer(int parallelismTarget) : ITrieWarmer.IStorageWarmer, IDisposable
    {
        private readonly ManualResetEventSlim _firstCall = new(initialState: false);
        private readonly ManualResetEventSlim _parallelismReached = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);

        private int _calls;
        private int _currentConcurrency;
        private int _maxConcurrency;

        public int Calls => Volatile.Read(ref _calls);

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public bool WaitForFirstCall(TimeSpan timeout) => _firstCall.Wait(timeout);

        public bool WaitForParallelism(TimeSpan timeout) => _parallelismReached.Wait(timeout);

        public void Release() => _release.Set();

        public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
        {
            int currentConcurrency = Interlocked.Increment(ref _currentConcurrency);
            UpdateMaxConcurrency(currentConcurrency);

            Interlocked.Increment(ref _calls);
            _firstCall.Set();
            if (currentConcurrency >= parallelismTarget)
            {
                _parallelismReached.Set();
            }

            _release.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref _currentConcurrency);
            return true;
        }

        public void Dispose()
        {
            _firstCall.Dispose();
            _parallelismReached.Dispose();
            _release.Dispose();
        }

        private void UpdateMaxConcurrency(int currentConcurrency)
        {
            while (true)
            {
                int currentMax = Volatile.Read(ref _maxConcurrency);
                if (currentConcurrency <= currentMax) return;
                if (Interlocked.CompareExchange(ref _maxConcurrency, currentConcurrency, currentMax) == currentMax) return;
            }
        }
    }

    public enum SlotPushMode
    {
        Spmc,
        Mpmc
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string message, int timeoutMs = 1000)
    {
        int delay = 10;
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(delay);
            elapsed += delay;
        }
        Assert.Fail(message);
    }
}
