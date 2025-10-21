// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.TxPool.Test;

[TestFixture]
public class SimpleRetryCacheTests
{
    public readonly struct ResourceRequestMessage : INew<int, ResourceRequestMessage>
    {
        public int Resource { get; init; }
        public static ResourceRequestMessage New(int resourceId) => new() { Resource = resourceId };
    }

    public interface ITestHandler : IMessageHandler<ResourceRequestMessage>;


    private CancellationTokenSource _cancellationTokenSource;
    private RetryCache<ResourceRequestMessage, int> cache;

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
        AnnounceResult result1 = cache.Announced(1, Substitute.For<ITestHandler>());
        AnnounceResult result2 = cache.Announced(1, Substitute.For<ITestHandler>());

        Assert.That(result1, Is.EqualTo(AnnounceResult.New));
        Assert.That(result2, Is.EqualTo(AnnounceResult.Enqueued));
    }

    [Test]
    public async Task Announced_AfterTimeout_ExecutesRetryRequests()
    {
        ITestHandler request1 = Substitute.For<ITestHandler>();
        ITestHandler request2 = Substitute.For<ITestHandler>();

        cache.Announced(1, request1);
        cache.Announced(1, request2);

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        request1.DidNotReceive().HandleMessage(Arg.Any<ResourceRequestMessage>());
        request2.Received(1).HandleMessage(Arg.Any<ResourceRequestMessage>());
    }

    [Test]
    public async Task Announced_MultipleResources_ExecutesAllRetryRequestsExceptInititalOne()
    {
        ITestHandler request1 = Substitute.For<ITestHandler>();
        ITestHandler request2 = Substitute.For<ITestHandler>();
        ITestHandler request3 = Substitute.For<ITestHandler>();
        ITestHandler request4 = Substitute.For<ITestHandler>();

        cache.Announced(1, request1);
        cache.Announced(1, request2);
        cache.Announced(2, request3);
        cache.Announced(2, request4);

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        request1.Received(0).HandleMessage(Arg.Any<ResourceRequestMessage>());
        request2.Received(1).HandleMessage(Arg.Any<ResourceRequestMessage>());
        request3.Received(0).HandleMessage(Arg.Any<ResourceRequestMessage>());
        request4.Received(1).HandleMessage(Arg.Any<ResourceRequestMessage>());
    }

    [Test]
    public void Received_RemovesResourceFromRetryQueue()
    {
        cache.Announced(1, Substitute.For<ITestHandler>());
        cache.Received(1);

        AnnounceResult result = cache.Announced(1, Substitute.For<ITestHandler>());
        Assert.That(result, Is.EqualTo(AnnounceResult.New));
    }

    [Test]
    public async Task Received_BeforeTimeout_PreventsRetryExecution()
    {
        ITestHandler request = Substitute.For<ITestHandler>();

        cache.Announced(1, request);
        cache.Announced(1, request);
        cache.Received(1);

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        request.DidNotReceive().HandleMessage(ResourceRequestMessage.New(1));
    }

    [Test]
    public async Task RetryExecution_HandlesExceptions()
    {
        ITestHandler faultyRequest = Substitute.For<ITestHandler>();
        ITestHandler normalRequest = Substitute.For<ITestHandler>();

        faultyRequest.When(x => x.HandleMessage(Arg.Any<ResourceRequestMessage>())).Do(x => throw new InvalidOperationException("Test exception"));

        cache.Announced(1, Substitute.For<ITestHandler>());
        cache.Announced(1, faultyRequest);
        cache.Announced(1, normalRequest);

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        normalRequest.Received(1).HandleMessage(Arg.Any<ResourceRequestMessage>());
    }

    [Test]
    public async Task CancellationToken_StopsProcessing()
    {
        ITestHandler request = Substitute.For<ITestHandler>();

        cache.Announced(1, request);
        _cancellationTokenSource.Cancel();
        await Task.Delay(Timeout);

        request.DidNotReceive().HandleMessage(Arg.Any<ResourceRequestMessage>());
    }

    [Test]
    public async Task Announced_AfterRetryInProgress_ReturnsNew()
    {
        cache.Announced(1, Substitute.For<ITestHandler>());

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        AnnounceResult result = cache.Announced(1, Substitute.For<ITestHandler>());
        Assert.That(result, Is.EqualTo(AnnounceResult.New));
    }

    [Test]
    public void Received_NonExistentResource_DoesNotThrow()
    {
        Assert.That(() => cache.Received(999), Throws.Nothing);
    }
}
