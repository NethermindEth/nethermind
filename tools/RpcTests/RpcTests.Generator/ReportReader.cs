// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;

namespace Nethermind.RpcTests.Generator;

// TODO: simplify readers, move common code to base class
public class ReportReader(FilePos[] sources, Filter filter)
{
    private class AmbiguousReportException(string message) : Exception(message);

    private const int LogPerLines = 1000;
    private int _lineN;
    private int _requestN;

    private readonly Dictionary<string, RequestInfo> _pendingRequests = [];

    public async Task ReadIntoAsync(ITargetBlock<TestCase> target, CancellationToken ct)
    {
        foreach (FilePos startLocation in sources)
        {
            int fileLineN = 1;
            await foreach (string line in File.ReadLinesAsync(startLocation.FilePath, ct))
            {
                if (++_lineN % LogPerLines == 0)
                    await Console.Out.WriteLineAsync($"Reading line #{_lineN}");

                if (fileLineN++ < startLocation.LineNumber) continue;

                if (GetResponse(line) is { } response && response.GetId() is { } responseId)
                {
                    if (!_pendingRequests.Remove(responseId, out RequestInfo? request))
                    {
                        await Console.Error.WriteLineAsync($"Request not found for response id: {responseId}");
                        continue;
                    }

                    TestCase testCase = new(request, response);
                    await target.SendAsync(testCase, ct);
                    continue;
                }

                if (!filter.IncludeRequest(line)) continue;

                FilePos pos = startLocation with { LineNumber = fileLineN };

                JsonNode? requestJson = null;
                try
                {
                    requestJson = JsonNode.Parse(line);
                }
                catch
                {
                    await Console.Error.WriteLineAsync($"Failed to parse request at {pos}");
                }

                if (requestJson is null) continue;
                if (!filter.IncludeRequest(requestJson)) continue;
                if (requestJson.GetId() is not { } requestId) continue;

                if (!_pendingRequests.TryAdd(requestId, new RequestInfo(pos, ++_requestN, requestJson)))
                    throw new AmbiguousReportException($"Multiple requests with the same id: {requestId}");
            }
        }

        target.Complete();
    }

    private static JsonNode? GetResponse(string line)
    {
        if (!line.Contains("\"response\"") || !line.Contains("\"report\"")) return null;

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(line);
        }
        catch
        {
            return null;
        }

        return json?["response"];
    }
}
