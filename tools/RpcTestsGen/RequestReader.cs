// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;

namespace Nethermind.RpcTestsGen;

public class RequestReader(FilePos[] sources, Filter filter)
{
    private const int LogPerLines = 1000;
    private int _lineN;
    private int _requestN;

    public async Task ReadIntoAsync(ITargetBlock<RequestInfo> target, CancellationToken ct)
    {
        foreach (FilePos startLocation in sources)
        {
            int fileLineN = 1;
            await foreach (string line in File.ReadLinesAsync(startLocation.FilePath, ct))
            {
                if (++_lineN % LogPerLines == 0)
                    await Console.Out.WriteLineAsync($"Reading line #{_lineN}");

                if (fileLineN++ < startLocation.LineNumber) continue;
                if (!filter.IncludeRequest(line)) continue;

                FilePos pos = startLocation with { LineNumber = fileLineN };

                JsonNode? data = null;
                try
                {
                    data = JsonNode.Parse(line);
                }
                catch
                {
                    await Console.Error.WriteLineAsync($"Failed to parse request at {pos}");
                }

                if (data is null) continue;
                if (!filter.IncludeRequest(data)) continue;

                await target.SendAsync(new RequestInfo(pos, ++_requestN, data), ct);
            }
        }

        target.Complete();
    }
}

public record RequestInfo(FilePos Pos, int Number, JsonNode Data)
{
    public string Id => Data.GetId() ?? $"{Number}";
}
