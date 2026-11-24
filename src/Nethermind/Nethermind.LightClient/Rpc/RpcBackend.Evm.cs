// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class RpcBackend
{
    internal Task<string> CallAsync(TransactionForRpc transaction, JsonElement block, bool estimateGas, CancellationToken cancellationToken,
        Dictionary<Address, AccountOverride>? stateOverrides = null, BlockOverride? blockOverrides = null) =>
        WithCallHeadAsync(block, (executor, head) => executor.ExecuteAsync(head, transaction, estimateGas, cancellationToken, stateOverrides, blockOverrides),
            cancellationToken);

    internal Task<AccessListResultForRpc> CreateAccessListAsync(TransactionForRpc transaction, JsonElement block,
        Dictionary<Address, AccountOverride>? stateOverrides, bool optimize, CancellationToken token) =>
        WithCallHeadAsync(block, (executor, head) => executor.CreateAccessListAsync(head, transaction, stateOverrides, optimize, token), token);

    internal Task<GethLikeTxTrace> TraceCallAsync(TransactionForRpc transaction, JsonElement block, GethTraceOptions? options, CancellationToken token) =>
        WithCallHeadAsync(block, (executor, head) => executor.TraceCallAsync(head, transaction, options, token), token);

    internal Task<GethLikeTxTrace> TraceTransactionAsync(Hash256 hash, GethTraceOptions? options, CancellationToken token)
    {
        if (calls is null) throw new RpcException(-32601, "Method is not supported by the verified RPC.");
        TimeSpan timeout = EvmExecutor.TraceTimeLimit(options);
        if (options!.StateOverrides is not null || options.BlockOverrides is not null)
            throw new RpcException(-32602, "Transaction tracing uses the authenticated block context and state.");
        return calls.TraceTransactionAsync(options, timeout,
            cancel => FindRecentTransactionAsync(hash, (data, header, lookup) => data.FindTransactionInBlockAsync(header, hash, lookup), cancel), token);
    }

    private async Task<T> WithCallHeadAsync<T>(JsonElement block, Func<EvmExecutor, VerifiedHead, Task<T>> execute, CancellationToken token)
    {
        if (calls is null) throw new RpcException(-32601, "Method is not supported by the verified RPC.");
        (VerifiedHead head, bool historical) = block.ValueKind == JsonValueKind.Undefined
            ? (await LatestHeadAsync(token), false) : await ResolveStateHeadAsync(block, token);
        try { return await execute(calls, head); }
        finally { if (historical) _historicalStateRequests.Release(); }
    }
}
