// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace RpcTestsGen;

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
                   Request: {info.Request.Data.ToCompactString()}
                   Clients: {clientUrls[0]}, {clientUrls[i]}
                 """
            );
            yield break;
        }

        yield return new TestCase(info.Request.Location, request, response0);
    }
}

public record TestCase(FileLocation Location, JsonNode Request, JsonNode Response);
