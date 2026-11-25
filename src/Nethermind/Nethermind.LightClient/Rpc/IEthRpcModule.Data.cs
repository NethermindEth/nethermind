// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.LightClient.Rpc;

internal partial interface IEthRpcModule
{
    Task<ResultWrapper<SerializedJson>> eth_getHeaderByNumber(JsonElement blockParameter);
    Task<ResultWrapper<SerializedJson>> eth_getHeaderByHash([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictHashConverter))] Hash256 blockHash);
    Task<ResultWrapper<AccountForRpc?>> eth_getAccount([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictAddressConverter))] Address address, JsonElement? blockParameter = null);
    Task<ResultWrapper<AccountInfoForRpc>> eth_getAccountInfo([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictAddressConverter))] Address address, JsonElement? blockParameter = null);
    Task<ResultWrapper<StorageValuesResult>> eth_getStorageValues(JsonElement requests, JsonElement? blockParameter = null);
    Task<ResultWrapper<SerializedJson>> eth_getBlockAccessList(JsonElement blockParameter);
    Task<ResultWrapper<UInt256?>> eth_baseFee();
    Task<ResultWrapper<JsonNode>> eth_config();
    Task<ResultWrapper<EthCapabilities>> eth_capabilities();
    Task<ResultWrapper<SerializedJson>> eth_getRawTransactionByHash([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictHashConverter))] Hash256 transactionHash);
    Task<ResultWrapper<SerializedJson>> eth_getRawTransactionByBlockNumberAndIndex(JsonElement blockParameter, [JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictQuantityConverter))] UInt256 position);
    Task<ResultWrapper<SerializedJson>> eth_getRawTransactionByBlockHashAndIndex([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictHashConverter))] Hash256 blockHash, [JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictQuantityConverter))] UInt256 position);
}
