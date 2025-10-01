// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
public class SimpleRetryCacheTests
{
    private ILogManager _logManager;
    private CancellationTokenSource _cancellationTokenSource;

    [SetUp]
    public void Setup()
    {
        _logManager = TestLogManager.Instance;
        _cancellationTokenSource = new CancellationTokenSource();
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
        SimpleRetryCache<int, string> cache = new(_logManager, _cancellationTokenSource.Token);
        Action request1 = Substitute.For<Action>();
        Action request2 = Substitute.For<Action>();

        AnnounceResult result1 = cache.Announced(1, "node1", request1);
        AnnounceResult result2 = cache.Announced(1, "node2", request2);

        Assert.That(result1, Is.EqualTo(AnnounceResult.New));
        Assert.That(result2, Is.EqualTo(AnnounceResult.Enqueued));
    }

    [Test]
    public async Task Announced_AfterTimeout_ExecutesRetryRequests()
    {
        SimpleRetryCache<int, string> cache = new(_logManager, _cancellationTokenSource.Token);
        Action request1 = Substitute.For<Action>();
        Action request2 = Substitute.For<Action>();

        cache.Announced(1, "node1", request1);
        cache.Announced(1, "node2", request2);

        // Wait for timeout (2.5 seconds) plus some buffer
        await Task.Delay(3000, _cancellationTokenSource.Token);

        request1.Received(0).Invoke();
        request2.Received(1).Invoke();
    }

    [Test]
    public async Task Announced_MultipleResources_ExecutesAllRetryRequestsExceptInititalOne()
    {
        SimpleRetryCache<int, string> cache = new(_logManager, _cancellationTokenSource.Token);
        Action request1 = Substitute.For<Action>();
        Action request2 = Substitute.For<Action>();
        Action request3 = Substitute.For<Action>();
        Action request4 = Substitute.For<Action>();

        cache.Announced(1, "node1", request1);
        cache.Announced(1, "node2", request2);
        cache.Announced(2, "node1", request3);
        cache.Announced(2, "node3", request4);

        await Task.Delay(3000, _cancellationTokenSource.Token);

        request1.Received(0).Invoke();
        request2.Received(1).Invoke();
        request3.Received(0).Invoke();
        request4.Received(1).Invoke();
    }

    [Test]
    public void Received_RemovesResourceFromRetryQueue()
    {
        SimpleRetryCache<int, string> cache = new(_logManager, _cancellationTokenSource.Token);
        Action request = Substitute.For<Action>();

        cache.Announced(1, "node1", request);
        cache.Received(1);

        // Announce again should return New since the resource was removed
        AnnounceResult result = cache.Announced(1, "node2", Substitute.For<Action>());
        Assert.That(result, Is.EqualTo(AnnounceResult.New));
    }

    [Test]
    public async Task Received_BeforeTimeout_PreventsRetryExecution()
    {
        SimpleRetryCache<int, string> cache = new(_logManager, _cancellationTokenSource.Token);
        Action request = Substitute.For<Action>();

        cache.Announced(1, "node1", request);
        cache.Announced(1, "node2", request);
        cache.Received(1);

        await Task.Delay(3000, _cancellationTokenSource.Token);

        request.DidNotReceive().Invoke();
    }

    [Test]
    public async Task RetryExecution_HandlesExceptions()
    {
        SimpleRetryCache<int, string> cache = new(_logManager, _cancellationTokenSource.Token);
        Action initialRequest = Substitute.For<Action>();
        Action faultyRequest = Substitute.For<Action>();
        Action normalRequest = Substitute.For<Action>();

        faultyRequest.When(x => x.Invoke()).Do(x => throw new InvalidOperationException("Test exception"));

        cache.Announced(1, "node1", initialRequest);
        cache.Announced(1, "node2", faultyRequest);
        cache.Announced(1, "node3", normalRequest);

        await Task.Delay(3000, _cancellationTokenSource.Token);

        normalRequest.Received(1).Invoke();
    }

    [Test]
    public async Task CancellationToken_StopsProcessing()
    {
        SimpleRetryCache<int, string> cache = new(_logManager, _cancellationTokenSource.Token);
        Action request = Substitute.For<Action>();

        cache.Announced(1, "node1", request);

        // Cancel immediately
        _cancellationTokenSource.Cancel();

        await Task.Delay(3000);

        request.DidNotReceive().Invoke();
    }

    [Test]
    public async Task Announced_AfterRetryInProgress_ReturnsNew()
    {
        SimpleRetryCache<int, string> cache = new(_logManager, _cancellationTokenSource.Token);
        Action request1 = Substitute.For<Action>();
        Action request2 = Substitute.For<Action>();

        // First announcement
        cache.Announced(1, "node1", request1);

        // Wait for timeout to trigger retry
        await Task.Delay(3000, _cancellationTokenSource.Token);

        // Second announcement after retry is in progress
        AnnounceResult result = cache.Announced(1, "node2", request2);

        Assert.That(result, Is.EqualTo(AnnounceResult.New));
    }

    [Test]
    public void Received_NonExistentResource_DoesNotThrow()
    {
        SimpleRetryCache<int, string> cache = new(_logManager, _cancellationTokenSource.Token);

        Action act = () => cache.Received(999);

        Assert.That(act, Throws.Nothing);
    }
}
