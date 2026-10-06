// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Facade.Test;

public class DefaultHttpClientTests
{
    private const string Endpoint = "http://localhost/endpoint";
    private const int RetryDelayMilliseconds = 1;

    [Test]
    public void Should_propagate_cancellation_when_token_is_cancelled_before_the_request([Values(1, 2, 3)] int retries)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using StubHandler handler = new(_ => Task.FromCanceled<HttpResponseMessage>(cancellation.Token));
        using DefaultHttpClient client = CreateClient(handler, retries);

        Assert.CatchAsync<OperationCanceledException>(async () => await client.GetAsync<object>(Endpoint, cancellation.Token));
    }

    [Test]
    public void Should_propagate_cancellation_when_the_request_is_cancelled_in_flight([Values(1, 2, 3)] int retries)
    {
        using CancellationTokenSource cancellation = new();
        using StubHandler handler = new(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        });
        using DefaultHttpClient client = CreateClient(handler, retries);

        Assert.CatchAsync<OperationCanceledException>(async () => await client.GetAsync<object>(Endpoint, cancellation.Token));
        Assert.That(handler.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_return_default_after_exhausting_retries(
        [Values(1, 3)] int retries,
        [ValueSource(nameof(TransportExceptions))] Exception exception)
    {
        using StubHandler handler = new(_ => Task.FromException<HttpResponseMessage>(exception));
        using DefaultHttpClient client = CreateClient(handler, retries);

        object? result = await client.GetAsync<object>(Endpoint, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(handler.CallCount, Is.EqualTo(retries));
        }
    }

    private static readonly Exception[] TransportExceptions =
    [
        new HttpRequestException("Connection refused"),
        new OperationCanceledException(),
    ];

    private static DefaultHttpClient CreateClient(HttpMessageHandler handler, int retries) =>
        new(new HttpClient(handler), new EthereumJsonSerializer(), LimboLogs.Instance, retries, RetryDelayMilliseconds);

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return send(cancellationToken);
        }
    }
}
