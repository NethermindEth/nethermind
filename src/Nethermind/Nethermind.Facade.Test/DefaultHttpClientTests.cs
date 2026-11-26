// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
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
    public async Task Should_propagate_cancellation_when_token_is_cancelled_before_the_request([Values(1, 2, 3)] int retries)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using StubHandler handler = new(_ => Task.FromCanceled<HttpResponseMessage>(cancellation.Token));
        using DefaultHttpClient client = CreateClient(handler, retries);

        await Assert.CatchAsync<OperationCanceledException>(async () => await client.GetAsync<object>(Endpoint, cancellation.Token));
    }

    [Test]
    public async Task Should_propagate_cancellation_when_the_request_is_cancelled_in_flight([Values(1, 2, 3)] int retries)
    {
        using CancellationTokenSource cancellation = new();
        using StubHandler handler = new(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        });
        using DefaultHttpClient client = CreateClient(handler, retries);

        await Assert.CatchAsync<OperationCanceledException>(async () => await client.GetAsync<object>(Endpoint, cancellation.Token));
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

    // A wait the client must not make would outlast the limit, which cancels it and fails the test.
    [TestCase(0, 60_000, 1)]
    [TestCase(-1, 60_000, 1)]
    [TestCase(2, -1, 2)]
    [TestCase(2, -2, 2)]
    [TestCase(2, int.MinValue, 2)]
    public async Task Should_send_at_least_once_and_not_wait_a_negative_delay(int retries, int retryDelayMilliseconds, int expectedCalls)
    {
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(10));
        using StubHandler handler = new(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused")));
        using DefaultHttpClient client = CreateClient(handler, retries, retryDelayMilliseconds);

        object? result = await client.PostJsonAsync<object>(Endpoint, cancellationToken: limit.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(handler.CallCount, Is.EqualTo(expectedCalls));
        }
    }

    [TestCase(HttpStatusCode.OK, "")]
    [TestCase(HttpStatusCode.OK, "not json")]
    [TestCase(HttpStatusCode.OK, "{")]
    [TestCase(HttpStatusCode.RequestTimeout, "[1,2]")]
    [TestCase(HttpStatusCode.TooManyRequests, "[1,2]")]
    [TestCase(HttpStatusCode.InternalServerError, "[1,2]")]
    [TestCase(HttpStatusCode.ServiceUnavailable, "[1,2]")]
    public async Task Should_not_send_again_after_a_response(HttpStatusCode status, string body)
    {
        using StubHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
        using DefaultHttpClient client = CreateClient(handler, 3);

        object? result = await client.PostJsonAsync<object>(Endpoint, cancellationToken: CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(handler.CallCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Should_send_again_after_a_transport_failure()
    {
        int sends = 0;
        using StubHandler handler = new(_ => ++sends == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[1,2]") }));
        using DefaultHttpClient client = CreateClient(handler, 3);

        object? result = await client.PostJsonAsync<object>(Endpoint, cancellationToken: CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result?.ToString(), Is.EqualTo("[1,2]"));
            Assert.That(handler.CallCount, Is.EqualTo(2));
        }
    }

    private static readonly Exception[] TransportExceptions =
    [
        new HttpRequestException("Connection refused"),
        new OperationCanceledException(),
    ];

    private static DefaultHttpClient CreateClient(HttpMessageHandler handler, int retries, int retryDelayMilliseconds = RetryDelayMilliseconds) =>
        new(new HttpClient(handler), new EthereumJsonSerializer(), LimboLogs.Instance, retries, retryDelayMilliseconds);

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
