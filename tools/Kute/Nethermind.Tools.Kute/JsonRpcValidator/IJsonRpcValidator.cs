// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcValidator;

public interface IJsonRpcValidator
{
    bool IsValid(JsonRpc.Request request, JsonRpc.Response response);
    bool IsInvalid(JsonRpc.Request request, JsonRpc.Response response) => !IsValid(request, response);
}
