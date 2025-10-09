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
    private CancellationTokenSource _cancellationTokenSource;
    private SimpleRetryCache<int, string> cache;

    private readonly int Timeout = 3000;

    [SetUp]
    public void Setup()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        cache = new(TestLogManager.Instance, token: _cancellationTokenSource.Token);
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
        AnnounceResult result1 = cache.Announced(1, "node1", Substitute.For<Action>());
        AnnounceResult result2 = cache.Announced(1, "node2", Substitute.For<Action>());

        Assert.That(result1, Is.EqualTo(AnnounceResult.New));
        Assert.That(result2, Is.EqualTo(AnnounceResult.Enqueued));
    }

    [Test]
    public async Task Announced_AfterTimeout_ExecutesRetryRequests()
    {
        Action request1 = Substitute.For<Action>();
        Action request2 = Substitute.For<Action>();

        cache.Announced(1, "node1", request1);
        cache.Announced(1, "node2", request2);

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        request1.Received(0).Invoke();
        request2.Received(1).Invoke();
    }

    [Test]
    public async Task Announced_MultipleResources_ExecutesAllRetryRequestsExceptInititalOne()
    {
        Action request1 = Substitute.For<Action>();
        Action request2 = Substitute.For<Action>();
        Action request3 = Substitute.For<Action>();
        Action request4 = Substitute.For<Action>();

        cache.Announced(1, "node1", request1);
        cache.Announced(1, "node2", request2);
        cache.Announced(2, "node1", request3);
        cache.Announced(2, "node3", request4);

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        request1.Received(0).Invoke();
        request2.Received(1).Invoke();
        request3.Received(0).Invoke();
        request4.Received(1).Invoke();
    }

    [Test]
    public void Received_RemovesResourceFromRetryQueue()
    {
        cache.Announced(1, "node1", Substitute.For<Action>());
        cache.Received(1);

        AnnounceResult result = cache.Announced(1, "node2", Substitute.For<Action>());
        Assert.That(result, Is.EqualTo(AnnounceResult.New));
    }

    [Test]
    public async Task Received_BeforeTimeout_PreventsRetryExecution()
    {
        Action request = Substitute.For<Action>();

        cache.Announced(1, "node1", request);
        cache.Announced(1, "node2", request);
        cache.Received(1);

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        request.DidNotReceive().Invoke();
    }

    [Test]
    public async Task RetryExecution_HandlesExceptions()
    {
        Action faultyRequest = Substitute.For<Action>();
        Action normalRequest = Substitute.For<Action>();

        faultyRequest.When(x => x.Invoke()).Do(x => throw new InvalidOperationException("Test exception"));

        cache.Announced(1, "node1", Substitute.For<Action>());
        cache.Announced(1, "node2", faultyRequest);
        cache.Announced(1, "node3", normalRequest);

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        normalRequest.Received(1).Invoke();
    }

    [Test]
    public async Task CancellationToken_StopsProcessing()
    {
        Action request = Substitute.For<Action>();

        cache.Announced(1, "node1", request);
        _cancellationTokenSource.Cancel();
        await Task.Delay(Timeout);

        request.DidNotReceive().Invoke();
    }

    [Test]
    public async Task Announced_AfterRetryInProgress_ReturnsNew()
    {
        cache.Announced(1, "node1", Substitute.For<Action>());

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        AnnounceResult result = cache.Announced(1, "node2", Substitute.For<Action>());
        Assert.That(result, Is.EqualTo(AnnounceResult.New));
    }

    [Test]
    public void Received_NonExistentResource_DoesNotThrow()
    {
        Assert.That(() => cache.Received(999), Throws.Nothing);
    }
}
