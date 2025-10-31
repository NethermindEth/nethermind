// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test;

namespace Nethermind.TxPool.Test;

[TestFixture]
public class RetryCacheTests
{
    public readonly struct ResourceRequestMessage : INew<int, ResourceRequestMessage>
    {
        public int Resource { get; init; }
        public static ResourceRequestMessage New(int resourceId) => new() { Resource = resourceId };
    }

    public interface ITestHandler : IMessageHandler<ResourceRequestMessage>;

    private CancellationTokenSource _cancellationTokenSource;
    private RetryCache<ResourceRequestMessage, int> _cache;

    private readonly int Timeout = 5000;

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
        AnnounceResult result1 = _cache.Announced(1, Substitute.For<ITestHandler>());
        AnnounceResult result2 = _cache.Announced(1, Substitute.For<ITestHandler>());

        Assert.That(result1, Is.EqualTo(AnnounceResult.New));
        Assert.That(result2, Is.EqualTo(AnnounceResult.Enqueued));
    }

    [Test]
    public void Announced_AfterTimeout_ExecutesRetryRequests()
    {
        ITestHandler request1 = Substitute.For<ITestHandler>();
        ITestHandler request2 = Substitute.For<ITestHandler>();

        _cache.Announced(1, request1);
        _cache.Announced(1, request2);

        Assert.That(() => request2.ReceivedCallsMatching(r => r.HandleMessage(Arg.Any<ResourceRequestMessage>())), Is.True.After(Timeout, 100));
        request1.DidNotReceive().HandleMessage(Arg.Any<ResourceRequestMessage>());
    }

    [Test]
    public void Announced_MultipleResources_ExecutesAllRetryRequestsExceptInititalOne()
    {
        ITestHandler request1 = Substitute.For<ITestHandler>();
        ITestHandler request2 = Substitute.For<ITestHandler>();
        ITestHandler request3 = Substitute.For<ITestHandler>();
        ITestHandler request4 = Substitute.For<ITestHandler>();

        _cache.Announced(1, request1);
        _cache.Announced(1, request2);
        _cache.Announced(2, request3);
        _cache.Announced(2, request4);

        Assert.That(() => request2.ReceivedCallsMatching(r => r.HandleMessage(Arg.Any<ResourceRequestMessage>())), Is.True.After(Timeout, 100));
        Assert.That(() => request4.ReceivedCallsMatching(r => r.HandleMessage(Arg.Any<ResourceRequestMessage>())), Is.True.After(Timeout, 100));
        request1.DidNotReceive().HandleMessage(Arg.Any<ResourceRequestMessage>());
        request3.DidNotReceive().HandleMessage(Arg.Any<ResourceRequestMessage>());
    }

    [Test]
    public void Received_RemovesResourceFromRetryQueue()
    {
        _cache.Announced(1, Substitute.For<ITestHandler>());
        _cache.Received(1);

        AnnounceResult result = _cache.Announced(1, Substitute.For<ITestHandler>());
        Assert.That(result, Is.EqualTo(AnnounceResult.New));
    }

    [Test]
    public async Task Received_BeforeTimeout_PreventsRetryExecution()
    {
        ITestHandler request = Substitute.For<ITestHandler>();

        _cache.Announced(1, request);
        _cache.Announced(1, request);
        _cache.Received(1);


        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        request.DidNotReceive().HandleMessage(ResourceRequestMessage.New(1));
    }

    [Test]
    public void RetryExecution_HandlesExceptions()
    {
        ITestHandler faultyRequest = Substitute.For<ITestHandler>();
        ITestHandler normalRequest = Substitute.For<ITestHandler>();

        faultyRequest.When(x => x.HandleMessage(Arg.Any<ResourceRequestMessage>())).Do(x => throw new InvalidOperationException("Test exception"));

        _cache.Announced(1, Substitute.For<ITestHandler>());
        _cache.Announced(1, faultyRequest);
        _cache.Announced(1, normalRequest);

        Assert.That(() => normalRequest.ReceivedCallsMatching(r => r.HandleMessage(ResourceRequestMessage.New(1))), Is.True.After(Timeout, 100));
    }

    [Test]
    public async Task CancellationToken_StopsProcessing()
    {
        ITestHandler request = Substitute.For<ITestHandler>();

        _cache.Announced(1, request);
        await _cancellationTokenSource.CancelAsync();
        await Task.Delay(Timeout);

        request.DidNotReceive().HandleMessage(Arg.Any<ResourceRequestMessage>());
    }

    [Test]
    public async Task Announced_AfterRetryInProgress_ReturnsNew()
    {
        _cache.Announced(1, Substitute.For<ITestHandler>());

        await Task.Delay(Timeout, _cancellationTokenSource.Token);

        Assert.That(() => _cache.Announced(1, Substitute.For<ITestHandler>()), Is.EqualTo(AnnounceResult.New).After(Timeout, 100));
    }

    [Test]
    public void Received_NonExistentResource_DoesNotThrow()
    {
        Assert.That(() => _cache.Received(999), Throws.Nothing);
    }
}
