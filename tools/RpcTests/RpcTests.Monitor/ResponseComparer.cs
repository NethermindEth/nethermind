// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

internal static class ResponseComparer
{
    // attempts to match Erigon rpc-tests comparison
    public static bool Compare(JsonNode actual, JsonNode expected, IReadOnlyList<JsonPath> ignorePaths, bool isStatic)
    {
        if (actual is not JsonObject actualObj || expected is not JsonObject expectedObj)
            return false;

        bool actualHasResult = actualObj.ContainsKey("result");
        bool expectedHasResult = expectedObj.ContainsKey("result");
        bool actualHasError = actualObj.ContainsKey("error");
        bool expectedHasError = expectedObj.ContainsKey("error");

        if (isStatic)
        {
            // "result": null → don't care about actual result value
            if (actualHasResult && expectedHasResult && expectedObj["result"] is null)
                return true;

            // "error": null → don't care about error details
            if (actualHasError && expectedHasError && expectedObj["error"] is null)
                return true;

            // only "jsonrpc" & "id" → don't care about response content
            if (!expectedHasResult && !expectedHasError && expectedObj.Count == 2)
                return true;
        }

        foreach (JsonPath path in ignorePaths)
        {
            actualObj.RemoveAt(path);
            expectedObj.RemoveAt(path);
        }

        return JsonNode.DeepEquals(actual, expected);
    }
}
