// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.TxPool.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class RetryCacheTests
{
    public readonly struct ResourceId(int value) : IEquatable<ResourceId>, IHash64bit<ResourceId>
    {
        public readonly int Value = value;
        public bool Equals(ResourceId other) => Value == other.Value;
        public bool Equals(in ResourceId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is ResourceId other && Equals(other);
        public override int GetHashCode() => Value;
        public long GetHashCode64() => Value * unchecked((long)0x9E3779B97F4A7C15UL);
        public static implicit operator ResourceId(int value) => new(value);
    }

    public readonly struct ResourceRequestMessage : INew<ResourceId, ResourceRequestMessage>
    {
        public ResourceId Resource { get; init; }
        public static ResourceRequestMessage New(ResourceId resourceId) => new() { Resource = resourceId };
    }

    public interface ITestHandler : IMessageHandler<ResourceRequestMessage>;

    /// <summary>
    /// A test handler that tracks HandleMessage calls without using NSubstitute.
    /// </summary>
    private class TestHandler : ITestHandler
    {
        private int _handleMessageCallCount;
        private long _lastHandleTimestamp;
        public int HandleMessageCallCount => Volatile.Read(ref _handleMessageCallCount);
        public long LastHandleTimestamp => Volatile.Read(ref _lastHandleTimestamp);
        public bool WasCalled => HandleMessageCallCount > 0;
        public Action<ResourceRequestMessage> OnHandleMessage { get; set; }

        public virtual void HandleMessage(ResourceRequestMessage message)
        {
            Volatile.Write(ref _lastHandleTimestamp, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _handleMessageCallCount);
            OnHandleMessage?.Invoke(message);
        }
    }

    private sealed class BatchTestHandler : TestHandler, IBatchMessageHandler<ResourceRequestMessage, ResourceId>
    {
        private readonly Lock _lock = new();
        private readonly List<int> _batchResourceValues = [];
        private int _handleMessagesCallCount;

        public int HandleMessagesCallCount => Volatile.Read(ref _handleMessagesCallCount);

        public int[] BatchResourceValues
        {
            get
            {
                lock (_lock)
                {
                    return [.. _batchResourceValues];
                }
            }
        }

        public void HandleMessages(ReadOnlySpan<ResourceId> resourceIds)
        {
            Interlocked.Increment(ref _handleMessagesCallCount);
            lock (_lock)
            {
                for (int i = 0; i < resourceIds.Length; i++)
                {
                    _batchResourceValues.Add(resourceIds[i].Value);
                }
            }
        }
    }

    private CancellationTokenSource _cancellationTokenSource;
    private RetryCache<ResourceRequestMessage, ResourceId> _cache;

    // Short cache timeout so retries fire quickly (~600ms); generous assertion timeout for slow CI
    private const int CacheTimeoutMs = 500;
    private const int AssertTimeoutMs = 10_000;

    [SetUp]
    public void Setup()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _cache = new(TestLogManager.Instance, timeoutMs: CacheTimeoutMs, token: _cancellationTokenSource.Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _cancellationTokenSource.CancelAsync();
        await _cache.DisposeAsync();
        _cancellationTokenSource.Dispose();
    }

    [Test]
    public void Announced_SameResourceDifferentNode_ReturnsEnqueued()
    {
        AnnounceResult result1 = _cache.Announced(1, new TestHandler());
        AnnounceResult result2 = _cache.Announced(1, new TestHandler());

        Assert.That(result1, Is.EqualTo(AnnounceResult.RequestRequired));
        Assert.That(result2, Is.EqualTo(AnnounceResult.Delayed));
    }

    [Test]
    public void Announced_AfterTimeout_ExecutesOneRetryPerTimeout()
    {
        TestHandler request1 = new();
        TestHandler request2 = new();
        TestHandler request3 = new();

        _cache.Announced(1, request1);
        _cache.Announced(1, request2);
        _cache.Announced(1, request3);

        Assert.That(
            () => request2.HandleMessageCallCount + request3.HandleMessageCallCount,
            Is.EqualTo(2).After(AssertTimeoutMs, 100));

        long firstTimestamp = Math.Min(request2.LastHandleTimestamp, request3.LastHandleTimestamp);
        long secondTimestamp = Math.Max(request2.LastHandleTimestamp, request3.LastHandleTimestamp);
        TimeSpan retrySpacing = Stopwatch.GetElapsedTime(firstTimestamp, secondTimestamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retrySpacing, Is.GreaterThan(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2)));
            Assert.That(request1.WasCalled, Is.False);
        }
    }

    [Test]
    public void Announced_MultipleResources_ExecutesAllRetryRequestsExceptInitialOne()
    {
        TestHandler request1 = new();
        TestHandler request2 = new();
        TestHandler request3 = new();
        TestHandler request4 = new();

        _cache.Announced(1, request1);
        _cache.Announced(1, request2);
        _cache.Announced(2, request3);
        _cache.Announced(2, request4);

        Assert.That(() => request2.WasCalled, Is.True.After(AssertTimeoutMs, 100));
        Assert.That(() => request4.WasCalled, Is.True.After(AssertTimeoutMs, 100));
        Assert.That(request1.WasCalled, Is.False);
        Assert.That(request3.WasCalled, Is.False);
    }

    [Test]
    [NonParallelizable]
    public void Announced_MultipleResourcesForSameBatchHandler_ExecutesOneBatchedRetryRequest()
    {
        TestHandler request1 = new();
        TestHandler request3 = new();
        BatchTestHandler batchRequest = new();

        _cache.Announced(1, request1);
        _cache.Announced(1, batchRequest);
        _cache.Announced(2, request3);
        _cache.Announced(2, batchRequest);

        Assert.That(() => batchRequest.HandleMessagesCallCount, Is.EqualTo(1).After(AssertTimeoutMs, 100));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batchRequest.BatchResourceValues, Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(batchRequest.HandleMessageCallCount, Is.Zero);
            Assert.That(request1.WasCalled, Is.False);
            Assert.That(request3.WasCalled, Is.False);
        }
    }

    [Test]
    public void Received_RemovesResourceFromRetryQueue()
    {
        _cache.Announced(1, new TestHandler());
        _cache.Received(1);

        AnnounceResult result = _cache.Announced(1, new TestHandler());
        Assert.That(result, Is.EqualTo(AnnounceResult.RequestRequired));
    }

    [Test]
    public async Task Received_BeforeTimeout_PreventsRetryExecution()
    {
        TestHandler request1 = new();
        TestHandler request2 = new();
        TestHandler request3 = new();

        _cache.Announced(1, request1);
        _cache.Announced(1, request2);
        _cache.Announced(1, request3);
        _cache.Received(1);

        await Task.Delay(CacheTimeoutMs * 3, _cancellationTokenSource.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request1.WasCalled, Is.False);
            Assert.That(request2.WasCalled, Is.False);
            Assert.That(request3.WasCalled, Is.False);
        }
    }

    [Test]
    public void Clear_cache_after_timeout()
    {
        Parallel.For(0, 100, (i) =>
        {
            Parallel.For(0, 100, (j) =>
            {
                _cache.Announced(i, new TestHandler());
            });
        });

        Assert.That(() => _cache.ResourcesInRetryQueue, Is.Zero.After(AssertTimeoutMs, 100));
    }

    [Test]
    public void RetryExecution_HandlesExceptions()
    {
        TestHandler faultyRequest = new() { OnHandleMessage = _ => throw new InvalidOperationException("Test exception") };
        TestHandler normalRequest = new();

        _cache.Announced(1, new TestHandler());
        _cache.Announced(1, faultyRequest);
        _cache.Announced(2, new TestHandler());
        _cache.Announced(2, normalRequest);

        Assert.That(() => normalRequest.WasCalled, Is.True.After(AssertTimeoutMs, 100));
    }

    [Test]
    public async Task Received_AfterFirstRetry_PreventsRemainingRetries()
    {
        TestHandler request2 = new() { OnHandleMessage = _ => _cache.Received(1) };
        TestHandler request3 = new() { OnHandleMessage = _ => _cache.Received(1) };

        _cache.Announced(1, new TestHandler());
        _cache.Announced(1, request2);
        _cache.Announced(1, request3);

        Assert.That(
            () => request2.HandleMessageCallCount + request3.HandleMessageCallCount,
            Is.EqualTo(1).After(AssertTimeoutMs, 100));

        _cache.Received(1);
        await Task.Delay(CacheTimeoutMs * 2, _cancellationTokenSource.Token);

        Assert.That(request2.HandleMessageCallCount + request3.HandleMessageCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Announced_SourceHandlerAgain_DoesNotRetrySameHandler()
    {
        TestHandler source = new();

        Assert.That(_cache.Announced(1, source), Is.EqualTo(AnnounceResult.RequestRequired));
        Assert.That(_cache.Announced(1, source), Is.EqualTo(AnnounceResult.Delayed));

        await Task.Delay(CacheTimeoutMs * 2, _cancellationTokenSource.Token);

        Assert.That(source.HandleMessageCallCount, Is.Zero);
    }

    [Test]
    public void Announced_LateAlternateHandler_RetriesWithinBoundedCycle()
    {
        TestHandler firstAlternate = new();
        TestHandler lateAlternate = new();

        _cache.Announced(1, new TestHandler());
        _cache.Announced(1, firstAlternate);
        Assert.That(() => firstAlternate.WasCalled, Is.True.After(AssertTimeoutMs, 100));

        Assert.That(_cache.Announced(1, lateAlternate), Is.EqualTo(AnnounceResult.Delayed));
        Assert.That(() => lateAlternate.WasCalled, Is.True.After(AssertTimeoutMs, 100));
    }

    [Test]
    public async Task Announced_SelectedBatchHandlerAgain_DoesNotRetryHandlerTwice()
    {
        BatchTestHandler batchHandler = new();

        _cache.Announced(1, new TestHandler());
        _cache.Announced(1, batchHandler);
        Assert.That(() => batchHandler.HandleMessagesCallCount, Is.EqualTo(1).After(AssertTimeoutMs, 100));

        Assert.That(_cache.Announced(1, batchHandler), Is.EqualTo(AnnounceResult.Delayed));
        await Task.Delay(CacheTimeoutMs * 2, _cancellationTokenSource.Token);

        Assert.That(batchHandler.HandleMessagesCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Received_ThenReannounced_IgnoresPreviousExpiry()
    {
        const int timeoutMs = 1500;
        long elapsedTicks = 0;
        DateTimeOffset start = DateTimeOffset.UnixEpoch;
        TimerCallback timerCallback = null;
        object timerState = null;
        TaskCompletionSource timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeProvider timeProvider = Substitute.For<TimeProvider>();
        System.Threading.ITimer timer = Substitute.For<System.Threading.ITimer>();
        timeProvider.GetUtcNow().Returns(_ => start.AddTicks(Volatile.Read(ref elapsedTicks)));
        timeProvider.CreateTimer(
            Arg.Any<TimerCallback>(),
            Arg.Any<object>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<TimeSpan>()).Returns(callInfo =>
            {
                timerCallback = callInfo.ArgAt<TimerCallback>(0);
                timerState = callInfo.ArgAt<object>(1);
                timerCreated.TrySetResult();
                return timer;
            });

        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: timeoutMs,
            token: cancellationTokenSource.Token);

        try
        {
            await timerCreated.Task.WaitAsync(TimeSpan.FromMilliseconds(AssertTimeoutMs), cancellationTokenSource.Token);
            cache.Announced(1, new TestHandler());
            Interlocked.Add(ref elapsedTicks, TimeSpan.FromMilliseconds(timeoutMs / 2).Ticks);

            cache.Received(1);
            TestHandler retryHandler = new();
            cache.Announced(1, new TestHandler());
            cache.Announced(1, retryHandler);

            Interlocked.Add(ref elapsedTicks, TimeSpan.FromMilliseconds(timeoutMs / 2).Ticks);
            timerCallback(timerState);

            Assert.That(() => cache.ResourcesInRetryQueue, Is.EqualTo(1).After(AssertTimeoutMs, 10));
            Assert.That(retryHandler.WasCalled, Is.False);

            Interlocked.Add(ref elapsedTicks, TimeSpan.FromMilliseconds(timeoutMs / 2).Ticks);
            timerCallback(timerState);

            Assert.That(() => retryHandler.WasCalled, Is.True.After(AssertTimeoutMs, 100));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task CancellationToken_StopsProcessing()
    {
        TestHandler request = new();

        _cache.Announced(1, request);
        await _cancellationTokenSource.CancelAsync();
        await Task.Delay(CacheTimeoutMs * 3);

        Assert.That(request.WasCalled, Is.False);
    }

    [Test]
    public async Task Announced_AfterRetryInProgress_ReturnsNew()
    {
        _cache.Announced(1, new TestHandler());

        await Task.Delay(CacheTimeoutMs * 3, _cancellationTokenSource.Token);

        Assert.That(() => _cache.Announced(1, new TestHandler()), Is.EqualTo(AnnounceResult.RequestRequired).After(AssertTimeoutMs, 100));
    }

    [Test]
    public void Received_NonExistentResource_DoesNotThrow() => Assert.That(() => _cache.Received(999), Throws.Nothing);

    [Test]
    public async Task Announced_WhenRetryHandlerLimitReached_DoesNotExecuteRejectedHandler()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, timeoutMs: CacheTimeoutMs, maxRetryRequests: 2, token: cancellationTokenSource.Token);
        try
        {
            TestHandler request1 = new();
            TestHandler request2 = new();
            TestHandler request3 = new();
            TestHandler rejectedRequest = new();

            cache.Announced(1, request1);
            AnnounceResult result1 = cache.Announced(1, request2);
            AnnounceResult result2 = cache.Announced(1, request3);
            AnnounceResult result3 = cache.Announced(1, rejectedRequest);

            Assert.That(() => request2.WasCalled, Is.True.After(AssertTimeoutMs, 100));
            Assert.That(() => request3.WasCalled, Is.True.After(AssertTimeoutMs, 100));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result1, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(result2, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(result3, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(request1.WasCalled, Is.False);
                Assert.That(rejectedRequest.WasCalled, Is.False);
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_WhenRetryQueueIsFull_BypassesRetryTracking()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, timeoutMs: CacheTimeoutMs, expiringQueueLimit: 0, token: cancellationTokenSource.Token);
        try
        {
            cache.Announced(1, new TestHandler());
            AnnounceResult result = cache.Announced(2, new TestHandler());

            Assert.That(result, Is.EqualTo(AnnounceResult.RequestRequired));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_ConcurrentUniqueResources_DoesNotExceedRetryQueueLimit()
    {
        const int queueLimit = 10;
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeoutMs: AssertTimeoutMs,
            expiringQueueLimit: queueLimit,
            token: cancellationTokenSource.Token);

        try
        {
            Parallel.For(0, 100, i => cache.Announced(i, new TestHandler()));
            Assert.That(cache.ResourcesInRetryQueue, Is.EqualTo(queueLimit));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_SameHandler_DoesNotExceedPendingResourceLimit()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeoutMs: AssertTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 2);

        try
        {
            TestHandler source = new();
            AnnounceResult result1 = cache.Announced(1, source);
            AnnounceResult result2 = cache.Announced(2, source);
            AnnounceResult limitedResult = cache.Announced(3, source);

            cache.Received(1);
            AnnounceResult resultAfterRelease = cache.Announced(4, source);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result1, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(result2, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(limitedResult, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(resultAfterRelease, Is.EqualTo(AnnounceResult.RequestRequired));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_SameAlternateHandler_DoesNotExceedPendingResourceLimit()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 2);

        try
        {
            TestHandler alternate = new();
            for (int resourceId = 1; resourceId <= 3; resourceId++)
            {
                cache.Announced(resourceId, new TestHandler());
                cache.Announced(resourceId, alternate);
            }

            Assert.That(() => alternate.HandleMessageCallCount, Is.EqualTo(2).After(AssertTimeoutMs, 100));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public void HandlerBag_StaleAddAfterDrainAndReuse_IsRejected()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        long staleGeneration = bag.Activate();

        TestHandler handler1 = new();
        TestHandler staleHandler = new();

        bag.Add(handler1, 8, staleGeneration);
        Assert.That(TakeAll(bag, staleGeneration), Has.Count.EqualTo(1));

        bag.Reset();
        long currentGeneration = bag.Activate();

        HandlerBagAddResult staleAdd = bag.Add(staleHandler, 8, staleGeneration);
        HandlerBagAddResult currentAdd = bag.Add(handler1, 8, currentGeneration);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(staleAdd, Is.EqualTo(HandlerBagAddResult.Inactive));
            Assert.That(currentAdd, Is.EqualTo(HandlerBagAddResult.Added));
            Assert.That(TakeAll(bag, currentGeneration), Is.EqualTo(new[] { handler1 }));
        }
    }

    [Test]
    public void HandlerBag_AddAfterDrainWithoutReactivation_IsRejected()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        long generation = bag.Activate();

        bag.Add(new TestHandler(), 8, generation);
        TakeAll(bag, generation);

        HandlerBagAddResult added = bag.Add(new TestHandler(), 8, generation);
        Assert.That(added, Is.EqualTo(HandlerBagAddResult.Inactive));
    }

    [Test]
    public void HandlerBag_AddAfterDeactivate_IsRejected()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        long generation = bag.Activate();

        bag.Add(new TestHandler(), 8, generation);
        NoopHandlerBagProcessor processor = default;
        bag.Deactivate(generation, ref processor);

        HandlerBagAddResult added = bag.Add(new TestHandler(), 8, generation);
        Assert.That(added, Is.EqualTo(HandlerBagAddResult.Inactive));
    }

    [Test]
    public void HandlerBag_PreservesSetSemantics_NoDuplicates()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        long generation = bag.Activate();

        TestHandler handler = new();
        Assert.That(bag.Add(handler, 8, generation), Is.EqualTo(HandlerBagAddResult.Added));
        Assert.That(bag.Add(handler, 8, generation), Is.EqualTo(HandlerBagAddResult.Duplicate));

        List<IMessageHandler<ResourceRequestMessage>> handlers = TakeAll(bag, generation);
        Assert.That(handlers, Has.Count.EqualTo(1));
    }

    [Test]
    public void HandlerBag_AcceptsLateDistinctHandlersWithinLifetimeBound()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        long generation = bag.Activate();

        Assert.That(bag.Add(new TestHandler(), 3, generation), Is.EqualTo(HandlerBagAddResult.Added));
        Assert.That(bag.Add(new TestHandler(), 3, generation), Is.EqualTo(HandlerBagAddResult.Added));
        Assert.That(bag.TryTake(generation, out _, out bool hasMoreHandlers), Is.True);
        Assert.That(hasMoreHandlers, Is.True);
        Assert.That(bag.Add(new TestHandler(), 3, generation), Is.EqualTo(HandlerBagAddResult.Added));
        Assert.That(bag.Add(new TestHandler(), 3, generation), Is.EqualTo(HandlerBagAddResult.Full));
    }

    [Test]
    public void Announced_RetryHandlerReceivesCorrectResourceId()
    {
        int receivedResourceId = -1;
        TestHandler retryHandler = new() { OnHandleMessage = msg => receivedResourceId = msg.Resource.Value };

        _cache.Announced(42, new TestHandler());
        _cache.Announced(42, retryHandler);

        Assert.That(() => receivedResourceId, Is.EqualTo(42).After(AssertTimeoutMs, 100));
    }

    private static List<IMessageHandler<ResourceRequestMessage>> TakeAll(HandlerBag<ResourceRequestMessage> bag, long generation)
    {
        List<IMessageHandler<ResourceRequestMessage>> handlers = [];
        while (bag.TryTake(generation, out IMessageHandler<ResourceRequestMessage> handler, out _))
        {
            handlers.Add(handler);
        }

        return handlers;
    }

    private readonly struct NoopHandlerBagProcessor : IHandlerBagProcessor<ResourceRequestMessage>
    {
        public void Process(IMessageHandler<ResourceRequestMessage> handler) { }
    }
}
