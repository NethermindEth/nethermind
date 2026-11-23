// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.LightClient.Rpc;

internal partial interface IEthRpcModule
{
    Task<ResultWrapper<UInt256>> eth_newFilter(JsonElement filter);
    Task<ResultWrapper<UInt256>> eth_newBlockFilter();
    Task<ResultWrapper<UInt256>> eth_newPendingTransactionFilter();
    Task<ResultWrapper<SerializedJson>> eth_getFilterChanges([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictQuantityConverter))] UInt256 filterId);
    Task<ResultWrapper<SerializedJson>> eth_getFilterLogs([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictQuantityConverter))] UInt256 filterId);
    Task<ResultWrapper<bool>> eth_uninstallFilter([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictQuantityConverter))] UInt256 filterId);
}
