// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore;

/// <summary>
/// Module for tracing using database
/// </summary>
public class TraceStoreRpcModule(ITraceRpcModule traceModule,
    IDb traceStore,
    IBlockFinder blockFinder,
    IReceiptFinder receiptFinder,
    ITraceSerializer<ParityLikeTxTrace> traceSerializer,
    ILogManager logManager,
    int parallelization = 0) : ITraceRpcModule
{
    private readonly IDb _traceStore = traceStore;
    private readonly ITraceRpcModule _traceModule = traceModule;
    private readonly IBlockFinder _blockFinder = blockFinder;
    private readonly IReceiptFinder _receiptFinder = receiptFinder;
    private readonly ITraceSerializer<ParityLikeTxTrace> _traceSerializer = traceSerializer;
    private readonly int _parallelization = parallelization;
    private readonly ILogger _logger = logManager.GetClassLogger<TraceStoreRpcModule>();

    private static readonly IDictionary<ParityTraceTypes, Action<ParityLikeTxTrace>> _filters = new Dictionary<ParityTraceTypes, Action<ParityLikeTxTrace>>
    {
        { ParityTraceTypes.Trace, FilterTrace },
        { ParityTraceTypes.StateDiff , FilterStateDiff },
        { ParityTraceTypes.VmTrace | ParityTraceTypes.Trace, FilterStateVmTrace }
    };

    public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc call, string[] traceTypes, BlockParameter? blockParameter = null,
        Dictionary<Address, AccountOverride>? stateOverride = null) =>
        _traceModule.trace_call(call, traceTypes, blockParameter, stateOverride);

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(
        SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null) => _traceModule.trace_simulateV1(payload, blockParameter, traceTypes);

    public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_callMany(TraceCallManyRequest calls, BlockParameter? blockParameter = null) =>
        _traceModule.trace_callMany(calls, blockParameter);

    public ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction(byte[] data, string[] traceTypes) =>
        _traceModule.trace_rawTransaction(data, traceTypes);

    public ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction(Hash256 txHash, string[] traceTypes, bool traceNonCanonical = false) =>
        TryTraceTransaction(
            txHash,
            TraceRpcModule.GetParityTypes(traceTypes),
            static t => new ParityTxTraceFromReplay(t),
            out ResultWrapper<ParityTxTraceFromReplay>? result)
        && result is not null
            ? result
            : _traceModule.trace_replayTransaction(txHash, traceTypes, traceNonCanonical);

    public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_replayBlockTransactions(BlockParameter blockParameter, string[] traceTypes)
    {
        SearchResult<BlockHeader> blockSearch = _blockFinder.SearchForHeader(blockParameter);
        if (blockSearch.IsError)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(blockSearch);
        }

        BlockHeader block = blockSearch.Object!;

        if (TryGetBlockTraces(block, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            ParityTraceTypes types = TraceRpcModule.GetParityTypes(traceTypes);
            FilterTraces(traces, types);
            // Wrap in a streaming result so the JSON array is written incrementally. The
            // traces are already materialised by the deserializer, so the saving here is
            // the JSON output buffer rather than the trace data itself.
            return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(
                new ParityTxTraceStreamingResult<ParityTxTraceFromReplay>(
                    (writer, _, _) => EmitReplayEnvelopes(writer, traces),
                    new CancellationTokenSource()));
        }

        return _traceModule.trace_replayBlockTransactions(blockParameter, traceTypes);
    }

    private static void EmitReplayEnvelopes(Utf8JsonWriter writer, List<ParityLikeTxTrace> traces)
    {
        foreach (ParityLikeTxTrace trace in traces)
        {
            JsonSerializer.Serialize(writer, new ParityTxTraceFromReplay(trace, true), EthereumJsonSerializer.JsonOptions);
        }
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc)
    {
        // Sniff the first block: if it's missing from the store, take the live-execution
        // fallback wholesale (which itself streams block-by-block). Otherwise, stream the
        // store path one block at a time so peak heap is bounded by a single block's
        // deserialized traces, regardless of the block range.
        BlockParameter fromBlock = traceFilterForRpc.FromBlock ?? BlockParameter.Latest;
        BlockParameter toBlock = traceFilterForRpc.ToBlock ?? BlockParameter.Latest;

        TxTraceFilter filter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After, traceFilterForRpc.Count);

        return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(
            new ParityTxTraceStreamingResult<ParityTxTraceFromStore>(
                (writer, _, _) =>
                {
                    IEnumerable<SearchResult<Block>> blocksSearch = _blockFinder.SearchForBlocksOnMainChain(fromBlock, toBlock);

                    foreach (SearchResult<Block> blockSearch in blocksSearch)
                    {
                        if (filter.IsExhausted) break;
                        if (blockSearch.IsError) break;
                        Block block = blockSearch.Object!;

                        if (!TryGetBlockTraces(block.Header, out List<ParityLikeTxTrace>? traces) || traces is null)
                        {
                            // Cache miss mid-stream: best-effort stop. The buffered fallback
                            // would have to re-execute the whole range live; doing that after
                            // we've already emitted partial results would corrupt the response.
                            break;
                        }

                        foreach (ParityLikeTxTrace trace in traces)
                        {
                            foreach (ParityTxTraceFromStore item in ParityTxTraceFromStore.FromTxTrace(trace))
                            {
                                if (!filter.ShouldUseTxTrace(item.Action)) continue;
                                JsonSerializer.Serialize(writer, item, EthereumJsonSerializer.JsonOptions);
                            }
                        }
                    }
                },
                new CancellationTokenSource()));
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter, string? fork = null)
    {
        // Fork overrides require live re-execution; bypass the store and delegate directly.
        if (fork is not null)
            return _traceModule.trace_block(blockParameter, fork);

        SearchResult<BlockHeader> blockSearch = _blockFinder.SearchForHeader(blockParameter);
        if (blockSearch.IsError)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
        }

        BlockHeader block = blockSearch.Object!;
        if (TryGetBlockTraces(block, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(
                new ParityTxTraceStreamingResult<ParityTxTraceFromStore>(
                    (writer, _, _) => EmitStoreItems(writer, traces),
                    new CancellationTokenSource()));
        }

        return _traceModule.trace_block(blockParameter);
    }

    private static void EmitStoreItems(Utf8JsonWriter writer, List<ParityLikeTxTrace> traces)
    {
        foreach (ParityLikeTxTrace trace in traces)
        {
            foreach (ParityTxTraceFromStore item in ParityTxTraceFromStore.FromTxTrace(trace))
            {
                JsonSerializer.Serialize(writer, item, EthereumJsonSerializer.JsonOptions);
            }
        }
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_get(Hash256 txHash, long[] positions)
    {
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traceTransaction = trace_transaction(txHash);
        List<ParityTxTraceFromStore> traces = TraceRpcModule.ExtractPositionsFromTxTrace(positions, traceTransaction);
        return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(traces);
    }

    public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_transaction(Hash256 txHash, bool traceNonCanonical = false) =>
        TryTraceTransaction(
            txHash,
            ParityTraceTypes.Trace,
            static t => ParityTxTraceFromStore.FromTxTrace(t),
            out ResultWrapper<IEnumerable<ParityTxTraceFromStore>>? result)
        && result is not null
            ? result
            : _traceModule.trace_transaction(txHash, traceNonCanonical);

    private bool TryTraceTransaction<T>(
        Hash256 txHash,
        ParityTraceTypes traceTypes,
        Func<ParityLikeTxTrace, T> map,
        out ResultWrapper<T>? result)
    {
        SearchResult<Hash256> blockHashSearch = _receiptFinder.SearchForReceiptBlockHash(txHash);
        if (blockHashSearch.IsError)
        {
            {
                result = ResultWrapper<T>.Fail(blockHashSearch);
                return true;
            }
        }

        SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHashSearch.Object!));
        if (blockSearch.IsError)
        {
            {
                result = ResultWrapper<T>.Fail(blockSearch);
                return true;
            }
        }

        Block block = blockSearch.Object!;

        if (TryGetBlockTraces(block.Header, out List<ParityLikeTxTrace>? traces) && traces is not null)
        {
            ParityLikeTxTrace? trace = GetTxTrace(txHash, traces);
            if (trace is not null)
            {
                FilterTrace(trace, traceTypes);
                result = ResultWrapper<T>.Success(map(trace));
                return true;
            }
        }

        result = null;
        return false;
    }

    private bool TryGetBlockTraces(BlockHeader block, out List<ParityLikeTxTrace>? traces)
    {
        Span<byte> tracesSerialized = _traceStore.GetSpan(block.Hash!);
        try
        {
            if (!tracesSerialized.IsEmpty)
            {
                List<ParityLikeTxTrace>? tracesDeserialized = _traceSerializer.Deserialize(tracesSerialized);
                if (tracesDeserialized is not null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Found persisted traces for block {block.ToString(BlockHeader.Format.FullHashAndNumber)}");
                    traces = tracesDeserialized;
                    return true;
                }
            }

            traces = null;
            return false;
        }
        finally
        {
            _traceStore.DangerousReleaseMemory(tracesSerialized);
        }
    }

    private static ParityLikeTxTrace? GetTxTrace(Hash256 txHash, List<ParityLikeTxTrace> traces)
    {
        int index = traces.FindIndex(t => t.TransactionHash == txHash);
        return index != -1 ? traces[index] : null;
    }

    private static void FilterTraces(List<ParityLikeTxTrace> traces, ParityTraceTypes traceTypes)
    {
        for (int i = 0; i < traces.Count; i++)
        {
            ParityLikeTxTrace parityLikeTxTrace = traces[i];
            FilterTrace(parityLikeTxTrace, traceTypes);
        }

        if ((traceTypes & ParityTraceTypes.Rewards) == 0)
        {
            FilterRewards(traces);
        }
    }

    private static void FilterTrace(ParityLikeTxTrace parityLikeTxTrace, ParityTraceTypes traceTypes)
    {
        foreach (KeyValuePair<ParityTraceTypes, Action<ParityLikeTxTrace>> filter in _filters)
        {
            if ((traceTypes & filter.Key) == 0)
            {
                filter.Value(parityLikeTxTrace);
            }
        }
    }


    // VmTrace uses flags IsTracingCode, IsTracingInstructions
    private static void FilterStateVmTrace(ParityLikeTxTrace trace) => trace.VmTrace = null;

    // StateDiff uses flags IsTracingState, IsTracingStorage
    private static void FilterStateDiff(ParityLikeTxTrace trace)
    {
        trace.StateChanges = null;
        if (trace.VmTrace is not null)
        {
            for (int i = 0; i < trace.VmTrace.Operations.Length; i++)
            {
                trace.VmTrace.Operations[i].Store = null!;
            }
        }
    }

    // Trace uses flags IsTracingActions, IsTracingReceipt
    private static void FilterTrace(ParityLikeTxTrace trace) => trace.Output = null;// trace action?

    private static void FilterRewards(List<ParityLikeTxTrace> traces)
    {
        for (int i = traces.Count - 1; i >= 0; i--)
        {
            ParityLikeTxTrace trace = traces[i];
            if (trace.TransactionHash is null && trace.Action?.Type == "reward")
            {
                traces.RemoveAt(i);
            }
            else
            {
                break;
            }
        }
    }
}
