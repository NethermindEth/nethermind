// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using Nethermind.Tools.Kute.Auth;

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

public sealed class HttpJsonRpcSubmitter(HttpClient httpClient, IAuth auth, string hostAddress) : IJsonRpcSubmitter
{
    private readonly Uri _uri = new(hostAddress);
    private readonly HttpClient _httpClient = httpClient;
    private readonly IAuth _auth = auth;

    public async Task<JsonRpc.Response> Submit(JsonRpc.Request rpc, CancellationToken token = default)
    {
        HttpRequestMessage request = new(HttpMethod.Post, _uri)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _auth.AuthToken) },
            Content = new StringContent(rpc.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json),
        };

        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(request, token);
        return await JsonRpc.Response.FromHttpResponseAsync(httpResponse, token);
    }
}
