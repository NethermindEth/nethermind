// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

internal class TestExecutor(RpcClient target, RpcClient? reference, EmptyTestsTracker emptyTests, IStatsReporter stats)
{
    private long _requestNumber;

    public async Task<TestFailure?> ExecuteAsync(TestContext test, CancellationToken ct = default)
    {
        stats.RecordTestRun();

        RequestContext requestContext = new(Interlocked.Increment(ref _requestNumber));
        test = test with { Request = requestContext };

        JsonNode request = test.Definition.Request.Compile(test);

        Console.WriteLine($"{test.Definition.FilePath} @ {test.RecentNumber}/{test.Head.Number}\n{request.ToCompactString()}\n");

        // TODO: add test identifier to request params
        // TODO: inject custom IRcpClient implementation finding corresponding test
        JsonNode? expectedStatic = test.Definition.Response?.Compile(test);

        // retry refence node, but require testee to reply on the first attempt
        JsonNode[] requests = await Task.WhenAll(
            target.SendAsync(request, ct),
            expectedStatic is null ? reference!.RetrySendAsync(request, ct) : Task.FromResult(expectedStatic)
        );

        JsonNode actual = requests[0], expected = requests[1];
        emptyTests.OnTestExecuted(test, request, expected);

        return ResponseComparer.Compare(actual, expected, test.Definition.IgnorePaths, isStatic: expectedStatic is not null)
            ? null
            : new TestFailure(test, request, actual, expected);
    }
}
