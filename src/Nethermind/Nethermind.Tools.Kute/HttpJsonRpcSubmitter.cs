// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Text;

namespace Nethermind.Tools.Kute;

class HttpJsonRpcSubmitter : IJsonRpcSubmitter
{
    private readonly Uri _uri;
    private readonly HttpClient _httpClient;
    private readonly IAuth _auth;

    public HttpJsonRpcSubmitter(HttpClient httpClient, IAuth auth, Config config)
    {
        _httpClient = httpClient;
        _auth = auth;
        _uri = new Uri(config.Host);
    }

    public async Task Submit(string jsonContent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _uri)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _auth.AuthToken) },
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };
        await _httpClient.SendAsync(request);
    }
}
