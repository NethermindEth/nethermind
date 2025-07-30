// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcValidator.Eth;

public sealed class NonErrorJsonRpcValidator : IJsonRpcValidator
{
    public bool IsValid(JsonRpc.Request request, JsonRpc.Response response)
    {
        if (request is JsonRpc.Request.Batch)
        {
            return true;
        }

        return !response.Json.TryGetProperty("error", out _);
    }
}
