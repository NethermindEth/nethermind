// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;

namespace Nethermind.RpcTests.Generator;

internal class RequestProcessor(FilePos[] sources, ITargetBlock<TestInfo> target, Filter filter)
    : JsonlProcessor<TestInfo>(sources, target)
{
    protected override async Task ProcessEntryAsync(FilePos pos, JsonNode json, CancellationToken ct)
    {
        if (!filter.IncludeRequest(json)) return;
        await Target.SendAsync(new TestInfo(pos, ++RequestN, json), ct);
    }
}
