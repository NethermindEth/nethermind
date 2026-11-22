// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;

namespace Nethermind.LightClient.Rpc;

[RpcModule(ModuleType.Eth)]
internal partial interface IEthRpcModule : IRpcModule
{
    ResultWrapper<ulong> eth_chainId();
    ResultWrapper<Address[]> eth_accounts();
    ResultWrapper<SyncingResult> eth_syncing();
    Task<ResultWrapper<ulong>> eth_blockNumber();
    Task<ResultWrapper<UInt256>> eth_blobBaseFee();
    Task<ResultWrapper<UInt256>> eth_gasPrice();
    Task<ResultWrapper<UInt256>> eth_maxPriorityFeePerGas();
    Task<ResultWrapper<UInt256>> eth_getBalance([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictAddressConverter))] Address address, JsonElement? blockParameter = null);
    Task<ResultWrapper<ulong>> eth_getTransactionCount([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictAddressConverter))] Address address, JsonElement? blockParameter = null);
    Task<ResultWrapper<byte[]>> eth_getCode([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictAddressConverter))] Address address, JsonElement? blockParameter = null);
    Task<ResultWrapper<byte[]>> eth_getStorageAt([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictAddressConverter))] Address address, [JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StorageIndexParameterConverter))] StorageIndex position, JsonElement? blockParameter = null);
    Task<ResultWrapper<AccountProof>> eth_getProof([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictAddressConverter))] Address address, [JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StorageKeysConverter))] StorageIndex[] storageKeys, JsonElement? blockParameter = null);
    Task<ResultWrapper<Hash256>> eth_sendRawTransaction([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictDataConverter))] byte[] transaction);
    Task<ResultWrapper<JsonElement>> eth_sendRawTransactionSync(
        [JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictDataConverter))] byte[] transaction,
        JsonElement? timeoutMs = null);
    Task<ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>>> eth_simulateV1(JsonElement payload, JsonElement? blockParameter = null);
    Task<ResultWrapper<JsonElement>> eth_feeHistory([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictQuantityConverter))] UInt256 blockCount, JsonElement newestBlock, double[] rewardPercentiles);
    Task<ResultWrapper<JsonElement>> eth_getBlockByNumber(JsonElement blockParameter, bool returnFullTransactionObjects);
    Task<ResultWrapper<JsonElement>> eth_getBlockByHash([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictHashConverter))] Hash256 blockHash, bool returnFullTransactionObjects);
    Task<ResultWrapper<JsonElement>> eth_getBlockTransactionCountByNumber(JsonElement blockParameter);
    Task<ResultWrapper<JsonElement>> eth_getBlockTransactionCountByHash([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictHashConverter))] Hash256 blockHash);
    Task<ResultWrapper<JsonElement>> eth_getTransactionByBlockNumberAndIndex(JsonElement blockParameter, [JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictQuantityConverter))] UInt256 position);
    Task<ResultWrapper<JsonElement>> eth_getTransactionByBlockHashAndIndex([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictHashConverter))] Hash256 blockHash, [JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictQuantityConverter))] UInt256 position);
    Task<ResultWrapper<JsonElement>> eth_getTransactionByHash([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictHashConverter))] Hash256 transactionHash);
    Task<ResultWrapper<JsonElement>> eth_getTransactionReceipt([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictHashConverter))] Hash256 transactionHash);
    Task<ResultWrapper<JsonElement>> eth_getBlockReceipts(JsonElement blockParameter);
    Task<ResultWrapper<JsonElement>> eth_getLogs(JsonElement filter);
}
