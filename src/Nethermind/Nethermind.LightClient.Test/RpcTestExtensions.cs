// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Core;
using Nethermind.LightClient.Rpc;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using RpcTransport = Nethermind.JsonRpc.Modules.RpcEndpoint;

namespace Nethermind.LightClient.Test;

internal static class RpcTestExtensions
{
    internal static async Task<object> InvokeAsync(this RpcBackend rpc, string method, JsonElement parameters, CancellationToken token)
    {
        using RpcService service = new(rpc, NullLogManager.Instance);
        return await service.InvokeAsync(method, parameters, token);
    }

    internal static async Task<object> InvokeAsync(this RpcService service, string method, JsonElement parameters, CancellationToken token)
    {
        using JsonRpcResponse response = await service.Service.SendRequestAsync(
            new JsonRpcRequest { JsonRpc = "2.0", Method = method, Params = parameters, Id = new JsonRpcId(1L) },
            new JsonRpcContext(RpcTransport.Http) { RequestAborted = token }, token);
        if (response is JsonRpcErrorResponse { Error: { } error }) throw new RpcException(error.Code, error.Message ?? "RPC error.");
        if (response is not IResultWrapper result) throw new RpcException(-32603, "Missing verified RPC result.");
        if (result.Result.ResultType != ResultType.Success)
            throw new RpcException(result.ErrorCode, result.Result.Error!, result.HasErrorData ? result.Data as string : null);
        if (result.Data is JsonElement existing) return existing;
        JsonElement value = BoundedJson.ToElement(result.Data);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => value,
        };
    }

    internal static Task<string> ExecuteAsync(this EvmExecutor call, VerifiedHead head, JsonElement request,
        bool estimateGas, CancellationToken token)
    {
        TransactionForRpc model = new EthereumJsonSerializer().Deserialize<TransactionForRpc>(request.GetRawText())
            ?? throw new RpcException(-32602, "Invalid transaction object.");
        return call.ExecuteAsync(head, model, estimateGas, token);
    }
}
