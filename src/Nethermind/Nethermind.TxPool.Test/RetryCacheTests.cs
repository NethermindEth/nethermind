// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.TxPool.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class RetryCacheTests
{
    public readonly struct ResourceRequestMessage : INew<int, ResourceRequestMessage>
    {
        public int Resource { get; init; }
        public static ResourceRequestMessage New(int resourceId) => new() { Resource = resourceId };
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

        public void HandleMessage(ResourceRequestMessage message)
        {
            Interlocked.Increment(ref _handleMessageCallCount);
            OnHandleMessage?.Invoke(message);
        }
    }

    private CancellationTokenSource _cancellationTokenSource;
    private RetryCache<ResourceRequestMessage, int> _cache;

    private readonly int Timeout = 10000;

    [SetUp]
    public void Setup()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _cache = new(TestLogManager.Instance, timeoutMs: Timeout / 2, token: _cancellationTokenSource.Token);
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

        _cache.Announced(1, request1);
        _cache.Announced(1, request2);

        Assert.That(() => request2.WasCalled, Is.True.After(Timeout, 100));
        Assert.That(request1.WasCalled, Is.False);
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

        Assert.That(() => request2.WasCalled, Is.True.After(Timeout, 100));
        Assert.That(() => request4.WasCalled, Is.True.After(Timeout, 100));
        Assert.That(request1.WasCalled, Is.False);
        Assert.That(request3.WasCalled, Is.False);
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
        TestHandler request = new();

        _cache.Announced(1, request);
        _cache.Announced(1, request);
        _cache.Received(1);

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        Assert.That(request.WasCalled, Is.False);
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

        await Task.Delay(Timeout * 2, _cancellationTokenSource.Token);

        Assert.That(_cache.ResourcesInRetryQueue, Is.Zero);
    }

    [Test]
    [Retry(3)]
    public void RetryExecution_HandlesExceptions()
    {
        TestHandler faultyRequest = new() { OnHandleMessage = _ => throw new InvalidOperationException("Test exception") };
        TestHandler normalRequest = new();

        _cache.Announced(1, new TestHandler());
        _cache.Announced(1, faultyRequest);
        _cache.Announced(1, normalRequest);

        Assert.That(() => normalRequest.WasCalled, Is.True.After(Timeout, 100));
    }

    [Test]
    public async Task CancellationToken_StopsProcessing()
    {
        TestHandler request = new();

        _cache.Announced(1, request);
        await _cancellationTokenSource.CancelAsync();
        await Task.Delay(Timeout);

        Assert.That(request.WasCalled, Is.False);
    }

    [Test]
    public async Task Announced_AfterRetryInProgress_ReturnsNew()
    {
        _cache.Announced(1, new TestHandler());

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        Assert.That(() => _cache.Announced(1, new TestHandler()), Is.EqualTo(AnnounceResult.RequestRequired).After(Timeout, 100));
    }

    [Test]
    public void Received_NonExistentResource_DoesNotThrow()
    {
        Assert.That(() => _cache.Received(999), Throws.Nothing);
    }
}
