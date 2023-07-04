// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcService
    {
        Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context);
        JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage);
        JsonRpcErrorResponse GetErrorResponse(string methodName, int errorCode, string errorMessage, object id);
        JsonConverter[] Converters { get; }
    }
}
