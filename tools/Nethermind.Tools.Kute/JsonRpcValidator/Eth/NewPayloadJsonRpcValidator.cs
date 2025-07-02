// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nethermind.Tools.Kute.JsonRpcValidator.Eth;

public class NewPayloadJsonRpcValidator : IJsonRpcValidator
{
    private readonly Regex _pattern = new Regex("engine_newPayload");

    public bool IsValid(JsonRpc request, JsonDocument? response)
    {
        // If preconditions are not met, then mark it as Valid.
        if (!ShouldValidateRequest(request) || response is null)
        {
            return true;
        }

        if (!response.RootElement.TryGetProperty("result", out var result))
        {
            return false;
        }

        if (!result.TryGetProperty("status", out var status))
        {
            return false;
        }

        return status.GetString() == "VALID";
    }

    private bool ShouldValidateRequest(JsonRpc request)
    {
        if (request is JsonRpc.SingleJsonRpc { MethodName: not null } single)
        {
            if (_pattern.IsMatch(single.MethodName))
            {
                return true;
            }
        }

        return false;
    }
}
