// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;

namespace RpcTestsGen;

public class RequestLoader(FileLocation[] sources, string? include, string? exclude)
{
    public async Task LoadIntoAsync(ITargetBlock<RequestInfo> target, CancellationToken ct)
    {
        foreach (FileLocation startLocation in sources)
        {
            int lineN = 1;
            await foreach (string line in File.ReadLinesAsync(startLocation.FilePath, ct))
            {
                if (lineN++ < startLocation.LineNumber) continue;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (exclude is not null && line.Contains(exclude, StringComparison.Ordinal)) continue;
                if (include is not null && !line.Contains(include, StringComparison.Ordinal)) continue;

                FileLocation location = startLocation with {LineNumber = lineN};

                JsonNode? data = null;
                try
                {
                    data = JsonNode.Parse(line);
                }
                catch
                {
                    await Console.Error.WriteLineAsync($"Failed to parse request at {location}");
                }

                if (data is null) continue;

                await target.SendAsync(new RequestInfo(location, data), ct);
            }
        }

        target.Complete();
    }
}

public record RequestInfo(FileLocation Location, JsonNode Data);
