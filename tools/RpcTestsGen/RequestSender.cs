// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RpcTestsGen;

public class RequestSender(Uri[] clientUrls, HttpClient httpClient)
{
    private const int LogPerRequests = 10;
    private int _requestN;

    public async Task<ResponseInfo> SendAsync(RequestInfo request)
    {
        if (++_requestN % LogPerRequests == 0)
            await Console.Out.WriteLineAsync($"Sending request #{_requestN}");

        JsonNode[] responses = await Task.WhenAll(clientUrls.Select(url => SendToClientAsync(url, request.Data)));
        return new ResponseInfo(request, responses);
    }

    private async Task<JsonNode> SendToClientAsync(Uri url, JsonNode requestData)
    {
        try
        {
            using JsonContent content = JsonContent.Create(requestData);
            using HttpResponseMessage response = await httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            JsonNode responseData = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync())
                ?? throw new JsonException("Empty response received.");

            if (responseData["error"] is {} error)
                throw new Exception(error.ToCompactString());

            return responseData;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"""
                 Request error: {ex.Message}
                   Client: {url}
                   Request: {requestData.ToCompactString()}
                 """
            );

            //return;
            throw;
        }
    }
}

public readonly record struct FilePos(string FilePath, int LineNumber)
{
    public override string ToString() => LineNumber == 0 ? FilePath : $"{FilePath}:{LineNumber}";

    public static FilePos Parse(string location)
    {
        int splitIndex = location.LastIndexOf(':');

        return splitIndex != -1 && int.TryParse(location[(splitIndex + 1)..], out int lineNumber)
            ? new FilePos(location[..splitIndex], lineNumber)
            : new FilePos(location, 0);
    }
}

public record ResponseInfo(RequestInfo Request, JsonNode[] Responses);
