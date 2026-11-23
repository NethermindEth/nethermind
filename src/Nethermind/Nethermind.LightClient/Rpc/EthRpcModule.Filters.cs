// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using RpcTransport = Nethermind.JsonRpc.Modules.RpcEndpoint;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class EthRpcModule
{
    public Task<ResultWrapper<UInt256>> eth_newFilter(JsonElement filter) =>
        Execute((rpc, _) => Task.FromResult(rpc.Filters.Install(RpcBackend.ParseLogQuery(filter))));

    public Task<ResultWrapper<UInt256>> eth_newBlockFilter() =>
        Execute(static (rpc, _) => Task.FromResult(rpc.Filters.Install()));

    public Task<ResultWrapper<UInt256>> eth_newPendingTransactionFilter() =>
        Execute(static (rpc, _) => Task.FromResult(rpc.Filters.Install(pending: true)));

    public Task<ResultWrapper<SerializedJson>> eth_getFilterChanges(UInt256 filterId)
    {
        bool webSocket = JsonRpcContext.Current?.RpcEndpoint == RpcTransport.Ws;
        return Execute((rpc, token) => rpc.Filters.PollAsync(filterId, rpc, token, webSocket), static response => response);
    }

    public Task<ResultWrapper<SerializedJson>> eth_getFilterLogs(UInt256 filterId) =>
        Execute((rpc, token) => rpc.GetLogsAsync(rpc.Filters.GetLogQuery(filterId), token));

    public Task<ResultWrapper<bool>> eth_uninstallFilter(UInt256 filterId) =>
        Execute((rpc, _) => Task.FromResult(rpc.Filters.Uninstall(filterId)));
}
