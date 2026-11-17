// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Threading;
using Nethermind.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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

    public readonly struct CollisionResourceId(int value) : IEquatable<CollisionResourceId>, IHash64bit<CollisionResourceId>
    {
        public readonly int Value = value;
        public bool Equals(CollisionResourceId other) => Value == other.Value;
        public bool Equals(in CollisionResourceId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is CollisionResourceId other && Equals(other);
        public override int GetHashCode() => 0;
        public long GetHashCode64() => 0;
    }

    public readonly struct CollisionRequestMessage : INew<CollisionResourceId, CollisionRequestMessage>
    {
        public static CollisionRequestMessage New(CollisionResourceId resourceId) => new();
    }

    private sealed class CollisionHandler : IMessageHandler<CollisionRequestMessage>
    {
        public void HandleMessage(CollisionRequestMessage message) { }
    }

    private sealed class BlockingHashState : IDisposable
    {
        private int _blocking;
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Continue = new();

        public void Block() => Volatile.Write(ref _blocking, 1);

        public long GetHashCode64(int value)
        {
            if (Volatile.Read(ref _blocking) != 0)
            {
                Entered.Set();
                Continue.Wait();
            }

            return value;
        }

        public void Dispose()
        {
            Entered.Dispose();
            Continue.Dispose();
        }
    }

    private readonly struct BlockingResourceId(int value, BlockingHashState state) : IEquatable<BlockingResourceId>, IHash64bit<BlockingResourceId>
    {
        public readonly int Value = value;
        public bool Equals(BlockingResourceId other) => Value == other.Value;
        public bool Equals(in BlockingResourceId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is BlockingResourceId other && Equals(other);
        public override int GetHashCode() => Value;
        public long GetHashCode64() => state.GetHashCode64(Value);
    }

    private readonly struct BlockingRequestMessage : INew<BlockingResourceId, BlockingRequestMessage>
    {
        public static BlockingRequestMessage New(BlockingResourceId resourceId) => new();
    }

    private sealed class BlockingHandler : IMessageHandler<BlockingRequestMessage>
    {
        public void HandleMessage(BlockingRequestMessage message) { }
    }

    public interface ITestHandler : IMessageHandler<ResourceRequestMessage>;

    /// <summary>
    /// A test handler that tracks HandleMessage calls without using NSubstitute.
    /// </summary>
    private class TestHandler : ITestHandler
    {
        private int _handleMessageCallCount;
        public int HandleMessageCallCount => Volatile.Read(ref _handleMessageCallCount);
        public bool WasCalled => HandleMessageCallCount > 0;
        public Action<ResourceRequestMessage> OnHandleMessage { get; set; }

        public virtual void HandleMessage(ResourceRequestMessage message)
        {
            Interlocked.Increment(ref _handleMessageCallCount);
            OnHandleMessage?.Invoke(message);
        }
    }

    private delegate void BatchCallback(ReadOnlySpan<ResourceId> resources);

    private class BatchTestHandler : TestHandler, IBatchMessageHandler<ResourceRequestMessage, ResourceId>
    {
        private readonly Lock _lock = new();
        private readonly List<int> _batchResourceValues = [];
        private int _handleMessagesCallCount;
        public BatchCallback OnBatch { get; set; }

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
            OnBatch?.Invoke(resourceIds);
        }
    }

    private sealed class ValueEqualBatchTestHandler : BatchTestHandler
    {
        public override bool Equals(object obj) => obj is ValueEqualBatchTestHandler;
        public override int GetHashCode() => 0;
    }

    private CancellationTokenSource _cancellationTokenSource;
    private RetryCache<ResourceRequestMessage, ResourceId> _cache;
    private ManualTimeProvider _timeProvider;

    // Short cache timeout so retries fire quickly (~600ms); generous assertion timeout for slow CI
    private const int CacheTimeoutMs = 500;
    private const int AssertTimeoutMs = 10_000;

    [SetUp]
    public void Setup()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _timeProvider = new ManualTimeProvider();
        _cache = new(
            TestLogManager.Instance,
            _timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: _cancellationTokenSource.Token);
        Assert.That(_timeProvider.TimerCreated.IsCompleted, Is.True);
    }

    [Test]
    public void Local_refusal_retries_the_only_announcer_without_spending_alternate_attempts()
    {
        const int count = 100;
        BatchTestHandler source = new();
        bool refused = true;
        source.OnBatch = resources =>
        {
            foreach (ref readonly ResourceId resource in resources)
            {
                if (refused) Assert.That(_cache.TryDeferRequest(in resource, source), Is.True);
                else _cache.Received(resource);
            }
        };
        for (int index = 0; index < count; index++)
        {
            ResourceId resource = index;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_cache.Announced(in resource, source), Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(_cache.TryDeferRequest(in resource, source), Is.True);
                Assert.That(_cache.TryDeferRequest(in resource, source), Is.True);
            }
        }

        for (int attempt = 1; attempt <= 6; attempt++)
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            _cache.ProcessRetryTick();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.BatchResourceValues.Length, Is.EqualTo(count * attempt));
                Assert.That(_cache.TrackedRequestsInUse, Is.EqualTo(count));
                Assert.That(_cache.ResourcesInRetryQueue, Is.EqualTo(count));
            }
        }

        refused = false;
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.BatchResourceValues.GroupBy(static resource => resource)
                .Select(static group => group.Count()), Is.All.EqualTo(7));
            Assert.That(_cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
        }
    }

    [Test]
    public async Task Unsent_overflow_requests_keep_the_original_source_within_their_own_bound()
    {
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance,
            _timeProvider, timeoutMs: CacheTimeoutMs, maxPendingResourcesPerHandler: 1, overflowRequestLimit: 8192);
        BatchTestHandler source = new();
        Assert.That(cache.Announced(-1, source), Is.EqualTo(AnnounceResult.RequestRequired));
        for (int index = 0; index < 4096; index++)
        {
            ResourceId resource = index;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.Announced(in resource, source), Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(cache.TryDeferRequest(in resource, source), Is.True);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Announced(4096, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.TryDeferRequest(4096, source), Is.False);
        }
        cache.Received(0);
        Assert.That(cache.TryDeferRequest(4096, source), Is.True);
        for (int tick = 0; tick < 4096 / 256; tick++)
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 5));
            cache.ProcessRetryTick();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.BatchResourceValues, Is.EquivalentTo(Enumerable.Range(1, 4096)));
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Announced(-2, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.Announced(4097, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.TryDeferRequest(4097, source), Is.True, "sent overflow slots become reusable after their actual request timeout");
        }
        await cache.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.OverflowRequestsInUse, Is.Zero);
            Assert.That(cache.ResourcesInRetryQueue, Is.Zero);
        }
    }

    [Test]
    public async Task Deferred_overflow_budget_survives_generation_rotation_and_releases_on_receipt()
    {
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance,
            _timeProvider, timeoutMs: CacheTimeoutMs, maxPendingResourcesPerHandler: 1, overflowRequestLimit: 1);
        BatchTestHandler source = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Announced(0, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.Announced(1, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.TryDeferRequest(1, source), Is.True);
        }
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs * 2));
        Assert.That(cache.Announced(2, source), Is.EqualTo(AnnounceResult.Delayed),
            "rotating hash-only generations must not free the transferred unsent request budget");
        cache.Received(1);
        Assert.That(cache.Announced(2, source), Is.EqualTo(AnnounceResult.RequestRequired));
        cache.Received(2);
        cache.Received(0);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.OverflowRequestsInUse, Is.Zero);
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(cache.ResourcesInRetryQueue, Is.Zero);
        }
    }

    [Test]
    public async Task Local_refusal_reuses_the_original_sources_pending_slot()
    {
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance,
            _timeProvider, timeoutMs: CacheTimeoutMs, maxPendingResourcesPerHandler: 2);
        BatchTestHandler source = new();
        for (int index = 0; index < 2; index++)
        {
            ResourceId resource = index;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.Announced(in resource, source), Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(cache.TryDeferRequest(in resource, source), Is.True);
            }
        }
        Assert.That(cache.Announced(2, source), Is.EqualTo(AnnounceResult.Delayed));
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.BatchResourceValues, Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(2));
            Assert.That(cache.Announced(2, source), Is.EqualTo(AnnounceResult.Delayed));
        }
        cache.Received(0);
        Assert.That(cache.Announced(2, source), Is.EqualTo(AnnounceResult.RequestRequired));
        cache.Received(1);
        cache.Received(2);
        Assert.That(cache.TrackedRequestsInUse, Is.Zero);
    }

    [Test]
    public void Local_refusal_uses_an_alternate_announcer_before_retrying_the_refused_source()
    {
        ResourceId resource = 1;
        BatchTestHandler refused = new();
        BatchTestHandler available = new();
        available.OnBatch = resources =>
        {
            foreach (ref readonly ResourceId received in resources) _cache.Received(received);
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.Announced(in resource, refused), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(_cache.TryDeferRequest(in resource, refused), Is.True);
            Assert.That(_cache.Announced(in resource, available), Is.EqualTo(AnnounceResult.Delayed));
        }

        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused.BatchResourceValues, Is.Empty);
            Assert.That(available.BatchResourceValues, Is.EqualTo(new[] { 1 }));
            Assert.That(_cache.TrackedRequestsInUse, Is.Zero);
        }
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();
        Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
    }

    [Test]
    public async Task Local_refusal_racing_delivery_releases_both_handler_slots()
    {
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance,
            _timeProvider, timeoutMs: CacheTimeoutMs, maxPendingResourcesPerHandler: 2);
        TestHandler source = new();
        for (int attempt = 0; attempt < 100; attempt++)
        {
            ResourceId resource = attempt;
            Assert.That(cache.Announced(in resource, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Parallel.Invoke(() => cache.TryDeferRequest(in resource, source), () => cache.Received(resource));
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Announced(100, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.Announced(101, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(2));
        }
        cache.Received(100);
        cache.Received(101);
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result1, Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(result2, Is.EqualTo(AnnounceResult.Delayed));
        }
    }

    [Test]
    public void TryDefer_GrantsOneDeferralPerRequestSent([Values(0, 1, 4)] int retryPeers)
    {
        TestHandler source = new();
        TestHandler[] peers = new TestHandler[retryPeers];
        Assert.That(_cache.Announced(1, source), Is.EqualTo(AnnounceResult.RequestRequired));
        for (int i = 0; i < peers.Length; i++)
        {
            peers[i] = new TestHandler();
            Assert.That(_cache.Announced(1, peers[i]), Is.EqualTo(AnnounceResult.Delayed));
        }

        int granted = 0;
        for (int requestsSent = 1; requestsSent <= retryPeers + 1; requestsSent++)
        {
            for (int resend = 0; resend < 3; resend++)
            {
                if (_cache.TryDefer(1)) granted++;
            }

            Assert.That(granted, Is.EqualTo(requestsSent));
            _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            int retriesSent = Math.Min(requestsSent, retryPeers);
            Assert.That(() => peers.Sum(static peer => peer.HandleMessageCallCount), Is.EqualTo(retriesSent).After(AssertTimeoutMs, 10));
        }

        Assert.That(() => _cache.TrackedRequestsInUse, Is.Zero.After(AssertTimeoutMs, 10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.TryDefer(1), Is.False);
            Assert.That(granted, Is.EqualTo(retryPeers + 1));
            Assert.That(source.WasCalled, Is.False);
        }
    }

    [Test]
    public void TryAwaitAnnouncement_LetsOneAnnouncerRequestAnUnrequestedResource([Values] bool claimedWhileKnown)
    {
        TestHandler claimant = new();
        TestHandler retryPeer = new();
        Assert.That(_cache.TryAwaitAnnouncement(1), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.TryAwaitAnnouncement(1), Is.True, "a resend of the same unrequested resource");
            Assert.That(_cache.TryDefer(1), Is.False, "nothing was requested, so nothing is owed a deferral");
        }

        AnnounceResult claim = claimedWhileKnown
            ? _cache.TryClaimUnrequested(1, claimant) ? AnnounceResult.RequestRequired : AnnounceResult.Delayed
            : _cache.Announced(1, claimant);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claim, Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(_cache.TryClaimUnrequested(1, retryPeer), Is.False);
            Assert.That(_cache.Announced(1, retryPeer), Is.EqualTo(AnnounceResult.Delayed));
            Assert.That(_cache.TryAwaitAnnouncement(1), Is.False, "claimed, so a delivery is the claimant's response");
            Assert.That(_cache.TryDefer(1), Is.True);
            Assert.That(_cache.TryDefer(1), Is.False);
        }
    }

    [Test]
    public void TryAwaitAnnouncement_ClaimStartsAFullTimeout()
    {
        TestHandler claimant = new();
        TestHandler laterPeer = new();
        Assert.That(_cache.TryAwaitAnnouncement(1), Is.True);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs * 3 / 4));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.Announced(1, claimant), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(_cache.Announced(1, laterPeer), Is.EqualTo(AnnounceResult.Delayed));
        }

        // Past the push's deadline, inside the claim's: the claimed request is still in flight.
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2));
        _cache.ProcessRetryTick();
        Assert.That(laterPeer.WasCalled, Is.False, "a late claim must get a full timeout before a retry");

        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2 + 1));
        _cache.ProcessRetryTick();
        Assert.That(laterPeer.HandleMessageCallCount, Is.EqualTo(1), "once the claim's own timeout passes, the next announcer is asked");
        _cache.Received(1);
    }

    [Test]
    public void TryAwaitAnnouncement_ClaimRacingExpiryKeepsALiveLifecycle()
    {
        const int attempts = 1_000;
        for (int resourceId = 0; resourceId < attempts; resourceId++)
        {
            Assert.That(_cache.TryAwaitAnnouncement(resourceId), Is.True);
            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs + 1));

            bool claimed = false;
            Parallel.Invoke(
                _cache.ProcessRetryTick,
                () => claimed = _cache.TryClaimUnrequested(resourceId, new TestHandler()));

            // Expiry losing to the claim must not leave it a deactivated bag, or the claimed request is owed no deferral.
            if (claimed) Assert.That(_cache.TryDefer(resourceId), Is.True, $"attempt {resourceId}");
            _cache.Received(resourceId);
        }
    }

    [Test]
    public void TryAwaitAnnouncement_ExpiresUnclaimedWithoutRequesting()
    {
        Assert.That(_cache.TryAwaitAnnouncement(1), Is.True);
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => _cache.TrackedRequestsInUse, Is.Zero.After(AssertTimeoutMs, 10));

        TestHandler announcer = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
            Assert.That(_cache.TryClaimUnrequested(1, announcer), Is.False);
            Assert.That(_cache.Announced(1, announcer), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(_cache.Announced(1, new TestHandler()), Is.EqualTo(AnnounceResult.Delayed), "a fresh lifecycle, not one left firing");
        }
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

        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(
            () => request2.HandleMessageCallCount + request3.HandleMessageCallCount,
            Is.EqualTo(1).After(AssertTimeoutMs, 10));
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => request2.HandleMessageCallCount + request3.HandleMessageCallCount,
                Is.EqualTo(2).After(AssertTimeoutMs, 10));
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

        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => request2.WasCalled, Is.True.After(AssertTimeoutMs, 100));
        Assert.That(() => request4.WasCalled, Is.True.After(AssertTimeoutMs, 100));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request1.WasCalled, Is.False);
            Assert.That(request3.WasCalled, Is.False);
        }
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

        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
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
    public async Task Announced_OverlappingBatchHandlers_DistributesActualIdentities()
    {
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            BatchTestHandler sharedHandler = new();
            BatchTestHandler otherHandler = new();

            cache.Announced(1, new TestHandler());
            cache.Announced(1, sharedHandler);
            cache.Announced(2, new TestHandler());
            cache.Announced(2, sharedHandler);
            cache.Announced(2, otherHandler);

            timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            cache.ProcessRetryTick();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(sharedHandler.HandleMessagesCallCount, Is.EqualTo(1));
                Assert.That(sharedHandler.BatchResourceValues, Is.EquivalentTo(new[] { 1 }));
                Assert.That(otherHandler.BatchResourceValues, Is.EquivalentTo(new[] { 2 }));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_PreferredBatchAtFairShareWithoutAlternate_StillMakesProgress()
    {
        const int resourceCount = 65;
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            BatchTestHandler sharedHandler = new();

            for (int resourceId = 1; resourceId <= resourceCount; resourceId++)
            {
                cache.Announced(resourceId, new TestHandler());
                cache.Announced(resourceId, sharedHandler);
            }

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));

            Assert.That(() => sharedHandler.HandleMessagesCallCount, Is.EqualTo(1).After(AssertTimeoutMs, 10));
            Assert.That(sharedHandler.BatchResourceValues, Has.Length.EqualTo(resourceCount));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_ValueEqualBatchHandlers_GroupsByReference()
    {
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            ValueEqualBatchTestHandler firstHandler = new();
            ValueEqualBatchTestHandler secondHandler = new();

            cache.Announced(1, new TestHandler());
            cache.Announced(1, firstHandler);
            cache.Announced(2, new TestHandler());
            cache.Announced(2, secondHandler);

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));

            Assert.That(
                () => firstHandler.HandleMessagesCallCount + secondHandler.HandleMessagesCallCount,
                Is.EqualTo(2).After(AssertTimeoutMs, 10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstHandler.BatchResourceValues, Is.EqualTo(new[] { 1 }));
                Assert.That(secondHandler.BatchResourceValues, Is.EqualTo(new[] { 2 }));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
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
    public async Task RetryExpiryUsesMonotonicTime()
    {
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            TestHandler controlHandler = new();
            TestHandler retryHandler = new();
            cache.Announced(1, new TestHandler());
            cache.Announced(1, controlHandler);

            timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2));
            cache.Announced(2, new TestHandler());
            cache.Announced(2, retryHandler);

            timeProvider.JumpUtc(TimeSpan.FromDays(1));
            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2));
            Assert.That(() => controlHandler.WasCalled, Is.True.After(AssertTimeoutMs, 10));
            Assert.That(retryHandler.WasCalled, Is.False);

            timeProvider.JumpUtc(TimeSpan.FromDays(-2));
            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2));
            Assert.That(() => retryHandler.WasCalled, Is.True.After(AssertTimeoutMs, 10));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public void Received_BeforeTimeout_PreventsRetryExecution()
    {
        TestHandler request1 = new();
        TestHandler request2 = new();
        TestHandler request3 = new();

        _cache.Announced(1, request1);
        _cache.Announced(1, request2);
        _cache.Announced(1, request3);
        _cache.Received(1);

        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => _cache.ResourcesInRetryQueue, Is.Zero.After(AssertTimeoutMs, 10));

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
        using CancellationTokenSource cancellationTokenSource = new();
        ManualTimeProvider timeProvider = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            TestHandler[] handlers = new TestHandler[100];
            for (int i = 0; i < handlers.Length; i++)
            {
                handlers[i] = new TestHandler();
            }

            Parallel.For(0, 100, resourceId =>
                Parallel.For(0, handlers.Length, handlerIndex => cache.Announced(resourceId, handlers[handlerIndex])));

            for (int retry = 1; retry <= 4; retry++)
            {
                timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
                Assert.That(() => GetTotalCalls(handlers), Is.EqualTo(retry * 100).After(AssertTimeoutMs, 10));
            }

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));

            Assert.That(() => cache.ResourcesInRetryQueue, Is.Zero.After(AssertTimeoutMs, 10));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task RetryExecution_DispatchesAtMost256ResourcesPerTickAndContinuesOnNextTick()
    {
        const int resourceCount = 257;
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            TestHandler retryHandler = new();
            for (int resourceId = 0; resourceId < resourceCount; resourceId++)
            {
                cache.Announced(resourceId, new TestHandler());
                cache.Announced(resourceId, retryHandler);
            }

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            Assert.That(() => retryHandler.HandleMessageCallCount, Is.EqualTo(256).After(AssertTimeoutMs, 10));

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(1));
            Assert.That(() => retryHandler.HandleMessageCallCount, Is.EqualTo(resourceCount).After(AssertTimeoutMs, 10));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
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

        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => normalRequest.WasCalled, Is.True.After(AssertTimeoutMs, 100));
    }

    [Test]
    public void Received_AfterFirstRetry_PreventsRemainingRetries()
    {
        TestHandler request2 = new() { OnHandleMessage = _ => _cache.Received(1) };
        TestHandler request3 = new() { OnHandleMessage = _ => _cache.Received(1) };

        _cache.Announced(1, new TestHandler());
        _cache.Announced(1, request2);
        _cache.Announced(1, request3);

        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(
            () => request2.HandleMessageCallCount + request3.HandleMessageCallCount,
            Is.EqualTo(1).After(AssertTimeoutMs, 100));

        _cache.Received(1);
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => _cache.ResourcesInRetryQueue, Is.Zero.After(AssertTimeoutMs, 10));

        Assert.That(request2.HandleMessageCallCount + request3.HandleMessageCallCount, Is.EqualTo(1));
    }

    [Test]
    public void Announced_SourceHandlerAgain_DoesNotRetrySameHandler()
    {
        TestHandler source = new();

        Assert.That(_cache.Announced(1, source), Is.EqualTo(AnnounceResult.RequestRequired));
        Assert.That(_cache.Announced(1, source), Is.EqualTo(AnnounceResult.Delayed));

        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => _cache.ResourcesInRetryQueue, Is.Zero.After(AssertTimeoutMs, 10));

        Assert.That(source.HandleMessageCallCount, Is.Zero);
    }

    [Test]
    public void Announced_LateAlternateHandler_RetriesWithinBoundedCycle()
    {
        TestHandler firstAlternate = new();
        TestHandler lateAlternate = new();

        _cache.Announced(1, new TestHandler());
        _cache.Announced(1, firstAlternate);
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => firstAlternate.WasCalled, Is.True.After(AssertTimeoutMs, 100));

        Assert.That(_cache.Announced(1, lateAlternate), Is.EqualTo(AnnounceResult.Delayed));
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => lateAlternate.WasCalled, Is.True.After(AssertTimeoutMs, 100));
    }

    [Test]
    public void Announced_SelectedBatchHandlerAgain_DoesNotRetryHandlerTwice()
    {
        BatchTestHandler batchHandler = new();

        _cache.Announced(1, new TestHandler());
        _cache.Announced(1, batchHandler);
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => batchHandler.HandleMessagesCallCount, Is.EqualTo(1).After(AssertTimeoutMs, 100));

        Assert.That(_cache.Announced(1, batchHandler), Is.EqualTo(AnnounceResult.Delayed));
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => _cache.ResourcesInRetryQueue, Is.Zero.After(AssertTimeoutMs, 10));

        Assert.That(batchHandler.HandleMessagesCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Received_ThenReannounced_IgnoresPreviousExpiry()
    {
        const int timeoutMs = 1500;
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: timeoutMs,
            token: cancellationTokenSource.Token);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            cache.Announced(1, new TestHandler());
            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs / 2));

            cache.Received(1);
            TestHandler retryHandler = new();
            cache.Announced(1, new TestHandler());
            cache.Announced(1, retryHandler);

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(timeoutMs / 2));

            Assert.That(() => cache.ResourcesInRetryQueue, Is.EqualTo(1).After(AssertTimeoutMs, 10));
            Assert.That(retryHandler.WasCalled, Is.False);

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(timeoutMs / 2));

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
        await _cache.DisposeAsync();

        Assert.That(request.WasCalled, Is.False);
    }

    [Test]
    public void Announced_AfterRetryInProgress_ReturnsNew()
    {
        _cache.Announced(1, new TestHandler());

        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        Assert.That(() => _cache.ResourcesInRetryQueue, Is.Zero.After(AssertTimeoutMs, 10));

        Assert.That(_cache.Announced(1, new TestHandler()), Is.EqualTo(AnnounceResult.RequestRequired));
    }

    [Test]
    public void Received_NonExistentResource_DoesNotThrow() => Assert.That(() => _cache.Received(999), Throws.Nothing);

    [Test]
    public async Task Announced_WhenRetryHandlerLimitReached_DoesNotExecuteRejectedHandler()
    {
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            maxRetryRequests: 2,
            token: cancellationTokenSource.Token);
        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            TestHandler request1 = new();
            TestHandler request2 = new();
            TestHandler request3 = new();
            TestHandler rejectedRequest = new();

            cache.Announced(1, request1);
            AnnounceResult result1 = cache.Announced(1, request2);
            AnnounceResult result2 = cache.Announced(1, request3);
            AnnounceResult result3 = cache.Announced(1, rejectedRequest);

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            Assert.That(
                () => request2.HandleMessageCallCount + request3.HandleMessageCallCount,
                Is.EqualTo(1).After(AssertTimeoutMs, 100));
            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            Assert.That(
                () => request2.HandleMessageCallCount + request3.HandleMessageCallCount,
                Is.EqualTo(2).After(AssertTimeoutMs, 100));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result1, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(result2, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(result3, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(request1.WasCalled, Is.False);
                Assert.That(request2.WasCalled, Is.True);
                Assert.That(request3.WasCalled, Is.True);
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
    public async Task Announced_OverflowRequestsBeyondHandlerLimitWithoutRetryEntries()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            TimeProvider.System,
            timeoutMs: AssertTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 2,
            overflowRequestLimit: 2);

        try
        {
            TestHandler source = new();
            AnnounceResult tracked1 = cache.Announced(1, source);
            AnnounceResult tracked2 = cache.Announced(2, source);
            AnnounceResult overflow1 = cache.Announced(3, source);
            AnnounceResult overflow2 = cache.Announced(4, source);
            AnnounceResult limited = cache.Announced(5, source);

            cache.Received(3);
            AnnounceResult afterRelease = cache.Announced(6, source);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracked1, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(tracked2, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(overflow1, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(overflow2, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(limited, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(afterRelease, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(cache.ResourcesInRetryQueue, Is.EqualTo(2));
                Assert.That(cache.OverflowRequestsInUse, Is.EqualTo(2));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }

        Assert.That(cache.OverflowRequestsInUse, Is.Zero);
    }

    [Test]
    public async Task Announced_OverflowCapacityDoesNotConsumeAnotherHandlersTrackedLimit()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            TimeProvider.System,
            timeoutMs: AssertTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 2,
            overflowRequestLimit: 2);

        try
        {
            TestHandler spammer = new();
            TestHandler other = new();

            for (int resourceId = 1; resourceId <= 4; resourceId++)
            {
                Assert.That(cache.Announced(resourceId, spammer), Is.EqualTo(AnnounceResult.RequestRequired));
            }

            AnnounceResult spammerLimited = cache.Announced(5, spammer);
            AnnounceResult otherTracked1 = cache.Announced(101, other);
            AnnounceResult otherTracked2 = cache.Announced(102, other);
            AnnounceResult otherOverflowLimited = cache.Announced(103, other);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(spammerLimited, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(otherTracked1, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(otherTracked2, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(otherOverflowLimited, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(cache.ResourcesInRetryQueue, Is.EqualTo(4));
                Assert.That(cache.OverflowRequestsInUse, Is.EqualTo(2));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_ConcurrentSameHandlerDoesNotExceedOverflowCapacity()
    {
        const int trackedLimit = 1;
        const int overflowLimit = 10;
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: AssertTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: trackedLimit,
            overflowRequestLimit: overflowLimit);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            TestHandler source = new();
            int admitted = 0;
            Parallel.For(0, 100, resourceId =>
            {
                if (cache.Announced(resourceId, source) is AnnounceResult.RequestRequired)
                {
                    Interlocked.Increment(ref admitted);
                }
            });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(admitted, Is.EqualTo(trackedLimit + overflowLimit));
                Assert.That(cache.ResourcesInRetryQueue, Is.EqualTo(trackedLimit));
                Assert.That(cache.OverflowRequestsInUse, Is.EqualTo(overflowLimit));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_QueueFullRequestsConsumeOverflowCapacity()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            TimeProvider.System,
            timeoutMs: AssertTimeoutMs,
            expiringQueueLimit: 0,
            token: cancellationTokenSource.Token,
            overflowRequestLimit: 2);

        try
        {
            AnnounceResult overflow1 = cache.Announced(1, new TestHandler());
            AnnounceResult overflow2 = cache.Announced(2, new TestHandler());
            AnnounceResult limited = cache.Announced(3, new TestHandler());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(overflow1, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(overflow2, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(limited, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(cache.ResourcesInRetryQueue, Is.Zero);
                Assert.That(cache.OverflowRequestsInUse, Is.EqualTo(2));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_ReceivedTrackedRequestsReleaseLogicalCapacityBeforeQueueExpiry()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            TimeProvider.System,
            timeoutMs: AssertTimeoutMs,
            expiringQueueLimit: 2,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 2,
            overflowRequestLimit: 2);

        try
        {
            TestHandler source = new();
            cache.Announced(1, source);
            cache.Announced(2, source);
            cache.Received(1);
            cache.Received(2);

            AnnounceResult refilled1 = cache.Announced(3, source);
            AnnounceResult refilled2 = cache.Announced(4, source);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(refilled1, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(refilled2, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(2));
                Assert.That(cache.ResourcesInRetryQueue, Is.EqualTo(4));
                Assert.That(cache.OverflowRequestsInUse, Is.Zero);
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task ExpiryQueue_RetryVisibilitySurvivesBoundedStaleBacklog()
    {
        const int staleResources = 32_769;
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            expiringQueueLimit: 40_000,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 40_000);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            TestHandler source = new();
            for (int resourceId = 0; resourceId < staleResources; resourceId++)
            {
                cache.Announced(resourceId, source);
                cache.Received(resourceId);
            }

            TestHandler retryHandler = new();
            cache.Announced(staleResources, source);
            cache.Announced(staleResources, retryHandler);

            Assert.That(cache.ResourcesInRetryQueue, Is.EqualTo(staleResources + 1));

            timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            cache.ProcessRetryTick();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.ResourcesInRetryQueue, Is.EqualTo(staleResources + 1 - 8192));
                Assert.That(retryHandler.WasCalled, Is.True);
            }

            timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 5));
            cache.ProcessRetryTick();

            Assert.That(retryHandler.WasCalled, Is.True);
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Concurrent_pending_limits_are_per_peer_and_reusable()
    {
        ManualTimeProvider time = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            NullLogManager.Instance, time, maxPendingResourcesPerHandler: 4);
        Assert.That(time.TimerCreated.IsCompleted, Is.True);
        ValueEqualBatchTestHandler[] peers = [new(), new()];
        for (int cycle = 0; cycle < 16; cycle++)
        {
            int[] accepted = new int[peers.Length];
            Parallel.For(0, 128, i =>
            {
                int peer = i % peers.Length;
                if (cache.Announced(cycle * 128 + i, peers[peer]) == AnnounceResult.RequestRequired)
                    Interlocked.Increment(ref accepted[peer]);
            });
            Assert.That(accepted, Is.EqualTo(new[] { 4, 4 }));

            Parallel.For(0, 128, i => cache.Received(cycle * 128 + i));
            Assert.That(cache.PendingHandlersInUse, Is.Zero);
        }
    }

    [Test]
    public void BatchedRetries_ReuseStorageAfterCallback([Values] bool throws)
    {
        TestHandler source = new();
        for (int cycle = 0; cycle < 8; cycle++)
        {
            BatchTestHandler alternate = new()
            {
                OnBatch = throws ? _ => throw new InvalidOperationException("Test callback failure") : null
            };
            int first = cycle * 2;
            for (int id = first; id < first + 2; id++)
            {
                _cache.Announced(id, source);
                _cache.Announced(id, alternate);
            }
            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            _cache.ProcessRetryTick();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(alternate.BatchResourceValues, Is.EqualTo(new[] { first, first + 1 }));
                Assert.That(alternate.HandleMessagesCallCount, Is.EqualTo(1));
            }
            for (int id = first; id < first + 2; id++) _cache.Received(id);
            Assert.That(_cache.PendingHandlersInUse, Is.Zero);
        }
    }

    [Test]
    public void BatchedRetries_ReentrantTickUsesIndependentStorage()
    {
        TestHandler source = new();
        BatchTestHandler nested = new();
        ResourceId[] outerResourcesAfterNestedTick = [];
        BatchTestHandler outer = new()
        {
            OnBatch = resources =>
            {
                _cache.Announced(2, source);
                _cache.Announced(2, nested);
                _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
                _cache.ProcessRetryTick();
                outerResourcesAfterNestedTick = resources.ToArray();
            }
        };
        _cache.Announced(1, source);
        _cache.Announced(1, outer);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outer.BatchResourceValues, Is.EqualTo(new[] { 1 }));
            Assert.That(nested.BatchResourceValues, Is.EqualTo(new[] { 2 }));
            Assert.That(outer.HandleMessagesCallCount, Is.EqualTo(1));
            Assert.That(nested.HandleMessagesCallCount, Is.EqualTo(1));
            Assert.That(outerResourcesAfterNestedTick, Is.EqualTo(new ResourceId[] { 1 }));
        }
        _cache.Received(1);
        _cache.Received(2);
        Assert.That(_cache.PendingHandlersInUse, Is.Zero);
    }

    [Test]
    public void BatchedRetries_BoundRetainedScratchAcrossChangingPeerGroups()
    {
        const int count = 256;
        TestHandler source = new();
        for (int id = 0; id < count; id++)
        {
            _cache.Announced(id, source);
            _cache.Announced(id, new BatchTestHandler());
        }
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();
        Assert.That(_cache.RetainedBatchCapacity, Is.InRange(count, 1024));
        for (int id = 0; id < count; id++) _cache.Received(id);

        BatchTestHandler shared = new();
        for (int id = count; id < count * 2; id++)
        {
            _cache.Announced(id, source);
            _cache.Announced(id, shared);
        }
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(shared.BatchResourceValues, Has.Length.EqualTo(count));
            Assert.That(_cache.RetainedBatchCapacity, Is.LessThanOrEqualTo(1024));
        }
    }

    [Test]
    public void CompletedRetries_ReleaseHandlerReferences()
    {
        WeakReference[] references = CompleteRetryWithTemporaryHandlers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.PendingHandlersInUse, Is.Zero);
            foreach (WeakReference reference in references) Assert.That(reference.IsAlive, Is.False);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference[] CompleteRetryWithTemporaryHandlers()
    {
        TestHandler source = new();
        BatchTestHandler alternate = new();
        _cache.Announced(1, source);
        _cache.Announced(1, alternate);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();
        _cache.Received(1);
        return [new(source), new(alternate)];
    }

    [Test]
    public void RequestStorage_ReleasesOversizedEmptyStripe([Values] bool receive)
    {
        const int count = 2048;
        TestHandler source = new();
        for (int i = 0; i < count; i++) _cache.Announced(i * 64, source);
        Assert.That(_cache.RetryRequestRetainedCapacity, Is.GreaterThanOrEqualTo(count));

        if (receive)
        {
            for (int i = 0; i < count; i++) _cache.Received(i * 64);
        }
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
            Assert.That(_cache.RetryRequestRetainedCapacity, Is.LessThanOrEqualTo(1024));
        }
    }

    [Test]
    public void StorageReuse_PreservesReannouncedRequestDeadlines([Values(1, 64)] int keyStride)
    {
        const int count = 129;
        int[] expected = new int[count];
        for (int i = 0; i < count; i++) expected[i] = i * keyStride;
        List<int> retried = [];
        TestHandler source = new();
        TestHandler alternate = new() { OnHandleMessage = message => retried.Add(message.Resource.Value) };

        for (int cycle = 0; cycle < 8; cycle++)
        {
            foreach (int resource in expected) _cache.Announced(resource, source);
            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2));
            foreach (int resource in expected)
            {
                _cache.Received(resource);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(_cache.Announced(resource, source), Is.EqualTo(AnnounceResult.RequestRequired));
                    Assert.That(_cache.Announced(resource, alternate), Is.EqualTo(AnnounceResult.Delayed));
                }
            }

            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2));
            _cache.ProcessRetryTick();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retried, Is.Empty);
                Assert.That(_cache.TrackedRequestsInUse, Is.EqualTo(count));
                Assert.That(_cache.ResourcesInRetryQueue, Is.EqualTo(count));
            }

            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2));
            _cache.ProcessRetryTick();
            Assert.That(retried, Is.EqualTo(expected));
            retried.Clear();

            foreach (int resource in expected) _cache.Received(resource);
            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            _cache.ProcessRetryTick();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_cache.TrackedRequestsInUse, Is.Zero);
                Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
            }
        }
    }

    [Test]
    public void StorageReuse_ConcurrentReceiveAndReannouncePreservesAccounting([Values(1, 64)] int keyStride)
    {
        Parallel.For(0, 4, worker =>
        {
            TestHandler source = new();
            for (int cycle = 0; cycle < 16; cycle++)
            {
                for (int i = 0; i < 128; i++)
                {
                    ResourceId resource = (worker * 128 + i) * keyStride;
                    Assert.That(_cache.Announced(resource, source), Is.EqualTo(AnnounceResult.RequestRequired));
                    Assert.That(_cache.Announced(resource, source), Is.EqualTo(AnnounceResult.Delayed));
                    _cache.Received(resource);
                }
            }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(_cache.ResourcesInRetryQueue, Is.EqualTo(4 * 16 * 128));
            Assert.That(_cache.PendingHandlersInUse, Is.Zero);
        }

        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
        _cache.ProcessRetryTick();
        Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
    }

    [Test]
    public async Task ExpiryQueue_ReusesBoundedStorageAcrossCumulativeChurn()
    {
        const int batchCount = 64;
        const int resourcesPerBatch = 256;
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            expiringQueueLimit: resourcesPerBatch,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: resourcesPerBatch);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            TestHandler source = new();
            for (int batch = 0; batch < batchCount; batch++)
            {
                for (int resource = 0; resource < resourcesPerBatch; resource++)
                {
                    int resourceId = batch * resourcesPerBatch + resource;
                    cache.Announced(resourceId, source);
                    cache.Received(resourceId);
                }

                Assert.That(cache.ResourcesInRetryQueue, Is.EqualTo(resourcesPerBatch));
                timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
                cache.ProcessRetryTick();
                Assert.That(cache.ResourcesInRetryQueue, Is.Zero);
            }

            Assert.That(cache.ExpiringQueueCapacity, Is.EqualTo(resourcesPerBatch));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public void ExpiryQueue_ReleasesOversizedStorageWithOrWithoutPendingRequests([Values] bool keepPending)
    {
        const int count = 16_385;
        TestHandler source = new();
        for (int i = 0; i < count; i++)
        {
            _cache.Announced(i, source);
            _cache.Received(i);
        }
        Assert.That(_cache.ExpiringQueueCapacity, Is.GreaterThanOrEqualTo(count));

        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2));
        if (keepPending) _cache.Announced(count, source);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs / 2));
        _cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.ResourcesInRetryQueue, Is.EqualTo(keepPending ? 1 : 0));
            Assert.That(_cache.ExpiringQueueCapacity, Is.GreaterThanOrEqualTo(count));
        }
        _cache.ProcessRetryTick();
        _cache.ProcessRetryTick();
        if (keepPending)
        {
            Assert.That(_cache.ExpiringQueueCapacity, Is.LessThan(count));
            _cache.Received(count);
            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            _cache.ProcessRetryTick();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
            Assert.That(_cache.ExpiringQueueCapacity, Is.LessThan(count));
        }
    }

    [Test]
    public void Expiring_queue_keeps_capacity_across_recurring_bursts()
    {
        const int count = 16_385;
        TestHandler source = new();
        for (int burst = 0; burst < 4; burst++)
        {
            for (int i = 0; i < count; i++)
            {
                int resource = burst * count + i;
                _cache.Announced(resource, source);
                _cache.Received(resource);
            }
            int capacity = _cache.ExpiringQueueCapacity;
            _timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            _cache.ProcessRetryTick();
            _cache.ProcessRetryTick();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
                Assert.That(_cache.ExpiringQueueCapacity, Is.EqualTo(capacity));
            }
        }
        _cache.ProcessRetryTick();
        Assert.That(_cache.ExpiringQueueCapacity, Is.LessThan(count));
    }

    [Test]
    public async Task Announced_OverflowResourceIsNotPromotedWhenTrackedSlotBecomesAvailable()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            TimeProvider.System,
            timeoutMs: AssertTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 1,
            overflowRequestLimit: 1);

        try
        {
            TestHandler source = new();
            cache.Announced(1, source);
            AnnounceResult overflow = cache.Announced(2, source);
            cache.Received(1);

            AnnounceResult sameHandler = cache.Announced(2, source);
            AnnounceResult otherHandler = cache.Announced(2, new TestHandler());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(overflow, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(sameHandler, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(otherHandler, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(cache.ResourcesInRetryQueue, Is.EqualTo(1));
                Assert.That(cache.OverflowRequestsInUse, Is.EqualTo(1));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_OverflowResourceIsRetainedForTimeoutAcrossGenerationBoundary()
    {
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 0,
            overflowRequestLimit: 2);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);

            timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs - 1));
            AnnounceResult initial = cache.Announced(1, new TestHandler());
            AnnounceResult duplicate = cache.Announced(1, new TestHandler());
            cache.Announced(2, new TestHandler());
            AnnounceResult full = cache.Announced(3, new TestHandler());

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(1));
            AnnounceResult acrossBoundary = cache.Announced(1, new TestHandler());
            AnnounceResult capacityAcrossBoundary = cache.Announced(3, new TestHandler());
            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
            AnnounceResult afterTimeout = cache.Announced(1, new TestHandler());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(initial, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(duplicate, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(full, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(acrossBoundary, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(capacityAcrossBoundary, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(afterTimeout, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(cache.OverflowRequestsInUse, Is.EqualTo(1));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_OverflowExpiryUsesMonotonicTime()
    {
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 0,
            overflowRequestLimit: 1);

        try
        {
            Assert.That(cache.Announced(1, new TestHandler()), Is.EqualTo(AnnounceResult.RequestRequired));

            timeProvider.JumpUtc(TimeSpan.FromDays(1));
            Assert.That(cache.Announced(2, new TestHandler()), Is.EqualTo(AnnounceResult.Delayed));

            timeProvider.JumpUtc(TimeSpan.FromDays(-2));
            timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs * 2));
            Assert.That(cache.Announced(2, new TestHandler()), Is.EqualTo(AnnounceResult.RequestRequired));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_LateOverflowRotationPreservesGenerationBoundary()
    {
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 0,
            overflowRequestLimit: 1);

        try
        {
            Assert.That(cache.Announced(1, new TestHandler()), Is.EqualTo(AnnounceResult.RequestRequired));

            timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs * 2 - 1));
            Assert.That(cache.Announced(1, new TestHandler()), Is.EqualTo(AnnounceResult.Delayed));

            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            Assert.That(cache.Announced(1, new TestHandler()), Is.EqualTo(AnnounceResult.RequestRequired));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Announced_OverflowRetainsCollidingHashesExactly()
    {
        const int overflowLimit = 32;
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<CollisionRequestMessage, CollisionResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 0,
            overflowRequestLimit: overflowLimit);

        try
        {
            CollisionHandler handler = new();
            for (int resourceId = 0; resourceId < overflowLimit; resourceId++)
            {
                Assert.That(cache.Announced(new CollisionResourceId(resourceId), handler), Is.EqualTo(AnnounceResult.RequestRequired));
            }

            for (int resourceId = 0; resourceId < overflowLimit; resourceId++)
            {
                Assert.That(cache.Announced(new CollisionResourceId(resourceId), handler), Is.EqualTo(AnnounceResult.Delayed));
            }

            AnnounceResult limited = cache.Announced(new CollisionResourceId(overflowLimit), handler);
            int retainedCapacityAtLimit = cache.OverflowRetainedCapacity;
            timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs * 2));
            AnnounceResult afterExpiry = cache.Announced(new CollisionResourceId(overflowLimit), handler);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(limited, Is.EqualTo(AnnounceResult.Delayed));
                Assert.That(afterExpiry, Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(cache.OverflowRequestsInUse, Is.EqualTo(1));
                Assert.That(cache.OverflowRetainedCapacity, Is.LessThan(retainedCapacityAtLimit));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task OverflowStorage_ReleasesLargeExpiredGenerationWithoutNewAnnouncement()
    {
        const int resourceCount = 2_048;
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<CollisionRequestMessage, CollisionResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 0,
            overflowRequestLimit: RetryCache<CollisionRequestMessage, CollisionResourceId>.DefaultOverflowRequestLimit);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            CollisionHandler handler = new();
            for (int resourceId = 0; resourceId < resourceCount; resourceId++)
            {
                Assert.That(cache.Announced(new CollisionResourceId(resourceId), handler), Is.EqualTo(AnnounceResult.RequestRequired));
            }

            int retainedCapacityAtPeak = cache.OverflowRetainedCapacity;
            Assert.That(retainedCapacityAtPeak, Is.GreaterThan(resourceCount));

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs * 2));

            Assert.That(() => cache.OverflowRequestsInUse, Is.Zero.After(AssertTimeoutMs, 10));
            Assert.That(() => cache.OverflowRetainedCapacity, Is.InRange(1, 1024).After(AssertTimeoutMs, 10));

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs * 2));
            Assert.That(() => cache.OverflowRetainedCapacity, Is.Zero.After(AssertTimeoutMs, 10));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    // GC pause accounting is process-wide and other tests force collections.
    [NonParallelizable]
    public async Task OverflowStorage_ReusesBoundedCapacityAcrossRepeatedBursts(
        [Values(1024, 8192, 32768)] int resourceCount, [Values(2, 6)] int idlePeriods)
    {
        ManualTimeProvider timeProvider = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            LimboLogs.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            requestingCacheSize: 0,
            maxPendingResourcesPerHandler: 0,
            overflowRequestLimit: RetryCache<ResourceRequestMessage, ResourceId>.DefaultOverflowRequestLimit);
        TestHandler handler = new();

        Assert.That(AnnounceBurst(), Is.EqualTo(resourceCount));
        Assert.That(AnnounceBurst(), Is.Zero);

        bool measuresAllocation = resourceCount < 32768;
        int bursts = 4;
        int measuredBursts = 0;
        for (int burst = 0; burst < bursts; burst++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs * 2));
            TimeSpan pausedBefore = GC.GetTotalPauseDuration();
            long before = GC.GetAllocatedBytesForCurrentThread();
            cache.ProcessRetryTick();
            int retainedAfterExpiry = cache.OverflowRetainedCapacity;
            int requested = AnnounceBurst();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            // A background GC pausing this thread can add up to one 8 KB allocation buffer to its counter without any
            // allocation, so a paused window only rules out regrowth and another burst takes its place.
            bool paused = GC.GetTotalPauseDuration() != pausedBefore;
            bool measured = measuresAllocation && burst > 0 && !paused;
            if (measured) measuredBursts++;
            else if (measuresAllocation && burst > 0 && bursts < 16) bursts++;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(requested, Is.EqualTo(resourceCount));
                Assert.That(cache.OverflowRequestsInUse, Is.EqualTo(resourceCount));
                if (resourceCount == 32768)
                    Assert.That(retainedAfterExpiry, Is.LessThanOrEqualTo(1024), "repeated oversized bursts must not bypass the retention cap");
                else if (burst > 0)
                    Assert.That(allocated, Is.LessThan(paused ? 16_000 : 4_000), "repeated bursts should reuse the grown set after the warm spare proves useful");
            }
            Assert.That(AnnounceBurst(), Is.Zero);
        }

        if (measuresAllocation)
            Assert.That(measuredBursts, Is.EqualTo(3), "GC pauses interrupted too many allocation measurements");

        timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs * idlePeriods));
        cache.ProcessRetryTick();
        Assert.That(cache.OverflowRequestsInUse, Is.Zero);
        if (idlePeriods == 2 && resourceCount < 32768)
            Assert.That(cache.OverflowRetainedCapacity, Is.GreaterThanOrEqualTo(resourceCount));
        else
            Assert.That(cache.OverflowRetainedCapacity, Is.LessThanOrEqualTo(1024), "a long idle interval must discard learned capacity");
        timeProvider.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs * 2));
        cache.ProcessRetryTick();
        Assert.That(cache.OverflowRetainedCapacity, Is.Zero, "retention must fall after a generation without demand");

        int AnnounceBurst()
        {
            int requested = 0;
            for (int i = 0; i < resourceCount; i++)
            {
                if (cache.Announced(i * 64, handler) == AnnounceResult.RequestRequired) requested++;
            }
            return requested;
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Announced_ConcurrentTrackedAndOverflowAdmissionRequestsResourceOnce()
    {
        const int resourceCount = 200;
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: AssertTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 1,
            overflowRequestLimit: 256);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            TestHandler cappedHandler = new();
            cache.Announced(0, cappedHandler);

            for (int resourceId = 1; resourceId <= resourceCount; resourceId++)
            {
                int requestsRequired = 0;
                Parallel.Invoke(
                    () => CountRequest(cache.Announced(resourceId, cappedHandler)),
                    () => CountRequest(cache.Announced(resourceId, new TestHandler())));

                Assert.That(requestsRequired, Is.EqualTo(1));
                cache.Received(resourceId);

                void CountRequest(AnnounceResult result)
                {
                    if (result is AnnounceResult.RequestRequired)
                    {
                        Interlocked.Increment(ref requestsRequired);
                    }
                }
            }

        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task Dispose_AllowsRepeatedDisposalAndLateReceipt()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            TimeProvider.System,
            timeoutMs: AssertTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 0,
            overflowRequestLimit: 1);

        cache.Announced(1, new TestHandler());
        Task firstDisposal = cache.DisposeAsync().AsTask();
        Task concurrentDisposal = cache.DisposeAsync().AsTask();
        await Task.WhenAll(firstDisposal, concurrentDisposal).WaitAsync(TimeSpan.FromMilliseconds(AssertTimeoutMs));

        Assert.DoesNotThrow(() => cache.Received(1));
        await Assert.DoesNotThrowAsync(async () => await cache.DisposeAsync());
    }

    [Test]
    public void Constructor_ValidatesRequestingCacheSizeBeforeStartingTimer()
    {
        ManualTimeProvider timeProvider = new();

        Assert.That(
            () => new RetryCache<ResourceRequestMessage, ResourceId>(
                TestLogManager.Instance,
                timeProvider,
                requestingCacheSize: -1),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(timeProvider.TimerCreated.IsCompleted, Is.False);
    }

    [Test]
    public async Task Constructor_AcceptsTokenInOriginalPositionalSignatures()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cacheWithDefaultHandlerLimit = new(
            TestLogManager.Instance,
            CacheTimeoutMs,
            1_024,
            100,
            4,
            cancellationTokenSource.Token);
        RetryCache<ResourceRequestMessage, ResourceId> cacheWithExplicitHandlerLimit = new(
            TestLogManager.Instance,
            CacheTimeoutMs,
            1_024,
            100,
            4,
            cancellationTokenSource.Token,
            32);

        await cancellationTokenSource.CancelAsync();
        await cacheWithDefaultHandlerLimit.DisposeAsync();
        await cacheWithExplicitHandlerLimit.DisposeAsync();
    }

    [Test]
    public async Task Dispose_WaitsForActiveReceiptBeforeDisposingLock()
    {
        using BlockingHashState state = new();
        BlockingResourceId resourceId = new(1, state);
        RetryCache<BlockingRequestMessage, BlockingResourceId> cache = new(
            TestLogManager.Instance,
            TimeProvider.System,
            timeoutMs: AssertTimeoutMs,
            maxPendingResourcesPerHandler: 0,
            overflowRequestLimit: 1);

        cache.Announced(resourceId, new BlockingHandler());
        state.Block();
        Task receipt = Task.Run(() => cache.Received(resourceId));
        Task disposal = Task.CompletedTask;
        try
        {
            Assert.That(state.Entered.Wait(AssertTimeoutMs), Is.True);
            disposal = cache.DisposeAsync().AsTask();
            await cache.DisposalReachedOperationBarrier.WaitAsync(TimeSpan.FromMilliseconds(AssertTimeoutMs));
            Assert.That(disposal.IsCompleted, Is.False);
        }
        finally
        {
            state.Continue.Set();
        }

        await Task.WhenAll(receipt, disposal).WaitAsync(TimeSpan.FromMilliseconds(AssertTimeoutMs));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Announced(resourceId, new BlockingHandler()), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.OverflowRetainedCapacity, Is.Zero);
        }
    }

    [Test]
    public async Task Announced_SameAlternateHandler_DoesNotExceedPendingResourceLimit()
    {
        ManualTimeProvider timeProvider = new();
        using CancellationTokenSource cancellationTokenSource = new();
        RetryCache<ResourceRequestMessage, ResourceId> cache = new(
            TestLogManager.Instance,
            timeProvider,
            timeoutMs: CacheTimeoutMs,
            token: cancellationTokenSource.Token,
            maxPendingResourcesPerHandler: 2);

        try
        {
            Assert.That(timeProvider.TimerCreated.IsCompleted, Is.True);
            TestHandler alternate = new();
            for (int resourceId = 1; resourceId <= 3; resourceId++)
            {
                cache.Announced(resourceId, new TestHandler());
                cache.Announced(resourceId, alternate);
            }

            timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));
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
        List<IMessageHandler<ResourceRequestMessage>> currentHandlers = TakeAll(bag, currentGeneration);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(staleAdd, Is.EqualTo(HandlerBagAddResult.Inactive));
            Assert.That(currentAdd, Is.EqualTo(HandlerBagAddResult.Added));
            Assert.That(currentHandlers, Is.EqualTo(new[] { handler1 }));
        }
    }

    [Test]
    public void HandlerBag_AcceptsLateCandidateAfterSpecificSelection()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        long generation = bag.Activate();

        TestHandler first = new(), later = new();
        bag.Add(first, 8, generation);
        Assert.That(bag.TryTakeSpecific(generation, first), Is.True);

        HandlerBagAddResult added = bag.Add(later, 8, generation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(added, Is.EqualTo(HandlerBagAddResult.Added));
            Assert.That(bag.TryTakeSpecific(generation, later), Is.True);
            Assert.That(bag.TryTakeSpecific(generation, first), Is.False);
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bag.Add(handler, 8, generation), Is.EqualTo(HandlerBagAddResult.Added));
            Assert.That(bag.Add(handler, 8, generation), Is.EqualTo(HandlerBagAddResult.Duplicate));
            Assert.That(bag.TryTakeSpecific(generation, handler), Is.True);
            Assert.That(bag.TryTakeSpecific(generation, handler), Is.False);
        }
    }

    [Test]
    public void HandlerBag_AcceptsLateDistinctHandlersWithinLifetimeBound()
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        long generation = bag.Activate();

        TestHandler first = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bag.Add(first, 3, generation), Is.EqualTo(HandlerBagAddResult.Added));
            Assert.That(bag.Add(new TestHandler(), 3, generation), Is.EqualTo(HandlerBagAddResult.Added));
            Assert.That(bag.TryTakeSpecific(generation, first), Is.True);
            Assert.That(bag.Add(new TestHandler(), 3, generation), Is.EqualTo(HandlerBagAddResult.Added));
            Assert.That(bag.Add(new TestHandler(), 3, generation), Is.EqualTo(HandlerBagAddResult.Full));
        }
    }

    [Test]
    public void HandlerBag_growth_and_reuse_preserve_pending_handlers([Values(1, 4, 9)] int count)
    {
        HandlerBag<ResourceRequestMessage> bag = new();
        TestHandler[] handlers = new TestHandler[count + 1];
        for (int i = 0; i < handlers.Length; i++) handlers[i] = new();

        for (int cycle = 0; cycle < 2; cycle++)
        {
            long generation = bag.Activate();
            for (int i = 0; i < count; i++)
            {
                Assert.That(bag.Add(handlers[i], handlers.Length, generation), Is.EqualTo(HandlerBagAddResult.Added));
            }

            IMessageHandler<ResourceRequestMessage> taken = handlers[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bag.TryTakeSpecific(generation, taken), Is.True);
                Assert.That(bag.Add(taken, handlers.Length, generation), Is.EqualTo(HandlerBagAddResult.Duplicate));
                Assert.That(bag.Add(handlers[count], handlers.Length, generation), Is.EqualTo(HandlerBagAddResult.Added));
            }
            List<IMessageHandler<ResourceRequestMessage>> remaining = TakeAll(bag, generation);
            remaining.Add(taken);
            Assert.That(remaining, Is.EquivalentTo(handlers));
            bag.Reset();
        }
    }

    [Test]
    public void Announced_RetryHandlerReceivesCorrectResourceId()
    {
        int receivedResourceId = -1;
        TestHandler retryHandler = new() { OnHandleMessage = msg => receivedResourceId = msg.Resource.Value };

        _cache.Announced(42, new TestHandler());
        _cache.Announced(42, retryHandler);
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(CacheTimeoutMs));

        Assert.That(() => receivedResourceId, Is.EqualTo(42).After(AssertTimeoutMs, 100));
    }

    private static List<IMessageHandler<ResourceRequestMessage>> TakeAll(HandlerBag<ResourceRequestMessage> bag, long generation)
    {
        List<IMessageHandler<ResourceRequestMessage>> handlers = [];
        CollectHandlerBagProcessor processor = new(handlers);
        bag.Deactivate(generation, ref processor);
        return handlers;
    }

    private static int GetTotalCalls(TestHandler[] handlers)
    {
        int total = 0;
        for (int i = 0; i < handlers.Length; i++)
        {
            total += handlers[i].HandleMessageCallCount;
        }

        return total;
    }

    private readonly struct NoopHandlerBagProcessor : IHandlerBagProcessor<ResourceRequestMessage>
    {
        public void Process(IMessageHandler<ResourceRequestMessage> handler) { }
    }

    private readonly struct CollectHandlerBagProcessor(List<IMessageHandler<ResourceRequestMessage>> handlers)
        : IHandlerBagProcessor<ResourceRequestMessage>
    {
        public void Process(IMessageHandler<ResourceRequestMessage> handler) => handlers.Add(handler);
    }

    private sealed class PeerBatchHandler(PublicKey id) : BatchTestHandler, ITxPoolPeer
    {
        public PublicKey Id { get; } = id;
        public ulong HeadNumber { get; set; }
        public void SendNewTransactions(ReadOnlySpan<Transaction> transactions, bool sendFullTx) => throw new NotSupportedException();
    }

    private sealed class RearmingHandler(RetryCache<ResourceRequestMessage, ResourceId> cache)
        : IBatchMessageHandler<ResourceRequestMessage, ResourceId>
    {
        internal int Resources;
        public void HandleMessage(ResourceRequestMessage message) => cache.TryDeferRequest(message.Resource, this);
        public void HandleMessages(ReadOnlySpan<ResourceId> resources)
        {
            Resources += resources.Length;
            foreach (ResourceId resource in resources) cache.TryDeferRequest(resource, this);
        }
    }

    [Test]
    public async Task Local_refusal_count_bounds_actual_attempts([Values] bool overflow)
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock,
            maxPendingResourcesPerHandler: overflow ? 1 : 4096, overflowRequestLimit: overflow ? 4096 : 0);
        BatchTestHandler source = new();
        if (overflow) cache.Announced(-1, source);
        cache.Announced(0, source);
        Assert.That(cache.TryDeferRequest(0, source), Is.True);
        int attempts = 0;
        bool deferred = false;
        source.OnBatch = resources =>
        {
            attempts++;
            deferred = cache.TryDeferRequest(resources[0], source);
        };
        for (int attempt = 0; attempt < 48; attempt++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            cache.ProcessRetryTick();
            Assert.That(deferred, Is.EqualTo(attempts < 48));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempts, Is.EqualTo(48));
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(overflow ? 1 : 0));
            Assert.That(cache.OverflowRequestsInUse, Is.Zero);
            Assert.That(cache.ReadyAssociationsInUse, Is.Zero);
        }
        cache.Received(-1);
        clock.Advance(TimeSpan.FromSeconds(3.5));
        cache.ProcessRetryTick();
        Assert.That(cache.RawResourcesInRetryQueue, Is.Zero);
    }

    [Test]
    public async Task Duplicate_local_notifications_do_not_spend_the_attempt_bound()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new();
        cache.Announced(0, source);
        for (int duplicate = 0; duplicate < 100; duplicate++) Assert.That(cache.TryDeferRequest(0, source), Is.True);
        bool allDeferralsRetained = true;
        source.OnBatch = resources => allDeferralsRetained &= cache.TryDeferRequest(resources[0], source);
        for (int attempt = 0; attempt < 47; attempt++) cache.ProcessRetryTick();
        Assert.That(cache.TryDeferRequest(0, source), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(allDeferralsRetained, Is.True);
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(1));
            Assert.That(source.BatchResourceValues, Has.Length.EqualTo(47));
        }
    }

    [Test]
    public async Task Local_deadline_retires_an_undispatched_backlog_after_49_callbacks([Values] bool overflow)
    {
        const int count = 4096;
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock,
            maxPendingResourcesPerHandler: overflow ? 1 : count, overflowRequestLimit: overflow ? count : 0);
        BatchTestHandler source = new();
        if (overflow) cache.Announced(-1, source);
        source.OnBatch = resources =>
        {
            foreach (ResourceId resource in resources) cache.TryDeferRequest(resource, source);
        };
        clock.Advance(TimeSpan.FromMilliseconds(1));
        for (int resource = 0; resource < count; resource++)
        {
            cache.Announced(resource, source);
            Assert.That(cache.TryDeferRequest(resource, source), Is.True);
        }
        for (int callback = 1; callback <= 49; callback++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(callback == 1 ? 699 : 700));
            cache.ProcessRetryTick();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(cache.OverflowRequestsInUse, Is.Zero);
            Assert.That(cache.ReadyAssociationsInUse, Is.Zero);
            Assert.That(cache.RawResourcesInRetryQueue, Is.Zero);
        }
    }

    [Test]
    public async Task Stable_peer_identity_shares_pending_admission()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock,
            maxPendingResourcesPerHandler: 1);
        PeerBatchHandler first = new(TestItem.PublicKeyA);
        PeerBatchHandler replacement = new(new PublicKey(TestItem.PublicKeyA.Bytes));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Announced(0, first), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.Announced(1, replacement), Is.EqualTo(AnnounceResult.Delayed));
        }
        cache.Received(0);
        Assert.That(cache.Announced(1, replacement), Is.EqualTo(AnnounceResult.RequestRequired));
        cache.Received(1);
        Assert.That(cache.PendingHandlersInUse, Is.Zero);
    }

    [Test]
    public async Task Dispatch_caps_each_identity_even_when_another_has_one_hash()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler a = new(), b = new();
        AddLocallyDeferred(cache, a, 0, 512);
        AddLocallyDeferred(cache, b, 512, 1);
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.BatchResourceValues, Has.Length.EqualTo(256));
            Assert.That(b.BatchResourceValues, Has.Length.EqualTo(1));
        }
    }

    [Test]
    public async Task A_refusing_identity_cannot_delay_256_honest_hashes_beyond_one_callback()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler refusing = new(), honest = new();
        AddLocallyDeferred(cache, refusing, 0, 4096);
        AddLocallyDeferred(cache, honest, 4096, 256);
        bool refusalsRetained = true;
        refusing.OnBatch = resources =>
        {
            foreach (ResourceId resource in resources) refusalsRetained &= cache.TryDeferRequest(resource, refusing);
        };
        honest.OnBatch = resources =>
        {
            foreach (ResourceId resource in resources) cache.Received(resource);
        };
        for (int callback = 1; callback <= 49; callback++)
        {
            int before = refusing.BatchResourceValues.Length;
            clock.Advance(TimeSpan.FromMilliseconds(700));
            cache.ProcessRetryTick();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(refusing.BatchResourceValues.Length - before, Is.LessThanOrEqualTo(256));
                Assert.That(honest.BatchResourceValues, Is.EquivalentTo(Enumerable.Range(4096, 256)));
                Assert.That(honest.HandleMessagesCallCount, Is.EqualTo(1));
                Assert.That(refusalsRetained, Is.True);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(cache.RawResourcesInRetryQueue, Is.Zero);
            Assert.That(cache.HandlerReferences(refusing), Is.EqualTo((0, 0)));
            Assert.That(cache.HandlerReferences(honest), Is.EqualTo((0, 0)));
        }
    }

    [TestCase(512, 256, 256)]
    [TestCase(400, 400, 250)]
    public async Task Two_peer_assignment_preserves_feasible_shared_hash_dispatch(int aCount, int bCount, int shared)
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new(), a = new(), b = new();
        for (int resource = 0; resource < aCount; resource++)
        {
            cache.Announced(resource, source);
            cache.Announced(resource, a);
            if (resource < shared) cache.Announced(resource, b);
        }
        for (int resource = aCount; resource < aCount + bCount - shared; resource++)
        {
            cache.Announced(resource, source);
            cache.Announced(resource, b);
        }
        clock.Advance(TimeSpan.FromSeconds(3.5));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.BatchResourceValues, Has.Length.EqualTo(256));
            Assert.That(b.BatchResourceValues, Has.Length.EqualTo(256));
            Assert.That(a.BatchResourceValues.Concat(b.BatchResourceValues).Distinct().Count(), Is.EqualTo(512));
        }
    }

    [Test]
    public async Task Sixteen_ready_identities_get_32_hashes_each()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler[] handlers = Enumerable.Range(0, 16).Select(static _ => new BatchTestHandler()).ToArray();
        for (int peer = 0; peer < handlers.Length; peer++) AddLocallyDeferred(cache, handlers[peer], peer * 512, 512);
        cache.ProcessRetryTick();
        Assert.That(handlers.Select(static handler => handler.BatchResourceValues.Length), Is.All.EqualTo(32));
    }

    [Test]
    public async Task Two_full_backlogs_drain_once_after_a_10_second_pause()
    {
        const int perPeer = 8192;
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock,
            overflowRequestLimit: perPeer);
        BatchTestHandler a = new(), b = new();
        bool paused = true;
        bool everySendUnique = true;
        HashSet<int> sent = [];
        void Handle(BatchTestHandler source, ReadOnlySpan<ResourceId> resources)
        {
            foreach (ResourceId resource in resources)
            {
                if (paused) cache.TryDeferRequest(resource, source);
                else
                {
                    everySendUnique &= sent.Add(resource.Value);
                    cache.Received(resource);
                }
            }
        }
        a.OnBatch = resources => Handle(a, resources);
        b.OnBatch = resources => Handle(b, resources);
        AddLocallyDeferred(cache, a, 0, perPeer);
        AddLocallyDeferred(cache, b, perPeer, perPeer);
        for (int callback = 0; callback < 14; callback++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(700));
            cache.ProcessRetryTick();
        }
        clock.Advance(TimeSpan.FromMilliseconds(200));
        paused = false;
        for (int callback = 0; callback < 32; callback++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(700));
            cache.ProcessRetryTick();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(everySendUnique, Is.True);
            Assert.That(sent, Has.Count.EqualTo(perPeer * 2));
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(cache.OverflowRequestsInUse, Is.Zero);
            Assert.That(cache.ReadyAssociationsInUse, Is.Zero);
        }
    }

    private static void AddLocallyDeferred(RetryCache<ResourceRequestMessage, ResourceId> cache,
        IMessageHandler<ResourceRequestMessage> handler, int start, int count)
    {
        for (int resource = start; resource < start + count; resource++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.Announced(resource, handler), Is.EqualTo(AnnounceResult.RequestRequired));
                Assert.That(cache.TryDeferRequest(resource, handler), Is.True);
            }
        }
    }

    [Test]
    public async Task Waiting_batch_keeps_its_selected_alternate_admission_slot()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock,
            maxPendingResourcesPerHandler: 1, overflowRequestLimit: 0);
        BatchTestHandler a = new(), b = new();
        cache.Announced(0, new TestHandler());
        cache.Announced(0, a);
        cache.Announced(1, new TestHandler());
        cache.Announced(1, b);
        AnnounceResult competingAdmission = AnnounceResult.RequestRequired;
        a.OnBatch = resources =>
        {
            competingAdmission = cache.Announced(99, b);
            cache.Received(resources[0]);
        };
        b.OnBatch = resources => cache.Received(resources[0]);
        clock.Advance(TimeSpan.FromSeconds(3.5));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(competingAdmission, Is.EqualTo(AnnounceResult.Delayed));
            Assert.That(a.BatchResourceValues, Is.EqualTo(new[] { 0 }));
            Assert.That(b.BatchResourceValues, Is.EqualTo(new[] { 1 }));
            Assert.That(cache.RawResourcesInRetryQueue, Is.Zero);
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(cache.HandlerReferences(b), Is.EqualTo((0, 0)));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Announced(100, b), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.Announced(101, b), Is.EqualTo(AnnounceResult.Delayed));
        }
    }

    [Test]
    public async Task Batch_dispatch_revalidates_a_generation_replaced_by_an_earlier_callback()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler a = new(), b = new();
        cache.Announced(0, new TestHandler());
        cache.Announced(0, a);
        cache.Announced(1, new TestHandler());
        cache.Announced(1, b);
        AnnounceResult replacementAdmission = AnnounceResult.Delayed;
        a.OnBatch = _ =>
        {
            cache.Received(1);
            replacementAdmission = cache.Announced(1, b);
        };
        clock.Advance(TimeSpan.FromSeconds(3.5));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replacementAdmission, Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(a.BatchResourceValues, Is.EqualTo(new[] { 0 }));
            Assert.That(b.BatchResourceValues, Is.Empty);
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(2));
            Assert.That(cache.RetainedAssociationsInUse, Is.Zero);
        }
    }

    [Test]
    public async Task A_running_retry_callback_cannot_attribute_or_defer_its_replacement_generation()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new();
        cache.Announced(1, source);
        AddLocallyDeferred(cache, source, 0, 1);
        AnnounceResult replacementAdmission = AnnounceResult.Delayed;
        long capturedGeneration = -1;
        bool replacementDeferred = true;
        source.OnBatch = resources =>
        {
            cache.Received(resources[0]);
            replacementAdmission = cache.Announced(resources[0], source);
            RetryRequestToken<ResourceId>[] tokens = new RetryRequestToken<ResourceId>[1];
            cache.CaptureRequestTokens(resources, source, tokens);
            capturedGeneration = tokens[0].Generation;
            replacementDeferred = cache.TryDeferRequest(resources[0], source);
        };
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replacementAdmission, Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(capturedGeneration, Is.Zero);
            Assert.That(replacementDeferred, Is.False);
            Assert.That(source.BatchResourceValues, Is.EqualTo(new[] { 0 }));
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(2));
            Assert.That(cache.RetainedAssociationsInUse, Is.Zero);
        }
    }

    [Test]
    public async Task Unanswered_alternate_preserves_the_original_local_source()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new(), alternate = new();
        AddLocallyDeferred(cache, source, 0, 1);
        cache.Announced(0, alternate);
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alternate.BatchResourceValues, Is.EqualTo(new[] { 0 }));
            Assert.That(source.BatchResourceValues, Is.Empty);
        }
        clock.Advance(TimeSpan.FromSeconds(3.5));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.BatchResourceValues, Is.EqualTo(new[] { 0 }));
            Assert.That(alternate.BatchResourceValues, Is.EqualTo(new[] { 0 }));
        }
    }

    [Test]
    public async Task Successful_overflow_send_retains_its_source_slot_until_receipt_or_timeout([Values] bool receipt)
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock,
            maxPendingResourcesPerHandler: 1, overflowRequestLimit: 4096);
        BatchTestHandler source = new();
        cache.Announced(-1, source);
        AddLocallyDeferred(cache, source, 0, 1);
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.HandlerReferences(source), Is.EqualTo((1, 1)));
            Assert.That(cache.OverflowRequestsInUse, Is.EqualTo(1));
        }
        if (receipt) cache.Received(0);
        else
        {
            clock.Advance(TimeSpan.FromSeconds(3.5));
            cache.ProcessRetryTick();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.HandlerReferences(source).Overflow, Is.Zero);
            Assert.That(cache.OverflowRequestsInUse, Is.Zero);
            Assert.That(source.BatchResourceValues, Is.EqualTo(new[] { 0 }));
        }
    }

    [Test]
    public async Task Simultaneous_expiry_keeps_charged_entries_until_each_bounded_cleanup()
    {
        const int peers = 16, perPeer = 8192, cleanup = 8192;
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock,
            overflowRequestLimit: 500_000);
        BatchTestHandler[] sources = Enumerable.Range(0, peers).Select(static _ => new BatchTestHandler()).ToArray();
        for (int peer = 0; peer < peers; peer++) AddLocallyDeferred(cache, sources[peer], peer * perPeer, perPeer);
        clock.Advance(TimeSpan.FromSeconds(33.6));
        for (int callback = 1; callback <= peers; callback++)
        {
            cache.ProcessRetryTick();
            int retained = peers * perPeer - callback * cleanup;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.TrackedRequestsInUse + cache.OverflowRequestsInUse, Is.EqualTo(retained));
                Assert.That(cache.RetainedAssociationsInUse, Is.EqualTo(retained));
                Assert.That(cache.RawResourcesInRetryQueue, Is.EqualTo(retained));
                Assert.That(cache.HandlerReferences(sources[callback - 1]), Is.EqualTo((0, 0)));
                Assert.That(sources.Select(static source => source.BatchResourceValues.Length), Is.All.Zero);
            }
            clock.Advance(TimeSpan.FromMilliseconds(700));
        }
    }

    [Test]
    public async Task Positive_send_confirmation_obeys_the_attribution_deadline([Values(3499, 3500, 3501)] int elapsedMs)
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new();
        AddLocallyDeferred(cache, source, 1, 1);
        cache.Announced(0, source);
        RetryRequestToken<ResourceId>[] tokens = new RetryRequestToken<ResourceId>[1];
        cache.CaptureRequestTokens(new ResourceId[] { 0 }, source, tokens);
        object progress = cache.CaptureUsefulProgress(source, tokens[0]);
        Assert.That(progress, Is.Not.Null);
        cache.Received(0);
        cache.RecordUsefulProgress(progress, tokens[0]);
        clock.Advance(TimeSpan.FromMilliseconds(elapsedMs));
        cache.CompleteRequest(tokens[0].Confirmation, sent: true);
        clock.Advance(TimeSpan.FromMilliseconds(33_700 - elapsedMs));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(elapsedMs < 3500 ? 1 : 0));
            Assert.That(source.BatchResourceValues, Is.EqualTo(elapsedMs < 3500 ? new[] { 1 } : Array.Empty<int>()));
        }
    }

    [Test]
    public async Task Reversed_send_confirmations_cannot_refresh_with_an_older_payment()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new();
        AddLocallyDeferred(cache, source, 1, 1);
        RetryRequestToken<ResourceId>[] older = PendingPayment(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        RetryRequestToken<ResourceId>[] newer = PendingPayment(2);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        cache.CompleteRequest(newer[0].Confirmation, sent: true);
        clock.Advance(TimeSpan.FromMilliseconds(2399));
        cache.CompleteRequest(older[0].Confirmation, sent: true);
        clock.Advance(TimeSpan.FromMilliseconds(31_301));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(source.BatchResourceValues, Is.Empty);
        }

        RetryRequestToken<ResourceId>[] PendingPayment(int resource)
        {
            cache.Announced(resource, source);
            RetryRequestToken<ResourceId>[] tokens = new RetryRequestToken<ResourceId>[1];
            cache.CaptureRequestTokens(new ResourceId[] { resource }, source, tokens);
            object progress = cache.CaptureUsefulProgress(source, tokens[0]);
            Assert.That(progress, Is.Not.Null);
            cache.Received(resource);
            cache.RecordUsefulProgress(progress, tokens[0]);
            return tokens;
        }
    }

    [Test]
    public async Task Pending_send_payment_does_not_renew_a_later_local_admission([Values] bool sent)
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new();
        cache.Announced(0, source);
        AddLocallyDeferred(cache, source, 1, 1);
        RetryRequestToken<ResourceId>[] tokens = new RetryRequestToken<ResourceId>[1];
        cache.CaptureRequestTokens(new ResourceId[] { 0 }, source, tokens);
        object progress = cache.CaptureUsefulProgress(source, tokens[0]);
        Assert.That(progress, Is.Not.Null);
        cache.Received(0);
        cache.RecordUsefulProgress(progress, tokens[0]);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        AddLocallyDeferred(cache, source, 2, 1);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        cache.CompleteRequest(tokens[0].Confirmation, sent);
        clock.Advance(TimeSpan.FromSeconds(32.7));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(sent ? 1 : 0));
            Assert.That(source.BatchResourceValues, Is.EqualTo(sent ? new[] { 1 } : Array.Empty<int>()));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        cache.ProcessRetryTick();
        Assert.That(cache.TrackedRequestsInUse, Is.Zero);
    }

    [Test]
    public async Task Late_useful_progress_cannot_revive_a_generation_awaiting_physical_cleanup()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new();
        AddLocallyDeferred(cache, source, 0, 1);
        clock.Advance(TimeSpan.FromSeconds(34));
        PayUsefulReply(cache, source, 1);
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(cache.HandlerReferences(source), Is.EqualTo((0, 0)));
            Assert.That(source.BatchResourceValues, Is.Empty);
        }
    }

    [Test]
    public async Task Request_expires_after_its_last_retry_association_is_selected()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock, timeoutMs: CacheTimeoutMs);
        TestHandler source = new();
        BatchTestHandler alternate = new();
        const int batchSize = RetryCache<ResourceRequestMessage, ResourceId>.MaxRetryResourcesPerHandlerPerTick;
        for (int i = 0; i <= batchSize; i++)
        {
            cache.Announced(i, source);
            cache.Announced(i, alternate);
        }

        clock.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs + 1));
        cache.ProcessRetryTick();
        Assert.That(cache.RetainedAssociationsInUse, Is.EqualTo(1));

        cache.ProcessRetryTick();
        Assert.That(cache.RetainedAssociationsInUse, Is.Zero);
        clock.Advance(TimeSpan.FromMilliseconds(CacheTimeoutMs + 1));
        cache.ProcessRetryTick();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(alternate.BatchResourceValues, Has.Length.EqualTo(batchSize + 1));
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(cache.RawResourcesInRetryQueue, Is.Zero);
            Assert.That(cache.HandlerReferences(source), Is.EqualTo((0, 0)));
        }
    }

    [Test]
    public async Task Local_expiry_racing_receipt_keeps_the_reannounced_generation()
    {
        HookedClock clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new();
        AddLocallyDeferred(cache, source, 0, 1);
        clock.Advance(TimeSpan.FromSeconds(34));
        Task receipt = Task.CompletedTask;
        bool receiptRemovedEntry = false;
        AnnounceResult reannounced = AnnounceResult.Delayed;
        // Runs on the tick thread inside the expiry lock: the receipt removes the entry but cannot retire its state yet.
        clock.OnNextTimestamp(() =>
        {
            receipt = Task.Run(() => cache.Received(0));
            receiptRemovedEntry = SpinWait.SpinUntil(() => cache.TrackedRequestsInUse == 0, TimeSpan.FromSeconds(10));
            reannounced = cache.Announced(0, source);
        });
        cache.ProcessRetryTick();
        await receipt;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiptRemovedEntry, Is.True);
            Assert.That(reannounced, Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(1));
            Assert.That(cache.HandlerReferences(source), Is.EqualTo((1, 0)));
        }
    }

    private sealed class HookedClock : TimeProvider
    {
        private readonly ManualTimeProvider _clock = new();
        private Action _onNextTimestamp;

        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);
        public void OnNextTimestamp(Action action) => Volatile.Write(ref _onNextTimestamp, action);
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) =>
            _clock.CreateTimer(callback, state, dueTime, period);

        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref _onNextTimestamp, null)?.Invoke();
            return _clock.GetTimestamp();
        }
    }

    [Test]
    public async Task Continuous_progress_bridges_lazy_inspection_but_an_exact_window_gap_does_not([Values] bool missedWindow)
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler source = new();
        source.OnBatch = resources =>
        {
            foreach (ResourceId resource in resources) cache.TryDeferRequest(resource, source);
        };
        AddLocallyDeferred(cache, source, 0, 1);
        for (int payment = 1; payment <= 3; payment++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            PayUsefulReply(cache, source, payment);
        }
        clock.Advance(TimeSpan.FromSeconds(missedWindow ? 33.6 : 30));
        PayUsefulReply(cache, source, 4);
        clock.Advance(TimeSpan.FromSeconds(1));
        cache.ProcessRetryTick();
        Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(missedWindow ? 0 : 1));
        if (!missedWindow)
        {
            clock.Advance(TimeSpan.FromSeconds(31.9));
            cache.ProcessRetryTick();
            Assert.That(cache.TrackedRequestsInUse, Is.EqualTo(1));
            clock.Advance(TimeSpan.FromSeconds(0.7));
            cache.ProcessRetryTick();
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
        }
    }

    [Test]
    public async Task Whole_generation_expiry_releases_normal_and_overflow_sources_with_remaining_alternates()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock,
            maxPendingResourcesPerHandler: 1, overflowRequestLimit: 1);
        BatchTestHandler source = new();
        AddLocallyDeferred(cache, source, 0, 2);
        BatchTestHandler[] alternates = Enumerable.Range(0, 8).Select(static _ => new BatchTestHandler()).ToArray();
        for (int alternate = 0; alternate < alternates.Length; alternate++)
        {
            cache.Announced(alternate / 4, alternates[alternate]);
        }
        Assert.That(cache.HandlerReferences(source), Is.EqualTo((1, 1)));
        clock.Advance(TimeSpan.FromSeconds(33.6));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.HandlerReferences(source), Is.EqualTo((0, 0)));
            Assert.That(cache.TrackedRequestsInUse, Is.Zero);
            Assert.That(cache.OverflowRequestsInUse, Is.Zero);
            Assert.That(cache.RetainedAssociationsInUse, Is.Zero);
            Assert.That(cache.RawResourcesInRetryQueue, Is.Zero);
            foreach (BatchTestHandler alternate in alternates) Assert.That(cache.HandlerReferences(alternate), Is.EqualTo((0, 0)));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Announced(2, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.Announced(3, source), Is.EqualTo(AnnounceResult.RequestRequired));
            Assert.That(cache.TryDeferRequest(3, source), Is.True);
        }
    }

    [Test]
    public async Task Wide_ready_identity_sets_make_progress_and_rotate_the_cursor()
    {
        const int count = 20_000;
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        BatchTestHandler[] sources = Enumerable.Range(0, count).Select(static _ => new BatchTestHandler()).ToArray();
        for (int source = 0; source < count; source++)
        {
            BatchTestHandler handler = sources[source];
            handler.OnBatch = resources => cache.TryDeferRequest(resources[0], handler);
            AddLocallyDeferred(cache, handler, source, 1);
        }
        cache.ProcessRetryTick();
        Assert.That(sources.Count(static source => source.HandleMessagesCallCount == 1), Is.EqualTo(512));
        cache.ProcessRetryTick();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sources.Count(static source => source.HandleMessagesCallCount == 1), Is.EqualTo(1024));
            Assert.That(sources.Select(static source => source.HandleMessagesCallCount), Is.All.LessThanOrEqualTo(1));
            Assert.That(cache.RetainedSelectionCapacity, Is.LessThanOrEqualTo(16_384));
        }
    }

    [Test]
    public async Task Repeated_local_rearming_reuses_bounded_selection_and_association_storage()
    {
        ManualTimeProvider clock = new();
        await using RetryCache<ResourceRequestMessage, ResourceId> cache = new(TestLogManager.Instance, clock);
        RearmingHandler[] sources = Enumerable.Range(0, 16).Select(_ => new RearmingHandler(cache)).ToArray();
        for (int source = 0; source < sources.Length; source++) AddLocallyDeferred(cache, sources[source], source * 64, 64);
        for (int warmup = 0; warmup < 4; warmup++) cache.ProcessRetryTick();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int callback = 0; callback < 20; callback++) cache.ProcessRetryTick();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocated, Is.LessThan(20 * 2048));
            Assert.That(sources.Select(static source => source.Resources), Is.All.EqualTo(24 * 32));
            Assert.That(cache.RetainedSelectionCapacity, Is.LessThanOrEqualTo(16_384));
        }
    }

    private static void PayUsefulReply(RetryCache<ResourceRequestMessage, ResourceId> cache,
        IMessageHandler<ResourceRequestMessage> source, int resource)
    {
        Assert.That(cache.Announced(resource, source), Is.EqualTo(AnnounceResult.RequestRequired));
        RetryRequestToken<ResourceId>[] tokens = [default];
        cache.CaptureRequestTokens([resource], source, tokens);
        cache.CompleteRequest(tokens[0].Confirmation, sent: true);
        object progress = cache.CaptureUsefulProgress(source, tokens[0]);
        Assert.That(progress, Is.Not.Null);
        cache.Received(resource);
        cache.RecordUsefulProgress(progress, tokens[0]);
    }
}
