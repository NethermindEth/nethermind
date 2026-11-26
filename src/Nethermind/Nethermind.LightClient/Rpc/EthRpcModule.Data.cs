// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class EthRpcModule
{
    public Task<ResultWrapper<SerializedJson>> eth_getHeaderByNumber(JsonElement blockParameter) =>
        Execute((rpc, token) => rpc.GetBlockHeaderAsync(blockParameter, false, token));
    public Task<ResultWrapper<SerializedJson>> eth_getHeaderByHash(Hash256 blockHash) =>
        Execute((rpc, token) => rpc.GetBlockHeaderAsync(HashParameter(blockHash), true, token));
    public Task<ResultWrapper<AccountForRpc?>> eth_getAccount(Address address, JsonElement? blockParameter = null) =>
        Execute((rpc, token) => rpc.GetAccountForRpcAsync(address, blockParameter ?? default, token));
    public Task<ResultWrapper<AccountInfoForRpc>> eth_getAccountInfo(Address address, JsonElement? blockParameter = null) =>
        Execute((rpc, token) => rpc.GetAccountInfoAsync(address, blockParameter ?? default, token));
    public Task<ResultWrapper<StorageValuesResult>> eth_getStorageValues(JsonElement requests, JsonElement? blockParameter = null) =>
        Execute((rpc, token) => rpc.GetStorageValuesAsync(requests, blockParameter ?? default, token));
    public Task<ResultWrapper<SerializedJson>> eth_getBlockAccessList(JsonElement blockParameter) =>
        Execute((rpc, token) => rpc.GetBlockAccessListAsync(blockParameter, token));
    public Task<ResultWrapper<UInt256?>> eth_baseFee() => Execute(static (rpc, token) => rpc.GetBaseFeeAsync(token));
    public Task<ResultWrapper<JsonNode>> eth_config() => Execute(static (rpc, token) => rpc.GetConfigAsync(token));
    public Task<ResultWrapper<EthCapabilities>> eth_capabilities() =>
        Execute(static (rpc, _) => Task.FromResult(rpc.GetCapabilities()));
    public Task<ResultWrapper<SerializedJson>> eth_getRawTransactionByHash(Hash256 transactionHash) =>
        Execute((rpc, token) => rpc.GetRawTransactionByHashAsync(transactionHash, token));
    public Task<ResultWrapper<SerializedJson>> eth_getRawTransactionByBlockNumberAndIndex(JsonElement blockParameter, UInt256 position) =>
        Execute((rpc, token) => rpc.GetRawTransactionAsync(blockParameter, false, position, token));
    public Task<ResultWrapper<SerializedJson>> eth_getRawTransactionByBlockHashAndIndex(Hash256 blockHash, UInt256 position) =>
        Execute((rpc, token) => rpc.GetRawTransactionAsync(HashParameter(blockHash), true, position, token));
}
