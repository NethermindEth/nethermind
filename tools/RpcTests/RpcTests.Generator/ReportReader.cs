// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;

namespace Nethermind.RpcTests.Generator;

internal class ReportReader(FilePos[] sources, Filter filter) : JsonlProcessor(sources)
{
    private readonly Dictionary<string, TestInfo> _pendingRequests = [];
    private ITargetBlock<TestCase> _target = null!;

    public async Task ReadIntoAsync(ITargetBlock<TestCase> target, CancellationToken ct)
    {
        _target = target;
        await IterateLinesAsync(ct);
        target.Complete();
    }

    protected override async Task ProcessEntryAsync(JsonNode json, FilePos pos, CancellationToken ct)
    {
        if (json["response"] is { } response && response.GetId() is { } responseId)
        {
            if (!_pendingRequests.Remove(responseId, out TestInfo? request))
                Console.Error.WriteLine($"Request not found for response id: {responseId}");
            else
                await _target.SendAsync(new TestCase(request, response), ct);
            return;
        }

        if (!filter.IncludeRequest(json)) return;
        if (json.GetId() is not { } requestId) return;

        if (_pendingRequests.ContainsKey(requestId))
            Console.Error.WriteLine($"Multiple requests with the same id: {requestId}");

        _pendingRequests[requestId] = new TestInfo(pos, ++RequestN, json);
    }
}
