// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;

namespace Nethermind.RpcTests.Generator;

internal class RequestReader(FilePos[] sources, Filter filter) : JsonlProcessor(sources)
{
    private ITargetBlock<TestInfo> _target = null!;

    public async Task ReadIntoAsync(ITargetBlock<TestInfo> target, CancellationToken ct)
    {
        _target = target;
        await IterateLinesAsync(ct);
        target.Complete();
    }

    protected override async Task ProcessEntryAsync(JsonNode json, FilePos pos, CancellationToken ct)
    {
        if (!filter.IncludeRequest(json)) return;
        await _target.SendAsync(new TestInfo(pos, ++RequestN, json), ct);
    }
}
