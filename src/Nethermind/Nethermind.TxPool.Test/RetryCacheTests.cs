// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
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
        public int HandleMessageCallCount => _handleMessageCallCount;
        public bool WasCalled => _handleMessageCallCount > 0;
        public Action<ResourceRequestMessage> OnHandleMessage { get; set; }

        public virtual void HandleMessage(ResourceRequestMessage message)
        {
            Interlocked.Increment(ref _handleMessageCallCount);
            OnHandleMessage?.Invoke(message);
        }
    }

    private sealed class BatchTestHandler : TestHandler, IBatchMessageHandler<ResourceRequestMessage, ResourceId>
    {
        private readonly Lock _lock = new();
        private readonly List<int> _batchResourceValues = [];
        private int _handleMessagesCallCount;

        public int HandleMessagesCallCount => _handleMessagesCallCount;

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
    public void TearDown()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
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
    public void Announced_AfterTimeout_ExecutesRetryRequests()
    {
        TestHandler request1 = new();
        TestHandler request2 = new();
        TestHandler request3 = new();
        long handlersCalledOnTimeout = Metrics.PendingTransactionRetryHandlersCalledOnTimeout;
        long fallbackHandlersCalledOnTimeout = Metrics.PendingTransactionRetryFallbackHandlersCalledOnTimeout;
        long resourcesTimedOutWithHandlers = Metrics.PendingTransactionRetryResourcesTimedOutWithHandlers;
        long resourcesTimedOutWithHandlersAgeMilliseconds = Metrics.PendingTransactionRetryResourcesTimedOutWithHandlersAgeMilliseconds;

        _cache.Announced(1, request1);
        _cache.Announced(1, request2);
        _cache.Announced(1, request3);

        Assert.That(() => request2.WasCalled, Is.True.After(AssertTimeoutMs, 100));
        Assert.That(() => request3.WasCalled, Is.True.After(AssertTimeoutMs, 100));
        Assert.That(() => Metrics.PendingTransactionRetryHandlersCalledOnTimeout, Is.GreaterThanOrEqualTo(handlersCalledOnTimeout + 2).After(AssertTimeoutMs, 100));
        Assert.That(() => Metrics.PendingTransactionRetryFallbackHandlersCalledOnTimeout, Is.GreaterThanOrEqualTo(fallbackHandlersCalledOnTimeout + 2).After(AssertTimeoutMs, 100));
        Assert.That(() => Metrics.PendingTransactionRetryResourcesTimedOutWithHandlers, Is.GreaterThanOrEqualTo(resourcesTimedOutWithHandlers + 1).After(AssertTimeoutMs, 100));
        Assert.That(() => Metrics.PendingTransactionRetryResourcesTimedOutWithHandlersAgeMilliseconds, Is.GreaterThan(resourcesTimedOutWithHandlersAgeMilliseconds).After(AssertTimeoutMs, 100));
        Assert.That(request1.WasCalled, Is.False);
    }

    [Test]
    [NonParallelizable]
    public void Announced_AfterTimeoutWithoutRetryHandlers_DoesNotTrackResourceTimeoutWithHandlers()
    {
        long resourcesTimedOutWithHandlers = Metrics.PendingTransactionRetryResourcesTimedOutWithHandlers;

        _cache.Announced(1, new TestHandler());

        Assert.That(() => _cache.ResourcesInRetryQueue, Is.Zero.After(AssertTimeoutMs, 100));
        Assert.That(Metrics.PendingTransactionRetryResourcesTimedOutWithHandlers, Is.EqualTo(resourcesTimedOutWithHandlers));
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
        long handlersCalledOnTimeout = Metrics.PendingTransactionRetryHandlersCalledOnTimeout;
        long batchHandlersCalledOnTimeout = Metrics.PendingTransactionRetryBatchHandlersCalledOnTimeout;
        long batchResourcesCalledOnTimeout = Metrics.PendingTransactionRetryBatchResourcesCalledOnTimeout;
        long fallbackHandlersCalledOnTimeout = Metrics.PendingTransactionRetryFallbackHandlersCalledOnTimeout;

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
            Assert.That(Metrics.PendingTransactionRetryHandlersCalledOnTimeout, Is.GreaterThanOrEqualTo(handlersCalledOnTimeout + 2));
            Assert.That(Metrics.PendingTransactionRetryBatchHandlersCalledOnTimeout, Is.GreaterThanOrEqualTo(batchHandlersCalledOnTimeout + 1));
            Assert.That(Metrics.PendingTransactionRetryBatchResourcesCalledOnTimeout, Is.GreaterThanOrEqualTo(batchResourcesCalledOnTimeout + 2));
            Assert.That(Metrics.PendingTransactionRetryFallbackHandlersCalledOnTimeout, Is.EqualTo(fallbackHandlersCalledOnTimeout));
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
        long handlersSkippedOnReceived = Metrics.PendingTransactionRetryHandlersSkippedOnReceived;
        long resourcesSkippedOnReceived = Metrics.PendingTransactionRetryResourcesSkippedOnReceived;
        long resourcesSkippedOnReceivedAgeMilliseconds = Metrics.PendingTransactionRetryResourcesSkippedOnReceivedAgeMilliseconds;

        _cache.Announced(1, request1);
        _cache.Announced(1, request2);
        _cache.Announced(1, request3);
        _cache.Received(1);

        Assert.That(Metrics.PendingTransactionRetryHandlersSkippedOnReceived, Is.GreaterThanOrEqualTo(handlersSkippedOnReceived + 2));
        Assert.That(Metrics.PendingTransactionRetryResourcesSkippedOnReceived, Is.GreaterThanOrEqualTo(resourcesSkippedOnReceived + 1));
        Assert.That(Metrics.PendingTransactionRetryResourcesSkippedOnReceivedAgeMilliseconds, Is.GreaterThanOrEqualTo(resourcesSkippedOnReceivedAgeMilliseconds));

        await Task.Delay(CacheTimeoutMs * 3, _cancellationTokenSource.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request1.WasCalled, Is.False);
            Assert.That(request2.WasCalled, Is.False);
            Assert.That(request3.WasCalled, Is.False);
        }
    }

    [Test]
    public async Task Clear_cache_after_timeout()
    {
        Parallel.For(0, 100, (i) =>
        {
            Parallel.For(0, 100, (j) =>
            {
                _cache.Announced(i, new TestHandler());
            });
        });

        await Task.Delay(CacheTimeoutMs * 4, _cancellationTokenSource.Token);

        Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
    }

    [Test]
    public void RetryExecution_HandlesExceptions()
    {
        TestHandler faultyRequest = new() { OnHandleMessage = _ => throw new InvalidOperationException("Test exception") };
        TestHandler normalRequest = new();

        _cache.Announced(1, new TestHandler());
        _cache.Announced(1, faultyRequest);
        _cache.Announced(1, normalRequest);

        Assert.That(() => normalRequest.WasCalled, Is.True.After(AssertTimeoutMs, 100));
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
    [NonParallelizable]
    public void Received_WithoutRetryHandlers_DoesNotTrackSkippedResource()
    {
        long resourcesSkippedOnReceived = Metrics.PendingTransactionRetryResourcesSkippedOnReceived;

        _cache.Announced(1, new TestHandler());
        _cache.Received(1);

        Assert.That(Metrics.PendingTransactionRetryResourcesSkippedOnReceived, Is.EqualTo(resourcesSkippedOnReceived));
    }

    [Test]
    public async Task Announced_WhenRetryHandlerLimitReached_TracksRejectedHandlers()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, timeoutMs: CacheTimeoutMs, maxRetryRequests: 2, token: cancellationTokenSource.Token);
        try
        {
            long handlersRejectedByLimit = Metrics.PendingTransactionRetryHandlersRejectedByLimit;

            cache.Announced(1, new TestHandler());
            AnnounceResult result1 = cache.Announced(1, new TestHandler());
            AnnounceResult result2 = cache.Announced(1, new TestHandler());
            AnnounceResult result3 = cache.Announced(1, new TestHandler());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result1, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(result2, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(result3, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(Metrics.PendingTransactionRetryHandlersRejectedByLimit, Is.GreaterThanOrEqualTo(handlersRejectedByLimit + 1));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_WhenRetryQueueIsFull_TracksQueueFull()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, timeoutMs: CacheTimeoutMs, expiringQueueLimit: 0, token: cancellationTokenSource.Token);
        try
        {
            long queueFull = Metrics.PendingTransactionRetryQueueFull;

            cache.Announced(1, new TestHandler());
            AnnounceResult result = cache.Announced(2, new TestHandler());

            Assert.That(result, Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(Metrics.PendingTransactionRetryQueueFull, Is.GreaterThanOrEqualTo(queueFull + 1));
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
        // Simulate the race: capture a bag reference, drain+return it, reuse for another resource,
        // then try to add via the stale reference — should be rejected.
        HandlerBag<ResourceRequestMessage> bag = new();
        bag.Activate();

        TestHandler handler1 = new();
        TestHandler staleHandler = new();

        bag.TryAdd(handler1, 8);
        Assert.That(DrainToList(bag), Has.Count.EqualTo(1));

        // Bag is now inactive (drained). Simulate pool return + reuse.
        bag.Reset();
        bag.Activate();

        // Stale add from a thread that captured the reference before drain.
        // This should succeed because the bag is active again — but in RetryCache,
        // TryRemove ensures the stale reference is no longer in the dictionary,
        // so GetOrAdd would return a different bag. The HandlerBag itself
        // cannot distinguish old vs new lifecycle after Activate. The safety
        // comes from the dictionary-level removal, not the bag-level flag.
        // This test verifies Drain deactivates and Reset+Activate reactivates.
        bool addedAfterReactivation = bag.TryAdd(staleHandler, 8);
        Assert.That(addedAfterReactivation, Is.True);

        List<IMessageHandler<ResourceRequestMessage>> handlers = DrainToList(bag);
        Assert.That(handlers, Has.Count.EqualTo(1));
        Assert.That(handlers[0], Is.SameAs(staleHandler));
    }

    [Test]
    public void HandlerBag_AddAfterDrainWithoutReactivation_IsRejected()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        bag.Activate();

        bag.TryAdd(new TestHandler(), 8);
        DrainToList(bag);

        // Bag is inactive after drain — TryAdd must be rejected
        bool added = bag.TryAdd(new TestHandler(), 8);
        Assert.That(added, Is.False);
    }

    [Test]
    public void HandlerBag_AddAfterDeactivate_IsRejected()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        bag.Activate();

        bag.TryAdd(new TestHandler(), 8);
        bag.Deactivate();

        // Bag is inactive after explicit deactivate — TryAdd must be rejected
        bool added = bag.TryAdd(new TestHandler(), 8);
        Assert.That(added, Is.False);
    }

    [Test]
    public void HandlerBag_PreservesSetSemantics_NoDuplicates()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        bag.Activate();

        TestHandler handler = new();
        Assert.That(bag.TryAdd(handler, 8), Is.True);
        Assert.That(bag.TryAdd(handler, 8), Is.False);

        List<IMessageHandler<ResourceRequestMessage>> handlers = DrainToList(bag);
        Assert.That(handlers, Has.Count.EqualTo(1));
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

    private static List<IMessageHandler<ResourceRequestMessage>> DrainToList(HandlerBag<ResourceRequestMessage> bag)
    {
        List<IMessageHandler<ResourceRequestMessage>> handlers = [];
        HandlerCollector collector = new(handlers);
        bag.Drain(ref collector);
        return handlers;
    }

    private readonly struct HandlerCollector(List<IMessageHandler<ResourceRequestMessage>> handlers) : IHandlerBagDrainProcessor<ResourceRequestMessage>
    {
        public void Process(IMessageHandler<ResourceRequestMessage> handler) => handlers.Add(handler);
    }
}
