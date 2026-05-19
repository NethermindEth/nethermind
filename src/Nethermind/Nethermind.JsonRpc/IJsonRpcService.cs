// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public interface IJsonRpcService
{
    ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context);
    JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, JsonRpcId? id = null, string? methodName = null);
}
