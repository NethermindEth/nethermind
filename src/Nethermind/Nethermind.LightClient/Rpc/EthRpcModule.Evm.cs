// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.LightClient.Rpc;

internal partial interface IEthRpcModule
{
    Task<ResultWrapper<string>> eth_call(TransactionForRpc transaction, JsonElement? blockParameter = null,
        Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null);

    Task<ResultWrapper<string>> eth_estimateGas(TransactionForRpc transaction, JsonElement? blockParameter = null,
        Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null);

    Task<ResultWrapper<AccessListResultForRpc>> eth_createAccessList(TransactionForRpc transaction, JsonElement? blockParameter = null,
        Dictionary<Address, AccountOverride>? stateOverride = null, bool optimize = true);
}

[RpcModule(ModuleType.Debug)]
internal interface IDebugRpcModule : IRpcModule
{
    Task<ResultWrapper<GethLikeTxTrace>> debug_traceCall(TransactionForRpc call, JsonElement? blockParameter = null,
        [JsonRpcParameter(ConverterType = typeof(GethTraceOptionsConverter))] GethTraceOptions? options = null);

    Task<ResultWrapper<GethLikeTxTrace>> debug_traceTransaction(
        [JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictHashConverter))] Hash256 transactionHash,
        [JsonRpcParameter(ConverterType = typeof(GethTraceOptionsConverter))] GethTraceOptions? options = null);
}

internal sealed partial class EthRpcModule : IDebugRpcModule
{
    public Task<ResultWrapper<string>> eth_call(TransactionForRpc transaction, JsonElement? blockParameter = null,
        Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null) =>
        Execute((rpc, token) => rpc.CallAsync(transaction, blockParameter ?? default, false, token, stateOverride, blockOverride));

    public Task<ResultWrapper<string>> eth_estimateGas(TransactionForRpc transaction, JsonElement? blockParameter = null,
        Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null) =>
        Execute((rpc, token) => rpc.CallAsync(transaction, blockParameter ?? default, true, token, stateOverride, blockOverride));

    public Task<ResultWrapper<AccessListResultForRpc>> eth_createAccessList(TransactionForRpc transaction, JsonElement? blockParameter = null,
        Dictionary<Address, AccountOverride>? stateOverride = null, bool optimize = true) =>
        Execute((rpc, token) => rpc.CreateAccessListAsync(transaction, blockParameter ?? default, stateOverride, optimize, token));

    public Task<ResultWrapper<GethLikeTxTrace>> debug_traceCall(TransactionForRpc call, JsonElement? blockParameter = null,
        GethTraceOptions? options = null) => Execute((rpc, token) => rpc.TraceCallAsync(call, blockParameter ?? default, options, token));

    public Task<ResultWrapper<GethLikeTxTrace>> debug_traceTransaction(Hash256 transactionHash, GethTraceOptions? options = null) =>
        Execute((rpc, token) => rpc.TraceTransactionAsync(transactionHash, options, token));
}
