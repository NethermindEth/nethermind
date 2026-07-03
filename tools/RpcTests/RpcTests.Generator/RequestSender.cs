// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Generator;

public class RequestSender(Uri clientUrl, HttpClient httpClient)
{
    private const int LogPerRequests = 10;
    private int _requestN;

    public async Task<TestCase> SendAsync(TestInfo test, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _requestN) % LogPerRequests == 0)
            await Console.Out.WriteLineAsync($"Sending request #{_requestN}");

        JsonNode response = await SendToClientAsync(test.Data, ct);
        if (ArchiveIndexTxBuilder.ParseReturn(response) is { } probe)
            await Console.Out.WriteLineAsync($"  {Path.GetFileNameWithoutExtension(test.Pos.FilePath)}: {probe}");
        return new TestCase(test, response);
    }

    private async Task<JsonNode> SendToClientAsync(JsonNode requestData, CancellationToken ct)
    {
        try
        {
            using JsonContent content = JsonContent.Create(requestData);
            using HttpResponseMessage response = await httpClient.PostAsync(clientUrl, content, ct);
            response.EnsureSuccessStatusCode();

            JsonNode responseData = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct)
                ?? throw new JsonException("Empty response received.");

            if (responseData["error"] is { } error)
                throw new Exception(error.ToCompactString());

            return responseData;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"""
                 Request error: {ex.Message}
                   Client: {clientUrl}
                   Request: {requestData.ToCompactString()}
                 """
            );

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

