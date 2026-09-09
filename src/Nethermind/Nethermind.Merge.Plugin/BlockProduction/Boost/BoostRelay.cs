// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Facade.Proxy;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostRelay(IHttpClient httpClient, string relayUrl) : IBoostRelay
{
    public const string GetPayloadAttributesPath = "/eth/v1/relay/payload_attributes";
    public const string SendPayloadPath = "/eth/v1/relay/submit_block";

    private readonly IHttpClient _httpClient = httpClient;
    private readonly string _relayUrl = relayUrl;

    public async Task<BoostPayloadAttributes> GetPayloadAttributes(PayloadAttributes payloadAttributes, CancellationToken cancellationToken) =>
        await _httpClient.PostJsonAsync<BoostPayloadAttributes>(GetUri(_relayUrl, GetPayloadAttributesPath), payloadAttributes, cancellationToken)
        ?? throw new HttpRequestException("Boost relay returned no payload attributes.");

    public Task SendPayload(BoostExecutionPayloadV1 executionPayloadV1, CancellationToken cancellationToken) =>
        _httpClient.PostJsonAsync<object>(GetUri(_relayUrl, SendPayloadPath), executionPayloadV1, cancellationToken);

    private static string GetUri(string relayUrl, string relativeUrl) => relayUrl + relativeUrl;
}
