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
        using JsonContent content = JsonContent.Create(request);

        Console.WriteLine($"{test.Definition.FilePath} @ {test.RecentBlock}/{test.Head.Number}\n{request.ToCompactString()}\n");

        JsonNode? expectedStatic = test.Definition.Response?.Compile(test);
        JsonNode actual = await SendAsync(args.TargetUrl, content, ct);
        JsonNode expected = expectedStatic ?? await SendAsync(args.ReferenceUrl!, content, ct);

        return ResponseComparer.Compare(actual, expected, isStatic: expectedStatic is not null)
            ? null
            : new TestFailure(test, request, actual, expected);
    }

    private async Task<JsonNode> SendAsync(Uri url, JsonContent content, CancellationToken ct)
    {
        stats.RecordRequestRun();
        using HttpResponseMessage response = await httpClient.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        return await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct)
            ?? throw new JsonException("Empty response received.");
    }
}
