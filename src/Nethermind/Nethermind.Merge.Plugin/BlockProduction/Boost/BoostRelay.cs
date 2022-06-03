//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Facade.Proxy;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostRelay : IBoostRelay
{
    private readonly IHttpClient _httpClient;
    private readonly string _relayUrl;
    private const string getPayloadAttributes = "/eth/v1/relay/payload_attributes";
    private const string submitBlock = "/eth/v1/relay/submit_block";

    public BoostRelay(IHttpClient httpClient, string relayUrl)
    {
        _httpClient = httpClient;
        _relayUrl = relayUrl;
    }
    
    public Task<PayloadAttributes> GetPayloadAttributes(PayloadAttributes payloadAttributes, CancellationToken cancellationToken) => 
        _httpClient.PostJsonAsync<PayloadAttributes>(GetUri(_relayUrl, getPayloadAttributes), payloadAttributes, cancellationToken);

    public Task SendPayload(BoostExecutionPayloadV1 executionPayloadV1, CancellationToken cancellationToken) => 
        _httpClient.PostJsonAsync<object>(GetUri(_relayUrl, submitBlock), executionPayloadV1, cancellationToken);
    
    private string GetUri(string relayUrl, string relativeUrl) => relayUrl + relativeUrl;
}
