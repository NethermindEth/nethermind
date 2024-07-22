// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Nethermind.Tools.Kute.Auth;

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

class HttpJsonRpcSubmitter : IJsonRpcSubmitter
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

    public async Task<HttpResponseMessage> Submit(JsonRpc rpc)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _uri)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _auth.AuthToken) },
            Content = new StringContent(rpc.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json),
        };

        return await _httpClient.SendAsync(request);
    }
}
