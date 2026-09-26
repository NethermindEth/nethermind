// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

/// <summary>
/// Sends JSON-RPC requests to a single node, throwing on transport, status, or parse failure
/// with the request and response attached for reporting.
/// </summary>
internal sealed class RpcClient(Uri url) : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(60) };

    public event Action? OnRequestStart;

    public async Task<JsonNode> SendAsync(JsonNode requestData, CancellationToken ct = default)
    {
        OnRequestStart?.Invoke();

        using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = JsonContent.Create(requestData) };

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException opEx && opEx.CancellationToken == ct))
        {
            throw WithDetails(new HttpRequestException(GetErrorMessage(request, null), ex), requestData);
        }

        using HttpResponseMessage _ = response;

        string responseContent;
        try
        {
            responseContent = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException opEx && opEx.CancellationToken == ct))
        {
            throw WithDetails(new HttpRequestException(GetErrorMessage(request, response), ex), requestData);
        }

        if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(responseContent))
        {
            throw WithDetails(new HttpRequestException(GetErrorMessage(request, response)), requestData, responseContent);
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(responseContent);
        }
        catch (Exception ex)
        {
            throw WithDetails(new JsonException(GetErrorMessage(request, response), ex), requestData, responseContent);
        }

        return json ?? throw WithDetails(new JsonException(GetErrorMessage(request, response)), requestData, responseContent);
    }

    private static string GetErrorMessage(HttpRequestMessage request, HttpResponseMessage? response)
    {
        // strip url to host only to hide API key, if any
        Uri? uri = request.RequestUri;
        string host = uri is null ? "<no url>" : uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        return
            $"""
             Failed to {request.Method} {host}
             Got {(response is null ? "no response" : $"{(int)response.StatusCode} {response.StatusCode}")}
             """;
    }

    private static T WithDetails<T>(T exception, JsonNode requestData, string? responseContent = null) where T : Exception
    {
        exception.Data["request.txt"] = requestData.ToPrettyString();
        if (!string.IsNullOrEmpty(responseContent))
            exception.Data["response.txt"] = responseContent;
        return exception;
    }

    void IDisposable.Dispose() => _client.Dispose();
}

internal static class RpcClientExtensions
{
    public static async Task<JsonNode> RetrySendAsync(this RpcClient client, JsonNode requestData, CancellationToken ct = default)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await client.SendAsync(requestData, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       && !ct.IsCancellationRequested && attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 << (attempt - 1)), ct);
            }
        }
    }
}
