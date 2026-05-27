// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

internal class RequestSender(HttpClient httpClient)
{
    public async Task<JsonNode> SendAsync(Uri url, JsonNode request, CancellationToken ct = default)
    {
        using JsonContent content = JsonContent.Create(request);
        using HttpResponseMessage response = await httpClient.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        return await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct)
            ?? throw new JsonException("Empty response received.");
    }
}
