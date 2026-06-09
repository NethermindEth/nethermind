// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Generator;

internal abstract class JsonlProcessor(FilePos[] sources)
{
    private const int LogPerLines = 1000;
    protected int RequestN;
    private int _lineN;

    protected abstract Task ProcessEntryAsync(JsonNode json, FilePos pos, CancellationToken ct);

    protected async Task IterateLinesAsync(CancellationToken ct)
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

                if (JsonNode.Parse(line) is not {} json) continue;
                await ProcessEntryAsync(json, pos, ct);
            }
        }
    }
}
