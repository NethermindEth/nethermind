// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

namespace Nethermind.Tools.Kute.JsonRpcValidator.Eth;

public sealed class NewPayloadJsonRpcValidator : IJsonRpcValidator
{
    private readonly Regex _pattern = new Regex("engine_newPayload", RegexOptions.Compiled);

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
            if (_pattern.IsMatch(single.MethodName))
            {
                return true;
            }
        }

        return false;
    }
}
