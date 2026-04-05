// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.EraE.Proofs;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Proofs;

public class BeaconApiRootsProviderTests
{
    // Production code calls FetchBlockRootAsync first (headers endpoint), then FetchStateRootAsync.
    // Responses are enqueued in that order.

    private const string HeadersJson =
        """{"data":{"root":"0xaabbccdd00000000000000000000000000000000000000000000000000000000"}}""";

    private const string StateRootJson =
        """{"data":{"root":"0x1122334400000000000000000000000000000000000000000000000000000000"}}""";

    [Test]
    public async Task GetBeaconRoots_WithValidSlot_ReturnsBothRoots()
    {
        SequentialHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, HeadersJson);
        handler.Enqueue(HttpStatusCode.OK, StateRootJson);

        using BeaconApiRootsProvider sut = new(
            new Uri("http://localhost:5052"),
            new HttpClient(handler));

        (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)? result = await sut.GetBeaconRoots(1);

        result.Should().NotBeNull();
        result!.Value.BeaconBlockRoot.ToByteArray()[0].Should().Be(0xaa);
        result.Value.StateRoot.ToByteArray()[0].Should().Be(0x11);
    }

    [Test]
    public async Task GetBeaconRoots_WithSameSlotCalledTwice_MakesOnlyTwoHttpRequests()
    {
        SequentialHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, HeadersJson);
        handler.Enqueue(HttpStatusCode.OK, StateRootJson);
        // Only 2 responses enqueued — a third request would throw

        using BeaconApiRootsProvider sut = new(
            new Uri("http://localhost:5052"),
            new HttpClient(handler));

        await sut.GetBeaconRoots(1);
        await sut.GetBeaconRoots(1);

        handler.CallCount.Should().Be(2, "cache hit on second call must skip both HTTP requests");
    }

    [Test]
    public async Task GetBeaconRoots_WhenHeadersResponseMissingDataField_ReturnsNull()
    {
        SequentialHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, """{"other":"x"}""");
        // State root endpoint must NOT be called — handler throws on any extra request

        using BeaconApiRootsProvider sut = new(
            new Uri("http://localhost:5052"),
            new HttpClient(handler));

        (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)? result = await sut.GetBeaconRoots(1);

        result.Should().BeNull();
    }

    [Test]
    public async Task GetBeaconRoots_WhenHeadersResponseIsNonSuccess_ReturnsNull()
    {
        SequentialHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.NotFound, "{}");

        using BeaconApiRootsProvider sut = new(
            new Uri("http://localhost:5052"),
            new HttpClient(handler));

        (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)? result = await sut.GetBeaconRoots(1);

        result.Should().BeNull();
    }

    [Test]
    public async Task GetBeaconRoots_WhenStateRootResponseIsNonSuccess_ReturnsNull()
    {
        SequentialHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, HeadersJson);
        handler.Enqueue(HttpStatusCode.NotFound, "{}");

        using BeaconApiRootsProvider sut = new(
            new Uri("http://localhost:5052"),
            new HttpClient(handler));

        (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)? result = await sut.GetBeaconRoots(1);

        result.Should().BeNull();
    }

    private sealed class SequentialHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses = new();

        public int CallCount { get; private set; }

        public void Enqueue(HttpStatusCode statusCode, string body) =>
            _responses.Enqueue((statusCode, body));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException(
                    $"Unexpected HTTP request to {request.RequestUri} — no more responses queued.");

            (HttpStatusCode statusCode, string body) = _responses.Dequeue();
            HttpResponseMessage response = new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
