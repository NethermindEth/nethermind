// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Facade.Proxy;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostRelay : IBoostRelay
{
    public const string GetPayloadAttributesPath = "/eth/v1/relay/payload_attributes";
    public const string SendPayloadPath = "/eth/v1/relay/submit_block";

    private readonly IHttpClient _httpClient;
    private readonly string _relayUrl;

    public BoostRelay(IHttpClient httpClient, string relayUrl)
    {
        _httpClient = httpClient;
        _relayUrl = relayUrl;
    }

    public Task<PayloadAttributes> GetPayloadAttributes(PayloadAttributes payloadAttributes, CancellationToken cancellationToken) =>
        _httpClient.PostJsonAsync<PayloadAttributes>(GetUri(_relayUrl, GetPayloadAttributesPath), payloadAttributes, cancellationToken);

    public Task SendPayload(BoostExecutionPayloadV1 executionPayloadV1, CancellationToken cancellationToken) =>
        _httpClient.PostJsonAsync<object>(GetUri(_relayUrl, SendPayloadPath), executionPayloadV1, cancellationToken);

    private string GetUri(string relayUrl, string relativeUrl) => relayUrl + relativeUrl;
}
