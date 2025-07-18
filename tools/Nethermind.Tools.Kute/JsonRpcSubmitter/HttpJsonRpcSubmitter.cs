// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using Nethermind.Tools.Kute.Auth;

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

public sealed class HttpJsonRpcSubmitter : IJsonRpcSubmitter
{
    private readonly Uri _uri;
    private readonly HttpClient _httpClient;
    private readonly IAuth _auth;

    public HttpJsonRpcSubmitter(HttpClient httpClient, IAuth auth, string hostAddress)
    {
        _httpClient = httpClient;
        _auth = auth;
        _uri = new Uri(hostAddress);
    }

    public async Task<JsonRpc.Response> Submit(JsonRpc.Request rpc)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _uri)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _auth.AuthToken) },
            Content = new StringContent(rpc.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json),
        };

        var httpResponse = await _httpClient.SendAsync(request);
        return await JsonRpc.Response.FromHttpResponseAsync(httpResponse);
    }
}
