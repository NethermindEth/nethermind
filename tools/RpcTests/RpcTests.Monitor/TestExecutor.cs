// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

internal class TestExecutor(IStatsReporter stats, HttpClient httpClient)
{
    private long _requestNumber;

    public async Task<TestFailure?> ExecuteAsync(ExecutionArgs args, TestContext test, CancellationToken ct = default)
    {
        stats.RecordTestRun();

        RequestContext requestContext = new(Interlocked.Increment(ref _requestNumber));
        test = test with { Request = requestContext };

        JsonNode request = test.Definition.Request.Compile(test);

        Console.WriteLine($"{test.Definition.FilePath} @ {test.RecentBlock}/{test.Head.Number}\n{request.ToCompactString()}\n");

        JsonNode? expectedStatic = test.Definition.Response?.Compile(test);

        // retry refence node, but require testee to reply on the first attempt
        JsonNode[] requests = await Task.WhenAll(
            SendOnceAsync(args.TargetUrl, request, ct),
            expectedStatic is null ? SendRetryAsync(args.ReferenceUrl!, request, ct) : Task.FromResult(expectedStatic)
        );

        JsonNode actual = requests[0], expected = requests[1];
        return ResponseComparer.Compare(actual, expected, test.Definition.IgnorePaths, isStatic: expectedStatic is not null)
            ? null
            : new TestFailure(test, request, actual, expected);
    }

    private async Task<JsonNode> SendRetryAsync(Uri url, JsonNode requestData, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(url, requestData, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       && !ct.IsCancellationRequested && attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 << (attempt - 1)), ct);
            }
        }
    }

    private async Task<JsonNode> SendOnceAsync(Uri url, JsonNode requestData, CancellationToken ct)
    {
        stats.RecordRequestRun();

        using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = JsonContent.Create(requestData) };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            throw WithDetails(
                new HttpRequestException(GetErrorMessage(request, null), ex),
                requestData
            );
        }

        using HttpResponseMessage _ = response;

        string responseContent;
        try
        {
            responseContent = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            throw WithDetails(
                new HttpRequestException(GetErrorMessage(request, response), ex),
                requestData
            );
        }

        if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(responseContent))
        {
            throw WithDetails(
                new HttpRequestException(GetErrorMessage(request, response)),
                requestData, responseContent
            );
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(responseContent);
        }
        catch (Exception ex)
        {
            throw WithDetails(
                new JsonException(GetErrorMessage(request, response), ex),
                requestData, responseContent
            );
        }

        return json ?? throw WithDetails(
            new JsonException(GetErrorMessage(request, response)),
            requestData, responseContent
        );

        static string GetErrorMessage(HttpRequestMessage request, HttpResponseMessage? response)
        {
            // strip url to host only to hide API key, if any
            Uri? uri = request.RequestUri;
            string host = uri is null ? "<no url>" : uri.Port == 80 ? uri.Host : $"{uri.Host}:{uri.Port}";

            return
                $"""
                 Failed to {request.Method} {host}
                 Got {(response is null ? "no response" : $"{(int)response.StatusCode} {response.StatusCode}")}
                 """;
        }

        static T WithDetails<T>(T exception, JsonNode requestData, string? responseContent = null) where T : Exception
        {
            exception.Data["request.txt"] = requestData.ToPrettyString();
            if (!string.IsNullOrEmpty(responseContent))
                exception.Data["response.txt"] = responseContent;
            return exception;
        }
    }
}
