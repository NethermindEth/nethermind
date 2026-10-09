// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Test;

public class CheckpointSourceTests
{
    private const string Root = "0x6ac776d12a0f63834f8ebac4882ecc4ee863b2f0f349a765ab2058c92eb18047";
    private const string Endpoint = "https://checkpoint-sync.hoodi.ethpandaops.io";

    [TestCase("mainnet", null)]
    [TestCase("hoodi", "https://checkpoint-sync.hoodi.ethpandaops.io")]
    [TestCase("sepolia", "https://checkpoint-sync.sepolia.ethpandaops.io")]
    public void Only_ef_testnet_providers_are_defaults(string network, string? expected) =>
        Assert.That(CheckpointSource.DefaultUrl(network), Is.EqualTo(expected));

    [Test]
    public async Task Fetches_the_finalized_checkpoint_root()
    {
        using ResponseHandler handler = new(HttpStatusCode.OK, $"{{\"data\":{{\"finalized\":{{\"root\":\"{Root}\"}}}}}}");
        using HttpClient client = new(handler);

        Hash256 checkpoint = await CheckpointSource.FetchAsync(client, Endpoint, CancellationToken.None);

        Assert.That(checkpoint, Is.EqualTo(new Hash256(Root)));
        Assert.That(handler.RequestUri, Is.EqualTo(Endpoint + "/eth/v1/beacon/states/finalized/finality_checkpoints"));
    }

    [TestCase("0x0")]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0x6ac776d12a0f63834f8ebac4882ecc4ee863b2f0f349a765ab2058c92eb1804x")]
    public void Rejects_invalid_checkpoint_roots(string root)
    {
        using ResponseHandler handler = new(HttpStatusCode.OK, $"{{\"data\":{{\"finalized\":{{\"root\":\"{root}\"}}}}}}");
        using HttpClient client = new(handler);

        Assert.That(async () => await CheckpointSource.FetchAsync(client, Endpoint, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Rejects_http_provider()
    {
        using ResponseHandler handler = new(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);

        Assert.That(async () => await CheckpointSource.FetchAsync(client, "http://example.org", CancellationToken.None),
            Throws.TypeOf<ArgumentException>());
        Assert.That(handler.RequestUri, Is.Null);
    }

    [TestCase("{}")]
    [TestCase("{\"data\":{\"finalized\":{\"root\":23}}}")]
    public void Rejects_invalid_finality_response(string body)
    {
        using ResponseHandler handler = new(HttpStatusCode.OK, body);
        using HttpClient client = new(handler);

        Assert.That(async () => await CheckpointSource.FetchAsync(client, Endpoint, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
    }

    [TestCase(HttpStatusCode.Redirect)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    public void Rejects_non_success_responses(HttpStatusCode status)
    {
        using ResponseHandler handler = new(status, "{}");
        using HttpClient client = new(handler);

        Assert.That(async () => await CheckpointSource.FetchAsync(client, Endpoint, CancellationToken.None),
            Throws.TypeOf<HttpRequestException>());
    }

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
