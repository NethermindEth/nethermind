// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;

namespace Nethermind.RpcTests.Generator;

internal class ReportProcessor(FilePos[] sources, ITargetBlock<TestCase> target, Filter filter)
    : JsonlProcessor<TestCase>(sources, target)
{
    private class AmbiguousReportException(string message) : Exception(message);

    private readonly Dictionary<string, TestInfo> _pendingRequests = [];

    protected override async Task ProcessEntryAsync(FilePos pos, JsonNode json, CancellationToken ct)
    {
        if (json["response"] is { } response && response.GetId() is { } responseId)
        {
            if (!_pendingRequests.Remove(responseId, out TestInfo? request))
                Console.Error.WriteLine($"Request not found for response id: {responseId}");
            else
                await Target.SendAsync(new TestCase(request, response), ct);
            return;
        }

        if (!filter.IncludeRequest(json)) return;
        if (json.GetId() is not { } requestId) return;

        if (!_pendingRequests.TryAdd(requestId, new TestInfo(pos, ++RequestN, json)))
            throw new AmbiguousReportException($"Multiple requests with the same id: {requestId}");
    }
}
