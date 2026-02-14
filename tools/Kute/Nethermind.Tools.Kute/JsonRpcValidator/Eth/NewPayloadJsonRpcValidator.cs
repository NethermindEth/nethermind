// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcValidator.Eth;

public sealed class NewPayloadJsonRpcValidator : IJsonRpcValidator
{
    public bool IsValid(JsonRpc.Request request, JsonRpc.Response response)
    {
        // If preconditions are not met, then mark it as Valid.
        if (!ShouldValidateRequest(request))
        {
            return true;
        }

        if (response.Json["result"]?["status"] is { } status)
        {
            return (string?)status == "VALID";
        }

        return false;
    }

    private bool ShouldValidateRequest(JsonRpc.Request request)
    {
        if (request is JsonRpc.Request.Single { MethodName: not null } single)
        {
            if (single.MethodName.Contains("engine_newPayload", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
