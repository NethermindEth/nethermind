// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RpcTestsGen;

public class RequestSender(string[] clientUrls, HttpClient httpClient)
{
    public async Task<ResponseInfo> SendAsync(RequestInfo request)
    {
        JsonNode[] responses = await Task.WhenAll(clientUrls.Select(url => SendToClientAsync(url, request.Data)));
        return new ResponseInfo(request, responses);
    }

    private async Task<JsonNode> SendToClientAsync(string url, JsonNode requestData)
    {
        try
        {
            using JsonContent content = JsonContent.Create(requestData);
            using HttpResponseMessage response = await httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            return await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync())
                ?? throw new JsonException("Empty response received.");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"HTTP error for {url}: {ex.Message}");
            return string.Empty;
        }
    }
}

public readonly record struct FileLocation(string FilePath, int LineNumber)
{
    public override string ToString() => LineNumber == 0 ? FilePath : $"{FilePath}:{LineNumber}";

    public static FileLocation Parse(string location)
    {
        int splitIndex = location.LastIndexOf(':');

        return splitIndex != -1 && int.TryParse(location[(splitIndex + 1)..], out int lineNumber)
            ? new FileLocation(location[..splitIndex], lineNumber)
            : new FileLocation(location, 0);
    }
}

public record ResponseInfo(RequestInfo Request, JsonNode[] Responses);
