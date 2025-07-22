// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

public sealed class NullJsonRpcSubmitter : IJsonRpcSubmitter
{
    public async Task<JsonRpc.Response> Submit(JsonRpc.Request rpc, CancellationToken token = default)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), token);
        return new JsonRpc.Response(JsonDocument.Parse("""
        {
            "jsonrpc": "2.0",
            "id": 1,
            "error": {
                "code": -32601,
                "message": "Method not found"
            }
        }
        """));
    }
}
