// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules.Subscribe;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public partial class DebugRpcModule
{
    internal SubscriptionManager? TraceChainSubscriptions { get; set; }
    internal Func<Block, TraceChainOptions, CancellationToken, TraceChainTransaction?[]>? TraceChainReplay { get; set; }

    ResultWrapper<string> IDebugSubscriptionRpcModule.debug_subscribe(string subscription, BlockParameter start, BlockParameter end, TraceChainOptions? options)
    {
        IJsonRpcDuplexClient? client = JsonRpcContext.Current.Value?.DuplexClient;
        if (client is null || TraceChainSubscriptions is null || TraceChainReplay is null)
            return ResultWrapper<string>.Fail("notifications not supported", ErrorCodes.InvalidInput);
        if (subscription != "traceChain")
            return ResultWrapper<string>.Fail($"no \"{subscription}\" subscription in debug namespace", ErrorCodes.MethodNotFound);
        if (start.Type == BlockParameterType.BlockHash || end.Type == BlockParameterType.BlockHash)
            return ResultWrapper<string>.Fail("Invalid subscription parameters", ErrorCodes.InvalidParams);

        SearchResult<Block> from = blockFinder.SearchForBlock(start);
        if (from.IsError) return ResultWrapper<string>.Fail(from);
        SearchResult<Block> to = blockFinder.SearchForBlock(end);
        if (to.IsError) return ResultWrapper<string>.Fail(to);
        ulong first = from.Object.Number;
        ulong last = to.Object.Number;
        if (first >= last)
            return ResultWrapper<string>.Fail($"end block (#{last}) needs to come after start block (#{first})", ErrorCodes.InvalidInput);

        TraceChainSubscription pending = new(client, TraceChainSubscriptions, _logger,
            (sink, token) => sink.ReplayModule!.TraceChainAsync(sink, first, last, options ?? new TraceChainOptions(), token));
        try
        {
            TraceChainSubscriptions.AddSubscription(pending);
            if (pending.IsDisposed) pending.Abort();
            return new PendingTraceChainResponse(pending) { Data = pending.Id };
        }
        catch
        {
            pending.Abort();
            throw;
        }
    }

    ResultWrapper<bool> IDebugSubscriptionRpcModule.debug_unsubscribe(string subscriptionId)
    {
        IJsonRpcDuplexClient? client = JsonRpcContext.Current.Value?.DuplexClient;
        return ResultWrapper<bool>.Success(client is not null && TraceChainSubscriptions?.RemoveSubscription(client, subscriptionId) == true);
    }

    private async Task TraceChainAsync(TraceChainSubscription sink, ulong first, ulong last, TraceChainOptions options, CancellationToken token)
    {
        for (ulong number = first + 1; ; number++)
        {
            token.ThrowIfCancellationRequested();
            // Like Geth traceChain, endpoints bound the heights; each block is fetched by number during replay.
            Block block = blockFinder.FindBlock(new BlockParameter(number))
                ?? throw new InvalidOperationException($"Cannot find block {number}");
            using ResultWrapper<string>? stateError = CheckTraceBaseState<string>(block.Header);
            if (stateError is not null) throw new InvalidOperationException(stateError.Result.Error);

            TraceChainTransaction?[] traces = TraceChainReplay!(block, options, token);
            try
            {
                if (traces.Length != 0 || number == last)
                    await sink.SendAsync(new TraceChainBlock(number, block.Hash!, traces), token);
            }
            finally
            {
                foreach (TraceChainTransaction? trace in traces) trace?.Result?.Dispose();
            }
            if (number == last) break;
        }
    }
}
