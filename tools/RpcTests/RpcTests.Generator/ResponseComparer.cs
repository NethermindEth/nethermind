// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Generator;

public class ResponseComparer(Uri[] clientUrls)
{
    public IEnumerable<TestCase> Compare(ResponseInfo info)
    {
        JsonNode request = info.Request.Data;
        JsonNode response0 = info.Responses[0];

        for (int i = 1; i < info.Responses.Length; i++)
        {
            JsonNode responseI = info.Responses[i];

            if (JsonNode.DeepEquals(response0, responseI)) continue;

            Console.Error.WriteLine(
                $"""
                 Mismatch
                   Request: {request.ToCompactString()}
                   Clients: {clientUrls[0]}, {clientUrls[i]}
                 """
            );
            yield break;
        }

        yield return new TestCase(info.Request, response0);
    }
}

[SuppressMessage("ReSharper", "UnusedMember.Global", Justification = "Used by dynamic formatter")]
public record TestCase(RequestInfo RequestInfo, JsonNode Response)
{
    public FilePos Pos => RequestInfo.Pos;
    public JsonNode Request => RequestInfo.Data;

    public string FileDir => field ??= Path.GetDirectoryName(Pos.FilePath) ?? "";
    public string FileName => field ??= Path.GetFileNameWithoutExtension(Pos.FilePath);
    public string FileExt => field ??= Path.GetExtension(Pos.FilePath);
    public int RequestN => RequestInfo.Number;
    public string RequestId => RequestInfo.Id;
    public int TestN { get; set; }
}
