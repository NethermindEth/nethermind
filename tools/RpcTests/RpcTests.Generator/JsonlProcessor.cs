// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;

namespace Nethermind.RpcTests.Generator;

internal abstract class JsonlProcessor<T>(FilePos[] sources, ITargetBlock<T> target)
{
    private const int LogPerLines = 1000;
    protected int RequestN;
    private int _lineN;

    protected readonly ITargetBlock<T> Target = target;

    protected abstract Task ProcessEntryAsync(FilePos pos, JsonNode json, CancellationToken ct);

    public async Task ProcessAllAsync(CancellationToken ct)
    {
        foreach (FilePos startLocation in sources)
        {
            int fileLineN = 1;
            await foreach (string line in File.ReadLinesAsync(startLocation.FilePath, ct))
            {
                if (++_lineN % LogPerLines == 0)
                    await Console.Out.WriteLineAsync($"Reading line #{_lineN}");

                if (fileLineN++ < startLocation.LineNumber) continue;
                if (string.IsNullOrWhiteSpace(line)) continue;

                FilePos pos = startLocation with { LineNumber = fileLineN };

                if (JsonNode.Parse(line) is not { } entry) continue;

                if (entry is JsonArray array)
                {
                    foreach (JsonNode? arrayEntry in array)
                    {
                        if (arrayEntry is not null)
                            await ProcessEntryAsync(pos, arrayEntry, ct);
                    }
                }
                else
                {
                    await ProcessEntryAsync(pos, entry, ct);
                }
            }
        }

        Target.Complete();
    }
}
